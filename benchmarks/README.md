# Hayabusa Benchmarks (`benchmarks/`)

このディレクトリには、`Hayabusa` の性能とゼロアロケーション（`0 B/op`）を定量検証するためのベンチマークスイートおよび実測結果レポートが格納されています。

## ドキュメント・構成一覧

- **[`BENCHMARK_RESULTS.md`](BENCHMARK_RESULTS.md)**: **`Hayabusa (.NET 9)` vs `Go 1.22 (net/http)` vs `Rust 1.83 (axum / tokio)`** およびインプロセス・ホットパスの実測ベンチマーク結果レポート
- **[`Hayabusa.Benchmarks/`](Hayabusa.Benchmarks/)**:
  - [`Program.cs`](Hayabusa.Benchmarks/Program.cs): `BenchmarkDotNet` スイート（`[MemoryDiagnoser]`, `[DisassemblyDiagnoser]`）および `--quick-compare` インプロセス比較ランナー
  - [`CrossLanguageBenchmarks.cs`](Hayabusa.Benchmarks/CrossLanguageBenchmarks.cs): `--cross-lang` クロス言語 HTTP/1.1 Keep-Alive / Pipelining ベンチマークランナー（Docker Compose エンドポイント & `/tmp` エフェメラルビルド両対応）

## クイック実行コマンド

```bash
# 1. Docker Compose で Hayabusa vs Go vs Rust を一括計測 (.go/.rs ファイル不要)
docker compose -f docker-compose.benchmarks.yml up --build --abort-on-container-exit

# 2. 自動切替スクリプトで計測 (Docker または /tmp エフェメラル実行)
./scripts/run-cross-lang-benchmarks.sh

# 3. インプロセス・ホットパス 0 B アロケーション比較
dotnet run --project benchmarks/Hayabusa.Benchmarks -c Release -- --quick-compare
```

詳細な数値と考察は **[`BENCHMARK_RESULTS.md`](BENCHMARK_RESULTS.md)** を参照してください。
