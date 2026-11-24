using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;

/// <summary>
/// Establishes a ViewTrade trading session for one GrowGift user by running
/// the confirmed authentication chain in order (names are the Postman
/// requests in the "ViewTrade_APIs" collection):
///
///   1. AccessTokenGenerate  (UMA)  → initial token        [IViewTradeAuthTokenProvider]
///   2. GetUser              (UMA)  → ViewTrade user id    users_list[].id, matched by e-mail
///   3. ImpersonateLogin     (UMA)  → impersonated token   tokens.access_token
///   4. WatchmanAuthentication (UMA)→ watchman token       data.token  (short-lived)
///   5. PerformSSO           (SSO)  → session id           sessionId, stage == "ESTABLISHED"
///
/// Each token is kept in its own local variable and used only for the next
/// call. The Watchman token is NEVER confused with the final session id, and
/// nothing here is logged except stage names and HTTP status codes.
///
/// The ViewTrade user id always comes from looking up the caller's own
/// (server-verified) e-mail — never from the browser — and the returned user
/// must match that e-mail and be active, so one GrowGift user can't be
/// mapped onto another ViewTrade user.
/// </summary>
public interface IViewTradeSessionService
{
    Task<TradingSession> GetSessionAsync(Guid growGiftUserId, string email, CancellationToken ct);

    /// <summary>
    /// Same chain, but for a ViewTrade user id GrowGift ALREADY stored for this user (set when they
    /// onboarded or were linked — never client-supplied). Used by background work that has no e-mail,
    /// e.g. buying a redeemed gift into the recipient's account.
    /// </summary>
    Task<TradingSession> GetSessionForViewTradeUserAsync(Guid growGiftUserId, long viewTradeUserId, CancellationToken ct);
    void Invalidate(Guid growGiftUserId);
}

