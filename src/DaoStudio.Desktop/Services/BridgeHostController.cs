using DaoStudio.Bridge;
using DaoStudio.Bridge.Abstractions;

namespace DaoStudio.Desktop.Services;

public interface IBridgeHostController
{
    bool IsRunning { get; }
    int Port { get; }
    string TokenSummary { get; }
    Task StartAsync();
    Task StopAsync();
    BridgeTelemetrySnapshot GetSnapshot();
}

public sealed class BridgeHostController(BridgeTranslationOrchestrator orchestrator, BridgeTelemetry telemetry) : IBridgeHostController, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private BridgeServer? server;
    public bool IsRunning => server is not null;
    public int Port => server?.Port ?? 0;
    public string TokenSummary => server is null ? "—" : server.SessionToken[..8] + "…";
    public BridgeTelemetrySnapshot GetSnapshot() => telemetry.GetSnapshot();
    public async Task StartAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (server is not null) return;
            var next = new BridgeServer(orchestrator, telemetry, 0);
            try { await next.StartAsync(); server = next; }
            catch { await next.DisposeAsync(); throw; }
        }
        finally { gate.Release(); }
    }
    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try { if (server is not null) { await server.DisposeAsync(); server = null; } }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
