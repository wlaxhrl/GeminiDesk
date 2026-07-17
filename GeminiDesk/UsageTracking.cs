using Google.GenAI.Types;

namespace GeminiDesk;

internal sealed record AiRequestUsage(
    long InputTokens = 0,
    long CachedInputTokens = 0,
    long CacheWriteInputTokens = 0,
    long OutputTokens = 0,
    long ImageInputTokens = 0,
    long CachedImageInputTokens = 0,
    long ImageOutputTokens = 0,
    int SearchQueries = 0,
    int GeneratedImages = 0);

internal sealed record UsageRecord(
    long Id,
    DateTime OccurredAtUtc,
    string Provider,
    string ModelId,
    string ModelDisplayName,
    AiRequestUsage Usage,
    double EstimatedCostUsd,
    double UsdToKrw,
    double EstimatedCostKrw,
    string PricingVersion);

internal static class UsageMetadataMapper
{
    public static AiRequestUsage FromGoogle(
        GenerateContentResponse response,
        int generatedImages = 0)
    {
        var metadata = response.UsageMetadata;
        var searchQueries = GetGoogleSearchQueries(response).Count;

        if (metadata is null)
        {
            return new AiRequestUsage(
                SearchQueries: searchQueries,
                GeneratedImages: generatedImages);
        }

        var inputTokens = (long)(metadata.PromptTokenCount ?? 0) +
                          (metadata.ToolUsePromptTokenCount ?? 0);
        var outputTokens = (long)(metadata.CandidatesTokenCount ?? 0) +
                           (metadata.ThoughtsTokenCount ?? 0);
        var imageInputTokens = GetImageTokens(metadata.PromptTokensDetails) +
                               GetImageTokens(metadata.ToolUsePromptTokensDetails);

        return new AiRequestUsage(
            InputTokens: inputTokens,
            CachedInputTokens: metadata.CachedContentTokenCount ?? 0,
            OutputTokens: outputTokens,
            ImageInputTokens: imageInputTokens,
            ImageOutputTokens: GetImageTokens(metadata.CandidatesTokensDetails),
            SearchQueries: searchQueries,
            GeneratedImages: generatedImages);
    }

    public static IReadOnlySet<string> GetGoogleSearchQueries(GenerateContentResponse response)
    {
        return response.Candidates?
            .SelectMany(candidate =>
                (candidate.GroundingMetadata?.WebSearchQueries ?? [])
                .Concat(candidate.GroundingMetadata?.ImageSearchQueries ?? []))
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
    }

    private static long GetImageTokens(IEnumerable<ModalityTokenCount>? details)
    {
        return details?
            .Where(detail => detail.Modality == MediaModality.Image)
            .Sum(detail => (long)(detail.TokenCount ?? 0)) ?? 0;
    }
}

internal static class UsagePriceCalculator
{
    public const double FallbackUsdToKrw = 1400d;
    public const string PricingVersion = "2026-07-17";

    public static UsageRecord CreateRecord(
        AiModelOption model,
        AiRequestUsage usage,
        double usdToKrw = FallbackUsdToKrw)
    {
        if (!double.IsFinite(usdToKrw) || usdToKrw <= 0)
        {
            usdToKrw = FallbackUsdToKrw;
        }

        var estimatedUsd = Math.Max(0, CalculateUsd(model, usage));
        return new UsageRecord(
            0,
            DateTime.UtcNow,
            model.Provider,
            model.Id,
            model.DisplayName,
            usage,
            estimatedUsd,
            usdToKrw,
            estimatedUsd * usdToKrw,
            PricingVersion);
    }

    private static double CalculateUsd(AiModelOption model, AiRequestUsage usage)
    {
        return model.Provider switch
        {
            ModelProvider.Google => CalculateGoogle(model, usage),
            ModelProvider.OpenAi => CalculateOpenAi(model, usage),
            ModelProvider.Anthropic => CalculateAnthropic(model, usage),
            _ => 0
        };
    }

