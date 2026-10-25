---
name: benchmarking-and-profiling
description: >-
  BenchmarkDotNet を用いたマイクロベンチマーク、GC アロケーション計測、
  および Kestrel / MagicOnion との比較検証規約。
  性能改善やコアコンポーネントの実装後に使用する。
---

# ベンチマーク・性能計測規約（Benchmarking & Profiling）

「Kestrel を超える」という目標を感覚ではなく数値で証明するため、`hayabusa` ではすべてのコア機能に対して再現可能なベンチマークを整備します。

## 1. マイクロベンチマーク標準（`BenchmarkDotNet`）

`benchmarks/Hayabusa.Benchmarks/` プロジェクトでは、すべてのベンチマーククラスに以下の属性を付与します。

- `[MemoryDiagnoser]`（必須: `Allocated` カラムが **0 B** であることを確認する）
- `[DisassemblyDiagnoser(maxDepth: 2)]`（ホットパスの JIT アセンブリ出力で、インライン化漏れや境界チェック `CORINFO_HELP_RNGCHKFAIL` の有無を確認する）
- `[HardwareCounters]`（利用可能な環境では BranchMispredictions / CacheMisses を計測）

## 2. 必須ベンチマークスイートと目標指標

| ベンチマーク項目 | 比較対象（Baseline） | `hayabusa` の目標基準 |
| --- | --- | --- |
| **1. Frame Parse + Routing** | Kestrel `PipeReader` + ASP.NET Core Routing | スループット **2倍以上**、Allocated **0 B** |
| **2. MemoryPack Unary RPC (Echo / Struct)** | Kestrel + gRPC / MagicOnion | レイテンシ **50% 以下**、Allocated **0 B** |
| **3. Room Broadcast (1 Room × 100 Players × 60fps)** | `Task.WhenAll` + 標準 `Socket` | 1回のシリアライズ結果を全クライアントの送信キューへ Zero-Copy 共有参照渡し、Allocated **0 B** |
| **4. LogicLooper Tick Jitter (1,000 Rooms)** | `PeriodicTimer` / `Task.Delay` | 99パーセンタイル Tick ジッター **1ms 未満**、GC Gen0/Gen1/Gen2 発生回数 **0 回** |

## 3. 性能退行（Regression）の防止ルール

- ホットパスの PR で `Allocated` が `0 B` から `1 B` 以上に増加した場合、またはスループットが 5% 以上低下した場合は、アーキテクチャ上の明確な理由がない限りマージ不可とする。

## 4. クロス言語 Web サーバー比較ベンチマーク（Hayabusa vs Go `net/http` vs Rust `axum` / `tokio`）

リポジトリ内に `.go` や `.rs` のソースファイルを一切置かず、Docker Compose の `dockerfile_inline`（または `/tmp` エフェメラルビルド）を用いて同一 HTTP/1.1 ワークロードで比較計測できる構成を維持します。

```bash
# 1. Docker Compose によるワンコマンド比較計測（推奨: コンテナ内で Go / Rust / Hayabusa をビルド＆計測）
docker compose -f docker-compose.benchmarks.yml up --build --abort-on-container-exit

# 2. ラッパースクリプトによる実行（Docker 利用可能なら Docker、非対応環境なら /tmp エフェメラル実行へ自動切替）
./scripts/run-cross-lang-benchmarks.sh

# 3. dotnet run から直接実行
dotnet run --project benchmarks/Hayabusa.Benchmarks -c Release -- --cross-lang
```