public sealed class ViewTradeSessionService(
    IHttpClientFactory httpFactory,
    IViewTradeAuthTokenProvider initialTokens,
    IViewTradeUserDirectory directory,
    ViewTradeTradingOptions options,
    ILogger<ViewTradeSessionService> logger,
    TimeProvider? clock = null) : IViewTradeSessionService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Sessions are cached PER GrowGift user and never shared between users.
    private readonly ConcurrentDictionary<Guid, TradingSession> _cache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public void Invalidate(Guid growGiftUserId) => _cache.TryRemove(growGiftUserId, out _);

    public Task<TradingSession> GetSessionAsync(Guid userId, string email, CancellationToken ct) =>
        GetOrEstablishAsync(userId, async () =>
        {
            var matches = await directory.FindByEmailAsync(email, ct);
            return Pick(matches);
        }, ct);

    public Task<TradingSession> GetSessionForViewTradeUserAsync(Guid userId, long viewTradeUserId, CancellationToken ct) =>
        GetOrEstablishAsync(userId, () =>
        {
            if (!long.TryParse(options.BranchId, out var branch))
                throw new ViewTradeSessionException(TradingStage.UserLookup, "ViewTrade branch isn't configured.");
            return Task.FromResult((viewTradeUserId, branch));
        }, ct);

    private async Task<TradingSession> GetOrEstablishAsync(
        Guid userId, Func<Task<(long UserId, long BranchId)>> resolveUser, CancellationToken ct)
    {
        if (!options.IsConfigured)
            throw new ViewTradeSessionException(TradingStage.NotConfigured,
                "Trading isn't configured (ViewTrade SSO/Trading base URLs are missing).");

        if (_cache.TryGetValue(userId, out var cached) && cached.ExpiresAt > _clock.GetUtcNow())
            return cached;

        // One establishment at a time per user, so a double-click can't run the chain twice.
        var gate = _gates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(userId, out cached) && cached.ExpiresAt > _clock.GetUtcNow())
                return cached;

            var session = await EstablishAsync(resolveUser, ct);
            if (session.ExpiresAt > _clock.GetUtcNow()) _cache[userId] = session;
            else _cache.TryRemove(userId, out _);
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TradingSession> EstablishAsync(Func<Task<(long UserId, long BranchId)>> resolveUser, CancellationToken ct)
    {
        var uma = httpFactory.CreateClient("ViewTradeUma");
        // ---- Steps 1 + 2: find this user's ViewTrade id (the directory obtains the initial token) ----
        var (viewTradeUserId, branchId) = await resolveUser();
        var initial = await initialTokens.GetAccessTokenAsync(ct); // cached; fresh if the directory had to renew it
        HttpStatusCode status;
        string body;

        // ---- Step 3: impersonated login (initial token + user id) ----
        (status, body) = await PostAsync(uma, "api/v1/auth/b2c/login/impersonate", initial,
            new { user_id = viewTradeUserId }, ct);
        if (!IsSuccess(status)) throw Fail(TradingStage.Impersonation, "impersonated login", status, body);
        var impersonated = Extract(body, TradingStage.Impersonation, "tokens", "access_token");

        // ---- Step 4: Watchman authentication (impersonated token + branch) ----
        (status, body) = await PostAsync(uma, "api/v1/auth/watchmen/authentication-tokens", impersonated,
            new { branch_id = branchId }, ct);
        if (!IsSuccess(status)) throw Fail(TradingStage.Watchman, "Watchman authentication", status, body);
        var watchman = Extract(body, TradingStage.Watchman, "data", "token");

        // ---- Step 5: SSO (separate host, no Authorization header) ----
        var sso = httpFactory.CreateClient("ViewTradeSso");
        (status, body) = await PostAsync(sso, "api/auth/v1/sso/platform", bearer: null,
            new { platform = "uma", token = watchman }, ct);
        if (!IsSuccess(status)) throw Fail(TradingStage.Sso, "single sign-on", status, body);

        var sessionId = Extract(body, TradingStage.Sso, "sessionId");
        var stage = TryString(body, "stage");
        if (!string.Equals(stage, "ESTABLISHED", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("ViewTrade SSO returned a non-established stage ({Stage}).", stage ?? "(none)");
            throw new ViewTradeSessionException(TradingStage.Sso, "ViewTrade did not establish a trading session.");
        }

        return new TradingSession(sessionId, CacheUntil(sessionId));
    }

    // ---- helpers -------------------------------------------------------------

    private (long UserId, long BranchId) Pick(IReadOnlyList<ViewTradeUserMatch> matches)
    {
        // Only ACTIVE users in GrowGift's configured branch (when configured) are tradeable.
        var eligible = matches.Where(m => m.IsActive &&
            (!long.TryParse(options.BranchId, out var cfg) || m.BranchIds.Contains(cfg))).ToList();

        if (eligible.Count == 0)
            throw new ViewTradeSessionException(TradingStage.UserNotFound, "No active ViewTrade user matches this account.");
        // Never pick one of several arbitrarily — fail closed.
        if (eligible.Select(m => m.Id).Distinct().Count() > 1)
            throw new ViewTradeSessionException(TradingStage.UserAmbiguous, "More than one ViewTrade user matches this account.");

        var u = eligible[0];
        var branch = long.TryParse(options.BranchId, out var configured) ? configured
            : u.BranchIds.Count > 0 ? u.BranchIds[0]
            : throw new ViewTradeSessionException(TradingStage.UserLookup, "ViewTrade user has no branch.");
        return (u.Id, branch);
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(
        HttpClient client, string path, string? bearer, object payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        try
        {
            using var resp = await client.SendAsync(req, ct);
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException ex)
        {
            throw new ViewTradeSessionException(TradingStage.UserLookup, "Couldn't reach ViewTrade.", null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ViewTradeSessionException(TradingStage.UserLookup, "ViewTrade timed out.", null, ex);
        }
    }

    private static bool IsSuccess(HttpStatusCode s) => (int)s is >= 200 and < 300;

    private ViewTradeSessionException Fail(TradingStage stage, string what, HttpStatusCode status, string body)
    {
        // Status + ViewTrade's own numeric error code only — never the body (it may echo tokens or PII).
        logger.LogWarning("ViewTrade {What} failed with HTTP {Status} (code {Code}).", what, (int)status, TryCode(body) ?? "n/a");
        return new ViewTradeSessionException(stage, $"ViewTrade {what} failed.", status, viewTradeCode: TryCode(body));
    }

    private static string? TryCode(string body)
    {
        using var doc = TryParse(body);
        return doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object &&
               doc.RootElement.TryGetProperty("code", out var c) ? c.ToString() : null;
    }

    private static JsonDocument? TryParse(string body)
    {
        try { return JsonDocument.Parse(body); } catch (JsonException) { return null; }
    }

    private static string? TryString(string body, string prop)
    {
        using var doc = TryParse(body);
        return doc is not null ? TryString(doc.RootElement, prop) : null;
    }

    private static string? TryString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>Reads a required string at a path (e.g. "tokens","access_token"); fails closed if absent.</summary>
    private static string Extract(string body, TradingStage stage, params string[] path)
    {
        using var doc = TryParse(body) ?? throw new ViewTradeSessionException(stage, "Unreadable ViewTrade response.");
        var el = doc.RootElement;
        foreach (var p in path)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(p, out el))
                throw new ViewTradeSessionException(stage, $"ViewTrade response was missing '{string.Join('.', path)}'.");
        }
        var s = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        if (string.IsNullOrWhiteSpace(s))
            throw new ViewTradeSessionException(stage, $"ViewTrade response had an empty '{string.Join('.', path)}'.");
        return s;
    }

    /// <summary>
    /// Cache window: never longer than the configured minutes, never past the
    /// session JWT's own exp (minus a minute), and not at all if exp can't be
    /// read — "reuse a session only if permitted" means failing toward re-auth.
    /// </summary>
    private DateTimeOffset CacheUntil(string sessionId)
    {
        var now = _clock.GetUtcNow();
        if (options.SessionCacheMinutes <= 0) return now;
        var exp = ReadJwtExp(sessionId);
        if (exp is null) return now;
        var limit = now.AddMinutes(options.SessionCacheMinutes);
        var until = exp.Value.AddMinutes(-1);
        var result = until < limit ? until : limit;
        return result > now.AddSeconds(30) ? result : now;
    }

    internal static DateTimeOffset? ReadJwtExp(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var p = parts[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)));
            return doc.RootElement.TryGetProperty("exp", out var e) && e.TryGetInt64(out var secs)
                ? DateTimeOffset.FromUnixTimeSeconds(secs) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
