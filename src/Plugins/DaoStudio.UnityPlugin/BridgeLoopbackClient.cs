using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using DaoStudio.Contracts;

namespace DaoStudio.UnityPlugin;

public sealed record BridgeSession(int Port, string Token)
{
    public static BridgeSession? Load(string? sessionFile = null)
    {
        try
        {
            var port = Environment.GetEnvironmentVariable("DAOSTUDIO_BRIDGE_PORT");
            var token = Environment.GetEnvironmentVariable("DAOSTUDIO_BRIDGE_TOKEN");
            if (port != null || token != null)
                return int.TryParse(port, out var number) && number is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(token) ? new(number, token) : null;
            if (sessionFile == null) return null;
            var session = JsonSerializer.Deserialize<BridgeSession>(File.ReadAllText(sessionFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return session is { Port: > 0 and <= 65535 } && !string.IsNullOrWhiteSpace(session.Token) ? session : null;
        }
        catch { return null; }
    }
}

public sealed class BridgeLoopbackClient
{
    private readonly HttpClient client;
    private readonly BridgeSession? session;
    private readonly TimeSpan timeout;
    private readonly ConcurrentDictionary<BridgeTranslateRequest, string> cache = new();
    public BridgeLoopbackClient(HttpClient client, string? sessionFile = null, TimeSpan? timeout = null)
        : this(client, BridgeSession.Load(sessionFile), timeout) { }
    public BridgeLoopbackClient(HttpClient client, BridgeSession? session, TimeSpan? timeout = null)
    {
        this.client = client;
        this.session = session;
        this.timeout = timeout ?? TimeSpan.FromMilliseconds(1000);
    }
    public int CachedCount => cache.Count;

    public async Task<string> TranslateAsync(string text, string gameId = "guigubahuang", string targetLang = "pt-BR", int glossaryVersion = 1, UiHint? hint = null, int? maxChars = null, CancellationToken ct = default)
    {
        if (!TextInterceptorFilter.ShouldTranslate(text) || session is not { Port: > 0 and <= 65535 } || string.IsNullOrWhiteSpace(session.Token)) return text;
        var uiHint = (hint ?? TextInterceptorFilter.InferUiHint(text)) switch
        {
            UiHint.Button => "button", UiHint.Tooltip => "tooltip", UiHint.ItemName => "item_name", UiHint.SystemMessage => "system_message", _ => "dialogue"
        };
        var payload = new BridgeTranslateRequest(gameId, text, targetLang, glossaryVersion, uiHint, maxChars);
        if (cache.TryGetValue(payload, out var cached)) return cached;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cancellation.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{session.Port}/bridge/translate") { Content = JsonContent.Create(payload) };
            request.Headers.Add("X-Bridge-Token", session.Token);
            using var response = await client.SendAsync(request, cancellation.Token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<BridgeTranslateResponse>(cancellationToken: cancellation.Token);
            if (result == null || result.FallbackUsed || result.Error != null || result.SourceText != text || string.IsNullOrWhiteSpace(result.TranslatedText)) return text;
            cache[payload] = result.TranslatedText;
            return result.TranslatedText;
        }
        catch { return text; }
    }
}
