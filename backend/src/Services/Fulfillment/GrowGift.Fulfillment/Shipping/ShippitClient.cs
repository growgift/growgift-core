using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GrowGift.Fulfillment.Shipping;

/// <summary>
/// Server-side Shippit STAGING client. Nothing here logs a request/response body, an address or the secret;
/// failures are reduced to short codes. Writes (create / book) that time out or hit a 5xx return
/// <see cref="ShippitOutcome.Unknown"/> so the caller reconciles instead of resending.
/// </summary>
public sealed partial class ShippitClient(HttpClient http, ILogger<ShippitClient> log) : IShippitClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Configures the typed HttpClient. Throws unless the options are staging-only.</summary>
    public static void Configure(HttpClient c, ShippitOptions o)
    {
        c.BaseAddress = ShippitGuard.BaseAddress(o);            // throws on production / non-https / odd host
        c.Timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 5, 60));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (o.IsConfigured)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiSecret);
    }

    // ---------------- create ----------------
    public async Task<ShippitResult<ShippitCreated>> CreateOrderAsync(ShippitCreateRequest r, CancellationToken ct)
    {
        var (first, last) = AddressValidator.SplitName(r.To.Name);
        var line = string.IsNullOrWhiteSpace(r.To.Line2) ? r.To.Line1 : $"{r.To.Line1}, {r.To.Line2}";
        var parcel = new Dictionary<string, object?> { ["qty"] = 1 };
        if (r.Parcel.WeightKg is { } w) parcel["weight"] = w;
        if (r.Parcel.LengthM is { } l) parcel["length"] = l;
        if (r.Parcel.WidthM is { } wd) parcel["width"] = wd;
        if (r.Parcel.DepthM is { } d) parcel["depth"] = d;

        var body = new
        {
            order = new Dictionary<string, object?>
            {
                ["retailer_invoice"] = r.Reference,
                ["retailer_reference"] = r.Reference,
                ["courier_type"] = r.CourierType,
                ["authority_to_leave"] = r.AuthorityToLeave,
                ["delivery_address"] = line,
                ["delivery_suburb"] = r.To.City,
                ["delivery_state"] = r.To.State.ToUpperInvariant(),
                ["delivery_postcode"] = r.To.Postcode,
                ["delivery_country_code"] = "AU",
                ["receiver_name"] = r.To.Name,
                ["receiver_contact_number"] = r.To.Phone,
                ["user_attributes"] = new Dictionary<string, object?>
                {
                    ["email"] = r.To.Email, ["first_name"] = first, ["last_name"] = last, ["mobile"] = r.To.Phone
                },
                ["parcel_attributes"] = new[] { parcel }
            }
        };

        var (status, doc, err, ra) = await SendAsync(HttpMethod.Post, "orders", body, ct);
        if (err is not null) return ShippitResult<ShippitCreated>.Fail(ShippitOutcome.Unknown, err);   // outcome unknown — do NOT resend
        using var _ = doc;
        if (status is >= 200 and < 300)
        {
            var node = Unwrap(doc);
            var tn = Str(node, "tracking_number");
            // A 2xx with no tracking number is "created, but we can't see it" — treat as unknown, not as failure.
            return tn is null
                ? ShippitResult<ShippitCreated>.Fail(ShippitOutcome.Unknown, "create.no_tracking_number", status)
                : ShippitResult<ShippitCreated>.Ok(new ShippitCreated(tn, Str(node, "courier_name"), Str(node, "state"), Str(node, "tracking_url")), status);
        }
        return Classify<ShippitCreated>(status, doc, write: true, ra);
    }

    // ---------------- label ----------------
    public async Task<ShippitResult<ShippitLabel>> GetLabelAsync(string tn, CancellationToken ct)
    {
        var (status, doc, err, ra) = await SendAsync(HttpMethod.Get, $"orders/{Uri.EscapeDataString(tn)}/label", null, ct);
        if (err is not null) return ShippitResult<ShippitLabel>.Fail(ShippitOutcome.RetrySafe, err);   // a GET is safe to repeat
        using var _ = doc;
        if (status is >= 200 and < 300)
        {
            var resp = Unwrap(doc);
            var order = Prop(resp, "order");
            var url = Str(resp, "qualified_url") ?? DocUrl(order);
            return ShippitResult<ShippitLabel>.Ok(new ShippitLabel(url, Str(order, "courier_name"),
                Str(order, "manifest_number"), Str(order, "manifest_id"), Str(order, "state")), status);
        }
        var code = ErrorCode(doc);
        if (status == 422 && code is "label_failed_order_processing" or "order_not_available_to_label" or "label_failed_still_generating")
            return ShippitResult<ShippitLabel>.Fail(ShippitOutcome.StillProcessing, code!, status);
        return Classify<ShippitLabel>(status, doc, write: false, ra);
    }

    // ---------------- book ----------------
    public async Task<ShippitResult<ShippitBooked>> BookAsync(string tn, CancellationToken ct)
    {
        var (status, doc, err, ra) = await SendAsync(HttpMethod.Post, "book", new { orders = new[] { tn } }, ct);
        if (err is not null) return ShippitResult<ShippitBooked>.Fail(ShippitOutcome.Unknown, err);
        using var _ = doc;
        if (status is >= 200 and < 300)
        {
            // 200 can still carry success:false per carrier — check every entry.
            if (doc is not null && doc.RootElement.TryGetProperty("response", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False)
                        return ShippitResult<ShippitBooked>.Fail(ShippitOutcome.Permanent, Safe(Str(item, "error")) ?? "book.carrier_failed", status);
                    return ShippitResult<ShippitBooked>.Ok(new ShippitBooked(Str(item, "manifest"), Str(item, "manifest_id"), Str(item, "manifest_pdf")), status);
                }
            }
            return ShippitResult<ShippitBooked>.Fail(ShippitOutcome.Unknown, "book.unexpected_reply", status);
        }
        var code = ErrorCode(doc);
        if (code == "orders_not_available_to_book") return ShippitResult<ShippitBooked>.Fail(ShippitOutcome.Permanent, code, status);
        return Classify<ShippitBooked>(status, doc, write: true, ra);
    }

    // ---------------- tracking (no auth needed, but harmless to send) ----------------
    public async Task<ShippitResult<ShippitTracking>> GetTrackingAsync(string tn, CancellationToken ct)
    {
        var (status, doc, err, ra) = await SendAsync(HttpMethod.Get, $"orders/{Uri.EscapeDataString(tn)}/tracking", null, ct);
        if (err is not null) return ShippitResult<ShippitTracking>.Fail(ShippitOutcome.RetrySafe, err);
        using var _ = doc;
        if (status is >= 200 and < 300)
        {
            var resp = Unwrap(doc);
            var hist = new List<ShippitTrackingEvent>();
            var th = Prop(resp, "track");
            var arr = th is { ValueKind: JsonValueKind.Array } ? th : Prop(resp, "status_history");
            if (arr is { ValueKind: JsonValueKind.Array } a)
                foreach (var e in a.EnumerateArray())
                {
                    var st = Str(e, "status") ?? Str(e, "state");
                    if (st is null) continue;
                    hist.Add(new ShippitTrackingEvent(st, DateTimeOffset.TryParse(Str(e, "time") ?? Str(e, "timestamp"), out var t) ? t : null));
                }
            var cur = Str(resp, "current_state") ?? Str(resp, "state") ?? hist.LastOrDefault()?.State;
            return ShippitResult<ShippitTracking>.Ok(new ShippitTracking(cur, hist, Str(resp, "courier_name"), Str(resp, "tracking_url")), status);
        }
        return Classify<ShippitTracking>(status, doc, write: false, ra);
    }

    // ---------------- webhooks ----------------
    public async Task<ShippitResult<IReadOnlyList<ShippitWebhookInfo>>> ListWebhooksAsync(CancellationToken ct)
    {
        var (status, doc, err, ra) = await SendAsync(HttpMethod.Get, "merchants/webhooks", null, ct);
        if (err is not null) return ShippitResult<IReadOnlyList<ShippitWebhookInfo>>.Fail(ShippitOutcome.RetrySafe, err);
        using var _ = doc;
        if (status is >= 200 and < 300)
        {
            var list = new List<ShippitWebhookInfo>();
            var root = doc?.RootElement;
            var arr = root is { ValueKind: JsonValueKind.Array } ? root : Prop(root, "response") ?? Prop(root, "webhooks");
            if (arr is { ValueKind: JsonValueKind.Array } a)
                foreach (var w in a.EnumerateArray())
                    list.Add(new ShippitWebhookInfo(Str(w, "publishable_id") ?? Str(w, "id") ?? "", Str(w, "webhook_url"),
                        !(w.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False)));
            return ShippitResult<IReadOnlyList<ShippitWebhookInfo>>.Ok(list, status);
        }
        return Classify<IReadOnlyList<ShippitWebhookInfo>>(status, doc, write: false, ra);
    }

    public async Task<ShippitResult<ShippitWebhookInfo>> CreateWebhookAsync(string url, string token, CancellationToken ct)
    {
        var body = new
        {
            webhook_url = url,
            enabled_states = ShippitStateMap.WebhookStates,
            hide_delivery_address = true,       // we never need the address back
            hide_receiver_details = true,
            enabled = true,
            headers = new[] { new { key = ShippitOptions.WebhookHeaderName, value = token } }
        };
        var (status, doc, err, ra) = await SendAsync(HttpMethod.Post, "merchants/webhooks", body, ct);
        if (err is not null) return ShippitResult<ShippitWebhookInfo>.Fail(ShippitOutcome.Unknown, err);
        using var _ = doc;
        if (status is >= 200 and < 300)
        {
            var n = Unwrap(doc);
            return ShippitResult<ShippitWebhookInfo>.Ok(new ShippitWebhookInfo(Str(n, "publishable_id") ?? Str(n, "id") ?? "", url, true), status);
        }
        return Classify<ShippitWebhookInfo>(status, doc, write: true, ra);
    }

    // ---------------- plumbing ----------------
    private async Task<(int Status, JsonDocument? Doc, string? TransportError, TimeSpan? RetryAfter)> SendAsync(HttpMethod m, string path, object? body, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(m, path);
            if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            JsonDocument? doc = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { doc = JsonDocument.Parse(text); } catch (JsonException) { /* plain-string / HTML error bodies exist */ }
            }
            TimeSpan? retryAfter = (int)resp.StatusCode == 429 ? resp.Headers.RetryAfter?.Delta : null;
            return ((int)resp.StatusCode, doc, null, retryAfter);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { log.LogWarning("Shippit {Method} {Path}: timeout", m.Method, SafePath(path)); return (0, null, "transport.timeout", null); }
        catch (HttpRequestException e)
        {
            // Connect-level failures mean the request never left; but we can't tell connect from mid-flight reset here, so stay conservative.
            log.LogWarning("Shippit {Method} {Path}: transport error ({Kind})", m.Method, SafePath(path), e.HttpRequestError);
            return (0, null, "transport.error", null);
        }
    }

    // path may contain a tracking number: fine to log (not personal data), but strip anything after '?'.
    private static string SafePath(string p) => p.Split('?')[0];

    private static ShippitResult<T> Classify<T>(int status, JsonDocument? doc, bool write, TimeSpan? retryAfter)
    {
        var code = Safe(ErrorCode(doc));
        switch (status)
        {
            case 429:
                return ShippitResult<T>.Fail(ShippitOutcome.RetrySafe, "rate_limited", status, retryAfter ?? TimeSpan.FromSeconds(30));
            case 404:
                return ShippitResult<T>.Fail(ShippitOutcome.NotFound, code ?? "not_found", status);
            case 401:
            case 403:
                return ShippitResult<T>.Fail(ShippitOutcome.Permanent, code ?? "auth_rejected", status);
            case 400:
            case 422:
                return ShippitResult<T>.Fail(ShippitOutcome.Permanent, code ?? $"rejected_{status}", status);
            case >= 500:
                return ShippitResult<T>.Fail(write ? ShippitOutcome.Unknown : ShippitOutcome.RetrySafe, code ?? $"server_{status}", status);
            default:
                return ShippitResult<T>.Fail(write ? ShippitOutcome.Unknown : ShippitOutcome.Permanent, code ?? $"http_{status}", status);
        }
    }

    // ---- tolerant JSON helpers (the exact envelope isn't pinned down by the docs we could read) ----
    private static JsonElement? Unwrap(JsonDocument? d)
    {
        if (d is null) return null;
        var root = d.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object) return r;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Object
            && !root.TryGetProperty("tracking_number", out _)) return o;
        return root;
    }

    private static JsonElement? Prop(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static string? Str(JsonElement? e, string name)
    {
        var v = Prop(e, name);
        return v is null ? null : v.Value.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString(),
            JsonValueKind.Number => v.Value.GetRawText(),
            _ => null
        };
    }

    private static string? DocUrl(JsonElement? order)
    {
        var docs = Prop(order, "documents");
        if (docs is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("shipping_label", out var sl)) return Str(sl, "url");
        if (docs is { ValueKind: JsonValueKind.Array } a)
            foreach (var x in a.EnumerateArray())
                if (Str(x, "type") == "shipping_label" || x.TryGetProperty("url", out _)) return Str(x, "url");
        return null;
    }

    private static string? ErrorCode(JsonDocument? d)
    {
        if (d is null || d.RootElement.ValueKind != JsonValueKind.Object) return null;
        var root = d.RootElement;
        if (root.TryGetProperty("error", out var e))
        {
            if (e.ValueKind == JsonValueKind.String) return e.GetString();
            if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0 && e[0].ValueKind == JsonValueKind.String) return e[0].GetString();
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) return c.GetString();
        }
        return null;
    }

    /// <summary>Only short snake_case identifiers survive. Anything else (free text that could echo an address) is dropped.</summary>
    internal static string? Safe(string? code) => code is not null && CodeRx().IsMatch(code) ? code : null;

    [GeneratedRegex(@"^[a-z0-9_.]{1,64}$")] private static partial Regex CodeRx();
}
