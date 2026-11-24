using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GrowGift.Brokerage.Infrastructure.ViewTrade;
using Microsoft.Extensions.Caching.Memory;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;

/// <summary>One row of ViewTrade's country reference list (POST /api/countries/list).</summary>
public sealed record CountryDto(
    int Id,
    string Code,
    [property: JsonPropertyName("alpha_2_code")] string Alpha2Code,
    string Name,
    [property: JsonPropertyName("dial_code")] string? DialCode);

/// <summary>
/// Reference data for the onboarding form's "country" type questions (see
/// ViewTradeQuestionConfigResolver: applicant.primary.citizenship.country,
/// birth-country.country, address.home.country, document.country — all
/// type_name "country" with no static Options list in the exported config).
/// Their submitted value is the free-form country code — matches the
/// "code" field here (ISO alpha-3, e.g. "AUS"), which is why the old
/// placeholder text on those fields was literally "AUS".
/// </summary>
public interface IViewTradeCountriesClient
{
    Task<IReadOnlyList<CountryDto>> GetCountriesAsync(CancellationToken ct);
}

public sealed class ViewTradeCountriesClient(HttpClient http, IMemoryCache cache, ILogger<ViewTradeCountriesClient> logger)
    : IViewTradeCountriesClient
{
    private const string CacheKey = "viewtrade:countries";

    // Country reference data changes essentially never — cache generously so
    // the onboarding form doesn't round-trip to ViewTrade on every page load.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<CountryDto>> GetCountriesAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<CountryDto>? cached) && cached is not null)
            return cached;

        var response = await http.PostAsync("api/countries/list", content: null, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("ViewTrade rejected countries list: {Status} {Body}", response.StatusCode, body);
            throw new ViewTradeApiException(response.StatusCode, body);
        }

        var payload = await response.Content.ReadFromJsonAsync<CountriesListResponse>(ct)
            ?? throw new InvalidOperationException("Empty ViewTrade countries response.");

        // is_enabled filters out any countries ViewTrade has disabled for
        // account opening (still present in the list, e.g. sanctioned
        // jurisdictions) without us having to hardcode an exclusion list.
        var countries = payload.Countries
            .Where(c => c.IsEnabled == 1)
            .Select(c => new CountryDto(c.Id, c.Code, c.Alpha2Code, c.Name, c.DialCode))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        cache.Set(CacheKey, (IReadOnlyList<CountryDto>)countries, CacheDuration);
        return countries;
    }

    private sealed record CountriesListResponse(bool Status, List<CountryEntry> Countries);

    private sealed record CountryEntry(
        int Id,
        string Code,
        [property: JsonPropertyName("alpha_2_code")] string Alpha2Code,
        string Name,
        [property: JsonPropertyName("dial_code")] string? DialCode,
        [property: JsonPropertyName("is_enabled")] int IsEnabled);
}
