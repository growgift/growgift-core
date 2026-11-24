using Stripe;

namespace GrowGift.Gifting.Infrastructure.Payments;

/// <summary>
/// Verifies Stripe webhook signatures before any payload is trusted. The
/// endpoint secret comes from Secrets Manager. Unverified payloads are
/// rejected with 400 and logged to the audit trail.
/// </summary>
public interface IStripeWebhookVerifier
{
    Event VerifyAndParse(string payloadJson, string signatureHeader);
}

public sealed class StripeWebhookVerifier : IStripeWebhookVerifier
{
    private readonly string _endpointSecret =
        Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET")
        ?? throw new InvalidOperationException("STRIPE_WEBHOOK_SECRET is not configured.");

    public Event VerifyAndParse(string payloadJson, string signatureHeader) =>
        EventUtility.ConstructEvent(payloadJson, signatureHeader, _endpointSecret, throwOnApiVersionMismatch: false);
}
