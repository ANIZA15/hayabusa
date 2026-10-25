---
name: strict-typing
description: >-
  C# における厳格な型設計（Strict Typing）、プリミティブ型執着（Primitive Obsession）の排除、
  状態機械の型表現、不変性、および NativeAOT 互換性のルール。API 設計やモデル定義時に使用する。
---

# 型厳格設計（Strict Typing & Type-Safe Architecture）

`hayabusa` では、「コンパイルが通れば、不正な状態遷移・単位ミス・null 参照・スレッド違反が起きない」レベルの厳格な型システム活用を徹底します。しかも構造体（`readonly record struct`）を用いることで、**実行時のボクシングやメモリオーバーヘッドを一切増やさずに（Zero-Cost Newtype）** 型安全性を実現します。

## 1. コンパイラ・アナライザによる厳格設定

すべてのプロジェクトで以下を有効化し、型の曖昧さをコンパイルエラーとして排除します。

- `<Nullable>enable</Nullable>`（null 許容参照型の完全有効化）
- `<WarningsAsErrors>Nullable;CS8509;CS8524;CA1062;CA2000</WarningsAsErrors>`
  - `CS8509` / `CS8524`: `switch` 式での網羅性チェック漏れ（Exhaustiveness Check）をエラー化
- `dynamic` キーワード、`object` 型へのアップキャスト（ボクシング）の禁止
- `!`（null 免除演算子 / Null-forgiving operator）の原則禁止
  - やむを得ず使用する場合は、`[MemberNotNull]`、`[NotNullWhen(true)]`、`[MaybeNullWhen(false)]` などの `System.Diagnostics.CodeAnalysis` 属性でコンパイラにフローを証明できないか先に検討すること。

## 2. プリミティブ型執着（Primitive Obsession）の排除

ID、サイズ、時間、プロトコル識別子などに生の `int`、`long`、`ulong`、`string` をそのまま引き回すことを禁止します。引数の取り違え（例: `Send(connectionId, roomId)` に誤って `(roomId, connectionId)` を渡すバグ）を型レベルで防ぐため、**`readonly record struct`** でラップします。

```csharp
namespace Hayabusa.Core.Primitives;

/// <summary>接続を一意に識別する強い型（メモリ上は 8 byte の ulong と完全に同一）</summary>
public readonly record struct ConnectionId(ulong Value)
{
    public static readonly ConnectionId Invalid = new(0);
    public bool IsValid => Value != 0;
    public override string ToString() => $"conn:{Value}";
}

/// <summary>RPC メソッド識別子（4 byte int と同一レイアウト）</summary>
public readonly record struct MethodId(int Value);

/// <summary>ルーム（Actor）識別子</summary>
public readonly record struct RoomId(ulong Value);

/// <summary>ワーカースレッド（コア）のインデックス</summary>
public readonly record struct CoreIndex(int Value);

/// <summary>ゲームループのフレーム番号（Tick）</summary>
public readonly record struct TickNumber(ulong Value)
{
    public TickNumber Next() => new(Value + 1);
}
```

> **パフォーマンス上の利点**:
> 単一フィールドの `readonly record struct` は、JIT コンパイラによって内部のプリミティブ型（レジスタ渡し）と完全に同等に扱われるため、**実行時コストは完全にゼロ**です。

## 3. `Try*` パターンと `NotNullWhen` / `Result<T, TError>`

例外を使わずに失敗を表現する際、出力パラメータや戻り値の状態を型システムで保証します。

### 3.1 `bool Try...` メソッドへの属性付与

参照型または null 許容型を `out` で返す場合は、必ず `[MaybeNullWhen(false)]` または `[NotNullWhen(true)]` を付与します。

```csharp
public bool TryGetRoom(RoomId roomId, [MaybeNullWhen(false)] out RoomActor room)
```

### 3.2 判別共用体風 `Result<TValue, TError>` 構造体

複数の失敗理由を型安全に扱う場合は、列挙型エラーコードを持つ `readonly struct Result<TValue, TError> where TError : struct, Enum` を定義し、`switch` 式の網羅性チェック（`CS8509` / `CS8524`）で全エラーケースの処理を強制します。

```csharp
public enum FrameParseError : byte
{
    None = 0,
    IncompleteData,
    InvalidMagicByte,
    PayloadTooLarge,
    UnsupportedProtocolVersion,
}
```

## 4. ジェネリック型制約によるゼロコスト抽象化

インターフェース型（`IBufferWriter<byte>` や `ITransport`）をフィールドや引数として直接受け取ると、構造体実装であってもボクシングや仮想メソッド呼び出しが発生します。`hayabusa` ではジェネリック型パラメータ + `where` 制約を標準とします。

```csharp
// Bad: IBufferWriter<byte> をインターフェース参照で受けると仮想呼び出しになる
public static void WriteHeader(IBufferWriter<byte> writer, in RpcHeader header)

// Good: struct の BufferWriter をボクシングゼロ・インライン展開で呼び出せる
public static void WriteHeader<TWriter>(ref TWriter writer, in RpcHeader header)
    where TWriter : IBufferWriter<byte>
```

## 5. `in` / `ref readonly` / `scoped` による参照安全性の強制

- 16 バイトを超える構造体を読み取り専用で渡す場合は、`in`（または `ref readonly`）引数を使用してコピーを防ぐ。
- `ref struct` や `Span<T>` を受け取るメソッドでは、必要に応じて **`scoped`** キーワードを付与し、スタック上の参照がメソッド外（フィールド等）へエスケープしないことをコンパイラに保証させる。

```csharp
public FrameParseError TryParseFrame(scoped ReadOnlySpan<byte> input, out FrameSlice slice)
```
