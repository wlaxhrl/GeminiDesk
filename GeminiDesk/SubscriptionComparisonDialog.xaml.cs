using System.Globalization;
using System.Windows;

namespace GeminiDesk;

public partial class SubscriptionComparisonDialog : Window
{
    internal SubscriptionComparisonDialog(
        DateTime month,
        IReadOnlyList<SubscriptionComparisonItem> items,
        ExchangeRateSnapshot exchangeRate)
    {
        InitializeComponent();

        ComparisonMonthText.Text = month.ToString(
            "yyyy년 M월 사용량 기준",
            CultureInfo.GetCultureInfo("ko-KR"));
        ExchangeRateText.Text = FormatExchangeRate(exchangeRate);
        PriceReferenceText.Text =
            $"{SubscriptionComparisonCalculator.PriceReferenceDate} 공식 월간 정가·세전 기준 · " +
            "지역별 가격, 무료 한도, 모델과 사용 한도 차이는 반영되지 않아요.";
        ComparisonItems.ItemsSource = items;
    }

    private static string FormatExchangeRate(ExchangeRateSnapshot snapshot)
    {
        var rate = snapshot.UsdToKrw.ToString("N2", CultureInfo.GetCultureInfo("ko-KR"));

        if (snapshot.IsDefault)
        {
            return $"임시 환율 · $1 = ₩{rate}";
        }

        var date = snapshot.ReferenceDate?.ToString(
            "M월 d일",
            CultureInfo.GetCultureInfo("ko-KR")) ?? "최근";
        return snapshot.IsStale
            ? $"마지막 ECB {date} · $1 = ₩{rate}"
            : $"ECB {date} · $1 = ₩{rate}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
