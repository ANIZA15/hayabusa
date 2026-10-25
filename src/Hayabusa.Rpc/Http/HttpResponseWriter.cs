using System;
using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using Cysharp.Text;

namespace Hayabusa.Rpc.Http;

/// <summary>
/// Writes HTTP/1.1 responses directly into an <see cref="IBufferWriter{T}"/> with zero heap allocations
/// using UTF-8 byte literals and Cysharp <c>ZString</c>.
/// Supports C# 13 <c>allows ref struct</c> so stack-only buffer writers can be passed by reference.
/// </summary>
public static class HttpResponseWriter
{
    private static ReadOnlySpan<byte> Status200HeaderPrefix =>
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: keep-alive\r\nContent-Length: "u8;

    private static ReadOnlySpan<byte> Status400Response =>
        "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8;

    private static ReadOnlySpan<byte> Status404Response =>
        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n"u8;

    /// <summary>
    /// Writes a 200 OK HTTP/1.1 response with the given UTF-8 body into <paramref name="writer"/> with zero heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteOkUtf8<TWriter>(ref TWriter writer, scoped ReadOnlySpan<byte> utf8Body)
        where TWriter : IBufferWriter<byte>, allows ref struct
    {
        Span<byte> dest = writer.GetSpan(Status200HeaderPrefix.Length + 16 + utf8Body.Length);
        Status200HeaderPrefix.CopyTo(dest);
        int offset = Status200HeaderPrefix.Length;

        Utf8Formatter.TryFormat(utf8Body.Length, dest.Slice(offset), out int digitsWritten);
        offset += digitsWritten;

        "\r\n\r\n"u8.CopyTo(dest.Slice(offset));
        offset += 4;

        utf8Body.CopyTo(dest.Slice(offset));
        offset += utf8Body.Length;

        writer.Advance(offset);
    }

    /// <summary>
    /// Writes a formatted 200 OK HTTP/1.1 response directly into <paramref name="writer"/> using Cysharp <c>ZString</c> UTF-8 builder.
    /// </summary>
    public static void WriteFormattedOk<TWriter, TArg>(ref TWriter writer, string format, TArg arg)
        where TWriter : IBufferWriter<byte>, allows ref struct
    {
        using Utf8ValueStringBuilder sb = ZString.CreateUtf8StringBuilder();
        sb.AppendFormat(format, arg);
        WriteOkUtf8(ref writer, sb.AsSpan());
    }

    /// <summary>
    /// Writes a concatenated 200 OK HTTP/1.1 response using Cysharp <c>ZString</c> UTF-8 builder with zero string allocations.
    /// </summary>
    public static void WriteConcatOk<TWriter, T1, T2>(ref TWriter writer, T1 arg1, T2 arg2)
        where TWriter : IBufferWriter<byte>, allows ref struct
    {
        using Utf8ValueStringBuilder sb = ZString.CreateUtf8StringBuilder();
        sb.Append(arg1);
        sb.Append(arg2);
        WriteOkUtf8(ref writer, sb.AsSpan());
    }

    /// <summary>
    /// Writes a 400 Bad Request HTTP/1.1 response into <paramref name="writer"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteBadRequest<TWriter>(ref TWriter writer)
        where TWriter : IBufferWriter<byte>, allows ref struct
    {
        Span<byte> dest = writer.GetSpan(Status400Response.Length);
        Status400Response.CopyTo(dest);
        writer.Advance(Status400Response.Length);
    }

    /// <summary>
    /// Writes a 404 Not Found HTTP/1.1 response into <paramref name="writer"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNotFound<TWriter>(ref TWriter writer)
        where TWriter : IBufferWriter<byte>, allows ref struct
    {
        Span<byte> dest = writer.GetSpan(Status404Response.Length);
        Status404Response.CopyTo(dest);
        writer.Advance(Status404Response.Length);
    }
}
