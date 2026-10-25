---
name: penetration-testing
description: >-
  ネットワークサーバーおよびゲームサーバーとしてのペネトレーションテスト・セキュリティ検証・ファジング実装規約。
  プロトコルパーサ、MemoryPack デシリアライズ、HTTP ヘッダー解析、接続・Room 管理を実装・検証する際に使用する。
---

# ペネトレーションテスト・セキュリティ検証規約（Penetration & Robustness Testing）

`hayabusa` はインターネットに直接公開される Web サーバー兼リアルタイムゲームサーバーとして動作するため、悪意あるクライアントから細工されたバイト列や大量接続攻撃を受けても、**クラッシュ・メモリ枯渇（OOM）・CPU 100% 張り付き（DoS）・情報漏洩を絶対に起こさない**ことをテストコードで証明します。

## 1. 脅威モデルと防御設計（Threat Model）

実装およびペネトレーションテストで必ずカバーする7大攻撃ベクトル:

| # | 攻撃ベクトル | 具体的な攻撃ペイロード例 | 必須の防御機構 & テスト期待結果 |
| --- | --- | --- | --- |
| **1** | **巨大フレーム長詐称（Length-Prefix OOM Attack）** | RPC フレームヘッダーの Length フィールドに `0x7FFFFFFF`（2GB）や負数（`0xFFFFFFFF`）を送信し、サーバーに巨大配列を `Alloc` させて OOM クラッシュを狙う。 | メモリ確保**前**に `Length <= MaxFrameSize`（デフォルト例: 1MB、設定可能）を検証。超過時は即座に `PayloadTooLarge` を返し、1バイトも追加確保せず接続を即切断する。 |
| **2** | **MemoryPack デシリアライズ爆弾（Collection Length Bomb）** | フレーム自体は数十バイトだが、内部の配列/文字列要素数ヘッダーに 1億件と記述された不正バイナリを送り、`MemoryPackSerializer` 内で巨大アロケーションを誘発する。 | デシリアライズ前に残りバッファ長と要素数の整合性を検査、または深さ・要素数上限を設け、`MemoryPackSerializationException` 発生時もスレッドを落とさず安全に当該クライアントのみ切断する。 |
| **3** | **Slowloris / 断片化遅延攻撃（Slow Drip DoS）** | 接続確立後、1秒に1バイトずつしかデータを送らず、ソケットと Pinned バッファを長時間占有して全ワーカーを枯渇させる。 | イベントループの Timing Wheel による **Frame Read Timeout / Idle Timeout**、および最小受信レート制限（Min Data Rate）により、タイムアウト超過接続をロックなしで即座に強制回収する。 |
| **4** | **HTTP Request Smuggling / Header Bomb** | `Content-Length` と `Transfer-Encoding` の二重指定、64KB 超の巨大ヘッダー、数千個のヘッダー行、CRLF インジェクション（`\r\n` 混入）、Null バイト混入。 | 厳格な RFC 準拠チェック（不正な空白や二重 Content-Length は `400 Bad Request` で即切断）、`MaxHeaderSize` / `MaxHeaderCount` のハードリミット強制。 |
| **5** | **不正 MethodId / ルーティング境界外アクセス** | 存在しない `MethodId`（`int.MinValue`, `int.MaxValue`, `0`, `-1`）やパストラバーサル（`/../../etc/passwd`）を含むリクエスト。 | ジャンプテーブルの `default` アームでアロケーションなしに `UnknownMethod` / `404` を返し、ファイルパス解決時は正規化後のベースディレクトリ外アクセスを完全に遮断する。 |
| **6** | **Packet Flood / Rapid Connect-Disconnect（C100K DoS）** | 1秒間に数万回の接続・即切断、または1接続から毎秒数十万発の極小不正フレームを送りつける（Rapid Reset 風攻撃）。 | 接続あたりの未処理リクエスト上限（Max In-Flight Requests / Backpressure）、不正フレーム検知時の即座のブラックリスト・RST 切断、およびバッファプールの100%即時回収。 |
| **7** | **Room (Actor) なりすまし・クロステナント侵入** | 認証されていない `ConnectionId` から別プレイヤーの `RoomId` へ状態変更 RPC を送信する、または Tick 周期を超える高速連打（Speed Hack）を行う。 | Room 入室トークン / セッション権限の型レベル検証と、Room 側の Per-Connection レートリミッター（Token Bucket）による破棄。 |

## 2. ペネトレーションテストスイートの実装方法

`tests/Hayabusa.Tests/Security/`（または `Hayabusa.PenetrationTests`）に、攻撃シナリオを自動再現するテストスイートを必ず実装します。

### 2.1 悪意あるバイト列テスト（Malicious Payload Suite）

```csharp
[Theory]
[InlineData(new byte[] { 0x48, 0x59, 0xFF, 0xFF, 0xFF, 0x7F })] // 2GB Length Bomb
[InlineData(new byte[] { 0x48, 0x59, 0xFF, 0xFF, 0xFF, 0xFF })] // Negative Length (-1)
[InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })] // Invalid Magic
public void FrameParser_UnderMaliciousHeader_RejectsImmediatelyWithoutAllocation(byte[] payload)
{
    long beforeBytes = GC.GetAllocatedBytesForCurrentThread();

    var error = FrameParser.TryParse(payload, maxFrameSize: 64 * 1024, out _);

    long afterBytes = GC.GetAllocatedBytesForCurrentThread();
    Assert.NotEqual(FrameParseError.None, error);
    Assert.Equal(0L, afterBytes - beforeBytes);
}
```

### 2.2 構造認識ファジング（Structure-Aware Fuzzing）

- 決定論的シード（`new Random(Seed)`）を用いた **プロパティベース・ファジングテスト** を単体テスト内に組み込む。
- 正常な MemoryPack RPC フレームや HTTP リクエストを生成した上で、ランダムなビット反転（Bit-flip）、ランダム長での切り詰め（Truncation）、長さフィールドの改ざんを 10,000 パターン以上注入する。
- **合格条件**:
  1. 未処理例外（`IndexOutOfRangeException`, `AccessViolationException`, `OutOfMemoryException`）によってワーカースレッドが1度も死なないこと。
  2. ファジング終了後、メモリプール（Slab / ArrayPool）の貸出残数が `0` であること。
