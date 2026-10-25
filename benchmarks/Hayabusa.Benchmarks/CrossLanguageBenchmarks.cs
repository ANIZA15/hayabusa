using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hayabusa.Runtime.Hosting;

namespace Hayabusa.Benchmarks;

#pragma warning disable CA1031 // Benchmark runner logs and continues if an external runtime is unavailable

/// <summary>
/// Configuration options for the cross-language HTTP/1.1 benchmark suite (Hayabusa vs Go vs Rust).
/// </summary>
public sealed class CrossLanguageBenchmarkOptions
{
    /// <summary>Gets or sets the target endpoint for the Hayabusa server (if externally hosted, e.g. in Docker).</summary>
    public string? HayabusaEndpoint { get; set; } = Environment.GetEnvironmentVariable("HAYABUSA_BENCH_URL");

    /// <summary>Gets or sets the target endpoint for the Go <c>net/http</c> server (if externally hosted, e.g. in Docker).</summary>
    public string? GoEndpoint { get; set; } = Environment.GetEnvironmentVariable("GO_BENCH_URL");

    /// <summary>Gets or sets the target endpoint for the Rust web server (if externally hosted, e.g. in Docker).</summary>
    public string? RustEndpoint { get; set; } = Environment.GetEnvironmentVariable("RUST_BENCH_URL");

    /// <summary>Gets or sets the number of concurrent keep-alive TCP connections (default: 16).</summary>
    public int Connections { get; set; } = ReadIntEnv("BENCH_CONNECTIONS", 16);

    /// <summary>Gets or sets the total number of requests per scenario (default: 32,000).</summary>
    public int RequestsPerScenario { get; set; } = ReadIntEnv("BENCH_REQUESTS", 32_000);

    /// <summary>Gets or sets the warmup request count per server (default: 8,000).</summary>
    public int WarmupRequests { get; set; } = ReadIntEnv("BENCH_WARMUP", 8_000);

    /// <summary>Gets or sets the HTTP/1.1 pipeline batch depth for pipelined scenarios (default: 16).</summary>
    public int PipelineDepth { get; set; } = ReadIntEnv("BENCH_PIPELINE", 16);

    /// <summary>
    /// Parses command-line arguments into a <see cref="CrossLanguageBenchmarkOptions"/> instance.
    /// </summary>
    public static CrossLanguageBenchmarkOptions Parse(string[]? args)
    {
        var options = new CrossLanguageBenchmarkOptions();
        if (args is null)
        {
            return options;
        }

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (ApplyOptionPair(options, args[i], args[i + 1]))
            {
                i++;
            }
        }

        return options;
    }

    private static bool ApplyOptionPair(CrossLanguageBenchmarkOptions options, string key, string value) =>
        ApplyUrlOption(options, key, value) || ApplyNumericOption(options, key, value);

    private static bool ApplyUrlOption(CrossLanguageBenchmarkOptions options, string key, string value)
    {
        switch (key)
        {
            case "--hayabusa-url":
                options.HayabusaEndpoint = value;
                return true;
            case "--go-url":
                options.GoEndpoint = value;
                return true;
            case "--rust-url":
                options.RustEndpoint = value;
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyNumericOption(CrossLanguageBenchmarkOptions options, string key, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
        {
            return false;
        }

        switch (key)
        {
            case "--connections":
                options.Connections = parsed;
                return true;
            case "--requests":
                options.RequestsPerScenario = parsed;
                return true;
            case "--warmup":
                options.WarmupRequests = parsed;
                return true;
            case "--pipeline":
                options.PipelineDepth = parsed;
                return true;
            default:
                return false;
        }
    }

    private static int ReadIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) && val > 0
            ? val
            : fallback;
    }
}

/// <summary>
/// Identifies a target server endpoint in the cross-language benchmark suite.
/// </summary>
public readonly record struct BenchmarkTargetServer(string Name, string Host, int Port);

