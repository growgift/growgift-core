using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade;

/// <summary>
/// Exchanges GrowGift's ViewTrade api_key/api_secret for a short-lived
/// bearer token, per the confirmed "AccessTokenGenerate" request in the
/// GrowGift - ViewTrade Postman workspace (POST
/// /api/v1/auth/b2b/login/api-keys against the UMA host, body
/// {"api_key","api_secret"}). The response nests the actual token three
/// levels deep — {"status":201,"api_keys_login":{"tokens":
/// {"access_token":"...", ...}}} — confirmed via a captured raw response
/// after an earlier flat-shape assumption silently produced a null token
/// on every single call for a long stretch (every request went out as a
/// bare "Authorization: Bearer" with nothing after it, which some AMO
/// endpoints tolerated and others correctly rejected as 401 — the
/// inconsistency was what made this so hard to pin down).
///
/// This is a SEPARATE ViewTrade host/environment from AMO (the one that
/// serves CountriesList and the onboarding form endpoints) — see
/// ViewTrade-Staging-UMA vs ViewTrade-Staging-AMO in that workspace. The
/// token minted here is what AMO expects as Authorization: Bearer &lt;token&gt;;
/// sending AMO a static API key directly (the old behavior) gets rejected
/// because AMO can't resolve a tenant/firm from it — this was the root
/// cause of the earlier "No domain found" (code 5021) error.
/// </summary>
public interface IViewTradeAuthTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);

    /// <summary>
    /// Drops the cached token so the next GetAccessTokenAsync call fetches a
    /// fresh one from UMA, regardless of the cached one's remembered
    /// expiry. Needed because the token can go invalid server-side before
    /// its own exp claim says so — e.g. many B2B token systems only allow
    /// one active token per API key, so issuing a new one anywhere (a
    /// teammate testing in Postman, another instance of this service)
    /// silently kills this one. See ViewTradeAuthHandler's 401-retry.
    /// </summary>
    void InvalidateCachedToken();
}

public sealed class ViewTradeAuthTokenProvider(
    HttpClient http, // BaseAddress = the UMA host, configured separately from AMO's
    string apiKey,
    string apiSecret,
    ILogger<ViewTradeAuthTokenProvider> logger) : IViewTradeAuthTokenProvider
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    // Refresh a couple minutes before actual expiry so a request already in
    // flight never races a token that dies mid-call.
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt - RefreshSkew)
            return _cachedToken;

        await _lock.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while we were waiting on the lock.
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt - RefreshSkew)
                return _cachedToken;

            var response = await http.PostAsJsonAsync("api/v1/auth/b2b/login/api-keys",
                new { api_key = apiKey, api_secret = apiSecret }, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogError("ViewTrade UMA token request failed: {Status} {Body}", response.StatusCode, body);
                throw new ViewTradeApiException(response.StatusCode, body);
            }

            var rawTokenResponse = await response.Content.ReadAsStringAsync(ct);
            var payload = System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(rawTokenResponse)
                ?? throw new InvalidOperationException("Empty ViewTrade UMA token response.");

            // Confirmed real shape via a captured raw response — access_token
            // is nested three levels deep, not flat at the top level as
            // originally assumed: {"status":201,"api_keys_login":{"tokens":
            // {"access_token":"...", "refresh_token":"...", "pair_id":"...",
            // "access_expires_at":..., "refresh_expires_at":...}}}. That
            // wrong assumption meant every token this provider ever handed
            // out was silently null — explaining every "Unauthorized"
            // seen against AMO despite the token "looking" freshly refreshed.
            var accessToken = payload.ApiKeysLogin?.Tokens?.AccessToken;
            if (string.IsNullOrEmpty(accessToken))
                throw new InvalidOperationException(
                    "UMA's response shape changed again — api_keys_login.tokens.access_token wasn't found. " +
                    $"Raw body: {rawTokenResponse}");

            _cachedToken = accessToken;
            _expiresAt = ReadExpiry(accessToken);
            logger.LogInformation("Refreshed ViewTrade AMO bearer token, valid until {ExpiresAt}", _expiresAt);
            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void InvalidateCachedToken()
    {
        _cachedToken = null;
        _expiresAt = DateTimeOffset.MinValue;
    }

    // The access token is itself a JWT — its exp claim is the authoritative
    // source for when it goes stale (observed ~24h lifetime on a captured
    // staging token), so read that directly rather than guessing a fixed TTL.
    private static DateTimeOffset ReadExpiry(string jwt)
    {
        try
        {
            return new JwtSecurityTokenHandler().ReadJwtToken(jwt).ValidTo;
        }
        catch
        {
            // Can't confirm the token's age — refresh defensively soon
            // rather than caching something of unknown lifetime.
            return DateTimeOffset.UtcNow.AddMinutes(5);
        }
    }

    private sealed record TokenResponse([property: JsonPropertyName("api_keys_login")] ApiKeysLoginPayload? ApiKeysLogin);
    private sealed record ApiKeysLoginPayload([property: JsonPropertyName("tokens")] TokensPayload? Tokens);
    private sealed record TokensPayload([property: JsonPropertyName("access_token")] string? AccessToken);
}