    private static double CalculateGoogle(AiModelOption model, AiRequestUsage usage)
    {
        if (model.Id == "gemini-3.1-flash-image")
        {
            var imageOutputTokens = usage.ImageOutputTokens > 0
                ? usage.ImageOutputTokens
                : usage.GeneratedImages * 1120L;
            var textOutputTokens = Math.Max(0, usage.OutputTokens - imageOutputTokens);
            return PerMillion(usage.InputTokens, 0.50) +
                   PerMillion(textOutputTokens, 3.00) +
                   PerMillion(imageOutputTokens, 60.00) +
                   usage.SearchQueries * 0.014;
        }

        var isLongGeminiPro = model.Id == "gemini-3.1-pro-preview" && usage.InputTokens > 200_000;
        var (inputRate, cachedRate, outputRate) = model.Id switch
        {
            "gemini-3.5-flash" => (1.50, 0.15, 9.00),
            "gemini-3.1-pro-preview" when isLongGeminiPro => (4.00, 0.40, 18.00),
            "gemini-3.1-pro-preview" => (2.00, 0.20, 12.00),
            _ => (0d, 0d, 0d)
        };

        return CalculateTextTokens(usage, inputRate, cachedRate, inputRate * 1.25, outputRate) +
               usage.SearchQueries * 0.014;
    }

    private static double CalculateOpenAi(AiModelOption model, AiRequestUsage usage)
    {
        if (model.Id == "gpt-image-2")
        {
            var imageInputTokens = Math.Min(usage.InputTokens, usage.ImageInputTokens);
            var cachedImageTokens = Math.Min(imageInputTokens, usage.CachedImageInputTokens);
            var textInputTokens = Math.Max(0, usage.InputTokens - imageInputTokens);
            var cachedTextTokens = Math.Min(
                textInputTokens,
                Math.Max(0, usage.CachedInputTokens - cachedImageTokens));
            var imageOutputTokens = usage.ImageOutputTokens > 0
                ? usage.ImageOutputTokens
                : usage.OutputTokens;

            return PerMillion(textInputTokens - cachedTextTokens, 5.00) +
                   PerMillion(cachedTextTokens, 1.25) +
                   PerMillion(imageInputTokens - cachedImageTokens, 8.00) +
                   PerMillion(cachedImageTokens, 2.00) +
                   PerMillion(imageOutputTokens, 30.00);
        }

        var isFlex = string.Equals(model.ServiceTier, "flex", StringComparison.Ordinal);
        var (inputRate, cachedRate, outputRate) = model.Id switch
        {
            "gpt-5.6-luna" => (1.00, 0.10, 6.00),
            "gpt-5.6-terra" => (2.50, 0.25, 15.00),
            "gpt-5.6-sol-standard" => (5.00, 0.50, 30.00),
            "gpt-5.6-sol-flex" => (2.50, 0.25, 15.00),
            _ => (0d, 0d, 0d)
        };

        if (isFlex && model.Id is not "gpt-5.6-sol-flex")
        {
            inputRate *= 0.5;
            cachedRate *= 0.5;
            outputRate *= 0.5;
        }

        if (usage.InputTokens > 272_000)
        {
            inputRate *= 2;
            cachedRate *= 2;
            outputRate *= 1.5;
        }

        return CalculateTextTokens(usage, inputRate, cachedRate, inputRate * 1.25, outputRate) +
               usage.SearchQueries * 0.01;
    }

    private static double CalculateAnthropic(AiModelOption model, AiRequestUsage usage)
    {
        if (model.Id != "claude-opus-4-6")
        {
            return 0;
        }

        return CalculateTextTokens(usage, 5.00, 0.50, 6.25, 25.00);
    }

    private static double CalculateTextTokens(
        AiRequestUsage usage,
        double inputRate,
        double cachedInputRate,
        double cacheWriteRate,
        double outputRate)
    {
        var cached = Math.Min(usage.InputTokens, usage.CachedInputTokens);
        var cacheWrite = Math.Min(
            Math.Max(0, usage.InputTokens - cached),
            usage.CacheWriteInputTokens);
        var uncached = Math.Max(0, usage.InputTokens - cached - cacheWrite);

        return PerMillion(uncached, inputRate) +
               PerMillion(cached, cachedInputRate) +
               PerMillion(cacheWrite, cacheWriteRate) +
               PerMillion(usage.OutputTokens, outputRate);
    }

    private static double PerMillion(long tokens, double rate)
    {
        return Math.Max(0, tokens) / 1_000_000d * rate;
    }
}
