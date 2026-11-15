using DaoStudio.Contracts;

namespace DaoStudio.Bridge.Abstractions;

public enum CircuitBreakerState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2
}

public sealed record BridgeTelemetrySnapshot(
    long L0Hits,
    long L1Hits,
    long L2Hits,
    long Misses,
    long BatchRequestsSent,
    long ValidationFailures,
    long FallbackCount,
    double AverageLatencyMs,
    CircuitBreakerState CircuitState);

public interface ICloudTranslationClient
{
    Task<TranslationBatchResponse> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct = default);
    CircuitBreakerState CircuitState { get; }
}
