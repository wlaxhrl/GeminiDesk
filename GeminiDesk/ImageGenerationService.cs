using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.GenAI;
using Google.GenAI.Types;

namespace GeminiDesk;

internal sealed record GeneratedImageData(byte[] Data, string MimeType);

internal sealed record ImageGenerationResult(
    string Text,
    IReadOnlyList<GeneratedImageData> Images,
    AiRequestUsage Usage);

internal sealed class ImageGenerationService
{
    private const string OpenAiGenerationsUrl = "https://api.openai.com/v1/images/generations";
    private const string OpenAiEditsUrl = "https://api.openai.com/v1/images/edits";
    private const int MaxGoogleReferenceImages = 14;
    private const int MaxOpenAiReferenceImages = 4;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public Task<ImageGenerationResult> GenerateAsync(
        string apiKey,
        AiModelOption model,
        IReadOnlyList<Content> conversation,
        bool allowWebSearch,
        CancellationToken cancellationToken)
    {
        return model.Provider == ModelProvider.OpenAi
            ? GenerateWithOpenAiAsync(apiKey, model, conversation, cancellationToken)
            : GenerateWithGoogleAsync(apiKey, model, conversation, allowWebSearch, cancellationToken);
    }

    private static async Task<ImageGenerationResult> GenerateWithGoogleAsync(
        string apiKey,
        AiModelOption model,
        IReadOnlyList<Content> conversation,
        bool allowWebSearch,
        CancellationToken cancellationToken)
    {
        var client = new Client(apiKey: apiKey);
        var config = new GenerateContentConfig
        {
            ResponseModalities = ["TEXT", "IMAGE"],
            Tools = allowWebSearch
                ? [new Tool { GoogleSearch = new GoogleSearch() }]
                : null
        };
        var requestParts = new List<Part>
        {
            new() { Text = GetLatestUserPrompt(conversation) }
        };

        foreach (var image in GetMostRecentReferenceImages(conversation, MaxGoogleReferenceImages))
        {
            requestParts.Add(new Part
            {
                InlineData = new Blob
                {
                    Data = image.Data,
                    MimeType = image.MimeType
                }
            });
        }

        var response = await client.Models.GenerateContentAsync(
            model: model.RequestModelId,
            contents:
            [
                new Content
                {
                    Role = "user",
                    Parts = requestParts
                }
            ],
            config: config,
            cancellationToken: cancellationToken);
        var parts = response.Candidates?
            .SelectMany(candidate => candidate.Content?.Parts ?? [])
            .ToList() ?? [];
        var images = parts
            .Where(part => part.InlineData?.Data is { Length: > 0 })
            .Select(part => new GeneratedImageData(
                part.InlineData!.Data!,
                string.IsNullOrWhiteSpace(part.InlineData.MimeType)
                    ? "image/png"
                    : part.InlineData.MimeType))
            .ToList();

        if (images.Count == 0)
        {
            throw new InvalidOperationException("Google이 이미지를 반환하지 않았습니다. 프롬프트를 조금 더 구체적으로 적어 주세요.");
        }

        var text = string.Concat(parts
            .Select(part => part.Text)
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new ImageGenerationResult(
            text,
            images,
            UsageMetadataMapper.FromGoogle(response, images.Count));
    }

    private static async Task<ImageGenerationResult> GenerateWithOpenAiAsync(
        string apiKey,
        AiModelOption model,
        IReadOnlyList<Content> conversation,
        CancellationToken cancellationToken)
    {
        var prompt = GetLatestUserPrompt(conversation);
        var referenceImages = GetMostRecentReferenceImages(conversation, MaxOpenAiReferenceImages);
        using var request = referenceImages.Count == 0
            ? CreateOpenAiGenerationRequest(apiKey, model, prompt)
            : CreateOpenAiEditRequest(apiKey, model, prompt, referenceImages);
        using var response = await HttpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(CreateOpenAiErrorMessage(response.StatusCode, errorBody));
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(responseBody);
        var images = new List<GeneratedImageData>();

        if (document.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("b64_json", out var base64Property) ||
                    base64Property.ValueKind != JsonValueKind.String ||
                    base64Property.GetString() is not { Length: > 0 } base64)
                {
                    continue;
                }

                try
                {
                    images.Add(new GeneratedImageData(Convert.FromBase64String(base64), "image/png"));
                }
                catch (FormatException)
                {
                    throw new InvalidOperationException("OpenAI가 올바르지 않은 이미지 데이터를 반환했습니다.");
                }
            }
        }

        if (images.Count == 0)
        {
            throw new InvalidOperationException("OpenAI가 이미지를 반환하지 않았습니다. 프롬프트를 조금 더 구체적으로 적어 주세요.");
        }

