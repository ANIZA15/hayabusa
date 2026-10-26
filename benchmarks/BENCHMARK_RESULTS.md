# Hayabusa (隼) ベンチマーク計測結果レポート

本ドキュメントは、`Hayabusa (.NET 9)` と **Go 言語の標準 Web サーバー (`net/http`)**、**Rust のデファクト標準 Web サーバー (`axum` + `tokio` / `hyper`)**、および **.NET 標準 (`PipeReader` / `System.Text.Json` / `Task`)** の実測ベンチマーク結果をまとめたものです。

> **クリーンリポジトリ方針**:
> リポジトリ内には `.go` / `go.mod` / `.rs` / `Cargo.toml` などの他言語ソースファイルを一切配置していません。
> [`docker-compose.benchmarks.yml`](../docker-compose.benchmarks.yml) の `dockerfile_inline`（または [`scripts/run-cross-lang-benchmarks.sh`](../scripts/run-cross-lang-benchmarks.sh) の `/tmp` エフェメラルビルド）によって、計測時にのみ一時生成・コンパイルされます。

---

## 1. 計測環境

| 項目 | 内容 |
| --- | --- |
| **OS / Kernel** | `Linux 6.12.94+` (x86_64) |
| **CPU** | `Intel(R) Xeon(R) Processor` (4 vCPU, NUMA node0: 0-3) |
| **C# / .NET Runtime** | `.NET SDK 9.0.319` (`Release` ビルド, Tiered PGO / Dynamic PGO 有効) |
| **Go Toolchain** | `go1.22.2 linux/amd64` (`-ldflags="-s -w"`, 標準 `net/http` `http.ServeMux`) |
| **Rust Toolchain** | `rustc 1.83.0` (`--release`, `opt-level = 3`, `lto = "thin"`, `codegen-units = 1`, `axum 0.7.9` + `tokio 1.42.0` + `hyper 1.5.2`) |
| **プロセス分離** | 3 サーバーすべて独立した OS プロセスとして起動し、別プロセスの負荷生成クライアント（Raw TCP `Socket`, `TCP_NODELAY` 有効）から同一条件で計測 |

---

## 2. クロス言語 HTTP/1.1 ベンチマーク結果

### 2.1 高並行負荷（32 Keep-Alive Connections / 64,000 Requests / 16x Pipeline）

#### シナリオ 1: `GET /health`（Plaintext `"OK"` Keep-Alive）

| サーバー実装 | スループット (`req/s`) | 平均レイテンシ (`us`) | P50 (`us`) | P99 (`us`) | Hayabusa 比 |
| --- | ---: | ---: | ---: | ---: | ---: |
| **`Hayabusa (.NET 9)`** | **`142,877`** | `218.8` | `197.3` | **`515.0`** | **`1.00x`** |
| `Go 1.22 (net/http)` | `121,986` | `255.1` | `189.4` | `1,585.2` | `0.85x` |
| `Rust 1.83 (axum / tokio)` | `155,324` | `200.5` | `189.8` | `458.7` | `1.09x` |

#### シナリオ 2: `GET /api/status`（JSON Keep-Alive）

| サーバー実装 | スループット (`req/s`) | 平均レイテンシ (`us`) | P50 (`us`) | P99 (`us`) | Hayabusa 比 |
| --- | ---: | ---: | ---: | ---: | ---: |
| **`Hayabusa (.NET 9)`** | **`165,746`** | **`168.2`** | **`152.3`** | `622.9` | **`1.00x`** |
| `Go 1.22 (net/http)` | `142,701` | `215.6` | `131.7` | `2,522.9` | `0.86x` |
| `Rust 1.83 (axum / tokio)` | `163,477` | `191.9` | `196.8` | `332.1` | `0.99x` |

#### シナリオ 3: `GET /health`（16x HTTP/1.1 Pipelined Batch）

| サーバー実装 | スループット (`req/s`) | 平均レイテンシ (`us`) | P50 (`us`) | P99 (`us`) | Hayabusa 比 |
| --- | ---: | ---: | ---: | ---: | ---: |
| **`Hayabusa (.NET 9)`** | **`1,890,918`** | **`14.4`** | **`11.7`** | `66.2` | **`1.00x`** |
| `Go 1.22 (net/http)` | `229,426` | `130.0` | `91.9` | `648.8` | `0.12x` |
| `Rust 1.83 (axum / tokio)` | `1,140,031` | `26.4` | `25.7` | `46.3` | `0.60x` |

---

### 2.2 標準負荷（16 Keep-Alive Connections / 32,000 Requests / 16x Pipeline）

| シナリオ | サーバー実装 | スループット (`req/s`) | 平均レイテンシ (`us`) | P50 (`us`) | P99 (`us`) | Hayabusa 比 |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| **1. `GET /health` (Plaintext Keep-Alive)** | **`Hayabusa (.NET 9)`** | `102,150` | `140.4` | `133.3` | **`429.8`** | `1.00x` |
| | `Go 1.22 (net/http)` | `117,667` | `128.5` | `92.3` | `816.7` | `1.15x` |
| | `Rust 1.83 (axum / tokio)` | `151,647` | `103.4` | `99.4` | `239.3` | `1.48x` |
| **2. `GET /api/status` (JSON Keep-Alive)** | **`Hayabusa (.NET 9)`** | **`157,944`** | **`84.9`** | **`63.2`** | `461.8` | **`1.00x`** |
| | `Go 1.22 (net/http)` | `141,553` | `107.7` | `73.4` | `612.1` | `0.90x` |
| | `Rust 1.83 (axum / tokio)` | `162,439` | `96.1` | `94.9` | `227.2` | `1.03x` |
| **3. `GET /health` (16x HTTP/1.1 Pipelined)** | **`Hayabusa (.NET 9)`** | **`2,009,066`** | **`7.0`** | **`7.1`** | **`11.4`** | **`1.00x`** |
| | `Go 1.22 (net/http)` | `220,056` | `70.7` | `50.7` | `409.9` | `0.11x` |
| | `Rust 1.83 (axum / tokio)` | `1,045,998` | `14.5` | `14.3` | `39.3` | `0.52x` |

