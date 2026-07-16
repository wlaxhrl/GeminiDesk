using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GeminiDesk;

public sealed record GeminiModelOption(
    string Id,
    string DisplayName,
    string ShortName,
    string Icon,
    string? Badge,
    string Description,
    bool RequiresBilling);

public static class ModelCatalogService
{
    private const string RemoteCatalogUrl =
        "https://raw.githubusercontent.com/wlaxhrl/GeminiDesk/main/models.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static IReadOnlyList<GeminiModelOption> LoadInitialCatalog()
    {
        return TryLoadCatalog(CachePath)
            ?? TryLoadCatalog(Path.Combine(AppContext.BaseDirectory, "models.json"))
            ?? CreateEmergencyCatalog();
    }

    public static async Task<IReadOnlyList<GeminiModelOption>?> TryRefreshCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RemoteCatalogUrl);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await HttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var models = ParseCatalog(json);

            if (models is null)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(CachePath, json, cancellationToken);
            return models;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<GeminiModelOption>? TryLoadCatalog(string path)
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

    private static IReadOnlyList<GeminiModelOption>? ParseCatalog(string json)
    {
        var catalog = JsonSerializer.Deserialize<ModelCatalogDocument>(json, JsonOptions);
        if (catalog is null || catalog.SchemaVersion != 1 || catalog.Models is null)
        {
            return null;
        }

        var models = catalog.Models
            .Where(IsValidModel)
            .DistinctBy(model => model.Id, StringComparer.Ordinal)
            .ToList();

        return models.Count > 0 ? models : null;
    }

    private static bool IsValidModel(GeminiModelOption model)
    {
        return !string.IsNullOrWhiteSpace(model.Id) &&
               model.Id.Length <= 100 &&
               model.Id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') &&
               !string.IsNullOrWhiteSpace(model.DisplayName) &&
               !string.IsNullOrWhiteSpace(model.ShortName) &&
               !string.IsNullOrWhiteSpace(model.Icon) &&
               !string.IsNullOrWhiteSpace(model.Description);
    }

    private static IReadOnlyList<GeminiModelOption> CreateEmergencyCatalog()
    {
        return
        [
            new GeminiModelOption(
                "gemini-3.5-flash",
                "Gemini 3.5 Flash",
                "3.5 Flash",
                "⚡",
                "STABLE",
                "빠르고 균형 잡힌 안정 버전",
                false),
            new GeminiModelOption(
                "gemini-3.1-pro-preview",
                "Gemini 3.1 Pro Preview",
                "3.1 Pro",
                "✦",
                "PREVIEW",
                "복잡한 추론에 강한 Preview",
                true)
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
        IReadOnlyList<GeminiModelOption>? Models);
}
