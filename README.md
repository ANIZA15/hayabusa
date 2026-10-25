# Hayabusa (隼)

**Cysharp エコシステムフル活用 × ゲームサーバー兼用の次世代 C# ハイパフォーマンス Web / RPC サーバーフレームワーク**

`Hayabusa` は、ASP.NET Core (Kestrel) の汎用的な抽象化コストを極限まで削ぎ落とし、**ゼロアロケーション・構造体・C# Source Generator・アンマネージド/Pinned メモリ・Thread-per-Core イベントループ**によって、**「Kestrel を超えるステートレス API / RPC 性能」**と**「60fps/30fps の Tick 同期・Room (Actor) 状態管理を内包したリアルタイムゲームサーバー」**を単一ランタイムで両立する次世代サーバーエンジンです。

---

## コンセプトと解決する課題

Kestrel と ASP.NET Core は優れた汎用 Web サーバーですが、極限の低遅延・高スループットやリアルタイムゲームサーバーの領域では以下の構造的ボトルネックが存在します。

1. **`PipeReader` / `PipeWriter` とマルチセグメント処理のオーバーヘッド**:
   - 大半の API / RPC フレームは単一の連続メモリ（数 KB 以内）に収まるにもかかわらず、常に連結リスト構造（`ReadOnlySequence<byte>`）の分岐とスレッド間バックプレッシャー同期コストを支払っている。
2. **重い `HttpContext` と動的ミドルウェアパイプライン**:
   - リクエストごとに多数の機能コレクションや状態オブジェクトが関与し、最小構成でも無視できないオーバーヘッドが生じる。
3. **`Task` / `ThreadPool` によるコンテキストスイッチとロック競合**:
   - 汎用 `ThreadPool` 上でリクエストがランダムなコアにディスパッチされるため、CPU キャッシュ局所性（L1/L2 Cache Locality）が失われ、インメモリ状態（ゲームの Room など）を触るたびに `lock` や `ConcurrentDictionary` が必要になる。
4. **`.NET Socket` 抽象による Linux `io_uring` の性能限界**:
   - 標準 `Socket` を経由する限り、Linux カーネルの `io_uring` が持つ Registered Buffers、Fixed Files、Submission/Completion リングのバッチポーリング、ゼロコピー送信（`SEND_ZC`）の真価を 100% 引き出せない。

---

## アーキテクチャの4大ピラー

### 1. Cysharp エコシステム × 最新 C# によるゼロアロケーション設計

| レイヤー | Kestrel / ASP.NET Core 標準 | `Hayabusa` のアプローチ（Cysharp 流） |
| --- | --- | --- |
| **非同期・スレッド制御** | `Task` / `ValueTask` + `ThreadPool` | `UniTask` / カスタム `IValueTaskSource` + **Thread-per-Core 固定イベントループ**（アロケーションゼロ・コンテキストスイッチ排除） |
| **I/O トランスポート** | `Socket` + `PipeReader` (`epoll` / `IOCP`) | **Completion（完了通知）モデル抽象化**（Linux **`io_uring`** 最優先 + Windows **`IOCP`** 拡張可能）+ Thread-Local Pinned Slab |
| **シリアライズ** | `System.Text.Json` / Protobuf | **`MemoryPack`**（`ReadOnlySpan<byte>` 直結デシリアライズ & `IBufferWriter<byte>` 直書きによるコピーゼロ通信） |
| **文字列・バッファ生成** | `StringBuilder` / `string.Format` | **`ZString`** + `IBufferWriter<byte>` 直接書き込み |
| **ロギング** | `Microsoft.Extensions.Logging` | **`ZLogger`**（String Interpolation + UTF-8 直接書き込みによるゼロアロケーション構造化ログ） |
| **リアクティブ・購読管理** | C# `event` / `IObservable<T>` | **`R3`**（`DisposableBag` による購読リークの完全防止・イベントストリーム制御） |
| **ルーティング & RPC** | 実行時ルーティング / リフレクション / gRPC | **C# Incremental Source Generator**（コンパイル時 Radix Trie 木 & `MethodId` ジャンプテーブル生成・NativeAOT 完全対応） |

