using System.Diagnostics;
using DaoStudio.Contracts;
using DaoStudio.Engine.Abstractions;

namespace DaoStudio.Bridge;

public sealed class BridgeTranslationOrchestrator
{
    private readonly ITagMasker masker;
    private readonly IStringNormalizer normalizer;
    private readonly ITemplateHashCalculator hasher;
    private readonly ITranslationCache cache;
    private readonly MicroBatcher batcher;
    private readonly PostTranslationValidator validator;
    private readonly BridgeTelemetry telemetry;
    public BridgeTranslationOrchestrator(ITagMasker masker, IStringNormalizer normalizer, ITemplateHashCalculator hasher, ITranslationCache cache, MicroBatcher batcher, PostTranslationValidator validator, BridgeTelemetry telemetry)
    { this.masker = masker; this.normalizer = normalizer; this.hasher = hasher; this.cache = cache; this.batcher = batcher; this.validator = validator; this.telemetry = telemetry; }
    public async Task<BridgeTranslateResponse> TranslateAsync(BridgeTranslateRequest request, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        string hash = "";
        BridgeTranslateResponse Fallback(string error) { telemetry.RecordFallback(); return new(request.SourceText, request.SourceText, hash, "none", true, error); }
        try
        {
            ct.ThrowIfCancellationRequested();
            var masked = masker.Mask(request.SourceText);
            var template = normalizer.Normalize(masked.MaskedTemplate);
            hash = hasher.ComputeHash(template);
            var key = new CacheKey(request.GameId, request.TargetLang, hash, request.GlossaryVersion);
            var cached = await cache.TryGetAsync(key, ct);
            string? translated = cached.TranslatedTemplate;
            string tier = cached.Tier;
            if (cached.Hit) telemetry.RecordHit(tier);
            else
            {
                telemetry.RecordMiss();
                var result = await batcher.TranslateAsync(key, new(hash, template, request.UiHint, request.MaxChars), ct);
                translated = result?.TranslatedTemplate;
                tier = result?.FromCache == true ? "L2" : "cloud";
                if (result?.FromCache == true) telemetry.RecordHit("L2");
            }
            if (translated is null) return Fallback("Translation unavailable.");
            if (!validator.Validate(masked.MaskedTemplate, translated, out var reason)) { telemetry.RecordValidationFailure(); return Fallback(reason!); }
            ct.ThrowIfCancellationRequested();
            var text = masker.Unmask(translated, masked.Placeholders);
            if (!cached.Hit) await cache.SetAsync(key, translated, ct);
            return new(request.SourceText, text, hash, tier, false);
        }
        catch (Exception) { return Fallback("Translation failed or cancelled."); }
        finally { telemetry.RecordLatency(Stopwatch.GetElapsedTime(started)); }
    }
}
