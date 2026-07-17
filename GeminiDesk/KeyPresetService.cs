using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GeminiDesk;

internal static class KeyPresetService
{
    private const string FormatName = "BunnyDeskKeyPreset";
    private const int FormatVersion = 1;
    private const int Pbkdf2Iterations = 600_000;
    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const long MaxPresetFileSize = 128 * 1024;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("BunnyDeskKeyPreset:v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Save(
        string path,
        IReadOnlyDictionary<string, string> apiKeys,
        string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var keysToSave = apiKeys
            .Where(entry => IsSupportedProvider(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        if (keysToSave.Count == 0)
        {
            throw new InvalidOperationException("저장할 API 키가 없습니다.");
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new KeyPresetPayload(keysToSave),
            JsonOptions);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        var encryptionKey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySize);

        try
        {
            using var aes = new AesGcm(encryptionKey, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);

            var document = new KeyPresetDocument(
                FormatName,
                FormatVersion,
                Pbkdf2Iterations,
                Convert.ToBase64String(salt),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(tag));
            var json = JsonSerializer.Serialize(document, JsonOptions);
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static IReadOnlyDictionary<string, string> Load(string path, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

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
        byte[] salt;
        byte[] nonce;
        byte[] ciphertext;
        byte[] tag;

        try
        {
            document = JsonSerializer.Deserialize<KeyPresetDocument>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("키 프리셋 파일이 비어 있습니다.");
            ValidateDocument(document);
            salt = Convert.FromBase64String(document.Salt);
            nonce = Convert.FromBase64String(document.Nonce);
            ciphertext = Convert.FromBase64String(document.Ciphertext);
            tag = Convert.FromBase64String(document.Tag);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw new InvalidDataException("Bunny Desk 키 프리셋 파일이 아니거나 파일이 손상되었습니다.", exception);
        }

        if (salt.Length != SaltSize || nonce.Length != NonceSize || tag.Length != TagSize ||
            ciphertext.Length == 0 || ciphertext.Length > MaxPresetFileSize)
        {
            throw new InvalidDataException("키 프리셋의 암호화 정보가 올바르지 않습니다.");
        }

        var plaintext = new byte[ciphertext.Length];
        var encryptionKey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            document.Iterations,
            HashAlgorithmName.SHA256,
            KeySize);

        try
        {
            using var aes = new AesGcm(encryptionKey, TagSize);

            try
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException("프리셋 비밀번호가 다르거나 파일이 손상되었습니다.", exception);
            }

            var payload = JsonSerializer.Deserialize<KeyPresetPayload>(plaintext, JsonOptions);
            if (payload?.ApiKeys is null || payload.ApiKeys.Count == 0 ||
                payload.ApiKeys.Any(entry =>
                    !IsSupportedProvider(entry.Key) ||
                    string.IsNullOrWhiteSpace(entry.Value) ||
                    entry.Value.Length > 2048))
            {
                throw new InvalidDataException("키 프리셋 안의 API 키 정보가 올바르지 않습니다.");
            }

            return new Dictionary<string, string>(payload.ApiKeys, StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("키 프리셋 안의 API 키 정보가 손상되었습니다.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void ValidateDocument(KeyPresetDocument document)
    {
        if (document.Format != FormatName ||
            document.Version != FormatVersion ||
            document.Iterations is < 100_000 or > 2_000_000 ||
            string.IsNullOrWhiteSpace(document.Salt) ||
            string.IsNullOrWhiteSpace(document.Nonce) ||
            string.IsNullOrWhiteSpace(document.Ciphertext) ||
            string.IsNullOrWhiteSpace(document.Tag))
        {
            throw new InvalidDataException("지원하지 않는 Bunny Desk 키 프리셋 형식입니다.");
        }
    }

    private static bool IsSupportedProvider(string provider) =>
        provider is ModelProvider.Google or ModelProvider.OpenAi or ModelProvider.Anthropic;

    private sealed record KeyPresetPayload(IReadOnlyDictionary<string, string> ApiKeys);

    private sealed record KeyPresetDocument(
        string Format,
        int Version,
        int Iterations,
        string Salt,
        string Nonce,
        string Ciphertext,
        string Tag);
}
