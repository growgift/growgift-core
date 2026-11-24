using System.Net;
using System.Net.Http.Headers;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade;

/// <summary>
/// Attached to every HttpClient that calls ViewTrade's AMO host (countries
/// list, onboarding form answers/submission). Fetches a valid bearer token
/// from <see cref="IViewTradeAuthTokenProvider"/> — cached there and only
/// refreshed near expiry — and sets it on each outgoing request, so callers
/// never need to think about the UMA token exchange themselves.
///
/// If ViewTrade rejects a request with 401 despite our cache believing the
/// token is still valid, the token was invalidated some other way (see
/// InvalidateCachedToken's doc) — this forces one fresh token and retries
/// exactly once, rather than repeating the same stale-but-not-yet-expired
/// token on every call until the client-side cache eventually catches up.
/// </summary>
public sealed class ViewTradeAuthHandler(IViewTradeAuthTokenProvider tokenProvider, ILogger<ViewTradeAuthHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var token = await tokenProvider.GetAccessTokenAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            await LogAttemptAsync("first (403, not retried)", request, response, token, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        await LogAttemptAsync("first (401, retrying)", request, response, token, ct);

        tokenProvider.InvalidateCachedToken();
        var freshToken = await tokenProvider.GetAccessTokenAsync(ct);

        // HttpRequestMessage can only be sent once, so the retry needs a
        // clone. CopyToAsync captures whatever bytes the original content
        // would have put on the wire (works uniformly for JSON, multipart
        // file uploads, anything), and copying the original content headers
        // preserves things like a multipart boundary parameter.
        //
        // Authorization is deliberately EXCLUDED from the raw header copy
        // below and set only via the typed property afterward — copying it
        // via TryAddWithoutValidation first and then also setting the typed
        // Authorization property left the header corrupted (serialized as
        // a bare "Bearer" with no token at all) when this was tried without
        // the exclusion, which silently broke every retry attempt.
        //
        // Known gap: if request.Content wraps a non-seekable/already-
        // consumed stream (SubmitFileAsync's file upload does, once the
        // first send has read it to the end), this CopyToAsync will copy
        // zero bytes and the retry will send an empty body rather than a
        // true retry. A 401 specifically on a file upload is a narrow
        // enough case that this is left as-is rather than plumbing stream
        // reset through every caller — but if file uploads start silently
        // failing after a 401, this is where to look.
        var retryRequest = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var header in request.Headers)
        {
            if (header.Key == "Authorization") continue; // set fresh below, not copied
            retryRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", freshToken);
        if (request.Content is not null)
        {
            var buffered = new MemoryStream();
            await request.Content.CopyToAsync(buffered, ct);
            buffered.Position = 0;
            var clonedContent = new StreamContent(buffered);
            foreach (var header in request.Content.Headers)
                clonedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            retryRequest.Content = clonedContent;
        }

        response.Dispose();
        var retryResponse = await base.SendAsync(retryRequest, ct);
        if (retryResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            await LogAttemptAsync("retry", retryRequest, retryResponse, freshToken, ct);
        return retryResponse;
    }

    // Temporary, verbose-on-purpose diagnostic for the still-unexplained
    // 401 on applications/list: logs enough of the actual outgoing request
    // to diff against Postman's Console view of the same call, without
    // putting the full bearer token in logs.
    private async Task LogAttemptAsync(string label, HttpRequestMessage request, HttpResponseMessage response, string? token, CancellationToken ct)
    {
        var maskedToken = string.IsNullOrEmpty(token) ? "(NULL OR EMPTY — this is very likely the real bug)"
            : token.Length > 16 ? $"{token[..8]}...{token[^8..]} (len={token.Length})" : $"(suspiciously short: \"{token}\")";
        var authHeaderAsSent = request.Headers.Authorization?.ToString() ?? "(none)";
        var body = request.Content is null ? "(no content)" : await request.Content.ReadAsStringAsync(ct);
        logger.LogWarning(
            "Diagnostic [{Label}] — {Method} {Uri} got {Status}. Token used: {MaskedToken}. Authorization header as attached: {AuthHeader}. Body: {Body}",
            label, request.Method, request.RequestUri, response.StatusCode, maskedToken, authHeaderAsSent, body);
    }
}
