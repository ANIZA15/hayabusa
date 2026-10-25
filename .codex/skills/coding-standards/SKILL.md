---
name: coding-standards
description: >-
  C# 13 / .NET 9+ コーディング規約および Cysharp 流ハイパフォーマンス実装標準。
  hayabusa のすべての C# コード（Core / Transport / Runtime / Rpc / Generator / Tests）を
  新規作成・編集・レビューする際に必ず適用する。
---

# Hayabusa コーディング規約（Coding Standards）

`hayabusa` は Kestrel を超える性能とゲームサーバー兼用（Thread-per-Core + LogicLooper 風 Tick 同期）を目指す次世代 C# サーバーフレームワークです。すべてのコードは「明確さ」「型安全性」「ホットパスにおけるゼロアロケーション」を最優先します。

## 1. 言語・ランタイム基準

- **対象ターゲット**: `.NET 9.0` / `.NET 10.0` マルチターゲット + 将来の `.NET 11.0+` への動的ロールフォワード対応（`LangVersion: latest` / C# 13・C# 14+）
- **プロジェクト共通設定（`Directory.Build.props` / `global.json`）**:
  - `global.json` に `"rollForward": "latestMajor"`, `"allowPrerelease": true` を指定し、`.NET 10` / `.NET 11+` SDK 環境でもそのままビルド・実行可能にする
  - `<Nullable>enable</Nullable>`
  - `<ImplicitUsings>disable</ImplicitUsings>`（明示的な `using` により依存関係を可視化する）
  - `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
  - `<AnalysisLevel>latest-all</AnalysisLevel>`
  - `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`
  - `<IsAotCompatible>true</IsAotCompatible>`（Source Generator 以外のランタイムライブラリ）

## 2. 命名規則（Naming Conventions）

| 対象 | スタイル | 例 | 備考 |
| --- | --- | --- | --- |
| 名前空間 / 型 / メソッド / プロパティ | `PascalCase` | `Hayabusa.Runtime`, `RoomDispatcher` | 略語も2文字以外は頭文字のみ大文字 (`RpcHeader`, `HttpParser`, `IoUringTransport`) |
| インターフェース | `IPascalCase` | `ITransportCompletion`, `IRoomActor` | 役割を明確に表す名詞・形容詞 |
| 型パラメータ | `TPascalCase` | `TRequest`, `TState`, `TBufferWriter` | 単なる `T` ではなく意味のある名前を付与 |
| プライベートフィールド | `_camelCase` | `_ringBuffer`, `_tickInterval` | `readonly` を原則付与する |
| 定数 (`const` / `static readonly`) | `PascalCase` | `MaxFrameSize`, `DefaultTickRate` | `UPPER_SNAKE_CASE` は使用しない |
| ローカル変数 / 引数 | `camelCase` | `payloadSpan`, `connectionId` | 1文字変数はループ添字 `i` 以外禁止 |
| 非同期メソッド | `...Async` | `SendFrameAsync`, `RunLoopAsync` | 戻り値は `ValueTask` / `ValueTask<T>` または `UniTask` |

## 3. モダン C# 構文の適用ルール

1. **File-scoped namespace を必須とする**:
   - ネストを減らすため、すべてのファイルで `namespace Hayabusa.Core;` 形式を使用する。
2. **ガード節（Early Return）の徹底**:
   - `ArgumentNullException.ThrowIfNull(arg)`、`ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value)` 等を使用し、`else` ネストを排除する。
3. **`sealed` のデフォルト化**:
   - 継承を意図しないすべての `class` には必ず `sealed` を付与する（JIT / AOT における仮想呼び出しの脱仮想化 `Devirtualization` とインライン化を促進するため）。
4. **`readonly struct` / `ref struct` の積極活用**:
   - 変更不要な値型には必ず `readonly struct` を付ける（防御的コピー `Defensive Copy` の防止）。
   - スタック限定で扱うコンテキストやスライスビューは `readonly ref struct` として定義する。
5. **Primary Constructor の使い分け**:
   - DI や不変な依存注入のみの軽量クラスでは Primary Constructor を許可するが、フィールドを `readonly` として明示的に保持したい場合やバリデーションが必要な場合は従来のコンストラクタを使用する。

## 4. ホットパス（I/O・RPC・Tickループ）の鉄則

`Hayabusa.Core`、`Hayabusa.Transport`、`Hayabusa.Runtime`、`Hayabusa.Rpc` のリクエスト処理・Tick 処理パスでは以下を厳守する。

1. **LINQ 禁止**:
   - ホットパス内での `System.Linq`（`Select`, `Where`, `ToList`, `Any` 等）はクロージャとイテレータのヒープアロケーションを引き起こすため**全面禁止**。`for` ループまたは `foreach`（`Span<T>` または構造体 Enumerator）を使用する。
2. **`Task` 禁止（`ValueTask` / `UniTask` 必須）**:
   - I/O や RPC ハンドラの戻り値に `System.Threading.Tasks.Task` を使用しない。必ず `ValueTask`、`ValueTask<T>`、またはプーリングされた `IValueTaskSource` / `UniTask` を使用する。
3. **文字列連結・`StringBuilder` 禁止**:
   - ログ出力には **`ZLogger`**（UTF-8 構造化ログ）を使用する。
   - プロトコルヘッダーやテキスト生成には **`ZString`** または `IBufferWriter<byte>` / `Utf8Formatter` への直接書き込みを使用する。
4. **リフレクション・動的コード生成禁止**:
   - 実行時の `Type.GetMethod`、`Activator.CreateInstance`、`Expression.Compile`、`dynamic` は禁止。すべて **C# Source Generator** でコンパイル時に解決する（NativeAOT 完全対応を維持）。
5. **インターフェース呼び出しの最小化（Static Polymorphism）**:
   - ホットパスで高頻度に呼ばれる抽象は `interface` 変数経由ではなく、`where TWriter : IBufferWriter<byte>` のようにジェネリック型制約 + `struct` 実装で渡し、JIT に完全インライン化させる。

## 5. エラーハンドリング規約

- **例外をフロー制御に使わない**:
  - パース失敗、切断、不正フレーム、ルーティング不一致などの予測可能な事象には例外（`throw`）を使わず、`bool TryParse(...)` または `Result<T, ErrorCode>` 構造体パターンで返す。
- **例外を投げてよいケース**:
  - 起動時の設定不備、プログラミングバグ（契約違反）、回復不能な OS レベルの初期化失敗のみ。

## 6. コメント・ドキュメント規約

- **自明なコメントの禁止**:
  - コードを見ればわかる処理内容の言い換えコメント（例: `// カウンタを増やす`）は書かない。
