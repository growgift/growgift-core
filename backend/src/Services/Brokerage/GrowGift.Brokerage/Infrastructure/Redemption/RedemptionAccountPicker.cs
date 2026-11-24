namespace GrowGift.Brokerage.Infrastructure.Redemption;

/// <summary>
/// Which ViewTrade trading account a redeemed gift is bought into. The account GrowGift itself recorded when it
/// linked / onboarded the recipient is authoritative; the account number carried on the gift-redeemed event comes
/// from the user's token at redeem time, which can lag a link or still hold the Development placeholder.
/// A placeholder ("VT-DEV-…", from "Simulate verification") is never a real account, so it is never ordered into.
/// </summary>
public static class RedemptionAccountPicker
{
    public const string SimulatedPrefix = "VT-DEV-";

    public static bool IsUsable(string? account) =>
        !string.IsNullOrWhiteSpace(account) && !account.StartsWith(SimulatedPrefix, StringComparison.OrdinalIgnoreCase);

    /// <returns>The account to buy into, or null when neither source is a real account.</returns>
    public static string? Pick(string? storedRef, string? eventRef) =>
        IsUsable(storedRef) ? storedRef!.Trim() : IsUsable(eventRef) ? eventRef!.Trim() : null;

    /// <summary>"ORBS-01-004099" → "ORBS-••••99" for logs and the UI.</summary>
    public static string Mask(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return "(none)";
        var a = account.Trim();
        return a.Length <= 6 ? "••••" : a[..4] + "••••" + a[^2..];
    }
}
