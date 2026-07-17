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

internal sealed record AnthropicStreamChunk(
    string TextDelta,
    AiRequestUsage? Usage);

internal sealed class AnthropicMessagesService
{
    private const string MessagesUrl = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private const int MaxOutputTokens = 32768;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async IAsyncEnumerable<AnthropicStreamChunk> StreamResponseAsync(
        string apiKey,
        AiModelOption model,
        IReadOnlyList<Content> conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl)
        {
            Content = new StringContent(
                BuildRequestJson(model, conversation),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
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
        long baseInputTokens = 0;
        long cacheReadTokens = 0;
        long cacheWriteTokens = 0;
        long outputTokens = 0;
        var usageYielded = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].TrimStart();
            if (data.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            var eventType = TryGetString(root, "type");

            if (eventType == "message_start" &&
                root.TryGetProperty("message", out var message) &&
                message.TryGetProperty("usage", out var startUsage))
            {
                baseInputTokens = TryGetInt64(startUsage, "input_tokens");
                cacheReadTokens = TryGetInt64(startUsage, "cache_read_input_tokens");
                cacheWriteTokens = TryGetInt64(startUsage, "cache_creation_input_tokens");
                continue;
            }

            if (eventType == "content_block_delta" &&
                root.TryGetProperty("delta", out var delta) &&
                TryGetString(delta, "type") == "text_delta" &&
                TryGetString(delta, "text") is { Length: > 0 } textDelta)
            {
                yield return new AnthropicStreamChunk(textDelta, null);
                continue;
            }

            if (eventType == "message_delta" && root.TryGetProperty("usage", out var deltaUsage))
            {
                outputTokens = TryGetInt64(deltaUsage, "output_tokens");
                usageYielded = true;
                yield return new AnthropicStreamChunk(
                    string.Empty,
                    CreateUsage(baseInputTokens, cacheReadTokens, cacheWriteTokens, outputTokens));
                continue;
            }

            if (eventType == "message_stop" && !usageYielded)
            {
                usageYielded = true;
                yield return new AnthropicStreamChunk(
                    string.Empty,
                    CreateUsage(baseInputTokens, cacheReadTokens, cacheWriteTokens, outputTokens));
                continue;
            }

            if (eventType == "error")
            {
                throw new InvalidOperationException(ExtractStreamError(root));
            }
        }
    }

    private static AiRequestUsage CreateUsage(
        long baseInputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        long outputTokens)
    {
        return new AiRequestUsage(
            InputTokens: baseInputTokens + cacheReadTokens + cacheWriteTokens,
            CachedInputTokens: cacheReadTokens,
            CacheWriteInputTokens: cacheWriteTokens,
            OutputTokens: outputTokens);
    }

    private static string BuildRequestJson(
        AiModelOption model,
        IReadOnlyList<Content> conversation)
    {
        var payload = new JsonObject
        {
            ["model"] = model.RequestModelId,
            ["max_tokens"] = MaxOutputTokens,
            ["messages"] = BuildMessages(conversation),
            ["stream"] = true,
            ["thinking"] = new JsonObject
            {
                ["type"] = "adaptive"
            },
            ["output_config"] = new JsonObject
            {
                ["effort"] = string.IsNullOrWhiteSpace(model.ReasoningEffort)
                    ? "high"
                    : model.ReasoningEffort
            }
        };
        return payload.ToJsonString();
    }

    private static JsonArray BuildMessages(IReadOnlyList<Content> conversation)
    {
        var messages = new JsonArray();

        for (var turnIndex = 0; turnIndex < conversation.Count; turnIndex++)
        {
            var content = conversation[turnIndex];
            var isAssistant = string.Equals(content.Role, "model", StringComparison.OrdinalIgnoreCase);
            var blocks = BuildContentBlocks(content, turnIndex, isAssistant);
            messages.Add(new JsonObject
            {
                ["role"] = isAssistant ? "assistant" : "user",
                ["content"] = blocks
            });
        }

        return messages;
    }