### 2. Thread-per-Core + LogicLooper 風 Tick ループ & Room (Actor) アフィニティ

CPU コアごとに1本の専用ワーカースレッドを起動し、**「ネットワーク I/O 完了処理」**と**「定周期ゲームループ（Tick 更新）」**を同一スレッドのイベントループ内でシームレスに回します。

```text
┌───────────────────────────────────────────────────────────────────┐
│ Worker Thread Loop (Core #i)                                      │
│                                                                   │
│  [Phase 1: Drain I/O Completions & Cross-Core Mailboxes]          │
│     ├─ io_uring / Socket の受信完了イベントをバッチ処理           │
│     └─ ステートレス RPC または 当該コア所属 Room へのメッセージ実行│
│                          ▼                                        │
│  [Phase 2: Execute Scheduled Room Ticks (LogicLooper style)]      │
│     ├─ 目標 Tick 時刻に達していれば、所属する全 RoomActor.OnTick  │
│     └─ 状態差分のスナップショットを MemoryPack でシリアライズ     │
│                          ▼                                        │
│  [Phase 3: Sweep Timeouts & Return Pooled Slabs]                  │
│     └─ Timing Wheel による Idle 接続切断・一時アリーナの巻き戻し   │
│                          ▼                                        │
│  [Phase 4: Batch Flush / Submit I/O]                              │
│     ├─ 蓄積された送信フレームを一括 Submit                        │
│     └─ 次の Tick 期限 (Deadline) を上限として I/O Wait            │
└───────────────────────────────────────────────────────────────────┘
```

- **完全ロックフリーの Room 状態管理**:
  - 各 `RoomActor` は特定の `CoreIndex`（ワーカースレッド）にピン留め（アフィニティ固定）されます。
  - その Room のフィールドや `Dictionary` / `List` は割り当てられたスレッドからしか実行されないため、**ゲームロジック内で `lock` や `SemaphoreSlim` を一切書く必要がありません**。

### 3. 2つの開発者体験（Source Generator 属性ルーティング & Minimal API 風）

用途に応じて、MagicOnion 風の型安全な RPC サービス定義と、ASP.NET Core の Minimal API 風記法の両方をサポートし、どちらも Source Generator によって同じゼロオーバーヘッドなディスパッチコードへコンパイルされます。

#### スタイル A: 属性ベース RPC / サービス定義（Source Generator）

```csharp
using Hayabusa.Core.Primitives;
using Hayabusa.Rpc;
using MemoryPack;

[MemoryPackable]
public readonly partial record struct MoveCommand(float X, float Y, float Z);

[MemoryPackable]
public readonly partial record struct WorldSnapshot(TickNumber Tick, int PlayerCount);

[HayabusaService]
public partial class BattleRoomService : RoomActor
{
    [Rpc(MethodId = 1)]
    public ValueTask<WorldSnapshot> MoveAsync(MoveCommand command)
    {
        // 同一コアにピン留めされているため lock 不要でインメモリ状態を高速更新
        UpdatePlayerPosition(Context.ConnectionId, command);
        return new ValueTask<WorldSnapshot>(new WorldSnapshot(CurrentTick, PlayerCount));
    }

    protected override void OnTick(TickNumber tick)
    {
        // 60fps / 30fps の定周期でワーカースレッドから直接呼ばれる
        BroadcastWorldState(tick);
    }
}
```

#### スタイル B: Minimal API 風エンドポイント定義

```csharp
var app = HayabusaApp.CreateBuilder(args)
    .UseThreadPerCore(options => options.TickRate = 60)
    .Build();

app.MapRpc<MoveCommand, WorldSnapshot>(methodId: 1, static (ctx, cmd) =>
{
    return new WorldSnapshot(ctx.CurrentTick, 1);
});

app.MapGet("/health", static ctx => ctx.WriteUtf8("OK"u8));

await app.RunAsync();
```

---

## リポジトリ・モジュール構成（予定）

