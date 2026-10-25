using System;
using System.Runtime.CompilerServices;

namespace Hayabusa.Rpc.Http;

/// <summary>
/// Supported HTTP request methods in Hayabusa's minimal zero-allocation HTTP parser.
/// </summary>
public enum HttpMethodKind : byte
{
    /// <summary>Unknown or unsupported HTTP method.</summary>
    Unknown = 0,

    /// <summary>HTTP GET method.</summary>
    Get = 1,

    /// <summary>HTTP POST method.</summary>
    Post = 2,

    /// <summary>HTTP PUT method.</summary>
    Put = 3,

    /// <summary>HTTP DELETE method.</summary>
    Delete = 4,

    /// <summary>HTTP HEAD method.</summary>
    Head = 5,

    /// <summary>HTTP OPTIONS method (used for CORS preflight requests).</summary>
    Options = 6,
}

/// <summary>
/// Enumeration of all HTTP parsing error outcomes, including security rejections for Request Smuggling and Header Bombs.
/// </summary>
public enum HttpParseError : byte
{
    /// <summary>Request parsed completely and passed all RFC / security validations.</summary>
    None = 0,

    /// <summary>More bytes are required to reach the end of headers or body.</summary>
    IncompleteData = 1,

    /// <summary>Total header block exceeded <c>MaxHeaderSize</c>.</summary>
    HeaderTooLarge = 2,

    /// <summary>Number of header lines exceeded <c>MaxHeaderCount</c>.</summary>
    TooManyHeaders = 3,

    /// <summary>Malformed request line, invalid HTTP version, or unsupported method.</summary>
    InvalidRequestLine = 4,

    /// <summary>CRLF injection, null byte, or malformed header syntax detected.</summary>
    MalformedHeader = 5,

    /// <summary>HTTP Request Smuggling attempt detected (e.g. duplicate Content-Length or simultaneous Content-Length and Transfer-Encoding).</summary>
    RequestSmugglingDetected = 6,

    /// <summary>Path traversal sequence (<c>..</c> or encoded <c>%2e%2e</c> or backslash) detected in target URI.</summary>
    PathTraversalDetected = 7,

    /// <summary>Declared Content-Length is negative or exceeds the maximum body size.</summary>
    PayloadTooLarge = 8,
}

/// <summary>
/// Zero-allocation stack-only view over a parsed HTTP/1.1 request.
/// </summary>
public readonly ref struct HttpRequestView
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HttpRequestView"/> struct.
    /// </summary>
    public HttpRequestView(
        HttpMethodKind method,
        ReadOnlySpan<byte> path,
        ReadOnlySpan<byte> body,
        (int HeaderCount, int ConsumedBytes) metrics)
    {
        Method = method;
        Path = path;
        Body = body;
        HeaderCount = metrics.HeaderCount;
        ConsumedBytes = metrics.ConsumedBytes;
    }

    /// <summary>Gets the parsed HTTP method.</summary>
    public HttpMethodKind Method { get; }

    /// <summary>Gets the raw UTF-8 path slice (excluding query string if split, or full target path).</summary>
    public ReadOnlySpan<byte> Path { get; }

    /// <summary>Gets the raw UTF-8 request body slice.</summary>
    public ReadOnlySpan<byte> Body { get; }

    /// <summary>Gets the number of validated HTTP headers.</summary>
    public int HeaderCount { get; }

    /// <summary>Gets the total bytes consumed from the receive buffer for this HTTP request.</summary>
    public int ConsumedBytes { get; }
}
