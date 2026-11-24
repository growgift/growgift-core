using System.Text.RegularExpressions;

namespace GrowGift.Fulfillment.Shipping;

public sealed record AddressCheck(bool Ok, string? Code);

/// <summary>Local checks before anything is sent to Shippit. Codes only — never echoes the address back.</summary>
public static partial class AddressValidator
{
    private static readonly HashSet<string> AuStates = new() { "NSW", "QLD", "ACT", "WA", "NT", "SA", "VIC", "TAS" };

    public static AddressCheck Check(ShippitAddress a)
    {
        if (string.IsNullOrWhiteSpace(a.Name)) return new(false, "address.name_missing");
        if (string.IsNullOrWhiteSpace(a.Line1)) return new(false, "address.line1_missing");
        if (string.IsNullOrWhiteSpace(a.City)) return new(false, "address.suburb_missing");
        if (string.IsNullOrWhiteSpace(a.Email) || !a.Email.Contains('@')) return new(false, "address.email_missing");
        var country = (a.Country ?? "AU").Trim().ToUpperInvariant();
        if (country is not ("AU" or "AUS" or "AUSTRALIA")) return new(false, "address.country_unsupported");
        if (!AuStates.Contains((a.State ?? "").Trim().ToUpperInvariant())) return new(false, "address.state_invalid");
        if (!PostcodeRx().IsMatch((a.Postcode ?? "").Trim())) return new(false, "address.postcode_invalid");
        return new(true, null);
    }

    [GeneratedRegex(@"^\d{4}$")] private static partial Regex PostcodeRx();

    public static (string First, string? Last) SplitName(string full)
    {
        var parts = (full ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch { 0 => ("", null), 1 => (parts[0], null), _ => (parts[0], parts[1]) };
    }
}
