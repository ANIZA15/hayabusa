---
name: io-transport-architecture
description: >-
  Linux io_uring 最優先 + Windows IOCP / Socket 拡張可能な Completion（完了通知）モデル I/O 抽象化層の設計規約。
  Hayabusa.Transport およびネットワーク送受信パイプラインを実装する際に使用する。
---

# I/O トランスポート層 設計規約（Completion-Based I/O: Linux `io_uring` & Windows `IOCP`）

Kestrel は .NET 標準の `Socket` と `PipeReader`/`PipeWriter` を通すため、Linux カーネルの `io_uring` が持つ真の性能（システムコールバッチ化、Registered Buffers、Fixed Files、ゼロコピー送信 `SEND_ZC`、Multishot Accept/Recv）を100%引き出せません。`hayabusa` では **Completion（完了通知）モデル** を第一級市民とした独自の薄いトランスポート抽象層を定義します。

## 1. Readiness モデルではなく Completion（完了通知）モデルで統一する理由

- **Linux `epoll`（Readiness モデル）**: 「読み書き可能になった」という通知を受けてからシステムコール（`read` / `write`）を発行する。
- **Linux `io_uring` & Windows `IOCP`（Completion モデル）**: 「この Pinned バッファに対して非同期 Read/Write せよ」という要求（SQE）を積んでおき、**カーネルが I/O を完了した時点**で完了通知（CQE）を受け取る。
- `hayabusa` は最初から **Completion モデル** でトランスポートインターフェースを設計することで、Linux（`io_uring`）と Windows（`IOCP` / `SocketAsyncEventArgs` プール）の双方でアダプタ変換オーバーヘッドなしに動作します。

## 2. トランスポート層のコア設計（3ティア構成）

| バックエンド | 対象 OS / 環境 | 特徴と用途 |
| --- | --- | --- |
| **1. `IoUringTransport`（最優先）** | Linux コンテナ (Kernel 5.19+ / 6.x+) | Submission Queue (SQ) / Completion Queue (CQ) の共有メモリリング操作。`IORING_OP_RECV` / `IORING_OP_SEND_ZC`、Registered Buffers によるカーネル・ユーザー空間コピー削減、バッチ Submit によるシステムコール最小化。 |
| **2. `SocketEventTransport`（クロスプラットフォーム / Windows / macOS）** | Windows (`IOCP`) / macOS (`kqueue`) / 非特権環境 | Pre-allocated な `SocketAsyncEventArgs`（または `Socket.ReceiveAsync(Memory<byte>)` + Pinned Array）を用いたゼロアロケーション Completion 実装。開発機（macOS/Windows）や `io_uring` 無効環境でも同一コードで動作する。 |
| **3. `InMemoryTransport`（テスト・ベンチマーク用）** | 全環境（インメモリ） | ソケットを介さずメモリリング間で直接 CQE を発火させる決定論的トランスポート。単体テストおよびペネトレーションテストをマイクロ秒単位で高速実行するために使用する。 |

## 3. バッチ処理（Submission Batching）の鉄則

1リクエストごとに `io_uring_enter`（システムコール）や `Socket.SendAsync` を個別に呼んではいけません。

1. **Receive CQE ドレインフェーズ**:
   - Completion Queue に溜まっている完了イベント（最大 $N$ 件、例: 256 件）を1回のループでまとめて取り出し、ディスパッチャおよびハンドラを実行する。
2. **Send SQE 蓄積フェーズ**:
   - 各ハンドラが書き込んだレスポンスや、Room の Tick 更新で発生したブロードキャスト送信要求は、即座にシステムコールを発行せず、スレッドローカルの送信キュー（SQE バッファ）に積む。
3. **Flush Submit フェーズ**:
   - イベントループの1イテレーション末尾で、蓄積されたすべての送信・次回受信要求を **たった1回のシステムコール（またはバッチフラッシュ）** でカーネルへ Submit する。
