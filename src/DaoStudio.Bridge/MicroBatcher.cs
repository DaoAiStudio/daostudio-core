using System.Threading.Channels;
using DaoStudio.Bridge.Abstractions;
using DaoStudio.Contracts;
using DaoStudio.Engine.Abstractions;

namespace DaoStudio.Bridge;

public sealed class MicroBatcher : IAsyncDisposable
{
    private sealed record Flight(CacheKey Key, TranslationItem Item, TaskCompletionSource<TranslationResultItem?> Completion);
    private readonly Channel<Flight> channel = Channel.CreateUnbounded<Flight>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<CacheKey, Flight> flights = new();
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly ICloudTranslationClient cloud;
    private readonly TimeSpan window, timeout;
    private readonly int maxBatchSize;
    private readonly Task worker;
    private bool disposed;
    public MicroBatcher(ICloudTranslationClient cloud, TimeSpan? window = null, int maxBatchSize = 25, TimeSpan? batchTimeout = null)
    {
        this.cloud = cloud; this.window = window ?? TimeSpan.FromMilliseconds(65); timeout = batchTimeout ?? TimeSpan.FromSeconds(30);
        if (maxBatchSize is < 1 or > 25 || this.window < TimeSpan.Zero || timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxBatchSize));
        this.maxBatchSize = maxBatchSize; worker = RunAsync();
    }
    public async Task<TranslationResultItem?> TranslateAsync(CacheKey key, TranslationItem item, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return null;
        Flight flight;
        lock (gate)
        {
            if (disposed) return null;
            if (!flights.TryGetValue(key, out flight!))
            {
                flight = new(key, item, new(TaskCreationOptions.RunContinuationsAsynchronously));
                flights.Add(key, flight);
                channel.Writer.TryWrite(flight);
            }
        }
        try { return await flight.Completion.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
    }
    private async Task RunAsync()
    {
        try
        {
            while (await channel.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false))
            {
                var pending = new List<Flight> { await channel.Reader.ReadAsync(stop.Token) };
                using var windowStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                windowStop.CancelAfter(window);
                try
                {
                    while (pending.Count < maxBatchSize)
                    {
                        pending.Add(await channel.Reader.ReadAsync(windowStop.Token).ConfigureAwait(false));
                    }
                }
                catch (OperationCanceledException) { }
                foreach (var group in pending.GroupBy(f => (f.Key.GameId, f.Key.TargetLang, f.Key.GlossaryVersion)))
                {
                    TranslationBatchResponse? response = null;
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        deadline.CancelAfter(timeout);
                        response = await cloud.TranslateBatchAsync(new(group.Key.GameId, group.Key.TargetLang, group.Key.GlossaryVersion, group.Select(f => f.Item).ToArray()), deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception) { /* A failed batch completes every waiter with a safe miss. */ }
                    foreach (var flight in group)
                    {
                        var matches = response?.Items?.Where(i => i is not null && i.Hash == flight.Key.TemplateHash).ToArray();
                        var result = matches?.Length == 1 ? matches[0] : null;
                        lock (gate) { flight.Completion.TrySetResult(result); flights.Remove(flight.Key); }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        finally
        {
            lock (gate) { foreach (var flight in flights.Values) flight.Completion.TrySetResult(null); flights.Clear(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; disposed = true; channel.Writer.TryComplete(); stop.Cancel(); }
        await worker.ConfigureAwait(false);
        stop.Dispose();
    }
}

