namespace DeChimex.Application.Common;

/// <summary>
/// Currency text for e-mail and notifications, following the storefront convention
/// (the views render <c>₦1,234.56</c> — keep every customer-facing surface identical).
/// </summary>
public static class MoneyText
{
    public static string Format(decimal amount) => $"\u20a6{amount:N2}";
}
