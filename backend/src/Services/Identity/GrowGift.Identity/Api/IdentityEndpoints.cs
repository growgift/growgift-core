using System.Security.Claims;
using GrowGift.Identity.Domain;
using GrowGift.Identity.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace GrowGift.Identity.Api;

public static class IdentityEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/auth");

        v1.MapPost("/register", async ([FromBody] RegisterRequest body,
            IdentityDbContext db, IHttpClientFactory httpFactory, IConfiguration config,
            ILogger<RegisterRequest> logger, CancellationToken ct) =>
        {
            var exists = await db.Users.AnyAsync(u => u.Email == body.Email.ToLowerInvariant(), ct);
            if (exists) return Results.Conflict(new { error = new { code = "user.email_taken", message = "An account with this email already exists." } });

            if (body.Password.Length < 12)
                return Results.BadRequest(new { error = new { code = "user.password_too_short", message = "Password must be at least 12 characters." } });

            // Buyer, recipient, and parent are now genuinely distinct roles —
            // previously recipients silently registered as UserRole.Buyer too,
            // which made "is this a buyer" unanswerable from the role alone.
            var role = body.Role?.ToLowerInvariant() switch
            {
                "parent" => UserRole.Parent,
                "recipient" => UserRole.Recipient,
                _ => UserRole.Buyer
            };

            GrowGift.SharedKernel.Domain.ShippingAddress? shipping = null;
            if (body.ShippingAddress is not null)
            {
                var a = body.ShippingAddress;
                var addressResult = GrowGift.SharedKernel.Domain.ShippingAddress.Create(
                    a.FullName, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.CountryCode, a.Phone);
                if (addressResult.IsFailure) return Results.BadRequest(new { error = new { code = addressResult.Error.Code, message = addressResult.Error.Message } });
                shipping = addressResult.Value;
            }

            var user = User.Register(body.Email, PasswordHasher.Hash(body.Password), role, shipping);
            if (user.IsFailure) return Results.BadRequest(new { error = new { code = user.Error.Code, message = user.Error.Message } });

            // Email verification: a CSPRNG token whose hash alone is stored.
            var tokenBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var token = Convert.ToHexString(tokenBytes);
            var tokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(tokenBytes));
            user.Value.SetEmailVerificationToken(tokenHash);

            db.Users.Add(user.Value);
            await db.SaveChangesAsync(ct);

            // Absolute link to the web app's verify page. App:WebBaseUrl is the
            // address people actually open the site at (http://localhost:3000
            // locally, https://growgift.com.au in production).
            var webBase = (config["App:WebBaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
            var verificationUrl = $"{webBase}/verify-email?token={token}";

            // Email it through Notifications (SendGrid). A failure here must not
            // lose the account: the user can still sign in, and in Development
            // we fall back to returning the link so testing isn't blocked.
            var emailSent = false;
            try
            {
                var res = await httpFactory.CreateClient("notifications").PostAsJsonAsync(
                    "/internal/v1/notifications/verify-email",
                    new { email = user.Value.Email, verificationUrl }, ct);
                emailSent = res.IsSuccessStatusCode;
                if (!emailSent)
                    logger.LogWarning("Verification email for {UserId} was not sent: {Status}", user.Value.Id, res.StatusCode);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Verification email for {UserId} could not be sent", user.Value.Id);
            }

            var isDev = app.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment();
            return Results.Created($"/api/v1/users/{user.Value.Id}", new
            {
                userId = user.Value.Id,
                message = emailSent
                    ? "Account created. Check your email for a verification link."
                    : "Account created, but we couldn't send the verification email just now.",
                emailSent,
                devVerificationUrl = (isDev && !emailSent) ? verificationUrl : null
            });
        });

        // Verify the emailed link. Token arrives as hex; only its hash was stored.
        v1.MapPost("/verify-email", async ([FromBody] VerifyEmailRequest body,
            IdentityDbContext db, CancellationToken ct) =>
        {
            byte[] tokenBytes;
            try { tokenBytes = Convert.FromHexString(body.Token); }
            catch (FormatException) { return Results.BadRequest(new { error = new { code = "email.invalid_token", message = "Invalid verification link." } }); }

            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(tokenBytes));
            var user = await db.Users.FirstOrDefaultAsync(u => u.EmailVerificationTokenHash == hash, ct);
            if (user is null) return Results.BadRequest(new { error = new { code = "email.invalid_token", message = "Invalid verification link." } });

            var verified = user.VerifyEmail(hash);
            if (verified.IsFailure) return Results.BadRequest(new { error = new { code = verified.Error.Code, message = verified.Error.Message } });

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { message = "Email verified — you're all set." });
        });

        v1.MapPost("/login", async ([FromBody] LoginRequest body,
            IdentityDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == body.Email.ToLowerInvariant(), ct);
            var now = DateTimeOffset.UtcNow;

            // Constant-shape response for unknown email vs wrong password.
            if (user is null || user.IsLockedOut(now) || !PasswordHasher.Verify(body.Password, user.PasswordHash))
            {
                if (user is not null && !user.IsLockedOut(now))
                {
                    user.RecordFailedLogin(now);
                    await db.SaveChangesAsync(ct);
                }
                return Results.Unauthorized();
            }

            if (user.MfaEnabled)
            {
                if (body.TotpCode is null) return Results.Ok(new { mfaRequired = true });
                var secret = TotpVault.Decrypt(user.TotpSecretEncrypted!);
                var totp = new Totp(Base32Encoding.ToBytes(secret));
                if (!totp.VerifyTotp(body.TotpCode, out _, VerificationWindow.RfcSpecifiedNetworkDelay))
                {
                    user.RecordFailedLogin(now);
                    await db.SaveChangesAsync(ct);
                    return Results.Unauthorized();
                }
            }

            user.RecordSuccessfulLogin();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { accessToken = await tokens.IssueAccessTokenAsync(user, db, ct), expiresInSeconds = 900 });
        });

        // DEVELOPMENT ONLY: simulate a successful Persona KYC outcome so the
        // full redeem flow can be tested before the real integration is live.
        // Not mapped outside the Development environment.
        if (app.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            v1.MapPost("/dev/approve-kyc", async (ClaimsPrincipal principal,
                IdentityDbContext db, TokenService tokens, CancellationToken ct) =>
            {
                var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
                var user = await db.Users.FirstAsync(u => u.Id == userId, ct);

                if (user.Kyc == KycStatus.NotStarted) user.StartKyc($"dev-inquiry-{userId}");
                var completed = user.CompleteKyc("approved", $"VT-DEV-{userId.ToString()[..8].ToUpperInvariant()}");
                if (completed.IsFailure) return Results.Conflict(new { error = new { code = completed.Error.Code, message = completed.Error.Message } });

                await db.SaveChangesAsync(ct);
                // Fresh token so the new kyc_status / viewtrade_account claims apply immediately.
                return Results.Ok(new { accessToken = await tokens.IssueAccessTokenAsync(user, db, ct), expiresInSeconds = 900 });
            }).RequireAuthorization();
        }

        // Persona KYC webhook (HMAC verified in middleware).
        v1.MapPost("/webhooks/persona", async ([FromBody] PersonaWebhook body,
            IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(
                u => u.PersonaInquiryRef == body.InquiryRef, ct);
            if (user is null) return Results.Ok(); // unknown inquiry — ack and drop

            user.CompleteKyc(body.Outcome, body.ViewTradeAccountRef);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        v1.MapGet("/me", async (ClaimsPrincipal principal, IdentityDbContext db, CancellationToken ct) =>
        {
            var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null) return Results.NotFound();

            return Results.Ok(new
            {
                id = user.Id,
                email = user.Email,
                role = user.Role.ToString().ToLowerInvariant(),
                kycStatus = user.Kyc.ToString().ToLowerInvariant(),
                emailVerified = user.EmailVerified,
                shippingAddress = user.ShippingAddress is null ? null : new ShippingAddressDto(
                    user.ShippingAddress.FullName, user.ShippingAddress.Line1, user.ShippingAddress.Line2,
                    user.ShippingAddress.City, user.ShippingAddress.Region, user.ShippingAddress.PostalCode,
                    user.ShippingAddress.CountryCode, user.ShippingAddress.Phone)
            });
        }).RequireAuthorization("Buyer"); // "Buyer" policy = any authenticated user

        v1.MapPut("/me/shipping-address", async ([FromBody] ShippingAddressDto body,
            ClaimsPrincipal principal, IdentityDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null) return Results.NotFound();

            var addressResult = GrowGift.SharedKernel.Domain.ShippingAddress.Create(
                body.FullName, body.Line1, body.Line2, body.City, body.Region, body.PostalCode,
                body.CountryCode, body.Phone);
            if (addressResult.IsFailure) return Results.BadRequest(new { error = new { code = addressResult.Error.Code, message = addressResult.Error.Message } });

            user.SetShippingAddress(addressResult.Value);
            await db.SaveChangesAsync(ct);

            // The buyer's existing access token still carries
            // has_shipping_address=false from whenever it was issued —
            // Gifting's order-creation check reads that claim directly
            // (see its own comment: "no synchronous call back to Identity"),
            // so without a fresh token here, a buyer who adds their address
            // mid-session keeps failing that check with their still-current,
            // still-valid, but now-stale token until it naturally expires
            // (up to 15 minutes) or they log out and back in. Re-issuing
            // immediately — same pattern as dev/approve-kyc — closes that gap.
            return Results.Ok(new { message = "Shipping address saved.", accessToken = await tokens.IssueAccessTokenAsync(user, db, ct) });
        }).RequireAuthorization("Buyer");

        // Self-service version of PromoteToParent (the other caller is
        // Family's partner-linking flow, which promotes someone else's
        // account by email). Any signed-in account — buyer, recipient,
        // whatever they originally registered as — can gain family/parent
        // capabilities this way without creating a second account, which
        // is the whole point: roles here are additive capabilities a
        // session can pick up over time, not a single exclusive label
        // fixed at signup. A no-op, returning the same fresh-token shape,
        // if they're already a parent.
        // Re-issues the caller's token from CURRENT database state, so claims that changed after login
        // (kyc_status / viewtrade_account after an existing ViewTrade account is linked, email_verified
        // after clicking the e-mail link) apply without signing in again. Issues nothing the user didn't
        // already have a right to: every claim comes from the stored user, none from the old token.
        v1.MapPost("/me/refresh", async (ClaimsPrincipal principal,
            IdentityDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null) return Results.Unauthorized();
            return Results.Ok(new { accessToken = await tokens.IssueAccessTokenAsync(user, db, ct), expiresInSeconds = 900 });
        }).RequireAuthorization("Buyer");

        v1.MapPost("/me/become-parent", async (ClaimsPrincipal principal,
            IdentityDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = await db.Users.FirstAsync(u => u.Id == userId, ct);

            // A child's read-only login must never be able to promote itself:
            // without this, a child's own token could call this endpoint and
            // receive a role=parent token (the client-side menu hiding is not
            // a security boundary — this is).
            if (user.Role == UserRole.Child)
                return Results.Json(new { error = new { code = "role.child_forbidden", message = "Child accounts can't change their own role." } },
                    statusCode: StatusCodes.Status403Forbidden);

            user.PromoteToParent();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { accessToken = await tokens.IssueAccessTokenAsync(user, db, ct) });
        }).RequireAuthorization("Buyer");

        // ---- Internal (service-to-service, no auth — same trust model as
        // Gifting's calls to Fulfillment/Notifications) ----

        // Called by Family when a parent adds a child — creates the child's
        // login (see User.RegisterChildLogin's doc for why this reuses the
        // normal email/password login mechanism under different names).
        v1.MapPost("/internal/v1/child-accounts", async ([FromBody] CreateChildAccountRequest body,
            IdentityDbContext db, CancellationToken ct) =>
        {
            // 6 digits (1M combinations) is plenty for this volume; retried
            // on the rare collision rather than needing a longer code that
            // would be more annoying for a child to type in.
            string loginCode;
            do
            {
                loginCode = Random.Shared.Next(0, 1_000_000).ToString("D6");
            } while (await db.Users.AnyAsync(u => u.Email == loginCode, ct));

            var result = User.RegisterChildLogin(loginCode, PasswordHasher.Hash(body.Pin));
            if (result.IsFailure)
                return Results.BadRequest(new { error = new { code = result.Error.Code, message = result.Error.Message } });

            db.Users.Add(result.Value);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { userId = result.Value.Id, loginCode });
        });

        // Called by Family when a parent adds a partner by email — finds
        // the existing account and promotes it to role=parent so it passes
        // the existing "Parent" policy on its next login. Doesn't create an
        // account if none exists; Family surfaces that as "ask them to sign
        // up first" rather than this endpoint silently creating one with no
        // password the person never set.
        v1.MapPost("/internal/v1/users/link-as-parent", async ([FromBody] LinkAsParentRequest body,
            IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == body.Email.ToLowerInvariant(), ct);
            if (user is null) return Results.NotFound();

            user.PromoteToParent();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { userId = user.Id });
        });

        // Called by Fulfillment (server-to-server) to build a courier order from the purchaser's saved address.
        // Returns personal data, so — unlike the endpoints above — it is NOT left open on the trust model alone:
        // it needs the shared internal key, fails closed when none is configured, and the Gateway also refuses
        // every /internal/ path from outside. The address is never logged here.
        v1.MapGet("/internal/v1/users/{userId:guid}/shipping-address", async (Guid userId,
            HttpRequest request, IConfiguration config, IdentityDbContext db, CancellationToken ct) =>
        {
            var expected = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_KEY") ?? config["Internal:ServiceKey"];
            var presented = request.Headers["X-Internal-Key"].ToString();
            if (string.IsNullOrEmpty(expected)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var a = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(presented));
            var b = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected));
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b)) return Results.Unauthorized();

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user?.ShippingAddress is null) return Results.NotFound();
            var addr = user.ShippingAddress;
            return Results.Ok(new
            {
                fullName = addr.FullName, line1 = addr.Line1, line2 = addr.Line2, city = addr.City, region = addr.Region,
                postalCode = addr.PostalCode, countryCode = addr.CountryCode, phone = addr.Phone, email = user.Email
            });
        });

        app.MapGet("/.well-known/jwks.json", (TokenService tokens) =>
            Results.Json(new { keys = new[] { tokens.PublicJwk() } }));

        // Minimal OIDC discovery document. Gifting/Family/etc. configure
        // Authority = this service's base URL, and ASP.NET Core's JWT Bearer
        // handler fetches this well-known path automatically to learn where
        // the signing keys live (jwks_uri) — without it, Authority-based
        // validation 404s before ever reaching /.well-known/jwks.json.
        app.MapGet("/.well-known/openid-configuration", (IConfiguration config) =>
        {
            var issuer = config["Auth:Issuer"] ?? "https://identity.growgift.com";
            var selfUrl = config["Auth:SelfUrl"] ?? "http://localhost:5001";
            return Results.Json(new
            {
                issuer,
                jwks_uri = $"{selfUrl}/.well-known/jwks.json",
                id_token_signing_alg_values_supported = new[] { "RS256" }
            });
        });
    }

    public sealed record CreateChildAccountRequest(string Pin);
    public sealed record LinkAsParentRequest(string Email);

    public sealed record ShippingAddressDto(
        string FullName, string Line1, string? Line2, string City,
        string Region, string PostalCode, string CountryCode, string? Phone);

    public sealed record RegisterRequest(string Email, string Password, string? Role, ShippingAddressDto? ShippingAddress);
    public sealed record VerifyEmailRequest(string Token);
    public sealed record LoginRequest(string Email, string Password, string? TotpCode);
    public sealed record PersonaWebhook(string InquiryRef, string Outcome, string? ViewTradeAccountRef);
}

/// <summary>TOTP secrets are AES-256-GCM encrypted with a KMS data key. Dev fallback uses env key.</summary>
public static class TotpVault
{
    public static string Decrypt(string ciphertext) => ciphertext; // TODO: KMS envelope decryption
    public static string Encrypt(string plaintext) => plaintext;   // TODO: KMS envelope encryption
}
