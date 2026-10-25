---
name: unit-testing
description: >-
  hayabusa における単体テスト（Unit Testing）の設計・実装規約。
  境界値テスト、Source Generator スナップショット/ドライバテスト、決定論的 Tick ループ検証、
  およびゼロアロケーション検証テストを実装する際に使用する。
---

# 単体テスト実装規約（Unit Testing Standards）

`hayabusa` のすべてのコンポーネント（`Core`, `Transport`, `Runtime`, `Rpc`, `Generator`）は、機能追加と同時に高速・決定論的（Deterministic）な単体テストを実装します。テストがランダムに落ちる（Flaky Test）原因となる `Thread.Sleep` や実時間依存を排除します。

## 1. テストフレームワークと構成

- **テストランナー**: `xUnit`（または `TUnit`）+ `Microsoft.CodeAnalysis.CSharp.Testing`（Source Generator 検証用）
- **命名規則**: `MethodName_Condition_ExpectedBehavior`
  - 例: `TryReadFrame_WhenBufferIsFragmented_ReturnsIncompleteData`
  - 例: `DispatchRpc_ValidMethodId_InvokesHandlerWithZeroAllocation`
- **構造**: **AAA パターン（Arrange / Act / Assert）** を空行で明確に分離し、テストメソッド自体の循環的複雑度（CCN）は **1（分岐・ループなし）** に保つ。複数の入力パターンは `[Theory]` + `[InlineData]` で分離する。

## 2. レイヤー別テスト必須項目

### 2.1 プロトコル・フレームパーサ（`Hayabusa.Core` / `Hayabusa.Rpc`）

ネットワーク I/O は任意のバイト境界で分割・結合されて届くため、以下の全パターンを必ずテストします。

1. **完全な単一フレーム（Single Complete Frame）**: 1回の `Span<byte>` で1フレームがぴったり届くケース。
2. **細切れ受信（1-Byte Fragmentation Test）**: フレーム全体を1バイトずつスライスして渡しても状態が壊れず、最終バイト到達時に正しくパース完了すること。
3. **複数フレームの一括受信（Pipelined / Coalesced Frames）**: 1回の Read バッファに3フレーム分が連結されて届いた際、余剰バイトを失わずに順次処理できること。
4. **境界値（Boundary Values）**:
   - ペイロード長 `0 byte`（空ペイロード）
   - ペイロード長 `MaxFrameSize`（上限ちょうど）
   - ペイロード長 `MaxFrameSize + 1`（上限超過エラー）

### 2.2 Thread-per-Core & Tick ループ（`Hayabusa.Runtime`）

実時間（`DateTime.UtcNow` や `Task.Delay`）に依存するとテストが遅くなり不安定化するため、**`TimeProvider`（または `FakeTimeProvider` / 手動ステップ実行 `StepTick(TickNumber)`）** を注入して決定論的にテストします。

- **シングルスレッド・アフィニティの検証**:
  - 同一 `RoomActor` に対する RPC 呼び出しと `OnTick` コールバックが、常に同じ `CoreIndex`（ワーカースレッド ID）上で直列に実行されることを検証する。
- **フレーム予算（Tick Budget）超過時の挙動**:
  - 1 Tick の処理が周期（例: 16.6ms）を超過した際、スパイラル・オブ・デス（無限遅延ループ）に陥らず、設定されたスキップまたはキャッチアップ戦略通りに動作することを `FakeTimeProvider` で時間を進めて検証する。

### 2.3 C# Source Generator（`Hayabusa.Generator`）

Source Generator は通常の呼び出しテストだけでなく、**`CSharpGeneratorDriver`** を用いたコンパイルテストを必須とします。

1. **生成コードのコンパイル成功検証**:
   - `[HayabusaService]` / `[Rpc]` / `[Get]` を付与した入力ソースコードに対し、Generator 実行後の `Compilation` にエラーや警告が 0 件であること。
2. **不正な属性定義に対する Diagnostic（コンパイルエラー報告）の検証**:
   - MemoryPack 非対応の型を引数にした場合や、重複する `MethodId` / ルートパスを定義した場合に、`HAYABUSA001` 等の専用 DiagnosticDescriptor が正確な Location とともに報告されること。

### 2.4 ゼロアロケーション & プール返却テスト

- ホットパス（`TryParseFrame`, `RpcDispatcher.Invoke`, `RoomActor.Tick`）は、ウォームアップ1回実行後に `GC.GetAllocatedBytesForCurrentThread()` の前後差分が `0` であることを単体テストでアサートする。
- エラー発生時（不正な MemoryPack ペイロード等）であっても、貸し出された Slab / ArrayPool バッファが 100% 返却されていること（`pool.OutstandingRentals == 0`）を検証する。

## 3. モック方針（No Heavy Mocking Frameworks）

- `Moq` や `NSubstitute` などの動的プロキシ生成（Castle.Core）ライブラリは、`ref struct`（`Span<byte>` / `ReadOnlySpan<byte>`）を引数に取れず、アロケーションノイズも発生させるため**使用禁止**。
- 代わりに、テスト用の軽量な **インメモリ Fake / Stub 構造体・クラス**（例: `InMemoryTransport`, `RecordingBufferWriter`）をテストプロジェクト内に用意して検証する。
