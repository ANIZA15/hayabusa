using System;
using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Threading;
using Cysharp.Text;

namespace Hayabusa.Rpc.Http;

/// <summary>
/// Writes HTTP/1.1 responses directly into an <see cref="IBufferWriter{T}"/> with zero heap allocations
/// using UTF-8 byte literals, a Kestrel-style 1-second cached RFC 9110 <c>Date</c> + <c>Server</c> header block,
/// and Cysharp <c>ZString</c>.
/// Supports C# 13 <c>allows ref struct</c> so stack-only buffer writers can be passed by reference.
/// </summary>
public static class HttpResponseWriter
{
    private static long s_lastDateUpdateSecond = -1;
    private static byte[] s_cachedDateAndServerHeader = BuildDateAndServerHeaderBytes(DateTimeOffset.UtcNow);

    private static ReadOnlySpan<byte> Status200HeaderPrefix =>
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: keep-alive\r\nContent-Length: "u8;

    private static ReadOnlySpan<byte> Status200JsonHeaderPrefix =>
        "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nConnection: keep-alive\r\nContent-Length: "u8;

    private static ReadOnlySpan<byte> Status400Response =>
        "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8;

    private static ReadOnlySpan<byte> Status404Response =>
        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n"u8;

    /// <summary>
    /// Gets the pre-formatted <c>"Server: Hayabusa\r\nDate: &lt;RFC1123&gt;\r\n"</c> UTF-8 bytes, refreshed at most once per second.
    /// Eliminates per-request timestamp formatting overhead just like Kestrel's <c>DateHeaderValueManager</c>.
    /// </summary>
    public static ReadOnlySpan<byte> CachedServerAndDateHeader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long currentSecond = Environment.TickCount64 >> 10;
            if (currentSecond != Volatile.Read(ref s_lastDateUpdateSecond))
            {
                RefreshDateHeaderSlow(currentSecond);
            }

            return s_cachedDateAndServerHeader;
        }
    }

    /// <summary>
    /// Writes a 200 OK HTTP/1.1 response with the given UTF-8 body into <paramref name="writer"/> with zero heap allocations.
    /// Uses a single-digit / two-digit fast path for Content-Length formatting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteOkUtf8<TWriter>(ref TWriter writer, scoped ReadOnlySpan<byte> utf8Body)
        where TWriter : IBufferWriter<byte>, allows ref struct
    {
        WriteResponseWithPrefix(ref writer, Status200HeaderPrefix, utf8Body);
    }

    /// <summary>
    /// Writes a 200 OK HTTP/1.1 JSON response with the given UTF-8 JSON body into <paramref name="writer"/> with zero heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteOkJsonUtf8<TWriter>(ref TWriter writer, scoped ReadOnlySpan<byte> utf8JsonBody)
        where TWriter : IBufferWriter<byte>, allows ref struct
    {
        WriteResponseWithPrefix(ref writer, Status200JsonHeaderPrefix, utf8JsonBody);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteResponseWithPrefix<TWriter>(
        ref TWriter writer,
        scoped ReadOnlySpan<byte> headerPrefix,
        scoped ReadOnlySpan<byte> body)
        where TWriter : IBufferWriter<byte>, allows ref struct
    {
        int bodyLen = body.Length;
        Span<byte> dest = writer.GetSpan(headerPrefix.Length + 16 + bodyLen);
        headerPrefix.CopyTo(dest);
        int offset = headerPrefix.Length;

        if ((uint)bodyLen < 10u)
        {
            dest[offset++] = (byte)('0' + bodyLen);
        }
        else if ((uint)bodyLen < 100u)
        {
            int tens = bodyLen / 10;
            dest[offset++] = (byte)('0' + tens);
            dest[offset++] = (byte)('0' + (bodyLen - (tens * 10)));
        }
        else
        {
            Utf8Formatter.TryFormat(bodyLen, dest.Slice(offset), out int digitsWritten);
            offset += digitsWritten;
        }

        dest[offset] = (byte)'\r';
        dest[offset + 1] = (byte)'\n';
        dest[offset + 2] = (byte)'\r';
        dest[offset + 3] = (byte)'\n';
        offset += 4;

        body.CopyTo(dest.Slice(offset));
        writer.Advance(offset + bodyLen);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RefreshDateHeaderSlow(long currentSecond)
    {
        Volatile.Write(ref s_lastDateUpdateSecond, currentSecond);
        s_cachedDateAndServerHeader = BuildDateAndServerHeaderBytes(DateTimeOffset.UtcNow);
    }

    private static byte[] BuildDateAndServerHeaderBytes(DateTimeOffset utcNow)
    {
        // "Server: Hayabusa\r\nDate: Sun, 25 Oct 2026 05:29:00 GMT\r\n" (55 bytes)
        byte[] buffer = new byte[55];
        "Server: Hayabusa\r\nDate: "u8.CopyTo(buffer);
        Utf8Formatter.TryFormat(utcNow, buffer.AsSpan(24), out _, new StandardFormat('R'));
        buffer[53] = (byte)'\r';
        buffer[54] = (byte)'\n';
        return buffer;
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