```text
hayabusa/
├── .cursor/
│   └── skills/                      # 開発・品質・セキュリティ・設計規約スキル群 (全11種)
├── .devcontainer/                   # VS Code / Cursor 用 DevContainer (.NET 9 + liburing + 拡張機能永続化)
│   ├── devcontainer.json
│   └── Dockerfile
├── Dockerfile                       # 本番・スタンドアロン起動用マルチステージ Dockerfile (JIT / NativeAOT 両対応)
├── docker-compose.yml               # ワンコマンドで Hayabusa サーバーを立ち上げる Compose 設定
├── .env.example                     # サーバー起動パラメータ（ポート・TickRate・Transport）の設定サンプル
├── Directory.Build.props            # 全プロジェクト共通の厳格ビルド設定・ライセンス同梱設定
├── .editorconfig                    # コードスタイル & 複雑度 (CA1502) / 型厳格 (CS8509) 強制設定
├── global.json                      # .NET 9 SDK バージョン固定
├── THIRD-PARTY-NOTICES.md           # Cysharp OSS 等の著作権表示・MIT ライセンス全文
├── src/
│   ├── Hayabusa.Core/               # 強い型プリミティブ、軽量Context、Pinned Slab/RingBuffer、Completion I/O 抽象
│   ├── Hayabusa.Transport/          # Linux io_uring ネイティブ実装 & Windows IOCP / Socket 実装 & InMemoryTransport
│   ├── Hayabusa.Runtime/            # Thread-per-Core イベントループ、LogicLooper 風 Tick スケジューラ、Room(Actor) 管理
│   ├── Hayabusa.Rpc/                # MemoryPack RPC フレーム処理、HTTP 最小パーサ、ルーティング基盤
│   └── Hayabusa.Generator/          # Incremental Source Generator (RPC ディスパッチャ・Radix Trie・クライアント生成)
├── templates/
│   └── Hayabusa.Templates/          # `dotnet new hayabusa-server` 用プロジェクトテンプレート
├── samples/
│   └── Hayabusa.SampleServer/       # MemoryPack RPC + Room Tick 同期サンプルサーバー
├── benchmarks/
│   └── Hayabusa.Benchmarks/         # BenchmarkDotNet スイート (Kestrel / gRPC 比較・0B アロケーション検証)
└── tests/
    ├── Hayabusa.Tests/              # 単体テスト・Source Generator テスト・ゼロリーク検証
    └── Hayabusa.PenetrationTests/   # 悪意あるペイロード・Length Bomb・Slowloris・ファジング検証
```

---

## クイックスタート（サーバーの立ち上げ方・ローカル開発環境）

さまざまな開発者・運用者が用途に応じてすぐに `Hayabusa` サーバーを立てて使用できるよう、以下の方法を用意しています。

### 1. Docker Compose で即座にサーバーを立てる（.NET SDK のインストール不要）

Docker さえあれば、1コマンドで `io_uring` 最適化設定済みの `Hayabusa` サーバーを起動できます。

```bash
# 設定をカスタマイズする場合は .env を作成（省略時はデフォルト値で起動）
cp .env.example .env

# サーバーのビルドと起動（HTTP: 5000, MemoryPack RPC / Room: 5001）
docker compose up -d --build
```

NativeAOT シングルバイナリとしてビルドしたい場合は `PUBLISH_AOT=true` を指定できます。

```bash
docker compose build --build-arg PUBLISH_AOT=true
```

### 2. 環境変数によるゼロコンフィグ設定（コンテナ・本番運用向け）

コードを書き換えることなく、環境変数（または `.env`）でサーバーの挙動を制御できます。

