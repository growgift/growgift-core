using System.Security.Claims;

namespace GrowGift.SharedKernel.Billing;

/// <summary>
/// Reads the subscription decision Identity put in the caller's access token. Identity is the only place that
/// decides (it knows the plan, the trial and whether enforcement is on); other services just obey the claim.
///
/// Fail-open on purpose: a token with no <c>billing_ok</c> claim (issued before billing existed, or by a service
/// that does not know about it) is NOT blocked. Only an explicit "false" blocks, and only the actions a service
/// chooses to gate — redeeming, selling, viewing and withdrawing are never gated.
/// </summary>
public static class BillingGate
{
    public const string ClaimType = "billing_ok";

    public static bool IsBlocked(ClaimsPrincipal user) =>
        string.Equals(user.FindFirst(ClaimType)?.Value, "false", StringComparison.OrdinalIgnoreCase);

    public static readonly object BlockedBody = new
    {
        error = new
        {
            code = "subscription.required",
            message = "Your GrowGift subscription needs attention before you can do this. Your account, gifts and investments are safe — you can still sign in, redeem and sell."
        }
    };
}