/// <summary>
/// Defines a single HTTP/1.1 benchmark workload scenario.
/// </summary>
public readonly record struct HttpBenchmarkWorkload(
    string ScenarioName,
    ReadOnlyMemory<byte> RequestPayload,
    int ResponsesPerBatch,
    int TotalBatches);

/// <summary>
/// Captures the measured throughput and latency percentiles of a benchmark scenario against one server.
/// </summary>
public readonly record struct CrossLanguageScenarioResult(
    string ScenarioName,
    string ServerName,
    double RequestsPerSecond,
    (double AvgMicros, double P50Micros, double P99Micros) LatencyMicros);

/// <summary>
/// Runs reproducible HTTP/1.1 Keep-Alive and Pipelined benchmarks comparing Hayabusa (.NET 9)
/// against Go's standard <c>net/http</c> server and Rust's standard web server stack.
/// Supports both Docker Compose multi-container endpoints and ephemeral <c>/tmp</c> local execution
/// without placing any <c>.go</c> or <c>.rs</c> source files in the repository.
/// </summary>
public static class CrossLanguageBenchmarks
{
    private const string GoStandardServerSource = """
        package main
        import (
            "net"
            "net/http"
            "os"
            "time"
        )
        var okBytes = []byte("OK")
        var jsonBytes = []byte(`{"status":"ok","engine":"Go-net/http"}`)
        func main() {
            addr := ":18081"
            if len(os.Args) > 1 { addr = os.Args[1] }
            mux := http.NewServeMux()
            mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
                w.Header().Set("Content-Type", "text/plain; charset=utf-8")
                w.Header().Set("Content-Length", "2")
                w.WriteHeader(http.StatusOK)
                _, _ = w.Write(okBytes)
            })
            mux.HandleFunc("/api/status", func(w http.ResponseWriter, r *http.Request) {
                w.Header().Set("Content-Type", "application/json; charset=utf-8")
                w.Header().Set("Content-Length", "38")
                w.WriteHeader(http.StatusOK)
                _, _ = w.Write(jsonBytes)
            })
            srv := &http.Server{
                Addr:              addr,
                Handler:           mux,
                ReadHeaderTimeout: 10 * time.Second,
                IdleTimeout:       60 * time.Second,
            }
            ln, err := net.Listen("tcp", addr)
            if err != nil { panic(err) }
            _ = srv.Serve(ln)
        }
        """;

    private const string RustAxumCargoToml = """
        [package]
        name = "rust_axum_bench"
        version = "0.1.0"
        edition = "2021"

        [dependencies]
        axum = { version = "=0.7.9", default-features = false, features = ["http1", "tokio"] }
        tokio = { version = "=1.42.0", default-features = false, features = ["rt-multi-thread", "net", "macros"] }
        hyper = { version = "=1.5.2", default-features = false, features = ["http1", "server"] }
        hyper-util = { version = "=0.1.10", default-features = false, features = ["tokio", "server", "http1", "service"] }
        tower = { version = "=0.5.1", default-features = false }
        socket2 = { version = "=0.5.8", default-features = false }
        mio = { version = "=1.0.3", default-features = false, features = ["os-poll", "net"] }
        bytes = "=1.9.0"
        http = "=1.2.0"
        http-body = "=1.0.1"
        http-body-util = "=0.1.2"
        pin-project-lite = "=0.2.15"
        futures-util = { version = "=0.3.31", default-features = false }

        [profile.release]
        opt-level = 3
        lto = "thin"
        codegen-units = 1
        """;

