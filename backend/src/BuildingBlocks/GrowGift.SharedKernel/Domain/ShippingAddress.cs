using GrowGift.SharedKernel.Results;

namespace GrowGift.SharedKernel.Domain;

/// <summary>
/// A physical shipping address. Deliberately loose on format validation
/// (postal formats vary wildly by country) — the carrier integration is the
/// authority on deliverability, this type just enforces "the required fields
/// aren't blank."
/// </summary>
public sealed class ShippingAddress : ValueObject
{
    public string FullName { get; }
    public string Line1 { get; }
    public string? Line2 { get; }
    public string City { get; }
    public string Region { get; }       // state / province / territory
    public string PostalCode { get; }
    public string CountryCode { get; }  // ISO 3166-1 alpha-2, e.g. "AU"
    public string? Phone { get; }       // carriers generally want one

    private ShippingAddress(string fullName, string line1, string? line2, string city,
        string region, string postalCode, string countryCode, string? phone)
    {
        FullName = fullName;
        Line1 = line1;
        Line2 = line2;
        City = city;
        Region = region;
        PostalCode = postalCode;
        CountryCode = countryCode;
        Phone = phone;
    }

    public static Result<ShippingAddress> Create(string fullName, string line1, string? line2,
        string city, string region, string postalCode, string countryCode, string? phone)
    {
        string[] required = [fullName, line1, city, region, postalCode, countryCode];
        if (required.Any(string.IsNullOrWhiteSpace))
            return Result.Failure<ShippingAddress>(Error.Validation(
                "address.incomplete", "Name, address line 1, city, state/region, postal code, and country are all required."));
        if (countryCode.Trim().Length != 2)
            return Result.Failure<ShippingAddress>(Error.Validation(
                "address.country_code", "Country must be a 2-letter code (e.g. AU, US, GB)."));

        return new ShippingAddress(
            fullName.Trim(), line1.Trim(), string.IsNullOrWhiteSpace(line2) ? null : line2.Trim(),
            city.Trim(), region.Trim(), postalCode.Trim(), countryCode.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(phone) ? null : phone.Trim());
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return FullName; yield return Line1; yield return Line2; yield return City;
        yield return Region; yield return PostalCode; yield return CountryCode; yield return Phone;
    }

    public override string ToString() =>
        $"{FullName}, {Line1}{(Line2 is null ? "" : ", " + Line2)}, {City} {Region} {PostalCode}, {CountryCode}";
}
