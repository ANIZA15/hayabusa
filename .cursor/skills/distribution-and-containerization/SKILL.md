---
name: distribution-and-containerization
description: >-
  さまざまな開発者・運用者が Hayabusa サーバーを簡単に立ち上げ・組み込みできるようにするための
  配布形態（Docker / NuGet / dotnet new テンプレート / 環境変数設定）および DevContainer 設計規約。
---

# サーバー配布・コンテナ化・導入体験規約（Distribution, Containerization & DX）

`hayabusa` は一部の専門家だけが手元でビルドする実験コードではなく、**「世界中の C# / Unity / Web 開発者が 1 コマンドで自分のプロジェクトに導入し、本番サーバーを立てられる」** オープンソースプロダクトとして設計します。

## 1. 多様なユーザーが Hayabusa サーバーを立てられる3つの導入経路

ユーザーの用途（まずは動かして試したい人、新規ゲーム/API サーバーを作りたい人、既存 .NET ソリューションに組み込みたい人）に合わせ、以下の3経路を提供します。

| 導入経路 | 対象ユーザー | 実行コマンド例 |
| --- | --- | --- |
| **1. Docker / Compose 即時起動** | ローカルに .NET SDK を入れずにすぐ試したい人、コンテナオーケストレーション（Kubernetes / Cloud Run / ECS / OCI）にデプロイする人 | `docker compose up -d --build` |
| **2. `dotnet new` プロジェクトテンプレート** | ゼロから独自のゲームサーバーや RPC/Web サーバープロジェクトを数秒で立ち上げたい人 | `dotnet new install Hayabusa.Templates` → `dotnet new hayabusa-server -n MyGameServer` |
| **3. NuGet パッケージ導入** | 既存の C# サーバープロジェクトや Unity 共有プロジェクト（クライアント・サーバー型共有）に組み込みたい人 | `dotnet add package Hayabusa` / `dotnet add package Hayabusa.Client` |

## 2. ゼロコンフィグ起動 & 環境変数（12-Factor App 準拠）

誰がどの環境（Docker, Kubernetes, Linux VM, Windows, macOS）で起動してもコード書き換えなしで設定変更できるよう、`HayabusaApp.CreateBuilder(args)` は以下の標準環境変数とコマンドライン引数を自動的に読み取る規約とします。

| 環境変数 | コマンドライン引数 | デフォルト値 | 説明 |
| --- | --- | --- | --- |
| `HAYABUSA_BIND_ADDRESS` | `--bind` | `0.0.0.0` | バインドする IP アドレス |
| `HAYABUSA_HTTP_PORT` | `--http-port` | `5000` | HTTP / Minimal API 待ち受けポート |
| `HAYABUSA_RPC_PORT` | `--rpc-port` | `5001` | MemoryPack バイナリ RPC / Room 待ち受けポート |
| `HAYABUSA_WORKER_THREADS` | `--workers` | `0` (自動検出) | Thread-per-Core ワーカースレッド数（`0` の場合はコンテナの CPU クォータまたは論理コア数を自動採用） |
| `HAYABUSA_TICK_RATE` | `--tick-rate` | `60` | Room (Actor) の定周期 Tick レート（Hz） |
| `HAYABUSA_TRANSPORT` | `--transport` | `auto` | `auto`（Linux かつ `io_uring` 利用可能なら `io_uring`、それ以外は `socket` へ自動フォールバック）、`io_uring`、`socket` |

> **グレースフル・フォールバックの鉄則**:
> `HAYABUSA_TRANSPORT=auto`（デフォルト）のとき、古い Linux カーネルや制限付きコンテナ環境で `io_uring_queue_init` が `ENOSYS` / `EPERM` を返した場合は、**クラッシュせずに自動的に `SocketEventTransport` へフォールバック**し、`ZLogger` で起動モードを構造化ログ出力すること。これにより「環境によって起動すらできない」トラブルを完全に防ぎます。

## 3. `.devcontainer` と本番 `Dockerfile` の設計規約

1. **`.devcontainer`（ローカル開発環境の統一と永続化）**:
   - Windows (WSL2) / macOS / Linux のどのホスト OS からでも、Cursor / VS Code の「Reopen in Container」1つで `.NET 9 SDK` + `clang` (NativeAOT) + `liburing-dev` + `lizard`（複雑度計測）が揃った環境を提供する。
   - コンテナ再ビルド時に拡張機能や NuGet キャッシュ、シェル履歴が消えないよう、Named Volume（`hayabusa-vscode-extensions`, `hayabusa-cursor-extensions`, `hayabusa-nuget-cache`, `hayabusa-bashhistory`）をマウントする。
   - `io_uring` のシステムコールと Registered Buffers（Pinned Memory）が開発コンテナ内でブロックされないよう、`--security-opt seccomp=unconfined` と `--ulimit memlock=-1:-1` を設定する。
2. **本番用マルチステージ `Dockerfile`**:
   - `PUBLISH_AOT=true`（NativeAOT シングルバイナリ）と `PUBLISH_AOT=false`（標準 JIT / Dynamic PGO）の両方をビルド引数で切り替え可能にする。
   - 実行イメージには非 root ユーザー（`ubuntu`）を使用し、`THIRD-PARTY-NOTICES.md` を `/app/THIRD-PARTY-NOTICES.md` に自動配置して MIT ライセンス義務を自動的に満たす。
