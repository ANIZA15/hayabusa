# Hayabusa (隼) — AI Agent 共通指示書 (`AGENTS.md` / `agent.md` / `CLAUDE.md`)

このドキュメントは、**Cursor**、**Claude Code (Claude)**、**OpenAI Codex** のどの AI コーディングエージェントを使用しても、**完全に同一の設計思想・品質基準・Skills・ワークフローで動作させるための最上位ルール（Single Source of Truth）** です。

すべてのエージェントはセッション開始時（タスク着手の最優先ステップ）に本ファイルを読み込み、該当するタスクの `SKILL.md` を必ず参照してから作業を開始してください。

---

## 1. 基本動作・コミュニケーション規約（全エージェント共通）

1. **応答言語**:
   - ユーザーへの応答・説明・PR 本文・コミット背景の解説は**常に日本語**で行うこと。
2. **タスク着手前の必須ステップ**:
   - コードの作成・編集・レビュー・テストを行う前に、必ず第3節の **Skills カタログ（全11種）** から関連する `SKILL.md` を Read ツール等で読み込み、その規約に従って実装すること。
3. **ライセンスと帰属表示の遵守（自作主張の禁止）**:
   - 本プロジェクトは Cysharp, Inc. 様の OSS（`MemoryPack`, `ZString`, `ZLogger`, `R3`, `UniTask`, `LogicLooper` 思想, `MagicOnion` 思想）を利用させていただくことで成り立っている独立したオープンソースプロジェクトであり、Cysharp, Inc. 様の公式製品ではない。
   - Cysharp, Inc. 様の OSS や設計思想・API モデルを「自分たちが考案・作成した」と主張・誤認させる表現は一切行わず、常に原著作者である Cysharp, Inc. 様への帰属と敬意を明記すること。
   - `THIRD-PARTY-NOTICES.md` の MIT ライセンス表記を常に維持し、`Cysharp.*` 名前空間は使用せず `Hayabusa.*` を使用すること。

---

## 2. 各エージェント別のエントリポイントと Skills 配置構成

**Cursor / Claude Code / Codex では、起動時に自動認識するファイル名と Skills ディレクトリが異なります。**
どのツールでも一番最初に同じ指示と Skills が読み込まれるよう、本リポジトリでは以下のファイルを常時同期して配置しています。

| エージェント種別 | 最初に自動読み込みされるファイル | Skills 認識ディレクトリ | 常時適用ルール / 補助設定 |
| --- | --- | --- | --- |
| **Cursor** | `AGENTS.md` / `.cursor/rules/00-agent-instructions.mdc` | `.cursor/skills/<skill-name>/SKILL.md` | `.cursor/rules/00-agent-instructions.mdc` (`alwaysApply: true`) |
| **Claude Code** | `CLAUDE.md` (内部で `@AGENTS.md` / `agent.md` と完全同期) | `.claude/skills/<skill-name>/SKILL.md` | `CLAUDE.md` |
| **OpenAI Codex** | `AGENTS.md` | `.agents/skills/<skill-name>/SKILL.md` および `.codex/skills/<skill-name>/SKILL.md` | `AGENTS.md` |
| **人間 / 汎用エージェント指定** | `agent.md` | 上記4ディレクトリのいずれか（内容はすべて完全に同一） | `agent.md` |

---

## 3. プロジェクト Skills カタログ（全11種・参照必須）

以下の11個のスキルは `.cursor/skills/`、`.claude/skills/`、`.agents/skills/`、`.codex/skills/` のすべてに同一内容で配置されています。作業内容に応じて該当スキルを必ず読み込んでください。