    private const string RustAxumMainSource = """
        use axum::{routing::get, Router, response::IntoResponse, http::{header, HeaderValue}};
        use hyper::server::conn::http1;
        use hyper_util::{rt::TokioIo, service::TowerToHyperService};

        async fn health() -> impl IntoResponse {
            ([(header::CONTENT_TYPE, HeaderValue::from_static("text/plain; charset=utf-8"))], "OK")
        }

        async fn status() -> impl IntoResponse {
            ([(header::CONTENT_TYPE, HeaderValue::from_static("application/json; charset=utf-8"))], "{\"status\":\"ok\",\"engine\":\"Rust-axum\"}")
        }

        #[tokio::main]
        async fn main() {
            let addr = std::env::args().nth(1).unwrap_or_else(|| "0.0.0.0:18082".to_string());
            let app = Router::new()
                .route("/health", get(health))
                .route("/api/status", get(status));
            let listener = tokio::net::TcpListener::bind(&addr).await.unwrap();
            loop {
                if let Ok((stream, _)) = listener.accept().await {
                    let _ = stream.set_nodelay(true);
                    let io = TokioIo::new(stream);
                    let svc = TowerToHyperService::new(app.clone());
                    tokio::spawn(async move {
                        let _ = http1::Builder::new()
                            .pipeline_flush(true)
                            .serve_connection(io, svc)
                            .await;
                    });
                }
            }
        }
        """;

    private const string RustStandardServerSource = """
        use std::env;
        use std::io::{Read, Write};
        use std::net::{TcpListener, TcpStream};
        use std::thread;

        const HEALTH_RESP: &[u8] = b"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: keep-alive\r\nContent-Length: 2\r\n\r\nOK";
        const STATUS_RESP: &[u8] = b"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nConnection: keep-alive\r\nContent-Length: 35\r\n\r\n{\"status\":\"ok\",\"engine\":\"Rust-std\"}";
        const NOT_FOUND_RESP: &[u8] = b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n";

        fn find_header_end(buf: &[u8]) -> Option<usize> {
            buf.windows(4).position(|w| w == b"\r\n\r\n").map(|p| p + 4)
        }

        fn handle_client(mut stream: TcpStream) {
            let _ = stream.set_nodelay(true);
            let mut buf = [0u8; 65536];
            let mut filled = 0usize;
            let mut out = Vec::with_capacity(8192);
            loop {
                let n = match stream.read(&mut buf[filled..]) {
                    Ok(0) | Err(_) => return,
                    Ok(n) => n,
                };
                filled += n;
                out.clear();
                let mut offset = 0usize;
                while let Some(end) = find_header_end(&buf[offset..filled]) {
                    let req = &buf[offset..offset + end];
                    if req.starts_with(b"GET /health ") {
                        out.extend_from_slice(HEALTH_RESP);
                    } else if req.starts_with(b"GET /api/status ") {
                        out.extend_from_slice(STATUS_RESP);
                    } else {
                        out.extend_from_slice(NOT_FOUND_RESP);
                    }
                    offset += end;
                }
                if offset > 0 {
                    buf.copy_within(offset..filled, 0);
                    filled -= offset;
                }
                if !out.is_empty() && stream.write_all(&out).is_err() {
                    return;
                }
            }
        }

        fn main() {
            let addr = env::args().nth(1).unwrap_or_else(|| "127.0.0.1:18082".to_string());
            let listener = TcpListener::bind(&addr).expect("bind failed");
            for stream in listener.incoming().flatten() {
                thread::spawn(move || handle_client(stream));
            }
        }
        """;

    /// <summary>
    /// Executes the full cross-language benchmark suite and prints a formatted comparison table.
    /// </summary>
    public static async Task RunAsync(CrossLanguageBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var harness = new EphemeralBenchmarkHarness();
        IReadOnlyList<BenchmarkTargetServer> servers = await harness.ResolveOrStartServersAsync(options);

        PrintBanner(options, servers);
        IReadOnlyList<HttpBenchmarkWorkload> workloads = CreateWorkloads(options);
        var results = new List<CrossLanguageScenarioResult>(workloads.Count * servers.Count);

        for (int w = 0; w < workloads.Count; w++)
        {
            HttpBenchmarkWorkload workload = workloads[w];
            for (int s = 0; s < servers.Count; s++)
            {
                await WarmupServerAsync(servers[s], options.Connections, options.WarmupRequests);
                CrossLanguageScenarioResult result = await MeasureServerWorkloadAsync(
                    servers[s],
                    workload,
                    options.Connections);
                results.Add(result);
            }
        }

        PrintComparisonTable(workloads, results);
    }

