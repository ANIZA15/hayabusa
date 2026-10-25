---
name: zero-leak-memory
description: >-
  メモリリークを絶対に起こさない設計、ゼロアロケーション、Slab/Pinned メモリプール、
  バッファライフサイクル管理、およびリーク検知テストの規約。
  バッファ管理、I/O、RPC シリアライズ、Room 状態管理を実装する際に必ず参照する。
---

# メモリリーク防止 & ゼロアロケーション設計（Zero-Leak & Zero-Allocation Memory Design）

長時間稼働するゲームサーバー兼ハイパフォーマンス Web サーバーである `hayabusa` にとって、**メモリリーク（Managed / Unmanaged 両方）** と **LOH（Large Object Heap）断片化**、**GC による Stop-the-World スパイク** は致命的です。本スキルではメモリリークを構造的に防ぐ設計規約を定めます。

## 1. メモリリークが起きる5大原因と `hayabusa` での対策

| リーク要因 | `hayabusa` における設計上の防御策 |
| --- | --- |
| **1. `ArrayPool<T>` / `MemoryPool<T>` の返却漏れ・二重返却** | 生の `Rent`/`Return` を業務コードで直接呼ぶことを禁止。`using` スコープ付きの構造体オーナー、または **Thread-local Slab Pool / Arena Allocator** でリクエスト単位・Tick単位に一括リセットする。 |
| **2. Pinned メモリ / `NativeMemory` / `io_uring` リングの解放漏れ** | `SafeHandle` または `IDisposable` + デストラクタ監視（Debug ビルド時）を実装し、起動時に固定確保（Pre-allocated Fixed Buffers）して実行時の動的確保・解放自体を行わない。 |
| **3. イベント購読（`event` / `Action`）の解除忘れ** | C# 標準の `event` キーワードは原則使用禁止。代わりに **Cysharp `R3` の `IDisposable` 購読管理（`DisposableBag`）** または明示的なライフサイクル管理テーブルを使用する。 |
| **4. 切断済み Connection / 終了済み Room の辞書残留** | すべての Connection と Room にハートビート / TTL（Time-To-Live）タイムスタンプを持たせ、ワーカースレッドの定周期 Tick ループ内で Dead Connection Sweep を自動実行する。 |
| **5. `CancellationTokenSource` / タイマーの未 Dispose** | リクエストごとに `CancellationTokenSource` を `new` しない。`CancellationTokenSource` を使う場合はプール再利用（`TryReset()`）するか、イベントループ内蔵の Tick ベースタイムアウトホイール（Timing Wheel）を使用する。 |

## 2. バッファ設計規約（Kestrel `PipeReader` を超える軽量バッファ）

### 2.1 Thread-Local Pinned Slab / Ring Buffer

`hayabusa` は **Thread-per-Core** アーキテクチャを採用しているため、コア（スレッド）間でバッファを奪い合うロックが不要です。

- 各ワーカースレッドの初期化時に、`GC.AllocateArray<byte>(size, pinned: true)` または `NativeMemory.AlignedAlloc` で連続領域（Slab）を確保し、Linux `io_uring` の **Registered Buffers** としてカーネルに登録する。
- 単一リクエスト・単一 RPC フレーム（例: 64KB 以下）は **常に単一連続メモリ（`Span<byte>` / `ReadOnlySpan<byte>`）** としてスライスし、`ReadOnlySequence<byte>` のマルチセグメント分岐コストを排除する。

### 2.2 所有権（Ownership）の厳格なルール

1. **`ReadOnlySpan<byte>` / `Span<byte>` はその同期スコープ内でのみ有効**:
   - ハンドラに渡された `ReadOnlySpan<byte>` の中身は次の I/O サイクルで再利用されるため、非同期境界（`await`）を跨いで保持してはならない（`ref struct` によりコンパイラレベルで保証される）。
2. **`IBufferWriter<byte>` への直書き**:
   - レスポンスや RPC のシリアライズ（`MemoryPackSerializer.Serialize`）およびログ出力（`ZLogger` / `ZString`）は、中間 `byte[]` を作らず、スレッドローカルな送信リングバッファの `IBufferWriter<byte>` に直接書き込む。
3. **返却保証ラッパーと Debug トラッキング**:
   - 一時バッファをプールから借りる場合は必ず `scoped using var rental = pool.RentScoped(size);` 形式の `ref struct` ラッパーを介し、例外発生時にも確実に返却されるようにする。
   - `#if DEBUG` ビルドでは、貸出元のコールスタックまたはシリアル番号を記録し、返却されずに破棄された場合や二重返却された場合に即座に `InvalidOperationException` をスローする検出機構（Leak Detector）を組み込む。

## 3. クロージャ・隠れアロケーションの防止

- **ラムダ式への変数キャプチャ禁止**:
  - ホットパス内でローカル変数をキャプチャするラムダ式（`() => DoWork(localVar)`）を渡すと、コンパイラが裏で DisplayClass を `new` し、デリゲートインスタンスも毎回ヒープに割り当てられる。
  - 必ず **`static` ラムダ（`static state => ...`）** と状態引数（`TState`）のペアで渡すことを強制する。
- **ボクシングの完全排除**:
  - `enum` や `struct` を `object` 引数に渡さない。
  - `Dictionary<TKey, TValue>` のキーにする構造体（`ConnectionId`, `RoomId` 等）には必ず **`IEquatable<T>`** を実装する（`readonly record struct` なら自動実装される）。

## 4. メモリリーク・アロケーション検証の自動化

単体テストおよびベンチマークで、以下の2つを自動検査します。

1. **ホットパスのゼロアロケーション検証（Unit Test）**:
   ```csharp
   // ウォームアップ後に GC.GetAllocatedBytesForCurrentThread() の差分が 0 byte であることをアサート
   long before = GC.GetAllocatedBytesForCurrentThread();
   dispatcher.Invoke(requestSpan, ref responseWriter);
   long after = GC.GetAllocatedBytesForCurrentThread();
   Assert.Equal(0L, after - before);
   ```
2. **長時間負荷・接続切断サイクルでのメモリリーク検証**:
   - 10,000 回の「接続 → RPC 往復 → 切断」および「Room 作成 → Tick 更新 → Room 破棄」を実行した後、`PoolMetrics.ActiveRentals == 0` かつ `RoomManager.ActiveCount == 0`、さらに `GC.Collect()` 後のメモリ使用量がベースラインに戻ることを単体テストで検証する。
