using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GeminiDesk;

public static class ModelProvider
{
    public const string Google = "google";
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
}

public static class ModelOutputKind
{
    public const string Text = "text";
    public const string Image = "image";
}

public sealed record AiModelOption
{
    public string Id { get; init; } = string.Empty;
    public string? ApiModelId { get; init; }
    public string Provider { get; init; } = ModelProvider.Google;
    public string DisplayName { get; init; } = string.Empty;
    public string ShortName { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public string? Badge { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool RequiresBilling { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? ServiceTier { get; init; }
    public string OutputKind { get; init; } = ModelOutputKind.Text;

    public string RequestModelId => string.IsNullOrWhiteSpace(ApiModelId) ? Id : ApiModelId;
    public bool IsImageGeneration => OutputKind == ModelOutputKind.Image;
}

public static class ModelCatalogService
{
    private const int CurrentSchemaVersion = 6;
    private const string RemoteCatalogUrl =
        "https://raw.githubusercontent.com/wlaxhrl/GeminiDesk/main/models.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static IReadOnlyList<AiModelOption> LoadInitialCatalog()
    {
        var cachedCatalog = TryLoadCatalog(CachePath);
        var bundledCatalog = TryLoadCatalog(Path.Combine(AppContext.BaseDirectory, "models.json"));

        if (cachedCatalog is not null &&
            (bundledCatalog is null || cachedCatalog.SchemaVersion >= bundledCatalog.SchemaVersion))
        {
            return cachedCatalog.Models;
        }

        return bundledCatalog?.Models ?? CreateEmergencyCatalog();
    }

    public static async Task<IReadOnlyList<AiModelOption>?> TryRefreshCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var catalogUrl = $"{RemoteCatalogUrl}?refresh={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            using var request = new HttpRequestMessage(HttpMethod.Get, catalogUrl);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await HttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var catalog = ParseCatalog(json);

            if (catalog is null || catalog.SchemaVersion < CurrentSchemaVersion)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(CachePath, json, cancellationToken);
            return catalog.Models;
        }
        catch
        {
            return null;
        }
    }

    private static ParsedModelCatalog? TryLoadCatalog(string path)
    {
        try
        {
            return File.Exists(path)
                ? ParseCatalog(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static ParsedModelCatalog? ParseCatalog(string json)
    {
        var catalog = JsonSerializer.Deserialize<ModelCatalogDocument>(json, JsonOptions);
        if (catalog is null || catalog.SchemaVersion is < 1 or > CurrentSchemaVersion || catalog.Models is null)
        {
            return null;
        }

        var models = catalog.Models
            .Where(IsValidModel)
            .DistinctBy(model => model.Id, StringComparer.Ordinal)
            .ToList();

        return models.Count > 0
            ? new ParsedModelCatalog(catalog.SchemaVersion, models)
            : null;
    }

    private static bool IsValidModel(AiModelOption model)
    {
        return !string.IsNullOrWhiteSpace(model.Id) &&
               model.Id.Length <= 100 &&
               model.Id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') &&
               !string.IsNullOrWhiteSpace(model.DisplayName) &&
               !string.IsNullOrWhiteSpace(model.ShortName) &&
               !string.IsNullOrWhiteSpace(model.Icon) &&
               !string.IsNullOrWhiteSpace(model.Description) &&
               model.Provider is ModelProvider.Google or ModelProvider.OpenAi or ModelProvider.Anthropic &&
               (model.ApiModelId is null ||
                (model.ApiModelId.Length <= 100 &&
                 model.ApiModelId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))) &&
               (model.ReasoningEffort is null or "none" or "low" or "medium" or "high" or "xhigh" or "max") &&
               model.ServiceTier is null or "flex" &&
               model.OutputKind is ModelOutputKind.Text or ModelOutputKind.Image;
    }

    private static IReadOnlyList<AiModelOption> CreateEmergencyCatalog()
    {
        return
        [
            new()
            {
                Id = "gemini-3.5-flash",
                Provider = ModelProvider.Google,
                DisplayName = "Gemini 3.5 Flash",
                ShortName = "3.5 Flash",
                Icon = "⚡",
                Badge = "STABLE",
                Description = "빠르고 균형 잡힌 안정 버전"
            },
            new()
            {
                Id = "gemini-3.1-pro-preview",
                Provider = ModelProvider.Google,
                DisplayName = "Gemini 3.1 Pro Preview",
                ShortName = "3.1 Pro",
                Icon = "✦",
                Badge = "PREVIEW",
                Description = "복잡한 추론에 강한 Preview",
                RequiresBilling = true
            },
            new()
            {
                Id = "gemini-3.1-flash-image",
                Provider = ModelProvider.Google,
                DisplayName = "Nano Banana 2",
                ShortName = "Nano Banana 2",
                Icon = "🍌",
                Badge = "IMAGE",
                Description = "빠르고 똑똑한 이미지 생성·편집 모델",
                RequiresBilling = true,
                OutputKind = ModelOutputKind.Image
            },
            new()
            {
                Id = "gpt-5.6-luna",
                Provider = ModelProvider.OpenAi,
                DisplayName = "GPT-5.6 Luna",
                ShortName = "5.6 Luna",
                Icon = "☾",
                Badge = "VALUE",
                Description = "가볍고 경제적인 일상 작업용 GPT",
                RequiresBilling = true
            },
            new()
            {
                Id = "gpt-5.6-terra",
                Provider = ModelProvider.OpenAi,
                DisplayName = "GPT-5.6 Terra",
                ShortName = "5.6 Terra",
                Icon = "◇",
                Badge = "BALANCED",
                Description = "성능과 비용의 균형이 좋은 GPT",
                RequiresBilling = true
            },
            new()
            {
                Id = "gpt-5.6-sol-standard",
                ApiModelId = "gpt-5.6-sol",
                Provider = ModelProvider.OpenAi,
                DisplayName = "GPT-5.6 Sol Standard",
                ShortName = "Sol Standard",
                Icon = "☀",
                Badge = "STANDARD",
                Description = "제일 똑똑해요 · 비싸고 빨라요!",
                RequiresBilling = true,
                ReasoningEffort = "max"
            },
            new()
            {
                Id = "gpt-5.6-sol-flex",
                ApiModelId = "gpt-5.6-sol",
                Provider = ModelProvider.OpenAi,
                DisplayName = "GPT-5.6 Sol Flex",
                ShortName = "Sol Flex",
                Icon = "☀",
                Badge = "FLEX",
                Description = "제일 똑똑해요 · 느리고 반값!",
                RequiresBilling = true,
                ReasoningEffort = "max",
                ServiceTier = "flex"
            },
            new()
            {
                Id = "gpt-image-2",
                Provider = ModelProvider.OpenAi,
                DisplayName = "GPT Image 2",
                ShortName = "GPT Image 2",
                Icon = "🎨",
                Badge = "IMAGE",
                Description = "빠르고 고품질인 이미지 생성·편집 모델",
                RequiresBilling = true,
                OutputKind = ModelOutputKind.Image
            },
            new()
            {
                Id = "claude-opus-4-6",
                Provider = ModelProvider.Anthropic,
                DisplayName = "Claude Opus 4.6",
                ShortName = "Opus 4.6",
                Icon = "✺",
                Badge = "OPUS",
                Description = "공감형 감성친구, 추론과 글쓰기에 강해요.",
                RequiresBilling = true,
                ReasoningEffort = "high"
            }
        ];
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GeminiDesk-ModelCatalog/1.0");
        return client;
    }

    private static string CachePath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "GeminiDesk",
        "model-catalog.json");

    private sealed record ModelCatalogDocument(
        int SchemaVersion,
        IReadOnlyList<AiModelOption>? Models);

    private sealed record ParsedModelCatalog(
        int SchemaVersion,
        IReadOnlyList<AiModelOption> Models);
}