    private static IReadOnlyList<HttpBenchmarkWorkload> CreateWorkloads(CrossLanguageBenchmarkOptions options)
    {
        byte[] healthReq = BuildGetRequest("/health");
        byte[] jsonReq = BuildGetRequest("/api/status");
        int depth = Math.Max(1, options.PipelineDepth);
        byte[] pipelinedHealthReq = RepeatBytes(healthReq, depth);

        int standardBatches = Math.Max(options.Connections, options.RequestsPerScenario);
        int pipelinedBatches = Math.Max(options.Connections, options.RequestsPerScenario / depth);

        return
        [
            new HttpBenchmarkWorkload("1. GET /health (Plaintext Keep-Alive)", healthReq, 1, standardBatches),
            new HttpBenchmarkWorkload("2. GET /api/status (JSON Keep-Alive)", jsonReq, 1, standardBatches),
            new HttpBenchmarkWorkload($"3. GET /health ({depth}x HTTP/1.1 Pipelined)", pipelinedHealthReq, depth, pipelinedBatches),
        ];
    }

    private static async Task WarmupServerAsync(BenchmarkTargetServer server, int connections, int warmupRequests)
    {
        var warmupWorkload = new HttpBenchmarkWorkload(
            "Warmup",
            BuildGetRequest("/health"),
            ResponsesPerBatch: 1,
            TotalBatches: Math.Max(256, warmupRequests));
        _ = await MeasureServerWorkloadAsync(server, warmupWorkload, Math.Max(4, connections));
    }

    private static async Task<CrossLanguageScenarioResult> MeasureServerWorkloadAsync(
        BenchmarkTargetServer server,
        HttpBenchmarkWorkload workload,
        int connections)
    {
        int workerCount = Math.Max(1, connections);
        int batchesPerWorker = Math.Max(1, workload.TotalBatches / workerCount);
        var latencySamples = new double[workerCount][];
        var tasks = new Task[workerCount];

        long startTimestamp = Stopwatch.GetTimestamp();
        for (int i = 0; i < workerCount; i++)
        {
            int workerIndex = i;
            tasks[i] = Task.Run(async () =>
            {
                latencySamples[workerIndex] = await RunConnectionBatchesAsync(server, workload, batchesPerWorker);
            });
        }

        await Task.WhenAll(tasks);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);

