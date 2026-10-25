# Hayabusa Web サーバー構築クイックスタートガイド

このガイドでは、次世代 C# ハイパフォーマンスサーバーフレームワーク **`Hayabusa` (隼)** を用いて、**ゼロアロケーション・Thread-per-Core 駆動の高速 Web / HTTP API サーバー**を立ち上げる手順をステップバイステップで解説します。

> **Web サーバー単体運用（`WebOnly` モード）について**
> `Hayabusa` はリアルタイムゲームサーバー機能（Room Actor / 定周期 Tick）を内包していますが、`.UseWebServerOnly()`（または `--web-only` / `HAYABUSA_ROLE=web`）を指定することで、**ゲームの Tick スケジューラやバイナリ RPC 受付を完全に停止し、CPU コアごとの専用ワーカースレッドを 100% ステートレス HTTP/1.1 処理に専念させる純粋な Web サーバー**として動作します。
>
> ※ Web サーバーだけでなく、**MemoryPack RPC・60fps ゲームルーム（`RoomActor`）・C#/Unity クライアント・単体テストまで含めた総合入門ガイド**は **[`quickstart/クイックスタート.md`](../quickstart/クイックスタート.md)** を参照してください。

---

## 目次

1. [最短30秒で Web サーバーを起動して試す](#1-最短30秒で-web-サーバーを起動して試す)
2. [新規 Web サーバープロジェクトの作成](#2-新規-web-サーバープロジェクトの作成)
3. [最小構成の Web サーバー実装（Minimal API スタイル）](#3-最小構成の-web-サーバー実装minimal-api-スタイル)
4. [レスポンス出力 API（UTF-8 / JSON / ZString / MemoryPack）](#4-レスポンス出力-apiutf-8--json--zstring--memorypack)
5. [Source Generator 属性ルーティングによる Web サービス実装](#5-source-generator-属性ルーティングによる-web-サービス実装)
6. [CORS 自動応答と HTTP トンネル RPC（`POST /rpc`）](#6-cors-自動応答と-http-トンネル-rpcpost-rpc)
7. [環境変数・CLI オプション一覧（12-Factor App 準拠）](#7-環境変数cli-オプション一覧12-factor-app-準拠)
8. [NativeAOT シングルバイナリ出力と Docker 本番デプロイ](#8-nativeaot-シングルバイナリ出力と-docker-本番デプロイ)

---

## 1. 最短30秒で Web サーバーを起動して試す

### 方法 A: `.NET SDK` でサンプルサーバーを Web 専用モードで起動する

リポジトリに含まれる `Hayabusa.SampleServer` を `--web-only` フラグ付きで起動します。

```bash
dotnet run --project samples/Hayabusa.SampleServer -c Release -- --web-only --http-port 5000
```

別ターミナルから `curl` で動作確認します。

```bash
# 1. ヘルスチェック (プレーンテキスト "OK")
curl -i http://localhost:5000/health

# 2. JSON ステータス API
curl -i http://localhost:5000/api/status

# 3. CORS プリフライト (204 No Content が自動返却される)
curl -i -X OPTIONS http://localhost:5000/api/status
```

### 方法 B: Docker Compose で起動する（.NET SDK 不要）

`HAYABUSA_ROLE=web` を指定してコンテナを起動すると、Web サーバー専用モードで立ち上がります。

```bash
HAYABUSA_ROLE=web HAYABUSA_HTTP_PORT=5000 docker compose up -d --build
curl -i http://localhost:5000/health
```

---

## 2. 新規 Web サーバープロジェクトの作成

独自の Web サーバープロジェクトをゼロから立ち上げるには、以下のいずれかの方法を使用します。

### パターン 1: `dotnet new` テンプレートから作成する

```bash
# テンプレートのインストール（リポジトリルートから実行する場合）
dotnet new install ./templates/Hayabusa.Templates

# 新規プロジェクトの作成と起動
dotnet new hayabusa-server -n MyWebServer
cd MyWebServer
dotnet run -- --web-only --http-port 5000
```

### パターン 2: 空のコンソールプロジェクトから作成する

```bash
dotnet new console -n MyWebServer
cd MyWebServer

# NuGet パッケージの導入
dotnet add package Hayabusa.Core
dotnet add package Hayabusa.Rpc
dotnet add package Hayabusa.Transport
dotnet add package Hayabusa.Runtime
dotnet add package Hayabusa.Generator
```

---

## 3. 最小構成の Web サーバー実装（Minimal API スタイル）

`Program.cs` を以下のように記述すると、Thread-per-Core ランタイム上で動作する最小構成の Web サーバーが完成します。

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Hayabusa.Runtime.Hosting;

namespace MyWebServer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // 1. Web サーバー専用モード (HTTP ポート 5000) でアプリケーションを構築
        using HayabusaApp app = HayabusaApp.CreateBuilder(args)
            .UseWebServerOnly(httpPort: 5000)
            .Build();

        // 2. GET エンドポイントの登録 (UTF-8 リテラルによるゼロアロケーション出力)
        app.MapGet("/health", static ctx => ctx.WriteUtf8("OK"u8));

        app.MapGet("/api/status", static ctx =>
            ctx.WriteJsonUtf8("""{"status":"ok","server":"Hayabusa"}"""u8));

        // 3. POST エンドポイントの登録
        app.MapPost("/api/echo", static ctx =>
            ctx.WriteJsonUtf8("""{"received":true}"""u8));

        // 4. Ctrl+C (SIGINT) によるグレースフルシャットダウン設定と起動
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await app.RunAsync(cts.Token);
    }
}
```

### ポイント

- **`UseWebServerOnly(httpPort: 5000)`**:
  サーバーのロールを `HayabusaServerRole.WebOnly` に設定します。このとき環境変数 `HAYABUSA_HTTP_PORT` やコマンドライン引数 `--http-port` を指定したい場合は、引数を省略して `.UseWebServerOnly()` と書くことで外部設定がそのまま優先されます。
- **HTTP パイプライン最適化**:
  単一 TCP 接続上の HTTP Keep-Alive 連続リクエストや HTTP パイプライニング（複数リクエストの一括受信）は、ワーカー内の `PooledBufferWriter` に結合され、1回のシステムコール（`io_uring` バッチ Submit または `Socket` 送信）でまとめてフラッシュされます。

---

## 4. レスポンス出力 API（UTF-8 / JSON / ZString / MemoryPack）

リクエストハンドラに渡される `HayabusaContext` (`ctx`) は、ASP.NET Core の `HttpContext` のようなリクエストごとのヒープアロケーションを行わず、ワーカースレッド固有の再利用バッファ（`PooledBufferWriter`）へ直接 HTTP/1.1 レスポンスを書き込みます。

| メソッド | Content-Type | 用途・特徴 | コード例 |
| --- | --- | --- | --- |
| `ctx.WriteUtf8(ReadOnlySpan<byte>)` | `text/plain; charset=utf-8` | C# 11+ の `u8` UTF-8 文字列リテラルやスライスをコピーのみで直接返却 | `ctx.WriteUtf8("Hello Hayabusa!"u8);` |
| `ctx.WriteJsonUtf8(ReadOnlySpan<byte>)` | `application/json; charset=utf-8` | UTF-8 JSON バイト列を直接返却 | `ctx.WriteJsonUtf8("""{"ok":true}"""u8);` |
| `ctx.WriteZString<T>(string format, T arg)` | `text/plain; charset=utf-8` | Cysharp `ZString` の `Utf8ValueStringBuilder` を用いて、動的な値を `string` アロケーション 0B でフォーマット出力 | `ctx.WriteZString("Core: {0}", ctx.CoreIndex.Value);` |
| `ctx.WriteMemoryPack(ReadOnlySpan<byte>)` | `application/x-memorypack` | `MemoryPack` シリアライズ済みバイナリペイロードを HTTP 経由で返却 | `ctx.WriteMemoryPack(payloadSpan);` |

### より細かなレスポンス制御（`HttpResponseWriter` の直接利用）

400 Bad Request や 404 Not Found、複数引数の `ZString` 結合を行いたい場合は、`ctx.ResponseWriter` に対して `HttpResponseWriter` の静的メソッドを直接呼び出せます。

```csharp
using Hayabusa.Core.Buffers;
using Hayabusa.Rpc.Http;

app.MapGet("/api/worker-info", static ctx =>
{
    PooledBufferWriter writer = ctx.ResponseWriter;
    HttpResponseWriter.WriteConcatOk(ref writer, "WorkerCore="u8.Length, ctx.CoreIndex.Value);
});
```

---

## 5. Source Generator 属性ルーティングによる Web サービス実装

エンドポイント数が増えた場合は、`[HayabusaService]` 属性と `[Get]` / `[Post]` 属性を用いてクラス単位でルーティングを定義できます。C# Incremental Source Generator（`Hayabusa.Generator`）がコンパイル時に UTF-8 バイト列比較コードと型安全なルートパラメータバインディングコードを自動生成します（リフレクション 0・NativeAOT 完全対応）。

### 5.1 サービスクラスの定義

```csharp
using System;
using Hayabusa.Rpc;

namespace MyWebServer;

[HayabusaService]
public sealed partial class UserWebService
{
    // GET /users/health
    [Get("/users/health")]
    public void CheckHealth()
    {
        // 自動的に 200 OK ("OK") が書き込まれます
    }

    // GET /users/{id} — ルートパラメータ {id} は Utf8Parser によりゼロアロケーションで int に変換されます
    // 対応型: int, uint, long, ulong, Guid, ReadOnlySpan<byte>, string
    [Get("/users/{id}")]
    public void GetUserById(int id)
    {
        // id を使った処理
        _ = id;
    }

    // POST /users/create
    [Post("/users/create")]
    public void CreateUser()
    {
        // POST ハンドラ処理
    }
}
```

### 5.2 `HayabusaApp` へのサービス登録

```csharp
using HayabusaApp app = HayabusaApp.CreateBuilder(args)
    .UseWebServerOnly(httpPort: 5000)
    .Build();

// Source Generator が生成した IHayabusaRpcService 実装を登録
app.MapService<UserWebService>();

await app.RunAsync();
```

---

## 6. CORS 自動応答と HTTP トンネル RPC（`POST /rpc`）

`Hayabusa` の Web サーバーは、モダンな SPA（React / Vue / Blazor WebAssembly）や WebGL ゲームクライアントから直接呼び出せるよう、以下の機能を標準搭載しています。

1. **ゼロアロケーション CORS 対応**:
   - すべての HTTP レスポンスに `Access-Control-Allow-Origin: *` が標準付与されます。
   - ブラウザからの `OPTIONS` プリフライトリクエストに対しては、ルーティング定義不要で即座に `204 No Content`（`Access-Control-Allow-Methods: GET, POST, PUT, DELETE, OPTIONS, HEAD` 等）を返却します。
2. **HTTP トンネルによる `MemoryPack` バイナリ RPC（`POST /rpc`）**:
   - ハイブリッドモード（`UseWebAndGameServer` / デフォルト）で起動している場合、TCP 専用ポートが使えない環境でも `POST /rpc` エンドポイントへ 12 バイトの `RpcHeader` + `MemoryPack` ペイロードを POST することで、`[Rpc]` メソッドや `app.MapRpc(...)` ハンドラを HTTP 経由でそのまま呼び出せます（レスポンスは `Content-Type: application/x-memorypack` で返却されます）。
   - クライアント側では Source Generator が自動生成する `<ServiceName>Client.CreateHttp(httpClient, "/rpc")` をそのまま利用できます。

---

## 7. 環境変数・CLI オプション一覧（12-Factor App 準拠）

`HayabusaApp.CreateBuilder(args)` は以下の環境変数とコマンドライン引数を自動的に読み取ります（CLI 引数は環境変数より優先されます）。

| 環境変数 | CLI 引数 | デフォルト値 | Web サーバー構築時の推奨設定・説明 |
| --- | --- | --- | --- |
| `HAYABUSA_ROLE` | `--role` / `--web-only` / `--game-only` / `--web-and-game` | `both` | Web 専用サーバーなら `web`（または `--web-only`）。HTTP + RPC 両方受けるなら `both` |
| `HAYABUSA_BIND_ADDRESS` | `--bind` | `0.0.0.0` | バインドする IP アドレス（ローカル限定なら `127.0.0.1`） |
| `HAYABUSA_HTTP_PORT` | `--http-port` | `5000` | HTTP / Minimal API 待ち受けポート |
| `HAYABUSA_WORKER_THREADS` | `--workers` | `0` | Thread-per-Core ワーカースレッド数（`0` で論理 CPU コア数を自動検出） |
| `HAYABUSA_TRANSPORT` | `--transport` | `auto` | `auto`（Linux では `io_uring` を最優先し、非対応環境では `socket` に自動フォールバック）、`io_uring`、`socket` |
| `HAYABUSA_RPC_PORT` | `--rpc-port` | `5001` | バイナリ RPC ポート（`WebOnly` 時はバインドされません） |
| `HAYABUSA_TICK_RATE` | `--tick-rate` | `60` | ゲームループ Tick 周波数（`WebOnly` 時は実行されません） |

### 起動コマンド例

```bash
# CLI 引数でポート 8080・ワーカー数 4 の Web 専用サーバーとして起動
dotnet run -c Release -- --web-only --http-port 8080 --workers 4

# 環境変数で指定して起動
HAYABUSA_ROLE=web HAYABUSA_HTTP_PORT=8080 HAYABUSA_TRANSPORT=auto dotnet run -c Release
```

---

## 8. NativeAOT シングルバイナリ出力と Docker 本番デプロイ

`Hayabusa` は動的リフレクションを一切使用しないため、そのまま **NativeAOT** でネイティブ機械語のシングルバイナリへコンパイルできます。コールドスタート時間とメモリフットプリントを極限まで削減したいコンテナ環境やサーバーレス環境に最適です。

### ローカルでの NativeAOT ビルド

```bash
dotnet publish -c Release -p:PublishAot=true -o ./publish
./publish/MyWebServer --web-only --http-port 8000
```

### Docker での Web サーバー本番ビルド & 起動

リポジトリルートの `Dockerfile` と `docker-compose.yml` は、JIT ビルドと NativeAOT ビルドの両方に対応しています。

```bash
# 1. 標準 Release (JIT + Dynamic PGO) で Web 専用コンテナを起動
HAYABUSA_ROLE=web HAYABUSA_HTTP_PORT=8000 docker compose up -d --build

# 2. NativeAOT シングルバイナリとしてイメージをビルドする場合
docker compose build --build-arg PUBLISH_AOT=true
HAYABUSA_ROLE=web HAYABUSA_HTTP_PORT=8000 docker compose up -d
```

> **組み込みセキュリティ防御（デフォルト有効）**:
> `Hayabusa` の HTTP パーサ（`HttpParser`）とランタイムは、追加設定なしで **HTTP Request Smuggling 検知**（二重 `Content-Length` や `Transfer-Encoding` 併用拒否）、**Header Bomb 防御**（最大ヘッダーサイズ・個数制限）、**Path Traversal 検知**（`..` や `%2e%2e`、バックスラッシュの即時遮断）、**CRLF インジェクション防止**、および **Timing Wheel による Slowloris / アイドル接続の自動切断** をホットパス上で実行します。