    private static JsonArray BuildContentBlocks(Content content, int turnIndex, bool isAssistant)
    {
        var blocks = new JsonArray();
        var parts = content.Parts ?? [];
        var text = string.Concat(parts
            .Select(part => part.Text)
            .Where(value => !string.IsNullOrEmpty(value)));

        if (isAssistant && !string.IsNullOrWhiteSpace(text))
        {
            blocks.Add(CreateTextBlock(text));
        }

        if (!isAssistant)
        {
            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                var blob = parts[partIndex].InlineData;
                if (blob?.Data is not { Length: > 0 } bytes || string.IsNullOrWhiteSpace(blob.MimeType))
                {
                    continue;
                }

                if (IsSupportedImage(blob.MimeType))
                {
                    blocks.Add(CreateBase64Block("image", blob.MimeType, bytes));
                }
                else if (blob.MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
                {
                    blocks.Add(CreateBase64Block("document", blob.MimeType, bytes));
                }
                else if (IsTextDocument(blob.MimeType))
                {
                    blocks.Add(CreateTextBlock(
                        $"[첨부 텍스트 파일 {turnIndex + 1}-{partIndex + 1}]{System.Environment.NewLine}" +
                        Encoding.UTF8.GetString(bytes)));
                }
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                blocks.Add(CreateTextBlock(text));
            }
        }

        if (blocks.Count == 0)
        {
            blocks.Add(CreateTextBlock("내용이 없는 메시지입니다."));
        }

        return blocks;
    }

    private static JsonObject CreateTextBlock(string text)
    {
        return new JsonObject
        {
            ["type"] = "text",
            ["text"] = text
        };
    }

    private static JsonObject CreateBase64Block(string type, string mimeType, byte[] bytes)
    {
        return new JsonObject
        {
            ["type"] = type,
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = mimeType,
                ["data"] = Convert.ToBase64String(bytes)
            }
        };
    }

    private static bool IsSupportedImage(string mimeType)
    {
        return mimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
               mimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
               mimeType.Equals("image/gif", StringComparison.OrdinalIgnoreCase) ||
               mimeType.Equals("image/webp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextDocument(string mimeType)
    {
        return mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
               mimeType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
               mimeType.Equals("application/xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractStreamError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) &&
            TryGetString(error, "message") is { Length: > 0 } message)
        {
            return TryGetString(error, "type") switch
            {
                "rate_limit_error" => "Claude 사용 한도에 도달했습니다. 잠시 후 다시 시도해 주세요.",
                "overloaded_error" => "Claude 서버가 혼잡합니다. 잠시 후 다시 시도해 주세요.",
                _ => message
            };
        }

        return "Claude가 응답 생성을 완료하지 못했습니다.";
    }

    private static string CreateErrorMessage(HttpStatusCode statusCode, string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                TryGetString(error, "message") is { Length: > 0 } message)
            {
                return TryGetString(error, "type") switch
                {
                    "authentication_error" => "Anthropic API 키가 올바르지 않습니다.",
                    "permission_error" => "이 Anthropic API 키에는 Claude Opus 4.6 사용 권한이 없습니다.",
                    "rate_limit_error" => "Claude 사용 한도에 도달했습니다. 잠시 후 다시 시도해 주세요.",
                    "overloaded_error" => "Claude 서버가 혼잡합니다. 잠시 후 다시 시도해 주세요.",
                    _ => message
                };
            }
        }
        catch (JsonException)
        {
            // JSON이 아닌 오류는 상태 코드로 안내합니다.
        }

        return statusCode switch
        {
            HttpStatusCode.Unauthorized => "Anthropic API 키가 올바르지 않습니다.",
            HttpStatusCode.PaymentRequired => "Anthropic API 크레딧이 필요합니다.",
            HttpStatusCode.TooManyRequests => "Claude 사용 한도에 도달했습니다. 잠시 후 다시 시도해 주세요.",
            _ when (int)statusCode == 529 => "Claude 서버가 혼잡합니다. 잠시 후 다시 시도해 주세요.",
            _ => $"Claude 요청에 실패했습니다. ({(int)statusCode})"
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

    private static long TryGetInt64(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out var value)
            ? value
            : 0;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BunnyDesk/0.2");
        return client;
    }
}
