using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using DaoStudio.Bridge.Abstractions;
using DaoStudio.Contracts;
using DaoStudio.Engine.Abstractions;
using Xunit;

namespace DaoStudio.Bridge.Tests;

public sealed class BridgeTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public ConcurrentQueue<TranslationBatchRequest> Batches { get; } = new();
        public Func<TranslationBatchRequest, CancellationToken, Task<HttpResponseMessage>>? Handler { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/translate/batch", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("jwt", request.Headers.Authorization.Parameter);
            Assert.Equal("hwid", request.Headers.GetValues("X-HWID-Hash").Single());
            var batch = (await request.Content!.ReadFromJsonAsync<TranslationBatchRequest>(cancellationToken: cancellationToken))!;
            Batches.Enqueue(batch);
            return Handler is null ? Success(batch) : await Handler(batch, cancellationToken);
        }
    }
    private static HttpResponseMessage Success(TranslationBatchRequest batch) => new(HttpStatusCode.OK) { Content = JsonContent.Create(new TranslationBatchResponse(batch.GameId, batch.TargetLang, batch.Items.Select(i => new TranslationResultItem(i.Hash, "PT " + i.Template, true)).ToArray())) };
    private sealed class EngineStub : ITagMasker, IStringNormalizer, ITemplateHashCalculator, ITranslationCache
    {
        public ConcurrentDictionary<CacheKey, string> Cache { get; } = new();
        public MaskedTextResult Mask(string input, bool maskStandaloneNumbers = false) => new(input.Replace("<name>", "{{VAR_0}}"), new Dictionary<string, string> { ["{{VAR_0}}"] = "<name>" });
        public string Unmask(string translatedTemplate, IReadOnlyDictionary<string, string> placeholders) => translatedTemplate.Replace("{{VAR_0}}", "<name>");
        public bool ValidateMaskIntegrity(string maskedTemplate, string translatedTemplate, IReadOnlyDictionary<string, string> placeholders, out string? errorReason) => new PostTranslationValidator().Validate(maskedTemplate, translatedTemplate, out errorReason);
        public string Normalize(string input) => input.Trim();
        public string ComputeHash(string normalizedTemplate) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedTemplate)));
        public ValueTask<CacheLookupResult> TryGetAsync(CacheKey key, CancellationToken ct = default) => ValueTask.FromResult(Cache.TryGetValue(key, out var value) ? new CacheLookupResult(true, value, "L1") : new(false, null, "none"));
        public ValueTask SetAsync(CacheKey key, string translatedTemplate, CancellationToken ct = default) { Cache[key] = translatedTemplate; return ValueTask.CompletedTask; }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public MockHttpMessageHandler Handler { get; } = new();
        public BridgeTelemetry Telemetry { get; } = new();
        public EngineStub Engine { get; } = new();
        public HttpClient Http { get; }
        public CloudTranslationClient Cloud { get; }
        public MicroBatcher Batcher { get; }
        public BridgeTranslationOrchestrator Orchestrator { get; }
        public Rig(CloudTranslationClientOptions? options = null)
        {
            Http = new(Handler) { BaseAddress = new Uri("https://test.invalid") };
            Cloud = new(Http, "jwt", "hwid", Telemetry, options);
            Batcher = new(Cloud, TimeSpan.FromMilliseconds(70));
            Orchestrator = new(Engine, Engine, Engine, Engine, Batcher, new(), Telemetry);
        }
        public async ValueTask DisposeAsync() { await Batcher.DisposeAsync(); Http.Dispose(); }
    }
    [Fact]
    public async Task BurstOf80CoalescesDuplicatesIntoThreeBatches()
    {
        await using var rig = new Rig();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Handler = async (b, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return Success(b); };
        var tasks = Enumerable.Range(0, 80).Select(i => rig.Orchestrator.TranslateAsync(new("game", $"text {i % 60} <name>"))).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, r => { Assert.False(r.FallbackUsed); Assert.Equal("PT " + r.SourceText, r.TranslatedText); });
        Assert.Equal(3, rig.Handler.Batches.Count);
        Assert.All(rig.Handler.Batches, b => Assert.InRange(b.Items.Count, 1, 25));
        var hashes = rig.Handler.Batches.SelectMany(b => b.Items).Select(i => i.Hash).ToArray();
        Assert.Equal(60, hashes.Length); Assert.Equal(60, hashes.Distinct().Count());
        Assert.Equal(60, rig.Engine.Cache.Count);
        Assert.Equal(3, rig.Telemetry.GetSnapshot().BatchRequestsSent);
        Assert.False((await rig.Orchestrator.TranslateAsync(new("game", "text 0 <name>"))).FallbackUsed);
        Assert.Equal(1, rig.Telemetry.GetSnapshot().L1Hits);
    }
    [Theory]
    [InlineData("{{VAR_0}} {{VAR_0}}", "{{VAR_0}}", false)]
    [InlineData("{{TAG_1}} {{NUM_2}}", "{{TAG_1}} {{NUM_3}}", false)]
    [InlineData("hello", "hello {{VAR_0}}", false)]
    [InlineData("{{VAR_0}} {{TAG_1}} {{NUM_2}}", "{{NUM_2}} {{VAR_0}} {{TAG_1}}", true)]
    public void ValidatorEnforcesExactMultiset(string source, string translation, bool valid) => Assert.Equal(valid, new PostTranslationValidator().Validate(source, translation));
    [Fact]
    public async Task CorruptPlaceholderFallsBackAndNeverCaches()
    {
        await using var rig = new Rig();
        rig.Handler.Handler = (b, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new TranslationBatchResponse(b.GameId, b.TargetLang, b.Items.Select(i => new TranslationResultItem(i.Hash, "corrupted")).ToArray())) });
        var result = await rig.Orchestrator.TranslateAsync(new("game", "Hello <name>"));
        Assert.True(result.FallbackUsed); Assert.Equal(result.SourceText, result.TranslatedText); Assert.Empty(rig.Engine.Cache);
        Assert.Equal(1, rig.Telemetry.GetSnapshot().ValidationFailures);
    }
    [Fact]
    public async Task CircuitOpensRejectsThenAllowsOnlyOneHalfOpenProbe()
    {
        await using var rig = new Rig(new() { FailureThreshold = 2, MaxRetries = 1, RetryDelay = TimeSpan.FromMilliseconds(1), Cooldown = TimeSpan.FromMilliseconds(60) });
        rig.Handler.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var request = new TranslationBatchRequest("game", "pt-BR", 1, new[] { new TranslationItem("hash", "text") });
        Assert.Empty((await rig.Cloud.TranslateBatchAsync(request)).Items);
        Assert.Equal(CircuitBreakerState.Open, rig.Cloud.CircuitState);
        Assert.Empty((await rig.Cloud.TranslateBatchAsync(request)).Items); Assert.Equal(2, rig.Handler.Batches.Count);
        await Task.Delay(90);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Handler = async (b, ct) => { await release.Task.WaitAsync(ct); return Success(b); };
        var probe = rig.Cloud.TranslateBatchAsync(request);
        Assert.Equal(CircuitBreakerState.HalfOpen, rig.Cloud.CircuitState);
        Assert.Empty((await rig.Cloud.TranslateBatchAsync(request)).Items); Assert.Equal(3, rig.Handler.Batches.Count);
        release.SetResult(); Assert.Single((await probe).Items);
        Assert.Equal(CircuitBreakerState.Closed, rig.Cloud.CircuitState);
        Assert.Equal(CircuitBreakerState.Closed, rig.Telemetry.GetSnapshot().CircuitState);
    }
    [Fact]
    public async Task TimeoutsAndCancelledWaitersFallBackWithoutCancellingSharedWork()
    {
        await using var rig = new Rig(new() { RequestTimeout = TimeSpan.FromMilliseconds(30), MaxRetries = 0 });
        rig.Handler.Handler = async (b, ct) => { await Task.Delay(1000, ct); return Success(b); };
        var result = await rig.Orchestrator.TranslateAsync(new("game", "timeout"));
        Assert.True(result.FallbackUsed); Assert.Equal("timeout", result.TranslatedText);
        rig.Handler.Handler = null;
        using var cancelled = new CancellationTokenSource();
        var one = rig.Orchestrator.TranslateAsync(new("game", "same"), cancelled.Token);
        var two = rig.Orchestrator.TranslateAsync(new("game", "same"));
        cancelled.Cancel();
        Assert.True((await one).FallbackUsed); Assert.False((await two).FallbackUsed);
        Assert.Equal(2, rig.Handler.Batches.Count);
    }
    [Fact]
    public async Task ServerAuthenticatesAndTranslatesOnRealEphemeralLoopbackPort()
    {
        await using var rig = new Rig();
        await using var server = new BridgeServer(rig.Orchestrator, rig.Telemetry);
        await server.StartAsync(); Assert.InRange(server.Port, 1, 65535);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        using var unauthorized = await http.GetAsync("/bridge/status"); Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        http.DefaultRequestHeaders.Add("X-Bridge-Token", "wrong");
        using var wrong = await http.PostAsJsonAsync("/bridge/translate", new BridgeTranslateRequest("game", "hi")); Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        http.DefaultRequestHeaders.Remove("X-Bridge-Token"); http.DefaultRequestHeaders.Add("X-Bridge-Token", server.SessionToken);
        using var status = await http.GetAsync("/bridge/status"); Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.NotNull(await status.Content.ReadFromJsonAsync<BridgeTelemetrySnapshot>());
        using var translated = await http.PostAsJsonAsync("/bridge/translate", new BridgeTranslateRequest("game", "hi <name>"));
        Assert.Equal(HttpStatusCode.OK, translated.StatusCode);
        Assert.Equal("PT hi <name>", (await translated.Content.ReadFromJsonAsync<BridgeTranslateResponse>())!.TranslatedText);
        using var malformed = await http.PostAsync("/bridge/translate", new StringContent("{")); Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }
    [Fact]
    public async Task HalfOpenFailureReopensAndOrchestratorFallsBackWhileOpen()
    {
        await using var rig = new Rig(new() { FailureThreshold = 1, MaxRetries = 0, Cooldown = TimeSpan.FromMilliseconds(150) });
        rig.Handler.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        var request = new TranslationBatchRequest("game", "pt-BR", 1, new[] { new TranslationItem("hash", "text") });
        await rig.Cloud.TranslateBatchAsync(request);
        Assert.Equal(CircuitBreakerState.Open, rig.Cloud.CircuitState);
        var result = await rig.Orchestrator.TranslateAsync(new("game", "original"));
        Assert.True(result.FallbackUsed); Assert.Equal("original", result.TranslatedText); Assert.Single(rig.Handler.Batches);
        await Task.Delay(180);
        await rig.Cloud.TranslateBatchAsync(request);
        Assert.Equal(CircuitBreakerState.Open, rig.Cloud.CircuitState); Assert.Equal(2, rig.Handler.Batches.Count);
    }
    [Fact]
    public async Task BatchesKeepGameLanguageAndGlossaryContextsSeparate()
    {
        await using var rig = new Rig();
        var requests = new[] { new BridgeTranslateRequest("a", "same"), new("b", "same"), new("a", "same", "es"), new("a", "same", "pt-BR", 2) };
        var results = await Task.WhenAll(requests.Select(r => rig.Orchestrator.TranslateAsync(r)));
        Assert.All(results, r => Assert.False(r.FallbackUsed));
        Assert.Equal(4, rig.Handler.Batches.Count); Assert.Equal(4, rig.Engine.Cache.Count);
        Assert.All(rig.Handler.Batches, b => Assert.Single(b.Items));
    }
    [Fact]
    public async Task DisposalReleasesPendingWaitersWithFallback()
    {
        await using var rig = new Rig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Handler = async (b, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Success(b); };
        var pending = rig.Orchestrator.TranslateAsync(new("game", "pending"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await rig.Batcher.DisposeAsync();
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(3))).FallbackUsed);
    }
    [Fact]
    public async Task TransportFailureRetriesAndSuccessResetsFailureCounter()
    {
        await using var rig = new Rig(new() { FailureThreshold = 2, MaxRetries = 1, RetryDelay = TimeSpan.FromMilliseconds(1) });
        int attempts = 0;
        rig.Handler.Handler = (b, _) => Interlocked.Increment(ref attempts) == 1
            ? Task.FromException<HttpResponseMessage>(new IOException("Broken response stream"))
            : Task.FromResult(Success(b));
        var result = await rig.Orchestrator.TranslateAsync(new("game", "hello"));
        Assert.False(result.FallbackUsed); Assert.Equal(2, attempts);
        Assert.Equal(CircuitBreakerState.Closed, rig.Cloud.CircuitState);
    }
    [Fact]
    public void TelemetryCountersAreThreadSafe()
    {
        var t = new BridgeTelemetry();
        Parallel.For(0, 1000, _ => { t.RecordHit("L0"); t.RecordHit("L1"); t.RecordHit("L2"); t.RecordMiss(); t.RecordBatch(); t.RecordValidationFailure(); t.RecordFallback(); t.RecordLatency(TimeSpan.FromMilliseconds(5)); });
        var s = t.GetSnapshot(); Assert.Equal(1000, s.L0Hits); Assert.Equal(1000, s.L1Hits); Assert.Equal(1000, s.L2Hits); Assert.Equal(1000, s.Misses); Assert.Equal(1000, s.BatchRequestsSent); Assert.Equal(1000, s.ValidationFailures); Assert.Equal(1000, s.FallbackCount); Assert.Equal(5, s.AverageLatencyMs);
    }
}