- **書くべきコメント**:
  - 「なぜこのメモリレイアウトなのか」「なぜここで `Unsafe` を使うのか」「どの RFC / プロトコル仕様の何節に準拠しているか」「スレッドアフィニティの前提条件」のみを記述する。
- **公開 API の XML ドキュメント**:
  - `public` な型・メソッドには `<summary>` を記述し、所有権（Ownership: 呼び出し側が Dispose するか等）とスレッド安全性を明記する。

## 7. オープンソースライセンス遵守規約（Cysharp OSS / サードパーティ）

- **MIT ライセンス表記の保持**:
  - Cysharp 社製 OSS（`MemoryPack`, `ZString`, `ZLogger`, `R3`, `UniTask`, `LogicLooper` 等）を NuGet パッケージとして追加、または一部の構造体・アルゴリズムを参考に移植・改変する場合は、必ずリポジトリルートの `THIRD-PARTY-NOTICES.md` に著作権表示（`Copyright (c) Cysharp, Inc.`）と MIT ライセンス全文を保持すること。
- **ソースコード流用時のファイルヘッダー明記**:
  - 万が一、MIT ライセンスの外部 OSS からソースコードの一部（例: Source Generator 用の `EquatableArray<T>` 等）を直接コピーまたは改変して組み込む場合は、当該 `.cs` ファイルの先頭に元の著作権表示とライセンスコメントを必ず記載すること。
- **非公式プロジェクトであることの明示**:
  - ドキュメントやパッケージメタデータにおいて、Cysharp, Inc. の公式製品であると誤認させる表現（`Cysharp.Hayabusa` 名前空間の使用など）は絶対に行わないこと。
