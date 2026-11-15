using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using DaoStudio.Contracts;

namespace DaoStudio.Bridge;

/// <summary>Loopback HTTP/1.1 server. One bounded request per connection; responses close the connection.</summary>
public sealed class BridgeServer : IAsyncDisposable, IDisposable
{
    private readonly TcpListener listener;
    private readonly BridgeTranslationOrchestrator orchestrator;
    private readonly BridgeTelemetry telemetry;
    private readonly CancellationTokenSource stop = new();
    private readonly HashSet<Task> connections = new();
    private readonly object gate = new();
    private Task? acceptLoop, disposal;
    private bool started, disposed;
    public int Port { get; private set; }
    public string SessionToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public BridgeServer(BridgeTranslationOrchestrator orchestrator, BridgeTelemetry telemetry, int port = 0)
    { this.orchestrator = orchestrator; this.telemetry = telemetry; listener = new(IPAddress.Loopback, port); }
    public Task StartAsync(CancellationToken ct = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); ct.ThrowIfCancellationRequested();
            if (started) return Task.CompletedTask;
            listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; started = true; acceptLoop = AcceptAsync();
            return Task.CompletedTask;
        }
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                var task = HandleAsync(client);
                lock (gate) connections.Add(task);
                _ = task.ContinueWith(t => { lock (gate) connections.Remove(t); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            var ct = deadline.Token;
            try
            {
                var stream = client.GetStream();
                using var header = new MemoryStream();
                var single = new byte[1];
                int tail = 0;
                while (header.Length < 16384)
                {
                    if (await stream.ReadAsync(single, ct) == 0) return;
                    header.WriteByte(single[0]); tail = (tail << 8) | single[0];
                    if (tail == 0x0d0a0d0a) break;
                }
                if (tail != 0x0d0a0d0a) { await Respond(stream, 431, new { error = "Headers too large" }, ct); return; }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var first = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) { await Respond(stream, 400, new { error = "Invalid headers" }, ct); return; }
                }
                if (!headers.TryGetValue("X-Bridge-Token", out var token) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(SessionToken)))
                { await Respond(stream, 401, new { error = "Unauthorized" }, ct); return; }
                if (first.Length != 3 || first[2] != "HTTP/1.1") { await Respond(stream, 400, new { error = "Invalid request framing" }, ct); return; }
                if (first[0] == "GET" && first[1] == "/bridge/status") { await Respond(stream, 200, telemetry.GetSnapshot(), ct); return; }
                if (first[0] != "POST" || first[1] != "/bridge/translate") { await Respond(stream, 404, new { error = "Not found" }, ct); return; }
                bool chunked = headers.TryGetValue("Transfer-Encoding", out var transfer);
                int length = 0;
                if ((chunked && (!string.Equals(transfer, "chunked", StringComparison.OrdinalIgnoreCase) || headers.ContainsKey("Content-Length"))) ||
                    (!chunked && (!headers.TryGetValue("Content-Length", out var lengthText) || !int.TryParse(lengthText, out length) || length < 0)))
                { await Respond(stream, 400, new { error = "Invalid body framing" }, ct); return; }
                if (length > 1024 * 1024) { await Respond(stream, 413, new { error = "Body too large" }, ct); return; }
                if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase)) await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), ct);
                byte[] body;
                try
                {
                    if (chunked) body = await ReadChunksAsync(stream, ct);
                    else { body = new byte[length]; await stream.ReadExactlyAsync(body, ct); }
                }
                catch (InvalidDataException) { await Respond(stream, 400, new { error = "Invalid chunked body" }, ct); return; }
                BridgeTranslateRequest? request;
                try { request = JsonSerializer.Deserialize<BridgeTranslateRequest>(body); }
                catch (JsonException) { await Respond(stream, 400, new { error = "Invalid JSON" }, ct); return; }
                if (request is null || string.IsNullOrWhiteSpace(request.GameId) || request.SourceText is null || string.IsNullOrWhiteSpace(request.TargetLang)) { await Respond(stream, 400, new { error = "Invalid translation request" }, ct); return; }
                await Respond(stream, 200, await orchestrator.TranslateAsync(request, ct).WaitAsync(ct), ct);
            }
            catch (Exception) { /* Disconnects, deadlines and shutdown must not fault the accept loop. */ }
            finally
            {
                // Send FIN before draining unread request bytes, avoiding a Windows TCP reset
                // that could discard an early 401/400 response on the client.
                try
                {
                    client.Client.Shutdown(SocketShutdown.Send);
                    using var drain = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                    var buffer = new byte[4096];
                    while (await client.GetStream().ReadAsync(buffer, drain.Token) != 0) { }
                }
                catch (Exception) { }
            }
        }
    }
    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        using var line = new MemoryStream();
        var b = new byte[1];
        while (line.Length < 4096)
        {
            await stream.ReadExactlyAsync(b, ct);
            if (b[0] == 10)
            {
                var bytes = line.ToArray();
                if (bytes.Length == 0 || bytes[^1] != 13) throw new InvalidDataException();
                return Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1);
            }
            line.WriteByte(b[0]);
        }
        throw new InvalidDataException();
    }
    private static async Task<byte[]> ReadChunksAsync(NetworkStream stream, CancellationToken ct)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var line = await ReadLineAsync(stream, ct);
            if (!int.TryParse(line.Split(';')[0], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out int size) || size < 0 || body.Length + size > 1024 * 1024) throw new InvalidDataException();
            if (size == 0)
            {
                int trailerBytes = 0;
                while ((line = await ReadLineAsync(stream, ct)).Length != 0)
                    if ((trailerBytes += line.Length + 2) > 16384) throw new InvalidDataException();
                return body.ToArray();
            }
            var chunk = new byte[size];
            await stream.ReadExactlyAsync(chunk, ct); body.Write(chunk);
            if ((await ReadLineAsync(stream, ct)).Length != 0) throw new InvalidDataException();
        }
    }
    private static async Task Respond(NetworkStream stream, int status, object value, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 413 => "Content Too Large", 431 => "Request Header Fields Too Large", _ => "Error" };
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct); await stream.WriteAsync(body, ct);
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) { return new(disposal ??= DisposeCoreAsync()); }
    }
    private async Task DisposeCoreAsync()
    {
        disposed = true; stop.Cancel(); listener.Stop();
        if (acceptLoop is not null) await acceptLoop.ConfigureAwait(false);
        Task[] active; lock (gate) active = connections.ToArray();
        await Task.WhenAll(active).ConfigureAwait(false); stop.Dispose();
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

