using DaoStudio.Bridge.Abstractions;

namespace DaoStudio.Bridge;

public sealed class BridgeTelemetry
{
    private long l0, l1, l2, misses, batches, failures, fallbacks, latencyTicks, samples;
    private int state;
    public void RecordHit(string tier) { switch (tier.ToUpperInvariant()) { case "L0": Interlocked.Increment(ref l0); break; case "L1": Interlocked.Increment(ref l1); break; case "L2": Interlocked.Increment(ref l2); break; } }
    public void RecordMiss() => Interlocked.Increment(ref misses);
    public void RecordBatch() => Interlocked.Increment(ref batches);
    public void RecordValidationFailure() => Interlocked.Increment(ref failures);
    public void RecordFallback() => Interlocked.Increment(ref fallbacks);
    public void RecordLatency(TimeSpan latency) { Interlocked.Add(ref latencyTicks, latency.Ticks); Interlocked.Increment(ref samples); }
    public void SetCircuitState(CircuitBreakerState value) => Volatile.Write(ref state, (int)value);
    public BridgeTelemetrySnapshot GetSnapshot()
    {
        long count = Interlocked.Read(ref samples);
        return new(Interlocked.Read(ref l0), Interlocked.Read(ref l1), Interlocked.Read(ref l2), Interlocked.Read(ref misses), Interlocked.Read(ref batches), Interlocked.Read(ref failures), Interlocked.Read(ref fallbacks), count == 0 ? 0 : (double)Interlocked.Read(ref latencyTicks) / TimeSpan.TicksPerMillisecond / count, (CircuitBreakerState)Volatile.Read(ref state));
    }
}