        return new ImageGenerationResult(
            string.Empty,
            images,
            ExtractOpenAiImageUsage(document.RootElement, images.Count));
    }

    private static AiRequestUsage ExtractOpenAiImageUsage(JsonElement root, int generatedImages)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return new AiRequestUsage(GeneratedImages: generatedImages);
        }

        var inputTokens = TryGetInt64(usage, "input_tokens");
        var outputTokens = TryGetInt64(usage, "output_tokens");
        long imageInputTokens = 0;
        long cachedInputTokens = 0;
        long cachedImageInputTokens = 0;

        if (usage.TryGetProperty("input_tokens_details", out var inputDetails))
        {
            imageInputTokens = TryGetInt64(inputDetails, "image_tokens");
            cachedInputTokens = TryGetInt64(inputDetails, "cached_tokens");
            cachedImageInputTokens = TryGetInt64(inputDetails, "cached_image_tokens");
        }

        return new AiRequestUsage(
            InputTokens: inputTokens,
            CachedInputTokens: cachedInputTokens,
            OutputTokens: outputTokens,
            ImageInputTokens: imageInputTokens,
            CachedImageInputTokens: cachedImageInputTokens,
            ImageOutputTokens: outputTokens,
            GeneratedImages: generatedImages);
    }

    private static long TryGetInt64(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out var value)
            ? value
            : 0;
    }

    private static HttpRequestMessage CreateOpenAiGenerationRequest(
        string apiKey,
        AiModelOption model,
        string prompt)
    {
        var payload = new JsonObject
        {
            ["model"] = model.RequestModelId,
            ["prompt"] = prompt
        };
        var request = new HttpRequestMessage(HttpMethod.Post, OpenAiGenerationsUrl)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static HttpRequestMessage CreateOpenAiEditRequest(
        string apiKey,
        AiModelOption model,
        string prompt,
        IReadOnlyList<ImageReference> referenceImages)
    {
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(model.RequestModelId), "model");
        multipart.Add(new StringContent(prompt), "prompt");

        for (var index = 0; index < referenceImages.Count; index++)
        {
            var image = referenceImages[index];
            var content = new ByteArrayContent(image.Data);
            content.Headers.ContentType = new MediaTypeHeaderValue(image.MimeType);
            multipart.Add(content, "image[]", $"reference-{index + 1}{GetImageExtension(image.MimeType)}");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, OpenAiEditsUrl)
        {
            Content = multipart
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static string GetLatestUserPrompt(IReadOnlyList<Content> conversation)
    {
        for (var index = conversation.Count - 1; index >= 0; index--)
        {
            var content = conversation[index];
            if (!string.Equals(content.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prompt = string.Concat((content.Parts ?? [])
                .Select(part => part.Text)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                return prompt;
            }
        }

        return "첨부한 이미지를 자연스럽게 편집해 주세요.";
    }

    private static IReadOnlyList<ImageReference> GetMostRecentReferenceImages(
        IReadOnlyList<Content> conversation,
        int maxImages)
    {
        for (var contentIndex = conversation.Count - 1; contentIndex >= 0; contentIndex--)
        {
            var images = (conversation[contentIndex].Parts ?? [])
                .Select(part => part.InlineData)
                .Where(blob => blob?.Data is { Length: > 0 } && IsSupportedOpenAiImage(blob.MimeType))
                .Take(maxImages)
                .Select(blob => new ImageReference(blob!.Data!, blob.MimeType!))
                .ToList();

            if (images.Count > 0)
            {
                return images;
            }
        }

        return [];
    }

    private static bool IsSupportedOpenAiImage(string? mimeType)
    {
        return mimeType is not null &&
               (mimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
                mimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
                mimeType.Equals("image/webp", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetImageExtension(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".png"
    };

    private static string CreateOpenAiErrorMessage(HttpStatusCode statusCode, string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String &&
                message.GetString() is { Length: > 0 } errorMessage)
            {
                return errorMessage;
            }
        }
        catch (JsonException)
        {
            // JSON이 아닌 오류는 상태 코드로 안내합니다.
        }

        return statusCode switch
        {
            HttpStatusCode.Unauthorized => "OpenAI API 키가 올바르지 않습니다.",
            HttpStatusCode.TooManyRequests => "OpenAI 사용 한도에 도달했거나 결제 설정이 필요합니다.",
            _ => $"OpenAI 이미지 요청에 실패했습니다. ({(int)statusCode})"
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GeminiDesk/0.2");
        return client;
    }

    private sealed record ImageReference(byte[] Data, string MimeType);
}