| # | スキル ID (`<skill-name>`) | 適用タイミング・必須ルール概要 | パス（代表: `.cursor/skills/` ※他環境も同名） |
| --- | --- | --- | --- |
| 1 | **`coding-standards`** | **すべての C# コード作成・編集時（必須）**: C# 13 / .NET 9、`Nullable: enable`、`ImplicitUsings: disable`、File-scoped namespace、`sealed` デフォルト化、`readonly struct`、ホットパスでの LINQ・`Task`・リフレクション・文字列連結禁止 | `.cursor/skills/coding-standards/SKILL.md` |
| 2 | **`cyclomatic-complexity`** | **すべてのメソッド実装・リファクタリング時（必須）**: 手書きコードの循環的複雑度（CCN）上限 **7**、認知的複雑度上限 **10**、ネスト深度上限 **3**、引数上限 **4個**（5個以上は `readonly struct` 化） | `.cursor/skills/cyclomatic-complexity/SKILL.md` |
| 3 | **`strict-typing`** | **型・API・状態定義時**: Primitive Obsession の排除（`ConnectionId`, `RoomId`, `MethodId`, `TickNumber`, `CoreIndex`）、`Result<TValue, TError>`、`switch` 網羅性チェック（`CS8509`）、ジェネリック構造体制約による脱仮想化 | `.cursor/skills/strict-typing/SKILL.md` |
| 4 | **`zero-leak-memory`** | **バッファ・I/O・Room・イベント購読実装時**: `PinnedSlabPool` + `ScopedSlabRental`、`PooledBufferWriter` 直書き、Cysharp `R3` `DisposableBag` 購読管理、ウォームアップ後の `GC.GetAllocatedBytesForCurrentThread()` 差分 **0 B** | `.cursor/skills/zero-leak-memory/SKILL.md` |
| 5 | **`unit-testing`** | **単体テスト実装・修正時**: `MethodName_Condition_ExpectedBehavior` 命名、AAA パターン、1バイト断片化・複数フレーム結合・境界値テスト、`FakeTimeProvider` による決定論的 Tick テスト、`CSharpGeneratorDriver` テスト、重いモック（Moq 等）禁止 | `.cursor/skills/unit-testing/SKILL.md` |
| 6 | **`penetration-testing`** | **パーサ・シリアライザ・ネットワーク・Room 権限実装時**: 7大攻撃ベクトル（Length-Prefix OOM Bomb、MemoryPack 要素数爆弾、Slowloris、HTTP Smuggling / Header Bomb、不正 MethodId / Path Traversal、Rapid Connect-Disconnect Flood、Room なりすまし）防御と 10,000 パターン構造認識ファジング | `.cursor/skills/penetration-testing/SKILL.md` |
| 7 | **`source-generator-architecture`** | **`Hayabusa.Generator` 実装・変更時**: `IIncrementalGenerator`、`ForAttributeWithMetadataName`、`ISymbol`/`SyntaxNode` を保持しない `IEquatable<T>` / `EquatableArray<T>` モデル、`global::` 完全修飾出力、診断 `HAYABUSA001`〜`005` | `.cursor/skills/source-generator-architecture/SKILL.md` |
| 8 | **`io-transport-architecture`** | **`Hayabusa.Transport` 実装・変更時**: Completion（完了通知）モデル抽象化（`ITransportBackend`, `TransportCompletionEvent`, `TransportSubmissionQueue`）、`IoUringTransport` / `SocketEventTransport` / `InMemoryTransport` の3層構成とバッチ Submit | `.cursor/skills/io-transport-architecture/SKILL.md` |
| 9 | **`thread-per-core-game-loop`** | **`Hayabusa.Runtime`・Room・イベントループ実装時**: 1コア1スレッド固定ループ（`CoreWorker`）、`SingleThreadSynchronizationContext`（`ConfigureAwait(false)` 禁止）、4フェーズループ（I/O & Mailbox → Room Tick → Timeout Sweep → Batch Flush & Deadline Wait）、ロックフリー `RoomActor` | `.cursor/skills/thread-per-core-game-loop/SKILL.md` |
| 10 | **`benchmarking-and-profiling`** | **`Hayabusa.Benchmarks`・性能改善時**: `BenchmarkDotNet`（`[MemoryDiagnoser]`, `[DisassemblyDiagnoser(maxDepth: 2)]`）、ホットパス Allocated **0 B** 品質ゲート | `.cursor/skills/benchmarking-and-profiling/SKILL.md` |
| 11 | **`distribution-and-containerization`** | **Docker / DevContainer / NuGet / テンプレート変更時**: マルチステージ `Dockerfile`（JIT / NativeAOT 両対応）、`docker-compose.yml`、12-Factor 環境変数（`HAYABUSA_*`）、`io_uring` 自動フォールバック、`dotnet new hayabusa-server` テンプレート | `.cursor/skills/distribution-and-containerization/SKILL.md` |

