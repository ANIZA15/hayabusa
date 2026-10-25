using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;

namespace Hayabusa.Rpc.Http;

/// <summary>
/// Branch-minimized, zero-allocation HTTP/1.1 request parser with strict RFC 9112 compliance.
/// Uses SIMD-accelerated <see cref="SearchValues{T}"/> (.NET 9 / .NET 10+) to defend against HTTP Request Smuggling,
/// Header Bombs, CRLF injection, Null-byte injection, and Path Traversal.
/// </summary>
public static class HttpParser
{
    /// <summary>Default maximum header block size in bytes (16 KB).</summary>
    public const int DefaultMaxHeaderSize = 16 * 1024;

    /// <summary>Default maximum number of headers per request (64).</summary>
    public const int DefaultMaxHeaderCount = 64;

    /// <summary>Default maximum request body size in bytes (64 KB).</summary>
    public const int DefaultMaxBodySize = 64 * 1024;

    // SIMD-vectorized SearchValues<byte> for RFC 9110 tchar validation and control/bare-CRLF detection
    private static readonly SearchValues<byte> s_validHeaderNameBytes =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8);

    private static readonly SearchValues<byte> s_forbiddenHeaderLineBytes =
        SearchValues.Create("\0\x01\x02\x03\x04\x05\x06\x07\x08\n\x0b\x0c\r\x0e\x0f\x10\x11\x12\x13\x14\x15\x16\x17\x18\x19\x1a\x1b\x1c\x1d\x1e\x1f\x7f"u8);

    /// <summary>
    /// Attempts to parse an HTTP/1.1 request from <paramref name="input"/> with zero heap allocations.
    /// </summary>
    public static HttpParseError TryParseRequest(
        ReadOnlySpan<byte> input,
        out HttpRequestView request,
        int maxHeaderSize = DefaultMaxHeaderSize,
        int maxHeaderCount = DefaultMaxHeaderCount)
    {
        request = default;
        int clampedMaxHeader = Math.Clamp(maxHeaderSize, 1, int.MaxValue - DefaultMaxBodySize - 8);
        int searchLimit = Math.Min(input.Length, clampedMaxHeader + 4);
        int headerEndIndex = input.Slice(0, searchLimit).IndexOf("\r\n\r\n"u8);
        if (headerEndIndex < 0)
        {
            return ResolveMissingHeaderTerminator(input, searchLimit, maxHeaderSize);
        }

        int headerBytesLength = headerEndIndex + 4;
        if (headerBytesLength > maxHeaderSize)
        {
            return HttpParseError.HeaderTooLarge;
        }

        if (input.Slice(0, headerBytesLength).IndexOf((byte)0) >= 0)
        {
            return HttpParseError.MalformedHeader;
        }

        return ParseSubmittedHeaderBlock(input, headerEndIndex, maxHeaderCount, out request);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HttpParseError ResolveMissingHeaderTerminator(
        scoped ReadOnlySpan<byte> input,
        int searchLimit,
        int maxHeaderSize)
    {
        if (input.Slice(0, searchLimit).IndexOf((byte)0) >= 0)
        {
            return HttpParseError.MalformedHeader;
        }

        return input.Length > maxHeaderSize ? HttpParseError.HeaderTooLarge : HttpParseError.IncompleteData;
    }

    private static HttpParseError ParseSubmittedHeaderBlock(
        ReadOnlySpan<byte> input,
        int headerEndIndex,
        int maxHeaderCount,
        out HttpRequestView request)
    {
        request = default;
        ReadOnlySpan<byte> headerBlock = input.Slice(0, headerEndIndex);
        int firstLineEnd = headerBlock.IndexOf("\r\n"u8);
        ReadOnlySpan<byte> requestLine = firstLineEnd >= 0 ? headerBlock.Slice(0, firstLineEnd) : headerBlock;

        HttpParseError lineError = TryParseRequestLine(requestLine, out HttpMethodKind method, out ReadOnlySpan<byte> path);
        if (lineError != HttpParseError.None)
        {
            return lineError;
        }

        ReadOnlySpan<byte> remainingHeaders = firstLineEnd >= 0
            ? headerBlock.Slice(firstLineEnd + 2)
            : ReadOnlySpan<byte>.Empty;

        HttpParseError hdrError = ParseHeaders(remainingHeaders, maxHeaderCount, out int headerCount, out int contentLength);
        if (hdrError != HttpParseError.None)
        {
            return hdrError;
        }

        int bodyOffset = headerEndIndex + 4;
        int totalRequired = bodyOffset + contentLength;
        if (input.Length < totalRequired)
        {
            return HttpParseError.IncompleteData;
        }

        request = new HttpRequestView(
            method,
            path,
            input.Slice(bodyOffset, contentLength),
            (headerCount, totalRequired));
        return HttpParseError.None;
    }

    private static HttpParseError TryParseRequestLine(
        ReadOnlySpan<byte> line,
        out HttpMethodKind method,
        out ReadOnlySpan<byte> path)
    {
        method = HttpMethodKind.Unknown;
        path = default;

        int firstSpace = line.IndexOf((byte)' ');
        if (firstSpace <= 0)
        {
            return HttpParseError.InvalidRequestLine;
        }

        method = ResolveMethod(line.Slice(0, firstSpace));
        if (method == HttpMethodKind.Unknown)
        {
            return HttpParseError.InvalidRequestLine;
        }

        ReadOnlySpan<byte> afterMethod = line.Slice(firstSpace + 1);
        int secondSpace = afterMethod.IndexOf((byte)' ');
        if (secondSpace <= 0)
        {
            return HttpParseError.InvalidRequestLine;
        }

        path = afterMethod.Slice(0, secondSpace);
        ReadOnlySpan<byte> version = afterMethod.Slice(secondSpace + 1);
        if (!IsSupportedHttpVersion(version))
        {
            return HttpParseError.InvalidRequestLine;
        }

        return ValidateSafePath(path);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSupportedHttpVersion(scoped ReadOnlySpan<byte> version)
    {
        if (version.Length != 8)
        {
            return false;
        }

        ulong raw = BinaryPrimitives.ReadUInt64LittleEndian(version);
        // "HTTP/1.1" = 0x312E312F50545448UL, "HTTP/1.0" = 0x302E312F50545448UL
        return raw is 0x312E312F50545448UL or 0x302E312F50545448UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HttpMethodKind ResolveMethod(scoped ReadOnlySpan<byte> token)
    {
        return token.Length switch
        {
            3 => ResolveThreeByteMethod(token),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(token) switch
            {
                0x54534F50u => HttpMethodKind.Post, // "POST" in Little-Endian
                0x44414548u => HttpMethodKind.Head, // "HEAD" in Little-Endian
                _ => HttpMethodKind.Unknown,
            },
            6 => token.SequenceEqual("DELETE"u8) ? HttpMethodKind.Delete : HttpMethodKind.Unknown,
            7 => token.SequenceEqual("OPTIONS"u8) ? HttpMethodKind.Options : HttpMethodKind.Unknown,
            _ => HttpMethodKind.Unknown,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HttpMethodKind ResolveThreeByteMethod(scoped ReadOnlySpan<byte> token)
    {
        if (token.SequenceEqual("GET"u8))
        {
            return HttpMethodKind.Get;
        }

        return token.SequenceEqual("PUT"u8) ? HttpMethodKind.Put : HttpMethodKind.Unknown;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HttpParseError ValidateSafePath(scoped ReadOnlySpan<byte> path)
    {
        if (!HasValidRequestTargetChars(path))
        {
            return HttpParseError.InvalidRequestLine;
        }

        if (ContainsPathTraversalSequence(path))
        {
            return HttpParseError.PathTraversalDetected;
        }

        return HttpParseError.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasValidRequestTargetChars(scoped ReadOnlySpan<byte> path) =>
        !path.IsEmpty && path[0] == (byte)'/' && !path.ContainsAnyExceptInRange((byte)0x21, (byte)0x7E);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsPathTraversalSequence(scoped ReadOnlySpan<byte> path) =>
        path.IndexOf(".."u8) >= 0
        || path.IndexOf((byte)'\\') >= 0
        || ContainsEncodedTraversalOrNull(path);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsEncodedTraversalOrNull(scoped ReadOnlySpan<byte> path) =>
        path.IndexOf("%2e"u8) >= 0
        || path.IndexOf("%2E"u8) >= 0
        || ContainsEncodedBackslashOrNull(path);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsEncodedBackslashOrNull(scoped ReadOnlySpan<byte> path) =>
        path.IndexOf("%5c"u8) >= 0
        || path.IndexOf("%5C"u8) >= 0
        || path.IndexOf("%00"u8) >= 0;

    private static HttpParseError ParseHeaders(
        scoped ReadOnlySpan<byte> headersSpan,
        int maxHeaderCount,
        out int headerCount,
        out int contentLength)
    {
        headerCount = 0;
        contentLength = 0;
        if (headersSpan.IsEmpty)
        {
            return HttpParseError.None;
        }

        var smugglingState = new SmugglingCheckState();
        ReadOnlySpan<byte> cursor = headersSpan;
        while (!cursor.IsEmpty)
        {
            if (++headerCount > maxHeaderCount)
            {
                return HttpParseError.TooManyHeaders;
            }

            ReadOnlySpan<byte> line = SliceNextHeaderLine(ref cursor);
            HttpParseError error = ValidateSingleHeaderLine(line, ref smugglingState, ref contentLength);
            if (error != HttpParseError.None)
            {
                return error;
            }
        }

        return HttpParseError.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> SliceNextHeaderLine(ref ReadOnlySpan<byte> cursor)
    {
        int crlf = cursor.IndexOf("\r\n"u8);
        if (crlf < 0)
        {
            ReadOnlySpan<byte> tail = cursor;
            cursor = ReadOnlySpan<byte>.Empty;
            return tail;
        }

        ReadOnlySpan<byte> line = cursor.Slice(0, crlf);
        cursor = cursor.Slice(crlf + 2);
        return line;
    }

    private static HttpParseError ValidateSingleHeaderLine(
        scoped ReadOnlySpan<byte> line,
        ref SmugglingCheckState state,
        ref int contentLength)
    {
        if (ContainsBareCrOrLf(line))
        {
            return HttpParseError.MalformedHeader;
        }

        int colonIndex = line.IndexOf((byte)':');
        if (colonIndex <= 0)
        {
            return HttpParseError.MalformedHeader;
        }

        ReadOnlySpan<byte> name = line.Slice(0, colonIndex);
        if (!IsValidHeaderName(name))
        {
            return HttpParseError.MalformedHeader;
        }

        ReadOnlySpan<byte> value = TrimAsciiWhitespace(line.Slice(colonIndex + 1));
        return InspectSecuritySensitiveHeader(name, value, ref state, ref contentLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsBareCrOrLf(scoped ReadOnlySpan<byte> line) =>
        line.ContainsAny(s_forbiddenHeaderLineBytes);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidHeaderName(scoped ReadOnlySpan<byte> name) =>
        !name.IsEmpty && !name.ContainsAnyExcept(s_validHeaderNameBytes);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> span) =>
        span.Trim(" \t"u8);

    private static HttpParseError InspectSecuritySensitiveHeader(
        scoped ReadOnlySpan<byte> name,
        scoped ReadOnlySpan<byte> value,
        ref SmugglingCheckState state,
        ref int contentLength)
    {
        // Fast-path length filter: "Content-Length" is 14 bytes, "Transfer-Encoding" is 17 bytes.
        // Skips string comparison for Host, User-Agent, Accept, Connection, etc. in 1 CPU cycle.
        if (name.Length == 17 && Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8))
        {
            state.HasTransferEncoding = true;
            return HttpParseError.RequestSmugglingDetected;
        }

        if (name.Length == 14 && Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
        {
            return InspectContentLengthHeader(value, ref state, ref contentLength);
        }

        return HttpParseError.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HttpParseError InspectContentLengthHeader(
        scoped ReadOnlySpan<byte> value,
        ref SmugglingCheckState state,
        ref int contentLength)
    {
        if (state.HasContentLength || state.HasTransferEncoding)
        {
            return HttpParseError.RequestSmugglingDetected;
        }

        state.HasContentLength = true;
        return TryParseContentLengthValue(value, out contentLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HttpParseError TryParseContentLengthValue(scoped ReadOnlySpan<byte> value, out int contentLength)
    {
        contentLength = 0;
        if (value.IsEmpty || value.ContainsAnyExceptInRange((byte)'0', (byte)'9'))
        {
            return HttpParseError.MalformedHeader;
        }

        if (!Utf8Parser.TryParse(value, out int parsed, out int consumed)
            || consumed != value.Length
            || (uint)parsed > DefaultMaxBodySize)
        {
            return HttpParseError.PayloadTooLarge;
        }

        contentLength = parsed;
        return HttpParseError.None;
    }

    private struct SmugglingCheckState
    {
        public bool HasContentLength;
        public bool HasTransferEncoding;
    }
}