| 環境変数 | デフォルト値 | 説明 |
| --- | --- | --- |
| `HAYABUSA_BIND_ADDRESS` | `0.0.0.0` | バインドする IP アドレス |
| `HAYABUSA_HTTP_PORT` | `5000` | HTTP / Minimal API 待ち受けポート |
| `HAYABUSA_RPC_PORT` | `5001` | MemoryPack RPC / リアルタイム Room 待ち受けポート |
| `HAYABUSA_WORKER_THREADS` | `0` | Thread-per-Core スレッド数（`0` でコンテナの CPU コア数を自動検出） |
| `HAYABUSA_TICK_RATE` | `60` | Room (Actor) の定周期 Tick レート（Hz: `60` = 60fps、`30` = 30fps） |
| `HAYABUSA_TRANSPORT` | `auto` | `auto`（Linux `io_uring` 優先・非対応環境では `socket` へ自動フォールバック）、`io_uring`、`socket` |

### 3. 自分のプロジェクトで Hayabusa サーバーを作る（NuGet / `dotnet new` テンプレート）

他の開発者が独自のゲームサーバーや Web/RPC サーバーを構築する際は、以下の2通りの方法で導入できるようにします（Phase 1〜3 でパッケージ公開対応）。

```bash
# 方法A: プロジェクトテンプレートから新規サーバーを一発作成
dotnet new install Hayabusa.Templates
dotnet new hayabusa-server -n MyGameServer
cd MyGameServer && dotnet run

# 方法B: 既存の .NET 9+ プロジェクトに NuGet パッケージを追加
dotnet add package Hayabusa
dotnet add package Hayabusa.Client   # Unity / .NET クライアント共有用
```

### 4. `.devcontainer` を使ったローカル開発（Cursor / VS Code 対応）

Windows (WSL2) / macOS / Linux のどの環境でも、リポジトリを Cursor または VS Code で開いて **「Reopen in Container」** を実行するだけで、以下の開発環境が自動的に構築されます。

- `.NET 9 SDK` + `clang` / `zlib1g-dev`（NativeAOT コンパイル対応）
- `liburing-dev` + `seccomp=unconfined` + `ulimit memlock=-1:-1`（コンテナ内での Linux `io_uring` Registered Buffers 完全動作）
- `lizard`（循環的複雑度 CCN の自動計測 CLI）
- **拡張機能・キャッシュの永続化**: Cursor（`.cursor-server/extensions`）、VS Code（`.vscode-server/extensions`）、NuGet キャッシュ、bash 履歴をすべて Named Volume で永続化しているため、コンテナを再ビルドしても拡張機能や履歴が消えません。

---

## Cysharp OSS の導入状況と統合計画

現在 `Hayabusa` は **Phase 0（設計・規約策定フェーズ完了 / コード実装未着手）** であり、各 NuGet パッケージの実導入は **Phase 1 以降**で順次行います。