---

## 3. インプロセス・ホットパスマイクロベンチマーク（`--quick-compare`）

ソケット I/O を除いたエンジン内部のホットパス単体性能およびスレッドあたりの **GC アロケーション（`GC.GetAllocatedBytesForCurrentThread()` 差分）** の実測結果です。

| ベンチマーク項目 | 1操作あたり時間 (`ns/op`) | スループット (`ops/s`) | アロケーション (`B/op`) | 比較倍率 |
| --- | ---: | ---: | ---: | ---: |
| **1. HTTP Parse + Routing (`Hayabusa`)** | **`972.3 ns/op`** | **`1,028,522 ops/s`** | **`0 B/op`** | **1.25x** |
| 1. HTTP Parse + Routing (`PipeReader` + `string` Baseline) | `1,213.6 ns/op` | `824,014 ops/s` | `840 B/op` | 1.00x |
| **2. HTTP 16x Pipelined Batch (`Hayabusa` Coalesced)** | **`7,377.7 ns/op`** *(461 ns/req)* | **`135,543 batches/s`** *(2.17M req/s)* | **`0 B/op`** | — |
| **3. Unary RPC Echo (`Hayabusa` `MemoryPack`)** | **`197.8 ns/op`** | **`5,055,024 ops/s`** | **`0 B/op`** | **27.39x** |
| 3. Unary RPC Echo (`System.Text.Json` + `Task` Baseline) | `5,417.5 ns/op` | `184,586 ops/s` | `312 B/op` | 1.00x |
| **4. Room Broadcast 100 Players (`Hayabusa` Zero-Copy)** | **`1,866.2 ns/op`** | **`535,860 ops/s`** *(53.5M msg/s)* | **`0 B/op`** | **13.70x** |
| 4. Room Broadcast 100 Players (`Task.WhenAll` + JSON Baseline) | `25,558.5 ns/op` | `39,126 ops/s` | `52,632 B/op` | 1.00x |
| **5. Tick 1,000 Rooms (`Hayabusa` LogicLooper)** | **`12,397.3 ns/op`** | **`80,663 ticks/s`** *(80.6M room-ticks/s)* | **`0 B/op`** | — |

---

## 4. 結果の考察・アーキテクチャ上の特徴

1. **ゼロアロケーション (`0 B/op`) による低テールレイテンシ (P99)**:
   - `Go (net/http)` はリクエストごとに Goroutine スタックやヘッダー Map の GC 負荷が発生するため、32 並列時に P99 レイテンシが `1,585 us`〜`2,522 us` まで跳ね上がります。
   - 一方 `Hayabusa` は `PinnedSlabPool` と `PooledBufferWriter` への UTF-8 直接書き込みによりホットパスのヒープ割り当てが **0 B** であり、P99 レイテンシを `515 us`〜`622 us`（Go の **1/3〜1/4**）に抑えています。
2. **Dynamic PGO ウォームアップ後の JSON Keep-Alive 性能**:
   - `GET /api/status` では、`Hayabusa` が `165,746 req/s`（32 並列）を記録し、Go `net/http`（`142,701 req/s`）を約 16% 上回り、Rust `axum / tokio`（`163,477 req/s`）と同等以上のスループットを達成しています。
3. **HTTP/1.1 Pipelining における圧倒的なバッチ処理性能**:
   - `ContiguousReceiveBuffer` 上で連続する複数リクエストを `ReadOnlySpan<byte>` スライスとしてゼロコピー解析し、1 回の送信バッチに Coalesce（結合）して返す設計により、16x パイプライン時に **`1.89M〜2.00M req/s`**（Go `net/http` の **約 8.2〜9.1 倍**、Rust `axum / tokio` の **約 1.66〜1.92 倍**）を達成しています。

---

## 5. 再現コマンド

### 5.1 Docker Compose で計測する（推奨）

```bash
# Hayabusa (.NET 9) / Go (net/http) / Rust (axum + tokio) の3コンテナをビルド＆計測
docker compose -f docker-compose.benchmarks.yml up --build --abort-on-container-exit

# コンテナのクリーンアップ
docker compose -f docker-compose.benchmarks.yml down --remove-orphans
```

### 5.2 スクリプト / `dotnet run` で計測する

```bash
# 標準負荷 (16 connections / 32,000 requests)
./scripts/run-cross-lang-benchmarks.sh --connections 16 --requests 32000

# 高並行負荷 (32 connections / 64,000 requests)
./scripts/run-cross-lang-benchmarks.sh --connections 32 --requests 64000

# インプロセス・ホットパス比較 (0 B アロケーション検証)
dotnet run --project benchmarks/Hayabusa.Benchmarks -c Release -- --quick-compare

# BenchmarkDotNet フルスイート (DisassemblyDiagnoser / MemoryDiagnoser)
dotnet run --project benchmarks/Hayabusa.Benchmarks -c Release
```
