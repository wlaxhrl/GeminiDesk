using System.Globalization;

namespace GeminiDesk;

public sealed record SubscriptionComparisonItem(
    string Icon,
    string ProviderName,
    string PlanName,
    string ApiCostLabel,
    string ApiUsageLabel,
    string SubscriptionCostLabel,
    string SubscriptionUsdLabel,
    string RecommendationKind,
    string RecommendationTitle,
    string RecommendationDetail);

internal static class SubscriptionComparisonCalculator
{
    public const string SubscriptionRecommended = "subscription";
    public const string ApiRecommended = "api";
    public const string Similar = "similar";
    public const string PriceReferenceDate = "2026-07-17";

    private static readonly SubscriptionPlan[] Plans =
    [
        new(
            ModelProvider.OpenAi,
            "◎",
            "OpenAI GPT",
            "ChatGPT Plus",
            20.00),
        new(
            ModelProvider.Google,
            "✦",
            "Google Gemini",
            "Google AI Pro",
            19.99),
        new(
            ModelProvider.Anthropic,
            "✺",
            "Anthropic Claude",
            "Claude Pro",
            20.00)
    ];

    public static IReadOnlyList<SubscriptionComparisonItem> Create(
        IReadOnlyList<UsageRecord> records,
        double usdToKrw)
    {
        if (!double.IsFinite(usdToKrw) || usdToKrw <= 0)
        {
            usdToKrw = UsagePriceCalculator.FallbackUsdToKrw;
        }

        return Plans
            .Select(plan => CreateItem(plan, records, usdToKrw))
            .ToList();
    }

    private static SubscriptionComparisonItem CreateItem(
        SubscriptionPlan plan,
        IReadOnlyList<UsageRecord> records,
        double usdToKrw)
    {
        var providerRecords = records
            .Where(record => string.Equals(record.Provider, plan.Provider, StringComparison.Ordinal))
            .ToList();
        var apiCostKrw = providerRecords.Sum(record => record.EstimatedCostKrw);
        var subscriptionCostKrw = plan.MonthlyPriceUsd * usdToKrw;
        var difference = Math.Abs(apiCostKrw - subscriptionCostKrw);
        string kind;
        string title;
        string detail;

        if (difference < 1)
        {
            kind = Similar;
            title = "금액이 거의 같아요";
            detail = "차이가 1원 미만이라 사용 방식과 기능을 보고 고르면 돼요.";
        }
        else if (apiCostKrw > subscriptionCostKrw)
        {
            kind = SubscriptionRecommended;
            title = $"{plan.PlanName} 구독이 더 유리해요";
            detail = $"구독 쪽이 한 달에 약 {FormatApproximateKrw(difference)} 저렴해요.";
        }
        else
        {
            kind = ApiRecommended;
            title = "지금은 API가 더 유리해요";
            detail = $"현재 사용량에서는 API가 한 달에 약 {FormatApproximateKrw(difference)} 저렴해요.";
        }

        return new SubscriptionComparisonItem(
            plan.Icon,
            plan.ProviderName,
            plan.PlanName,
            FormatExactKrw(apiCostKrw),
            $"{providerRecords.Count:N0}번 호출",
            FormatExactKrw(subscriptionCostKrw),
            $"${plan.MonthlyPriceUsd:0.##} / 월",
            kind,
            title,
            detail);
    }

    private static string FormatExactKrw(double amount)
    {
        amount = double.IsFinite(amount) && amount > 0 ? amount : 0;
        var culture = CultureInfo.GetCultureInfo("ko-KR");
        var wholeWon = Math.Truncate(amount);
        return $"{wholeWon.ToString("N0", culture)}원 (₩{amount.ToString("N2", culture)})";
    }

    private static string FormatApproximateKrw(double amount)
    {
        var wholeWon = Math.Truncate(Math.Max(0, amount));
        return $"{wholeWon.ToString("N0", CultureInfo.GetCultureInfo("ko-KR"))}원";
    }

    private sealed record SubscriptionPlan(
        string Provider,
        string Icon,
        string ProviderName,
        string PlanName,
        double MonthlyPriceUsd);
}
