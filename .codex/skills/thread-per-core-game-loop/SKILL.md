---
name: thread-per-core-game-loop
description: >-
  Thread-per-Core（1コア1スレッド）イベントループ、LogicLooper 風の定周期 Tick スケジューラ、
  および Room(Actor) アフィニティ（ロックフリー状態管理）の設計・実装規約。
  Hayabusa.Runtime やゲームサーバー機能を実装する際に使用する。
---

# Thread-per-Core & ゲームループ統合設計規約（Thread-per-Core + LogicLooper Tick + Room Affinity）

`hayabusa` の最大の差別化ポイントは、**「超高速なステートレス Web/RPC サーバー」** と **「インメモリで状態と定周期フレーム更新（Tick）を持つステートフルなゲームサーバー」** を、単一の **Thread-per-Core イベントループ** 上で融合させている点です。

## 1. Thread-per-Core アーキテクチャの原則

1. **コア数固定の専用ワーカースレッド**:
   - コンテナに割り当てられた論理 CPU コア数（`Environment.ProcessorCount` または設定値）と同じ本数の専用スレッドを起動する。
   - 各スレッドには独自の **`CoreIndex`（`0 .. N-1`）**、独自の **Slab メモリプール**、独自の **I/O ループ**、独自の **`SynchronizationContext`** を持たせる。
2. **`ThreadPool` への退避禁止**:
   - ワーカースレッド上で始まった `ValueTask` / `UniTask` の継続（`await` 後の処理）は、カスタム `SynchronizationContext`（またはスレッドローカルスケジューラ）によって **必ず元の `CoreIndex` のスレッドに戻る** ように制御する。`ConfigureAwait(false)` で汎用 `ThreadPool` に逃がしてはならない。

## 2. ネットワーク I/O と ゲーム Tick ループの融合（LogicLooper 統合）

各ワーカースレッドのメインループは、以下の 4 フェーズを定周期（例: 60Hz = 16.66ms、または 30Hz = 33.33ms）で休むことなく回します。

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

- **ポイント**: Phase 4 の I/O 待機タイムアウトを「**次の Tick 予定時刻までの残り時間（$\Delta t$）**」に設定します。
  - パケットが来れば即座に（マイクロ秒で）目が覚めて RPC を処理し、パケットが来なくても次の Tick 時刻ぴったりに目が覚めてゲームループを更新します。これにより **「Web/RPC の極小レイテンシ」と「正確な 60fps/30fps ゲーム Tick」が1本のスレッド上で完全に両立** します。

## 3. Room (Actor) アフィニティによる「完全ロックフリー」保証

1. **Room のコア固定（Pinning）**:
   - 各 `RoomId` は作成時に特定の `CoreIndex`（例: `roomId.Value % workerCount` または最小負荷コア）に割り当てられる。
   - その Room の状態（プレイヤー座標、HP、インベントリ、盤面状態など）は、**割り当てられた `CoreIndex` のスレッドからしか読み書きされない**。
   - したがって、Room 内のゲームロジックでは **`lock`、`SemaphoreSlim`、`ConcurrentDictionary`、`Interlocked` を一切書く必要がない**（普通の `Dictionary<TKey, TValue>` や `List<T>` をそのまま最速で操作できる）。
2. **別コアからのメッセージ転送（SPSC / MPSC Lock-Free Mailbox）**:
   - もし `Core #0` で受理したソケット接続のプレイヤーが、`Core #2` に所属する `Room #100` 宛の RPC を送った場合は、`Core #2` のロックフリー MPSC リングバッファ（Mailbox）に構造体コマンドを Enqueue するだけで完了する。
