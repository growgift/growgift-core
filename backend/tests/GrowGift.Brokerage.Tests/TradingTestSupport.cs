using System.Net;
using System.Text;
using System.Text.Json;
using GrowGift.Brokerage.Infrastructure.ViewTrade;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;
using GrowGift.Infrastructure.Common.Idempotency;
using Microsoft.Extensions.Logging;

namespace GrowGift.Brokerage.Tests;

/// <summary>Everything here is a fake: no network, no real ViewTrade, no real orders.</summary>
internal sealed record Captured(string Host, string Path, string? Auth, string Body);

internal sealed class FakeViewTrade : HttpMessageHandler
{
    public const string InitialToken = "INITIAL-TOKEN-AAA";
    public const string ImpersonatedToken = "IMPERSONATED-TOKEN-BBB";
    public const string WatchmanToken = "WATCHMAN-TOKEN-CCC";
    public const string SessionId = "SESSION-ID-DDD";

    public List<Captured> Calls { get; } = new();
    public Func<string, HttpResponseMessage?>? Override { get; set; }
    public string UsersJson { get; set; } =
        """{"users_list":[{"id":124246,"email":"a@x.com","status":"active","branches":[{"id":2641}]}]}""";
    public string SsoStage { get; set; } = "ESTABLISHED";
    public string SessionValue { get; set; } = SessionId;
    public Func<int, HttpResponseMessage>? Order { get; set; }
    public int OrderCalls;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
        var path = r.RequestUri!.AbsolutePath.TrimStart('/');
        Calls.Add(new Captured(r.RequestUri.Host, path, r.Headers.Authorization is null
            ? (r.Headers.TryGetValues("Authorization", out var v) ? string.Join(",", v) : null)
            : r.Headers.Authorization.ToString(), body));

        var o = Override?.Invoke(path);
        if (o is not null) return o;

        return path switch
        {
            "api/v2/users/b2c/list" => Json(UsersJson),
            "api/v1/auth/b2c/login/impersonate" => Json($$$"""{"tokens":{"access_token":"{{{ImpersonatedToken}}}"}}"""),
            "api/v1/auth/watchmen/authentication-tokens" => Json($$$"""{"data":{"token":"{{{WatchmanToken}}}"}}"""),
            "api/auth/v1/sso/platform" => Json($$"""{"sessionId":"{{SessionValue}}","stage":"{{SsoStage}}"}"""),
            "api/orders/v2/place/equity" => (Order ?? (_ => Json("""{"OrderRef":"XX123456"}""")))(Interlocked.Increment(ref OrderCalls)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri(name switch
        {
            "ViewTradeUma" => "https://uma.test/",
            "ViewTradeSso" => "https://sso.test/",
            "ViewTradeTrading" => "https://trading.test/",
            "Fx" => "https://fx.test/",
            _ => throw new InvalidOperationException(name)
        })
    };
}

internal sealed class FakeInitialTokens : IViewTradeAuthTokenProvider
{
    public int Fetches, Invalidations;
    public Task<string> GetAccessTokenAsync(CancellationToken ct) { Fetches++; return Task.FromResult(FakeViewTrade.InitialToken); }
    public void InvalidateCachedToken() => Invalidations++;
}

internal sealed class MemoryIdempotency : IIdempotencyStore
{
    private readonly Dictionary<string, string> _claims = new(), _responses = new();
    public Task<bool> TryClaimAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    { lock (_claims) return Task.FromResult(_claims.TryAdd(key, "c")); }
    public Task<string?> GetCachedResponseAsync(string key, CancellationToken ct = default)
    { lock (_claims) return Task.FromResult(_responses.GetValueOrDefault(key)); }
    public Task StoreResponseAsync(string key, string json, TimeSpan ttl, CancellationToken ct = default)
    { lock (_claims) _responses[key] = json; return Task.CompletedTask; }
}

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f)
    { Lines.Add(f(s, ex)); if (ex is not null) Lines.Add(ex.ToString()); }
}

internal static class Jwt
{
    public static string WithExp(DateTimeOffset exp)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"exp\":{exp.ToUnixTimeSeconds()}}}")}.sig";
    }
}
