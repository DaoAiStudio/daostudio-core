using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DaoStudio.Bridge.Abstractions;
using DaoStudio.Contracts;

namespace DaoStudio.Bridge;

public sealed class CloudTranslationClientOptions
{
    public int MaxRetries { get; init; } = 2;
    public int FailureThreshold { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed class CloudTranslationClient : ICloudTranslationClient
{
    private readonly HttpClient http;
    private readonly string jwt, hwid;
    private readonly BridgeTelemetry telemetry;
    private readonly CloudTranslationClientOptions options;
    private readonly object gate = new();
    private CircuitBreakerState state;
    private int failures;
    private long openedAt;
    public CircuitBreakerState CircuitState { get { lock (gate) return state; } }
    public CloudTranslationClient(HttpClient http, string jwt, string hwidHash, BridgeTelemetry telemetry, CloudTranslationClientOptions? options = null)
    {
        this.http = http; this.jwt = jwt; hwid = hwidHash; this.telemetry = telemetry; this.options = options ?? new();
        if (this.options.FailureThreshold < 1 || this.options.MaxRetries < 0 || this.options.RequestTimeout <= TimeSpan.Zero || this.options.Cooldown < TimeSpan.Zero || this.options.RetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
    }
    private void SetState(CircuitBreakerState value) { state = value; telemetry.SetCircuitState(value); }
    public async Task<TranslationBatchResponse> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct = default)
    {
        TranslationBatchResponse Empty() => new(request.GameId, request.TargetLang, Array.Empty<TranslationResultItem>());
        bool probe;
        lock (gate)
        {
            if (ct.IsCancellationRequested || state == CircuitBreakerState.HalfOpen) return Empty();
            if (state == CircuitBreakerState.Open)
            {
                if (System.Diagnostics.Stopwatch.GetElapsedTime(openedAt) < options.Cooldown) return Empty();
                SetState(CircuitBreakerState.HalfOpen);
            }
            probe = state == CircuitBreakerState.HalfOpen;
        }
        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(options.RequestTimeout);
                using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/translate/batch");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                message.Headers.Add("X-HWID-Hash", hwid);
                message.Content = JsonContent.Create(request);
                telemetry.RecordBatch();
                using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<TranslationBatchResponse>(cancellationToken: timeout.Token).ConfigureAwait(false);
                if (result?.Items is null || result.GameId != request.GameId || result.TargetLang != request.TargetLang) throw new JsonException("Invalid batch response.");
                lock (gate) { if (probe || state == CircuitBreakerState.Closed) { failures = 0; SetState(CircuitBreakerState.Closed); } }
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException)
            {
                lock (gate)
                {
                    if (!ct.IsCancellationRequested) failures++;
                    if (probe || failures >= options.FailureThreshold) { openedAt = System.Diagnostics.Stopwatch.GetTimestamp(); SetState(CircuitBreakerState.Open); }
                    if (ct.IsCancellationRequested || state != CircuitBreakerState.Closed) return Empty();
                }
                if (attempt == options.MaxRetries) return Empty();
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(30000, options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt))), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return Empty(); }
            }
        }
        return Empty();
    }
}