| OSS ライブラリ | 形態 | 現在の状態 | 導入予定フェーズ | `Hayabusa` での役割 | ライセンス |
| --- | --- | --- | --- | --- | --- |
| **[MemoryPack](https://github.com/Cysharp/MemoryPack)** | NuGet 導入 | 未導入（設計済） | **Phase 1** (`Hayabusa.Rpc`) | RPC ペイロードおよびゲーム状態スナップショットのゼロコピーシリアライズ | MIT License |
| **[ZString](https://github.com/Cysharp/ZString)** | NuGet 導入 | 未導入（設計済） | **Phase 1** (`Hayabusa.Core` / `Rpc`) | UTF-8 文字列・HTTP ヘッダー構築のゼロアロケーション化 | MIT License |
| **[ZLogger](https://github.com/Cysharp/ZLogger)** | NuGet 導入 | 未導入（設計済） | **Phase 1〜2** (`Hayabusa.Runtime`) | String Interpolation + UTF-8 直書きによるゼロアロケーション構造化ロギング | MIT License |
| **[R3](https://github.com/Cysharp/R3)** | NuGet 導入 | 未導入（設計済） | **Phase 2** (`Hayabusa.Runtime`) | `DisposableBag` による購読ライフサイクル管理（イベントリーク防止）・リアクティブ制御 | MIT License |
| **[UniTask](https://github.com/Cysharp/UniTask)** | NuGet / 思想統合 | 未導入（設計済） | **Phase 2** (`Hayabusa.Runtime`) | Thread-per-Core ループ上のゼロアロケーション非同期制御（標準 `ValueTask` / `IValueTaskSource` と併用） | MIT License |
| **[LogicLooper](https://github.com/Cysharp/LogicLooper)** | アーキテクチャ統合 | 設計反映済 | **Phase 2** (`Hayabusa.Runtime`) | I/O イベントループと一体化した 60fps/30fps 定周期 Tick スケジューラの設計モデル | MIT License |
| **[MagicOnion](https://github.com/Cysharp/MagicOnion)** | API 体験モデル | 設計反映済 | **Phase 1 & 3** (`Rpc` / `Generator`) | `[HayabusaService]` / `[Rpc]` による C# ファーストな RPC 開発体験のモデル（通信層は独自実装） | MIT License |

---

## プロジェクト Skills（品質・アーキテクチャ規約）

本リポジトリでは、常に一貫した高品質・高安全性・ゼロアロケーションのコードベースを維持するため、`.cursor/skills/` 配下に以下の11個の専門スキルを定義しています。

| スキル名 | パス | 概要 |
| --- | --- | --- |
| **コーディング規約** | [`.cursor/skills/coding-standards/SKILL.md`](.cursor/skills/coding-standards/SKILL.md) | C# 13 / .NET 9+ 基準、命名規則、`sealed` / `readonly struct` 徹底、ホットパスでの LINQ・`Task`・リフレクション禁止規約 |
| **循環的複雑度の管理** | [`.cursor/skills/cyclomatic-complexity/SKILL.md`](.cursor/skills/cyclomatic-complexity/SKILL.md) | 循環的複雑度（上限 **7**）・認知的複雑度（上限 **10**）・ネスト深度（上限 **3**）の定量基準とテーブルルックアップ等による低減手法 |
| **型厳格設計** | [`.cursor/skills/strict-typing/SKILL.md`](.cursor/skills/strict-typing/SKILL.md) | `Nullable` 完全有効化、プリミティブ型執着を排するゼロコスト `readonly record struct`（`ConnectionId`, `RoomId`, `MethodId`, `TickNumber`）、`Result<T, E>` 網羅性チェック |
| **メモリリーク防止設計** | [`.cursor/skills/zero-leak-memory/SKILL.md`](.cursor/skills/zero-leak-memory/SKILL.md) | Thread-Local Pinned Slab、プール貸出スコープ保証、Debug リーク検出器、`R3` による購読管理、`GC.GetAllocatedBytesForCurrentThread()` による 0B 検証 |
| **単体テスト実装** | [`.cursor/skills/unit-testing/SKILL.md`](.cursor/skills/unit-testing/SKILL.md) | 1バイト断片化・パイプライン結合テスト、`FakeTimeProvider` による決定論的 Tick テスト、`CSharpGeneratorDriver` による Source Generator テスト |
| **ペネトレーションテスト** | [`.cursor/skills/penetration-testing/SKILL.md`](.cursor/skills/penetration-testing/SKILL.md) | Length-Prefix OOM Bomb、MemoryPack 要素数爆弾、Slowloris、HTTP Smuggling、Rapid Flood、構造認識ファジングの検証スイート規約 |
| **Source Generator 設計** | [`.cursor/skills/source-generator-architecture/SKILL.md`](.cursor/skills/source-generator-architecture/SKILL.md) | `IIncrementalGenerator`、`ForAttributeWithMetadataName`、不変 Equatable モデルによる IDE キャッシュ保護、診断 ID（`HAYABUSA001`〜） |
| **I/O トランスポート設計** | [`.cursor/skills/io-transport-architecture/SKILL.md`](.cursor/skills/io-transport-architecture/SKILL.md) | Completion（完了通知）モデル抽象化、Linux `io_uring` / Windows `IOCP` / `InMemoryTransport` の3層設計とシステムコールバッチ Submit |
| **Thread-per-Core & Tick 統合** | [`.cursor/skills/thread-per-core-game-loop/SKILL.md`](.cursor/skills/thread-per-core-game-loop/SKILL.md) | 1コア1スレッド固定ループ、Deadline 連動 I/O Wait と LogicLooper 風 Tick の統合、Room アフィニティとロックフリー MPSC Mailbox |
| **ベンチマーク & プロファイリング** | [`.cursor/skills/benchmarking-and-profiling/SKILL.md`](.cursor/skills/benchmarking-and-profiling/SKILL.md) | `BenchmarkDotNet`（`MemoryDiagnoser` / `DisassemblyDiagnoser`）による Kestrel 比較指標とアロケーション 0B ゲート |
| **配布・コンテナ化・導入体験** | [`.cursor/skills/distribution-and-containerization/SKILL.md`](.cursor/skills/distribution-and-containerization/SKILL.md) | Docker / NuGet / `dotnet new` テンプレートの3経路配布、環境変数によるゼロコンフィグ起動、`io_uring` 自動フォールバック、`.devcontainer` 永続化規約 |

---

## 実装ロードマップ

- [x] **Phase 0: 設計方針策定 & Skills / アーキテクチャ規約整備**
- [ ] **Phase 1: `Hayabusa.Core` & `Hayabusa.Rpc` コア基盤実装**
  - 強い型プリミティブ（`ConnectionId`, `RoomId`, `MethodId`, `TickNumber`, `CoreIndex`）
  - Thread-Local Slab Pool / Pinned RingBuffer & リーク検知機構
  - MemoryPack RPC フレームパーサ & 最小 HTTP パーサ（単体テスト & ペネトレーションテスト含む）
- [ ] **Phase 2: `Hayabusa.Runtime`（Thread-per-Core + LogicLooper 風 Tick + Room Actor）実装**
  - コア固定ワーカースレッド、ロックフリー MPSC Mailbox、Timing Wheel タイムアウト管理
  - `RoomActor` 基底クラスと定周期 Tick スケジューラ
- [ ] **Phase 3: `Hayabusa.Generator`（C# Incremental Source Generator）実装**
  - `[HayabusaService]` / `[Rpc]` / `[Get]` / `[Post]` からのゼロアロケーションディスパッチャ & クライアント自動生成
- [ ] **Phase 4: `Hayabusa.Transport`（Linux `io_uring` & Cross-Platform Socket IOCP）実装**
  - Completion トランスポート層とバッチ Submit パイプライン
- [ ] **Phase 5: `Hayabusa.Benchmarks` & `Hayabusa.SampleServer`**
  - Kestrel / MagicOnion とのベンチマーク比較とリアルタイムゲームルームデモ

---

## ライセンスと帰属表示（License, Attribution & Disclaimer）

### サードパーティ OSS ライセンス（Cysharp, Inc.）

本プロジェクトが導入・参照する Cysharp 社製のすべての OSS（`MemoryPack`, `ZString`, `ZLogger`, `R3`, `UniTask`, `LogicLooper`, `MagicOnion`）は **MIT License（`Copyright (c) Cysharp, Inc.`）** の下で提供されています。

MIT ライセンスの規約を遵守するため、本リポジトリでは以下の対応を行っています。

1. **著作権表示および許諾表示の同梱**:
   - リポジトリルートの [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) に `Copyright (c) Cysharp, Inc.` の著作権表示および MIT ライセンス全文を記載しています。バイナリやコンテナイメージとして配布する際も同ファイルを同梱します。
2. **ソースコード参照・改変時のヘッダー明記**:
   - MIT ライセンスのコード断片を移植・改変して組み込む場合は、該当ソースファイル先頭および [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) に原著作権表示を明記します（[`.cursor/skills/coding-standards/SKILL.md`](.cursor/skills/coding-standards/SKILL.md) 第7節で規約化済み）。
3. **非公式プロジェクトであることの明示（Disclaimer）**:
   - `Hayabusa` は Cysharp 社の OSS エコシステムと設計思想に深く敬意を表して開発されている**独立したオープンソースプロジェクト**であり、**Cysharp, Inc. の公式製品および提携プロジェクトではありません**。パッケージ名や名前空間にも `Cysharp.*` は使用せず、`Hayabusa.*` を使用します。

