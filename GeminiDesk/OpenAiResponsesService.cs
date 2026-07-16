using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.GenAI.Types;

namespace GeminiDesk;

internal sealed record OpenAiStreamChunk(
    string TextDelta,
    IReadOnlyList<ChatSource> Sources);

internal sealed class OpenAiResponsesService
{
    private const string ResponsesUrl = "https://api.openai.com/v1/responses";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async IAsyncEnumerable<OpenAiStreamChunk> StreamResponseAsync(
        string apiKey,
        AiModelOption model,
        IReadOnlyList<Content> conversation,
        bool allowWebSearch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ResponsesUrl)
        {
            Content = new StringContent(
                BuildRequestJson(model, conversation, allowWebSearch),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(CreateErrorMessage(response.StatusCode, errorBody));
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(responseStream);
        var streamedText = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].TrimStart();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            var type = TryGetString(root, "type");

            if (type == "response.output_text.delta")
            {
                var delta = TryGetString(root, "delta") ?? string.Empty;
                if (delta.Length > 0)
                {
                    streamedText.Append(delta);
                    yield return new OpenAiStreamChunk(delta, []);
                }

                continue;
            }

            if (type is "error" or "response.failed")
            {
                throw new InvalidOperationException(ExtractStreamError(root));
            }

            var sources = ExtractUrlCitations(root);
            var fallbackText = type == "response.completed" && streamedText.Length == 0
                ? ExtractCompletedText(root)
                : string.Empty;

            if (fallbackText.Length > 0)
            {
                streamedText.Append(fallbackText);
            }

            if (fallbackText.Length > 0 || sources.Count > 0)
            {
                yield return new OpenAiStreamChunk(fallbackText, sources);
            }
        }
    }

    private static string BuildRequestJson(
        AiModelOption model,
        IReadOnlyList<Content> conversation,
        bool allowWebSearch)
    {
        var input = new JsonArray();

        for (var turnIndex = 0; turnIndex < conversation.Count; turnIndex++)
        {
            input.Add(BuildInputMessage(conversation[turnIndex], turnIndex));
        }

        var payload = new JsonObject
        {
            ["model"] = model.RequestModelId,
            ["input"] = input,
            ["stream"] = true,
            ["store"] = false
        };

        if (!string.IsNullOrWhiteSpace(model.ServiceTier))
        {
            payload["service_tier"] = model.ServiceTier;
        }

        if (allowWebSearch)
        {
            payload["tools"] = new JsonArray
            {
                new JsonObject { ["type"] = "web_search" }
            };
        }

        if (!string.IsNullOrWhiteSpace(model.ReasoningEffort))
        {
            payload["reasoning"] = new JsonObject
            {
                ["effort"] = model.ReasoningEffort
            };
        }

        return payload.ToJsonString();
    }

    private static JsonObject BuildInputMessage(Content content, int turnIndex)
    {
        var isAssistant = string.Equals(content.Role, "model", StringComparison.OrdinalIgnoreCase);
        var parts = content.Parts ?? [];
        var text = string.Concat(parts
            .Select(part => part.Text)
            .Where(value => !string.IsNullOrEmpty(value)));

        if (isAssistant)
        {
            return new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = text
            };
        }

        var items = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = text
            }
        };
        var skippedUnsupportedAttachment = false;

        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            var blob = parts[partIndex].InlineData;
            if (blob?.Data is not { Length: > 0 } bytes)
            {
                continue;
            }

            var mimeType = string.IsNullOrWhiteSpace(blob.MimeType)
                ? "application/octet-stream"
                : blob.MimeType;
            var dataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";

            if (mimeType.Equals("image/bmp", StringComparison.OrdinalIgnoreCase))
            {
                skippedUnsupportedAttachment = true;
            }
            else if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = dataUrl
                });
            }
            else if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                     mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                skippedUnsupportedAttachment = true;
            }
            else
            {
                items.Add(new JsonObject
                {
                    ["type"] = "input_file",
                    ["filename"] = $"attachment-{turnIndex + 1}-{partIndex + 1}{GetExtension(mimeType)}",
                    ["file_data"] = dataUrl
                });
            }
        }

        if (skippedUnsupportedAttachment)
        {
            items.Add(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = "[이전 대화에서 GPT가 지원하지 않는 첨부 파일은 이 요청에서 제외되었습니다.]"
            });
        }

        return new JsonObject
        {
            ["role"] = "user",
            ["content"] = items
        };
    }

    private static string GetExtension(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf",
        "application/json" => ".json",
        "application/xml" => ".xml",
        "text/csv" => ".csv",
        "text/html" => ".html",
        "text/markdown" => ".md",
        "text/plain" => ".txt",
        _ => ".bin"
    };

    private static IReadOnlyList<ChatSource> ExtractUrlCitations(JsonElement root)
    {
        var sources = new List<ChatSource>();
        Walk(root, sources);
        return sources
            .Where(source => Uri.TryCreate(source.Uri, UriKind.Absolute, out _))
            .DistinctBy(source => source.Uri, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Walk(JsonElement element, List<ChatSource> sources)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(element, "type") == "url_citation" &&
                TryGetString(element, "url") is { Length: > 0 } url)
            {
                var title = TryGetString(element, "title");
                sources.Add(new ChatSource(string.IsNullOrWhiteSpace(title) ? url : title, url));
            }

            foreach (var property in element.EnumerateObject())
            {
                Walk(property.Value, sources);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Walk(item, sources);
            }
        }
    }

    private static string ExtractCompletedText(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (TryGetString(part, "type") == "output_text" &&
                    TryGetString(part, "text") is { } partText)
                {
                    text.Append(partText);
                }
            }
        }

        return text.ToString();
    }

    private static string ExtractStreamError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) &&
            TryGetString(error, "message") is { Length: > 0 } errorMessage)
        {
            return errorMessage;
        }

        if (root.TryGetProperty("response", out var response) &&
            response.TryGetProperty("error", out var responseError) &&
            TryGetString(responseError, "message") is { Length: > 0 } responseMessage)
        {
            return responseMessage;
        }

        return "OpenAI가 응답 생성을 완료하지 못했습니다.";
    }

    private static string CreateErrorMessage(HttpStatusCode statusCode, string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                TryGetString(error, "message") is { Length: > 0 } message)
            {
                return message;
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
            _ => $"OpenAI 요청에 실패했습니다. ({(int)statusCode})"
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GeminiDesk/0.2");
        return client;
    }
}
