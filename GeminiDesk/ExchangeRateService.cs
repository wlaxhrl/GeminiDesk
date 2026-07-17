using System.Globalization;
using System.Net.Http;
using System.Xml.Linq;

namespace GeminiDesk;

internal sealed record ExchangeRateSnapshot(
    double UsdToKrw,
    DateTime? ReferenceDate,
    bool IsDefault,
    bool IsStale);

internal sealed class ExchangeRateService
{
    private const string EcbDailyRatesUrl =
        "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    private const string RateSettingKey = "exchange-rate-usd-krw";
    private const string ReferenceDateSettingKey = "exchange-rate-reference-date";
    private const string FetchedAtSettingKey = "exchange-rate-fetched-at-utc";
    private static readonly TimeSpan FailedRequestRetryInterval = TimeSpan.FromHours(1);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly ChatStore _store;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private ExchangeRateSnapshot? _snapshot;
    private DateTime? _fetchedAtUtc;
    private DateTime? _lastAttemptUtc;

    public ExchangeRateService(ChatStore store)
    {
        _store = store;
        LoadCachedRate();
    }

    public ExchangeRateSnapshot Current => _snapshot ?? CreateDefaultSnapshot();

    public async Task<ExchangeRateSnapshot> GetUsdToKrwAsync(
        CancellationToken cancellationToken = default)
    {
        if (HasFreshRateForToday())
        {
            return Current;
        }

        if (_lastAttemptUtc is { } lastAttempt &&
            DateTime.UtcNow - lastAttempt < FailedRequestRetryInterval)
        {
            return CreateFallbackSnapshot();
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (HasFreshRateForToday())
            {
                return Current;
            }

            if (_lastAttemptUtc is { } lockedLastAttempt &&
                DateTime.UtcNow - lockedLastAttempt < FailedRequestRetryInterval)
            {
                return CreateFallbackSnapshot();
            }

            _lastAttemptUtc = DateTime.UtcNow;

            try
            {
                var freshSnapshot = await FetchLatestEcbRateAsync(cancellationToken);
                _snapshot = freshSnapshot;
                _fetchedAtUtc = DateTime.UtcNow;
                SaveCachedRate(freshSnapshot, _fetchedAtUtc.Value);
                return freshSnapshot;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return CreateFallbackSnapshot();
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal static ExchangeRateSnapshot ParseEcbDailyRates(XDocument document)
    {
        var datedCube = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Cube" && element.Attribute("time") is not null)
            ?? throw new InvalidOperationException("ECB 환율 기준일을 찾지 못했습니다.");
        var referenceDateText = datedCube.Attribute("time")?.Value;

        if (!DateTime.TryParseExact(
                referenceDateText,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var referenceDate))
        {
            throw new InvalidOperationException("ECB 환율 기준일 형식이 올바르지 않습니다.");
        }

        var usdPerEuro = GetCurrencyRate(datedCube, "USD");
        var krwPerEuro = GetCurrencyRate(datedCube, "KRW");
        var usdToKrw = krwPerEuro / usdPerEuro;

        if (!double.IsFinite(usdToKrw) || usdToKrw is < 500 or > 5000)
        {
            throw new InvalidOperationException("ECB 원/달러 환율 값이 유효 범위를 벗어났습니다.");
        }

        return new ExchangeRateSnapshot(usdToKrw, referenceDate, IsDefault: false, IsStale: false);
    }

    private async Task<ExchangeRateSnapshot> FetchLatestEcbRateAsync(
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(EcbDailyRatesUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        return ParseEcbDailyRates(document);
    }

    private void LoadCachedRate()
    {
        try
        {
            if (!double.TryParse(
                    _store.GetSetting(RateSettingKey),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var rate) ||
                !double.IsFinite(rate) ||
                rate is < 500 or > 5000)
            {
                return;
            }

            DateTime? referenceDate = null;
            if (DateTime.TryParseExact(
                    _store.GetSetting(ReferenceDateSettingKey),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedReferenceDate))
            {
                referenceDate = parsedReferenceDate;
            }

            if (DateTime.TryParse(
                    _store.GetSetting(FetchedAtSettingKey),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedFetchedAt))
            {
                _fetchedAtUtc = parsedFetchedAt.ToUniversalTime();
            }

            var isStale = _fetchedAtUtc is null ||
                          _fetchedAtUtc.Value.ToLocalTime().Date != DateTime.Now.Date;
            _snapshot = new ExchangeRateSnapshot(rate, referenceDate, IsDefault: false, IsStale: isStale);
        }
        catch
        {
            _snapshot = null;
            _fetchedAtUtc = null;
        }
    }

    private void SaveCachedRate(ExchangeRateSnapshot snapshot, DateTime fetchedAtUtc)
    {
        try
        {
            _store.SetSetting(
                RateSettingKey,
                snapshot.UsdToKrw.ToString("R", CultureInfo.InvariantCulture));
            _store.SetSetting(
                ReferenceDateSettingKey,
                snapshot.ReferenceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);
            _store.SetSetting(
                FetchedAtSettingKey,
                fetchedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        }
        catch
        {
            // 메모리의 최신 환율은 계속 사용하고 다음 실행에서 다시 저장합니다.
        }
    }

    private bool HasFreshRateForToday()
    {
        return _snapshot is { IsDefault: false } &&
               _fetchedAtUtc is { } fetchedAt &&
               fetchedAt.ToLocalTime().Date == DateTime.Now.Date;
    }

    private ExchangeRateSnapshot CreateFallbackSnapshot()
    {
        return _snapshot is null
            ? CreateDefaultSnapshot()
            : _snapshot with { IsStale = true };
    }

    private static ExchangeRateSnapshot CreateDefaultSnapshot()
    {
        return new ExchangeRateSnapshot(
            UsagePriceCalculator.FallbackUsdToKrw,
            ReferenceDate: null,
            IsDefault: true,
            IsStale: true);
    }

    private static double GetCurrencyRate(XElement datedCube, string currency)
    {
        var rateText = datedCube
            .Elements()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Cube" &&
                string.Equals(
                    element.Attribute("currency")?.Value,
                    currency,
                    StringComparison.OrdinalIgnoreCase))?
            .Attribute("rate")?
            .Value;

        if (!double.TryParse(
                rateText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var rate) ||
            rate <= 0)
        {
            throw new InvalidOperationException($"ECB {currency} 환율을 찾지 못했습니다.");
        }

        return rate;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BunnyDesk/0.2 exchange-rate");
        return client;
    }
}