---

## 4. 【重要】Skills および Agent 指示書の追加・更新時の全環境同期ルール

**もし Skills（`SKILL.md`）やエージェント指示書に追加・更新・削除がある場合は、特定のツール（例: `.cursor/skills/` のみ）だけを変更して終了することを固く禁じます。**
必ず以下の手順で **Claude / Cursor / Codex のすべてに完全反映** させてください。

### 4.1 同期対象ディレクトリ・ファイル一覧

1. **Skills ディレクトリ（4箇所すべて同一内容に保つこと）**:
   - `.cursor/skills/<skill-name>/SKILL.md` （Cursor 用・編集の正本）
   - `.claude/skills/<skill-name>/SKILL.md` （Claude Code 用）
   - `.agents/skills/<skill-name>/SKILL.md` （OpenAI Codex / Agent Skills 標準用）
   - `.codex/skills/<skill-name>/SKILL.md` （OpenAI Codex CLI 用）
2. **エージェント指示書ファイル（3ファイルすべて同一内容に保つこと）**:
   - `AGENTS.md` （Codex / Cursor 標準）
   - `agent.md` （汎用 / 明示参照用）
   - `CLAUDE.md` （Claude Code 標準）
3. **カタログ記載箇所**:
   - 新規スキルを追加した場合は、本ファイル（`AGENTS.md` / `agent.md` / `CLAUDE.md`）の「第3節 プロジェクト Skills カタログ」および `README.md` の「プロジェクト Skills」テーブルにも必ず行を追加すること。

### 4.2 ワンコマンド自動同期・検証スクリプトの実行（必須）

`.cursor/skills/` または `AGENTS.md` を編集した後は、必ず以下の同期スクリプトを実行して全環境に反映し、`--check` で差分ゼロを検証してからコミットしてください。

```bash
# 1. .cursor/skills/ と AGENTS.md の変更を Claude / Cursor / Codex の全パスへ一括同期する
./scripts/sync-agent-skills.sh

# 2. 全エージェント環境間で Skills と指示書が 100% 一致しているか検証する
./scripts/sync-agent-skills.sh --check
```

---

## 5. ビルド・テスト・品質ゲート実行コマンド（コミット前必須）

コードを変更した際は、完了報告や PR 作成の前に必ず以下のすべての品質ゲートを実行し、エラー・警告が **0件** であることを確認してください。

```bash
# 1. Release ビルド（Nullable / CA1852 sealed / CA1502 CCN / CS8509 網羅性 / CA2000 Dispose 等の全アナライザ検証）
dotnet build Hayabusa.sln -c Release

# 2. 単体テスト（27件）および ペネトレーション・ファジングテスト（20件）の全件実行
dotnet test Hayabusa.sln -c Release --no-build

# 3. 循環的複雑度（CCN <= 7）および 引数個数（<= 4）の定量検査
lizard -l csharp -C 7 -a 4 src/ samples/ benchmarks/ tests/ -x "*/obj/*" -x "*/bin/*"

# 4. Skills と Agent 指示書の全環境（Claude / Cursor / Codex）同期チェック
./scripts/sync-agent-skills.sh --check
```
