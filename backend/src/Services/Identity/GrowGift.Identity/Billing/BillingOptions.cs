namespace GrowGift.Identity.Billing;

/// <summary>
/// Billing configuration, read once from environment variables. Nothing is hard-coded: the price lives in Stripe
/// (change it there, point STRIPE_PRICE_ID at the new price, no deploy of code), and the trial length, minimum
/// card validity and the enforcement switch are settings.
/// </summary>
public sealed record BillingOptions(
    string? StripeSecretKey,
    string? WebhookSecret,
    string? PriceId,
    int TrialMonths,
    int MinCardMonths,
    DateTimeOffset? EnforceFromUtc,
    string WebBaseUrl)
{
    public bool Configured => !string.IsNullOrWhiteSpace(StripeSecretKey) && !string.IsNullOrWhiteSpace(PriceId);

    public static BillingOptions FromEnvironment(Func<string, string?> get, string? webBaseUrl)
    {
        static int Int(string? v, int fallback, int min, int max) =>
            int.TryParse(v, out var n) && n >= min && n <= max ? n : fallback;

        DateTimeOffset? enforce = null;
        // Unset or unparseable = enforcement OFF. A typo must never lock people out.
        if (DateTimeOffset.TryParse(get("BILLING_ENFORCE_FROM_UTC"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            enforce = parsed;

        return new BillingOptions(
            Clean(get("STRIPE_SECRET_KEY")),
            Clean(get("STRIPE_BILLING_WEBHOOK_SECRET")),
            Clean(get("STRIPE_PRICE_ID")),
            Int(get("BILLING_TRIAL_MONTHS"), 3, 0, 24),
            Int(get("BILLING_MIN_CARD_MONTHS"), 6, 0, 36),
            enforce,
            (webBaseUrl ?? "http://localhost:3000").TrimEnd('/'));
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>
    /// Same idea as the Shippit staging-only rule: a LIVE Stripe key may only be used when the host says it is
    /// Production. Returns a reason to refuse, or null.
    /// </summary>
    public string? Refusal(bool isProduction)
    {
        if (StripeSecretKey is { } k && k.StartsWith("sk_live_", StringComparison.Ordinal) && !isProduction)
            return "A live Stripe key is configured outside Production; billing is disabled.";
        return null;
    }
}
