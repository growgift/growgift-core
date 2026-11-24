using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using GrowGift.Identity.Billing;
using GrowGift.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace GrowGift.Identity.Infrastructure;

/// <summary>
/// Issues RS256 access tokens (15 min) and opaque refresh tokens (30 days,
/// rotated on every use, stored hashed). The RSA private key is loaded from
/// Secrets Manager; the public JWKS is served at /.well-known/jwks.json for
/// resource services to validate against.
/// </summary>
public sealed class TokenService
{
    private readonly RsaSecurityKey _key;
    private readonly string _issuer;
    private readonly BillingOptions _billing;

    public TokenService(IConfiguration config, BillingOptions billing)
    {
        _billing = billing;
        var rsa = RSA.Create();
        var pem = Environment.GetEnvironmentVariable("GG_JWT_PRIVATE_KEY_PEM");
        if (pem is not null) rsa.ImportFromPem(pem);
        else rsa.KeySize = 2048; // dev-only ephemeral key

        _key = new RsaSecurityKey(rsa) { KeyId = "gg-signing-1" };
        _issuer = config["Auth:Issuer"] ?? "https://identity.growgift.com";
    }

    /// <summary>
    /// The one way tokens are issued: reads the user's CURRENT subscription so billing_ok in the token always
    /// reflects stored state (login, refresh and every re-issue go through here).
    /// </summary>
    public async Task<string> IssueAccessTokenAsync(User user, IdentityDbContext db, CancellationToken ct)
    {
        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == user.Id, ct);
        var state = sub is null ? null : new SubscriptionState(sub.Status, sub.TrialUsed);
        var ok = SubscriptionPolicy.BillingOk(user.Role, user.CreatedAtUtc, state, _billing.EnforceFromUtc, _billing.TrialMonths, DateTimeOffset.UtcNow);
        var prompt = SubscriptionPolicy.NeedsCardPrompt(user.Role, state, _billing.EnforceFromUtc);
        var cardRequired = SubscriptionPolicy.CardRequiredAtRegistration(user.Role, user.CreatedAtUtc, state, _billing.EnforceFromUtc);
        return IssueAccessToken(user, ok, prompt, cardRequired, sub?.Status ?? SubscriptionStatus.None);
    }

    private string IssueAccessToken(User user, bool billingOk, bool billingPrompt, bool cardRequired, string subscriptionStatus)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("role", user.Role.ToString().ToLowerInvariant()),
            new("kyc_status", user.Kyc.ToString().ToLowerInvariant()),
            // Lets Brokerage refuse to match this user against a ViewTrade account by e-mail until the
            // address is proven (login itself doesn't require verification).
            new("email_verified", user.EmailVerified.ToString().ToLowerInvariant()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        if (user.ViewTradeAccountRef is not null)
            claims.Add(new Claim("viewtrade_account", user.ViewTradeAccountRef));
        // Lets Gifting check "does this buyer have somewhere to ship the box"
        // straight from the token — no synchronous call back to Identity.
        claims.Add(new Claim("has_shipping_address", (user.ShippingAddress is not null).ToString().ToLowerInvariant()));

        // Identity alone decides billing; Gifting and Family only obey this claim for the few actions they gate.
        claims.Add(new Claim(GrowGift.SharedKernel.Billing.BillingGate.ClaimType, billingOk.ToString().ToLowerInvariant()));
        claims.Add(new Claim("subscription", subscriptionStatus));
        claims.Add(new Claim("billing_prompt", billingPrompt.ToString().ToLowerInvariant()));
        claims.Add(new Claim("billing_card_required", cardRequired.ToString().ToLowerInvariant()));

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: "growgift.api",
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public JsonWebKey PublicJwk() => JsonWebKeyConverter.ConvertFromSecurityKey(_key);
}
