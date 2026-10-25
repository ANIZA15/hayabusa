using System;
using System.Buffers.Text;
using System.Runtime.CompilerServices;

namespace Hayabusa.Rpc.Http;

/// <summary>
/// Branch-minimized, zero-allocation HTTP/1.1 request parser with strict RFC 9112 compliance.
/// Defends against HTTP Request Smuggling, Header Bombs, CRLF injection, Null-byte injection, and Path Traversal.
/// </summary>
public static class HttpParser
{
    /// <summary>Default maximum header block size in bytes (16 KB).</summary>
    public const int DefaultMaxHeaderSize = 16 * 1024;

    /// <summary>Default maximum number of headers per request (64).</summary>
    public const int DefaultMaxHeaderCount = 64;

    /// <summary>Default maximum request body size in bytes (64 KB).</summary>
    public const int DefaultMaxBodySize = 64 * 1024;

    // 256-entry lookup table for valid RFC 9110 token characters (!#$%&'*+-.^_`|~0-9A-Za-z)
    private static ReadOnlySpan<byte> ValidHeaderNameChars =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 0, 1, 1, 1, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0,
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 0, 1, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

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
        if (input.IndexOf((byte)0) >= 0)
        {
            return HttpParseError.MalformedHeader;
        }

        int headerEndIndex = input.IndexOf("\r\n\r\n"u8);
        if (headerEndIndex < 0)
        {
            return input.Length > maxHeaderSize ? HttpParseError.HeaderTooLarge : HttpParseError.IncompleteData;
        }

        int headerBytesLength = headerEndIndex + 4;
        if (headerBytesLength > maxHeaderSize)
        {
            return HttpParseError.HeaderTooLarge;
        }

        return ParseSubmittedHeaderBlock(input, headerEndIndex, maxHeaderCount, out request);
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
        if (!version.SequenceEqual("HTTP/1.1"u8) && !version.SequenceEqual("HTTP/1.0"u8))
        {
            return HttpParseError.InvalidRequestLine;
        }

        return ValidateSafePath(path);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HttpMethodKind ResolveMethod(scoped ReadOnlySpan<byte> token)
    {
        if (token.SequenceEqual("GET"u8)) return HttpMethodKind.Get;
        if (token.SequenceEqual("POST"u8)) return HttpMethodKind.Post;
        if (token.SequenceEqual("PUT"u8)) return HttpMethodKind.Put;
        if (token.SequenceEqual("DELETE"u8)) return HttpMethodKind.Delete;
        return token.SequenceEqual("HEAD"u8) ? HttpMethodKind.Head : HttpMethodKind.Unknown;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HttpParseError ValidateSafePath(scoped ReadOnlySpan<byte> path)
    {
        if (path.IsEmpty || path[0] != (byte)'/')
        {
            return HttpParseError.InvalidRequestLine;
        }

        if (path.IndexOf(".."u8) >= 0 || path.IndexOf((byte)'\\') >= 0)
        {
            return HttpParseError.PathTraversalDetected;
        }

        if (ContainsCaseInsensitivePercent2E(path))
        {
            return HttpParseError.PathTraversalDetected;
        }

        return HttpParseError.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsCaseInsensitivePercent2E(scoped ReadOnlySpan<byte> path)
    {
        return path.IndexOf("%2e"u8) >= 0 || path.IndexOf("%2E"u8) >= 0;
    }

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
        line.IndexOf((byte)'\r') >= 0 || line.IndexOf((byte)'\n') >= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidHeaderName(scoped ReadOnlySpan<byte> name)
    {
        ReadOnlySpan<byte> table = ValidHeaderNameChars;
        for (int i = 0; i < name.Length; i++)
        {
            if (table[name[i]] == 0)
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        int start = 0;
        while (start < span.Length && (span[start] == (byte)' ' || span[start] == (byte)'\t'))
        {
            start++;
        }

        int end = span.Length - 1;
        while (end >= start && (span[end] == (byte)' ' || span[end] == (byte)'\t'))
        {
            end--;
        }

        return span.Slice(start, end - start + 1);
    }

    private static HttpParseError InspectSecuritySensitiveHeader(
        scoped ReadOnlySpan<byte> name,
        scoped ReadOnlySpan<byte> value,
        ref SmugglingCheckState state,
        ref int contentLength)
    {
        if (AsciiEqualsIgnoreCase(name, "Transfer-Encoding"u8))
        {
            state.HasTransferEncoding = true;
            return state.HasContentLength ? HttpParseError.RequestSmugglingDetected : HttpParseError.RequestSmugglingDetected;
        }

        if (!AsciiEqualsIgnoreCase(name, "Content-Length"u8))
        {
            return HttpParseError.None;
        }

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
        if (!Utf8Parser.TryParse(value, out int parsed, out int consumed) || consumed != value.Length)
        {
            contentLength = 0;
            return HttpParseError.MalformedHeader;
        }

        if ((uint)parsed > DefaultMaxBodySize)
        {
            contentLength = 0;
            return HttpParseError.PayloadTooLarge;
        }

        contentLength = parsed;
        return HttpParseError.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AsciiEqualsIgnoreCase(scoped ReadOnlySpan<byte> left, scoped ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if ((left[i] | 0x20) != (right[i] | 0x20))
            {
                return false;
            }
        }

        return true;
    }

    private struct SmugglingCheckState
    {
        public bool HasContentLength;
        public bool HasTransferEncoding;
    }
}
