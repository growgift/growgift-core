using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade;

/// <summary>One ViewTrade b2c user whose e-mail exactly matches the one asked about.</summary>
public sealed record ViewTradeUserMatch(long Id, string? Status, IReadOnlyList<long> BranchIds)
{
    public bool IsActive => Status is null || string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The ONE place GrowGift asks ViewTrade "which user has this e-mail?" —
/// the GetUser request in the ViewTrade_APIs Postman collection
/// (POST api/v2/users/b2c/list on the UMA host, Bearer initial token, filtered
/// to GrowGift's firm/branch, free-text `search` = e-mail). Shared by trading
/// (session establishment) and gift redemption (skip onboarding for existing
/// users) so the two can never disagree about who a user is.
///
/// `search` is a substring match, so only entries whose e-mail EQUALS the
/// asked one (case-insensitive) are returned. More than one match is returned
/// as-is: callers decide how to treat ambiguity, and none of them may pick one
/// arbitrarily.
/// </summary>
public interface IViewTradeUserDirectory
{
    /// <exception cref="ViewTradeSessionException">ViewTrade unreachable, timed out, refused us, or answered unreadably.</exception>
    Task<IReadOnlyList<ViewTradeUserMatch>> FindByEmailAsync(string email, CancellationToken ct);
}

public sealed class ViewTradeUserDirectory(
    IHttpClientFactory httpFactory,
    IViewTradeAuthTokenProvider tokens,
    ViewTradeFirmOptions firm,
    ILogger<ViewTradeUserDirectory> logger) : IViewTradeUserDirectory
{
    public async Task<IReadOnlyList<ViewTradeUserMatch>> FindByEmailAsync(string email, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("ViewTradeUma");
        var body = RequestBody(email);

        var (status, text) = await PostAsync(client, await tokens.GetAccessTokenAsync(ct), body, ct);
        if (status == HttpStatusCode.Unauthorized)
        {
            // The cached initial token can die server-side before its own exp. One fresh try.
            tokens.InvalidateCachedToken();
            (status, text) = await PostAsync(client, await tokens.GetAccessTokenAsync(ct), body, ct);
        }
        if ((int)status is < 200 or >= 300)
        {
            // Status + ViewTrade's numeric code only — the body may echo e-mail addresses or tokens.
            logger.LogWarning("ViewTrade user lookup failed with HTTP {Status} (code {Code}).", (int)status, TryCode(text) ?? "n/a");
            throw new ViewTradeSessionException(TradingStage.UserLookup, "ViewTrade user lookup failed.", status);
        }

        return Parse(text, email);
    }

    private object RequestBody(string email)
    {
        var filters = new Dictionary<string, object>();
        if (long.TryParse(firm.BranchId, out var b)) filters["branch_ids"] = new[] { b };
        if (long.TryParse(firm.FirmId, out var f)) filters["firm_ids"] = new[] { f };
        return new { filters, pagination = new { page = 1, size = 10 }, search = email };
    }

    internal static IReadOnlyList<ViewTradeUserMatch> Parse(string json, string email)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ViewTradeSessionException(TradingStage.UserLookup, "Unreadable user lookup response."); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("users_list", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new ViewTradeSessionException(TradingStage.UserLookup, "Unexpected user lookup response shape.");

            var matches = new List<ViewTradeUserMatch>();
            foreach (var u in list.EnumerateArray())
            {
                if (!string.Equals(Str(u, "email"), email, StringComparison.OrdinalIgnoreCase)) continue;
                if (!u.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id) || id <= 0) continue;

                var branches = new List<long>();
                if (u.TryGetProperty("branches", out var bs) && bs.ValueKind == JsonValueKind.Array)
                    foreach (var br in bs.EnumerateArray())
                        if (br.TryGetProperty("id", out var bid) && bid.TryGetInt64(out var bl)) branches.Add(bl);

                matches.Add(new ViewTradeUserMatch(id, Str(u, "status"), branches));
            }
            return matches;
        }
    }

    private async Task<(HttpStatusCode, string)> PostAsync(HttpClient client, string bearer, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v2/users/b2c/list") { Content = JsonContent.Create(body) };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
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

    private static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? TryCode(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("code", out var c) ? c.ToString() : null;
        }
        catch (JsonException) { return null; }
    }
}