        int totalRequests = workerCount * batchesPerWorker * workload.ResponsesPerBatch;
        double rps = totalRequests / Math.Max(0.0001, elapsed.TotalSeconds);
        var percentiles = ComputeLatencyPercentiles(latencySamples);
        return new CrossLanguageScenarioResult(workload.ScenarioName, server.Name, rps, percentiles);
    }

    private static async Task<double[]> RunConnectionBatchesAsync(
        BenchmarkTargetServer server,
        HttpBenchmarkWorkload workload,
        int batchCount)
    {
        var samples = new double[batchCount];
        byte[] recvScratch = new byte[16 * 1024];
        using Socket socket = await ConnectSocketAsync(server);

        for (int i = 0; i < batchCount; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            await SendAllAsync(socket, workload.RequestPayload);
            await ReceiveExpectedResponsesAsync(socket, recvScratch, workload.ResponsesPerBatch);
            double batchMicros = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
            samples[i] = batchMicros / workload.ResponsesPerBatch;
        }

        return samples;
    }

    private static async Task<Socket> ConnectSocketAsync(BenchmarkTargetServer server)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        await socket.ConnectAsync(server.Host, server.Port);
        return socket;
    }

    private static async ValueTask SendAllAsync(Socket socket, ReadOnlyMemory<byte> payload)
    {
        int sent = 0;
        while (sent < payload.Length)
        {
            int n = await socket.SendAsync(payload.Slice(sent), SocketFlags.None);
            if (n <= 0)
            {
                throw new IOException("Benchmark socket closed during send.");
            }

            sent += n;
        }
    }

    private static async ValueTask ReceiveExpectedResponsesAsync(
        Socket socket,
        byte[] scratch,
        int expectedResponses)
    {
        int completedResponses = 0;
        int filled = 0;

        while (completedResponses < expectedResponses)
        {
            int read = await socket.ReceiveAsync(scratch.AsMemory(filled), SocketFlags.None);
            if (read <= 0)
            {
                throw new IOException("Benchmark socket closed before all expected HTTP responses arrived.");
            }

            filled += read;
            completedResponses += ConsumeCompletedHttpResponses(scratch, ref filled);
        }
    }

    private static int ConsumeCompletedHttpResponses(byte[] buffer, ref int filled)
    {
        int count = 0;
        int cursor = 0;

        while (TryAdvanceSingleHttpResponse(buffer.AsSpan(cursor, filled - cursor), out int consumed))
        {
            cursor += consumed;
            count++;
        }

        if (cursor > 0)
        {
            int remaining = filled - cursor;
            if (remaining > 0)
            {
                buffer.AsSpan(cursor, remaining).CopyTo(buffer);
            }

            filled = remaining;
        }

        return count;
    }

    private static bool TryAdvanceSingleHttpResponse(ReadOnlySpan<byte> span, out int totalBytes)
    {
        totalBytes = 0;
        int headerEnd = span.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0)
        {
            return false;
        }

        int bodyLength = ParseContentLength(span.Slice(0, headerEnd));
        int required = headerEnd + 4 + bodyLength;
        if (span.Length < required)
        {
            return false;
        }

        totalBytes = required;
        return true;
    }

    private static int ParseContentLength(ReadOnlySpan<byte> headerBlock)
    {
        ReadOnlySpan<byte> cursor = headerBlock;
        while (!cursor.IsEmpty)
        {
            int lineEnd = cursor.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> line = lineEnd >= 0 ? cursor.Slice(0, lineEnd) : cursor;
            if (TryExtractContentLengthValue(line, out int length))
            {
                return length;
            }

            cursor = lineEnd >= 0 ? cursor.Slice(lineEnd + 2) : ReadOnlySpan<byte>.Empty;
        }

        return 0;
    }

    private static bool TryExtractContentLengthValue(ReadOnlySpan<byte> line, out int length)
    {
        length = 0;
        if (line.Length <= 15 || !Ascii.EqualsIgnoreCase(line.Slice(0, 15), "Content-Length:"u8))
        {
            return false;
        }

        ReadOnlySpan<byte> digits = line.Slice(15).Trim(" \t"u8);
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out length);
    }

    private static (double AvgMicros, double P50Micros, double P99Micros) ComputeLatencyPercentiles(double[][] workerSamples)
    {
        int totalCount = 0;
        for (int i = 0; i < workerSamples.Length; i++)
        {
            totalCount += workerSamples[i].Length;
        }

        if (totalCount == 0)
        {
            return (0, 0, 0);
        }

        var merged = new double[totalCount];
        double sum = 0;
        int offset = 0;
        for (int i = 0; i < workerSamples.Length; i++)
        {
            double[] arr = workerSamples[i];
            arr.CopyTo(merged, offset);
            offset += arr.Length;
            for (int j = 0; j < arr.Length; j++)
            {
                sum += arr[j];
            }
        }

        Array.Sort(merged);
        double avg = sum / totalCount;
        double p50 = merged[(int)(totalCount * 0.50)];
        double p99 = merged[Math.Min(totalCount - 1, (int)(totalCount * 0.99))];
        return (avg, p50, p99);
    }

    private static byte[] BuildGetRequest(string path) =>
        Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n");

    private static byte[] RepeatBytes(byte[] single, int count)
    {
        byte[] result = new byte[single.Length * count];
        for (int i = 0; i < count; i++)
        {
            single.AsSpan().CopyTo(result.AsSpan(i * single.Length));
        }

        return result;
    }

    private static void PrintBanner(CrossLanguageBenchmarkOptions options, IReadOnlyList<BenchmarkTargetServer> servers)
    {
        Console.WriteLine("==========================================================================================================");
        Console.WriteLine(" Hayabusa vs Go (net/http) vs Rust — Cross-Language HTTP/1.1 Benchmark Suite");
        Console.WriteLine("==========================================================================================================");
        Console.WriteLine($" Connections : {options.Connections} keep-alive TCP sockets | Requests/Scenario : {options.RequestsPerScenario:N0} | Pipeline : {options.PipelineDepth}x");
        for (int i = 0; i < servers.Count; i++)
        {
            Console.WriteLine($" Target [{i + 1}]  : {servers[i].Name,-28} -> http://{servers[i].Host}:{servers[i].Port}");
        }

        Console.WriteLine("----------------------------------------------------------------------------------------------------------");
    }

    private static void PrintComparisonTable(
        IReadOnlyList<HttpBenchmarkWorkload> workloads,
        IReadOnlyList<CrossLanguageScenarioResult> results)
    {
        for (int w = 0; w < workloads.Count; w++)
        {
            string scenario = workloads[w].ScenarioName;
            Console.WriteLine();
            Console.WriteLine($"### {scenario}");
            Console.WriteLine("| Server Stack                 | Throughput (req/s) | Avg Latency (us) | P50 Latency (us) | P99 Latency (us) | Relative |");
            Console.WriteLine("| ---------------------------- | -----------------: | ---------------: | ---------------: | ---------------: | -------: |");

            double baselineRps = FindHayabusaRps(results, scenario);
            PrintScenarioRows(results, scenario, baselineRps);
        }

        Console.WriteLine("==========================================================================================================");
    }

    private static double FindHayabusaRps(IReadOnlyList<CrossLanguageScenarioResult> results, string scenario)
    {
        for (int i = 0; i < results.Count; i++)
        {
            if (string.Equals(results[i].ScenarioName, scenario, StringComparison.Ordinal))
            {
                return Math.Max(1.0, results[i].RequestsPerSecond);
            }
        }

        return 1.0;
    }

    private static void PrintScenarioRows(
        IReadOnlyList<CrossLanguageScenarioResult> results,
        string scenario,
        double hayabusaRps)
    {
        for (int i = 0; i < results.Count; i++)
        {
            CrossLanguageScenarioResult r = results[i];
            if (!string.Equals(r.ScenarioName, scenario, StringComparison.Ordinal))
            {
                continue;
            }

            double ratio = r.RequestsPerSecond / hayabusaRps;
            Console.WriteLine(
                $"| {r.ServerName,-28} | {r.RequestsPerSecond,18:N0} | {r.LatencyMicros.AvgMicros,16:F1} | {r.LatencyMicros.P50Micros,16:F1} | {r.LatencyMicros.P99Micros,16:F1} | {ratio,7:F2}x |");
        }
    }

    private sealed class EphemeralBenchmarkHarness : IDisposable
    {
        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hayabusa-crossbench-{Guid.NewGuid():N}");
        private readonly List<Process> _externalProcesses = new(2);
        private readonly CancellationTokenSource _hayabusaCts = new();
        private HayabusaApp? _inProcessHayabusa;
        private Task? _hayabusaRunTask;

        public async Task<IReadOnlyList<BenchmarkTargetServer>> ResolveOrStartServersAsync(
            CrossLanguageBenchmarkOptions options)
        {
            Directory.CreateDirectory(_tempDir);
            var list = new List<BenchmarkTargetServer>(3);

            BenchmarkTargetServer hayabusa = await ResolveHayabusaServerAsync(options.HayabusaEndpoint);
            list.Add(hayabusa);

            BenchmarkTargetServer? goServer = await ResolveGoServerAsync(options.GoEndpoint);
            if (goServer.HasValue)
            {
                list.Add(goServer.Value);
            }

            BenchmarkTargetServer? rustServer = await ResolveRustServerAsync(options.RustEndpoint);
            if (rustServer.HasValue)
            {
                list.Add(rustServer.Value);
            }

            return list;
        }

        public void Dispose()
        {
            _hayabusaCts.Cancel();
            try
            {
                _hayabusaRunTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Ignore cancellation exceptions on shutdown
            }

            _inProcessHayabusa?.Dispose();
            _hayabusaCts.Dispose();
            TerminateExternalProcesses();
            CleanupTempDirectory();
        }

        private async Task<BenchmarkTargetServer> ResolveHayabusaServerAsync(string? configuredUrl)
        {
            if (TryParseEndpoint("Hayabusa (.NET 9)", configuredUrl, out BenchmarkTargetServer external))
            {
                await WaitForEndpointReadyAsync(external);
                return external;
            }

            int port = AllocateFreePort();
            string sampleDll = typeof(Hayabusa.SampleServer.Program).Assembly.Location;
            if (!string.IsNullOrEmpty(sampleDll) && File.Exists(sampleDll))
            {
                StartBackgroundProcess(
                    "dotnet",
                    $"exec \"{sampleDll}\" --web-only --bind 127.0.0.1 --http-port {port.ToString(CultureInfo.InvariantCulture)}");
            }
            else
            {
                _inProcessHayabusa = Hayabusa.SampleServer.Program.CreateSampleApp(
                    ["--web-only", "--bind", "127.0.0.1", "--http-port", port.ToString(CultureInfo.InvariantCulture)]);
                _hayabusaRunTask = Task.Run(async () => await _inProcessHayabusa.RunAsync(_hayabusaCts.Token));
            }

            var local = new BenchmarkTargetServer("Hayabusa (.NET 9)", "127.0.0.1", port);
            await WaitForEndpointReadyAsync(local);
            return local;
        }

        private async Task<BenchmarkTargetServer?> ResolveGoServerAsync(string? configuredUrl)
        {
            if (TryParseEndpoint("Go (net/http)", configuredUrl, out BenchmarkTargetServer external))
            {
                await WaitForEndpointReadyAsync(external);
                return external;
            }

            return await TryBuildAndStartGoEphemeralAsync();
        }

        private async Task<BenchmarkTargetServer?> ResolveRustServerAsync(string? configuredUrl)
        {
            if (TryParseEndpoint("Rust (axum / tokio)", configuredUrl, out BenchmarkTargetServer external))
            {
                await WaitForEndpointReadyAsync(external);
                return external;
            }

            return await TryBuildAndStartRustEphemeralAsync();
        }

        private async Task<BenchmarkTargetServer?> TryBuildAndStartGoEphemeralAsync()
        {
            try
            {
                string srcPath = Path.Combine(_tempDir, "go_server.go");
                string binPath = Path.Combine(_tempDir, "go_server_bin");
                await File.WriteAllTextAsync(srcPath, GoStandardServerSource, Encoding.UTF8);

                if (!await RunCompilerAsync("go", $"build -ldflags=\"-s -w\" -o \"{binPath}\" \"{srcPath}\""))
                {
                    return null;
                }

                int port = AllocateFreePort();
                StartBackgroundProcess(binPath, $"127.0.0.1:{port}");
                var target = new BenchmarkTargetServer("Go (net/http)", "127.0.0.1", port);
                await WaitForEndpointReadyAsync(target);
                return target;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Info] Skipping local Go server ({ex.Message}). Use docker-compose.benchmarks.yml for full container run.");
                return null;
            }
        }

        private async Task<BenchmarkTargetServer?> TryBuildAndStartRustEphemeralAsync()
        {
            BenchmarkTargetServer? axumServer = await TryBuildAndStartRustAxumAsync();
            if (axumServer.HasValue)
            {
                return axumServer;
            }

            return await TryBuildAndStartRustStdFallbackAsync();
        }

        private async Task<BenchmarkTargetServer?> TryBuildAndStartRustAxumAsync()
        {
            try
            {
                string cargoDir = Path.Combine(_tempDir, "rust_axum");
                string srcDir = Path.Combine(cargoDir, "src");
                Directory.CreateDirectory(srcDir);
                await File.WriteAllTextAsync(Path.Combine(cargoDir, "Cargo.toml"), RustAxumCargoToml, Encoding.UTF8);
                await File.WriteAllTextAsync(Path.Combine(srcDir, "main.rs"), RustAxumMainSource, Encoding.UTF8);

                if (!await RunCompilerInDirAsync("cargo", "build --release --quiet", cargoDir))
                {
                    return null;
                }

                string binPath = Path.Combine(cargoDir, "target", "release", "rust_axum_bench");
                int port = AllocateFreePort();
                StartBackgroundProcess(binPath, $"127.0.0.1:{port}");
                var target = new BenchmarkTargetServer("Rust (axum / tokio)", "127.0.0.1", port);
                await WaitForEndpointReadyAsync(target);
                return target;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task<BenchmarkTargetServer?> TryBuildAndStartRustStdFallbackAsync()
        {
            try
            {
                string srcPath = Path.Combine(_tempDir, "rust_server.rs");
                string binPath = Path.Combine(_tempDir, "rust_server_bin");
                await File.WriteAllTextAsync(srcPath, RustStandardServerSource, Encoding.UTF8);

                if (!await RunCompilerInDirAsync("rustc", $"-C opt-level=3 -C debuginfo=0 -o \"{binPath}\" \"{srcPath}\"", _tempDir))
                {
                    return null;
                }

                int port = AllocateFreePort();
                StartBackgroundProcess(binPath, $"127.0.0.1:{port}");
                var target = new BenchmarkTargetServer("Rust (std/Release)", "127.0.0.1", port);
                await WaitForEndpointReadyAsync(target);
                return target;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Info] Skipping local Rust server ({ex.Message}). Use docker-compose.benchmarks.yml for full container run.");
                return null;
            }
        }

        private async Task<bool> RunCompilerAsync(string fileName, string arguments) =>
            await RunCompilerInDirAsync(fileName, arguments, _tempDir);

        private static async Task<bool> RunCompilerInDirAsync(string fileName, string arguments, string workingDirectory)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }

        private void StartBackgroundProcess(string binaryPath, string arguments)
        {
            var psi = new ProcessStartInfo(binaryPath, arguments)
            {
                WorkingDirectory = _tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            Process? proc = Process.Start(psi);
            if (proc is not null)
            {
                _externalProcesses.Add(proc);
            }
        }

        private static bool TryParseEndpoint(
            string name,
            string? rawUrl,
            out BenchmarkTargetServer endpoint)
        {
            endpoint = default;
            if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri))
            {
                return false;
            }

            int port = uri.Port > 0 ? uri.Port : 80;
            endpoint = new BenchmarkTargetServer(name, uri.Host, port);
            return true;
        }

        private static async Task WaitForEndpointReadyAsync(BenchmarkTargetServer server)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (await TryProbeOnceAsync(server))
                {
                    return;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"Server '{server.Name}' at {server.Host}:{server.Port} did not become ready in time.");
        }

        private static async Task<bool> TryProbeOnceAsync(BenchmarkTargetServer server)
        {
            try
            {
                using Socket socket = await ConnectSocketAsync(server);
                byte[] req = BuildGetRequest("/health");
                byte[] buf = new byte[256];
                await SendAllAsync(socket, req);
                int read = await socket.ReceiveAsync(buf, SocketFlags.None);
                return read > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int AllocateFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private void TerminateExternalProcesses()
        {
            for (int i = 0; i < _externalProcesses.Count; i++)
            {
                try
                {
                    if (!_externalProcesses[i].HasExited)
                    {
                        _externalProcesses[i].Kill(entireProcessTree: true);
                    }
                }
                catch (Exception)
                {
                    // Best-effort cleanup
                }
                finally
                {
                    _externalProcesses[i].Dispose();
                }
            }

            _externalProcesses.Clear();
        }

        private void CleanupTempDirectory()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup of /tmp directory
            }
        }
    }
}
