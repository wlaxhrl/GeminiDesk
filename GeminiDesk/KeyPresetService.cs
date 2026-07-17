using System.IO;
using System.Text;
using System.Text.Json;

namespace GeminiDesk;

internal static class KeyPresetService
{
    private const string FormatName = "BunnyDeskKeyPreset";
    private const int FormatVersion = 2;
    private const long MaxPresetFileSize = 128 * 1024;
    private const string PlaintextWarning =
        "UNENCRYPTED API keys. Anyone with this file can read and use them.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Save(string path, IReadOnlyDictionary<string, string> apiKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var keysToSave = apiKeys
            .Where(entry =>
                IsSupportedProvider(entry.Key) &&
                !string.IsNullOrWhiteSpace(entry.Value) &&
                entry.Value.Length <= 2048)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        if (keysToSave.Count == 0)
        {
            throw new InvalidOperationException("저장할 API 키가 없습니다.");
        }

        var document = new KeyPresetDocument(
            FormatName,
            FormatVersion,
            PlaintextWarning,
            keysToSave);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("키 프리셋 파일을 찾을 수 없습니다.", path);
        }

        if (file.Length is <= 0 or > MaxPresetFileSize)
        {
            throw new InvalidDataException("키 프리셋 파일 크기가 올바르지 않습니다.");
        }

        KeyPresetDocument document;

        try
        {
            document = JsonSerializer.Deserialize<KeyPresetDocument>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("키 프리셋 파일이 비어 있습니다.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Bunny Desk 키 프리셋 파일이 아니거나 파일이 손상되었습니다.", exception);
        }

        if (document.Format == FormatName && document.Version == 1)
        {
            throw new InvalidDataException(
                "비밀번호를 사용하던 이전 프리셋입니다. 원래 PC에서 암호 없는 프리셋으로 다시 저장해 주세요.");
        }

        if (document.Format != FormatName ||
            document.Version != FormatVersion ||
            document.ApiKeys is null ||
            document.ApiKeys.Count == 0 ||
            document.ApiKeys.Any(entry =>
                !IsSupportedProvider(entry.Key) ||
                string.IsNullOrWhiteSpace(entry.Value) ||
                entry.Value.Length > 2048))
        {
            throw new InvalidDataException("지원하지 않거나 손상된 Bunny Desk 키 프리셋입니다.");
        }

        return new Dictionary<string, string>(document.ApiKeys, StringComparer.Ordinal);
    }

    private static bool IsSupportedProvider(string provider) =>
        provider is ModelProvider.Google or ModelProvider.OpenAi or ModelProvider.Anthropic;

    private sealed record KeyPresetDocument(
        string Format,
        int Version,
        string Warning,
        Dictionary<string, string>? ApiKeys);
}
