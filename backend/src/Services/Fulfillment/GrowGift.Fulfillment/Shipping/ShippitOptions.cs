namespace GrowGift.Fulfillment.Shipping;

/// <summary>
/// Shippit configuration. Staging ONLY for now: <see cref="ShippitGuard"/> refuses anything else, including the
/// production hostname, so a copied production URL or secret can't be used by mistake.
/// The API secret comes from the environment (SHIPPIT_API_SECRET), never from a file in the repo.
/// </summary>
public sealed class ShippitOptions
{
    public const string StagingHost = "app.staging.shippit.com";
    public const string DefaultBaseUrl = "https://app.staging.shippit.com/api/3";

    /// <summary>Must be "staging". Anything else is rejected.</summary>
    public string Environment { get; set; } = "staging";
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    /// <summary>Bearer secret. Never logged and never serialised.</summary>
    public string? ApiSecret { get; set; }
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Shippit courier_type for new orders (e.g. "standard"). The merchant's enabled services decide what is valid.</summary>
    public string CourierType { get; set; } = "standard";
    public bool AutoBook { get; set; } = true;
    /// <summary>"Yes" or "No" — whether the courier may leave the parcel unattended.</summary>
    public string AuthorityToLeave { get; set; } = "No";

    /// <summary>Parcel size/weight per box SKU (kg / metres). Optional for order creation, so unset means "not sent".</summary>
    public Dictionary<string, ParcelSpec> Parcels { get; set; } = new();
    public ParcelSpec DefaultParcel { get; set; } = new();

    // ---- retry / reconciliation ----
    public int MaxTransientAttempts { get; set; } = 8;
    public int LabelMaxAttempts { get; set; } = 12;
    public int BookMaxAttempts { get; set; } = 5;
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>Webhooks are primary. Polling starts only for shipments with no event for this long.</summary>
    public TimeSpan ReconcileAfterSilence { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromHours(6);
    public int ReconcileBatch { get; set; } = 20;
    /// <summary>An order whose creation outcome is unknown is raised for a human after this long.</summary>
    public TimeSpan UnconfirmedAlertAfter { get; set; } = TimeSpan.FromHours(2);
    public TimeSpan WorkerTick { get; set; } = TimeSpan.FromSeconds(15);

    // ---- webhook ----
    /// <summary>Public HTTPS URL Shippit will POST to (…/api/v1/shipping/webhooks/shippit). Empty = don't auto-register.</summary>
    public string? WebhookUrl { get; set; }
    /// <summary>Shared secret sent by Shippit in <see cref="WebhookHeaderName"/>. Required to accept any webhook.</summary>
    public string? WebhookToken { get; set; }
    public const string WebhookHeaderName = "X-GrowGift-Webhook-Token";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiSecret);

    public ParcelSpec ParcelFor(string? boxSku) =>
        boxSku is not null && Parcels.TryGetValue(boxSku, out var p) ? p : DefaultParcel;
}

public sealed class ParcelSpec
{
    public decimal? WeightKg { get; set; }
    public decimal? LengthM { get; set; }
    public decimal? WidthM { get; set; }
    public decimal? DepthM { get; set; }
}

public sealed class ShippitConfigurationException(string message) : Exception(message);

public static class ShippitGuard
{
    /// <summary>Throws unless the options point at Shippit STAGING over HTTPS. Pure and unit-tested.</summary>
    public static void EnsureStaging(ShippitOptions o)
    {
        if (!string.Equals(o.Environment?.Trim(), "staging", StringComparison.OrdinalIgnoreCase))
            throw new ShippitConfigurationException("Shippit environment must be 'staging'. Production is not enabled in this build.");

        if (!Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri))
            throw new ShippitConfigurationException("Shippit BaseUrl is not a valid absolute URL.");
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ShippitConfigurationException("Shippit BaseUrl must use HTTPS.");
        if (!uri.Host.Equals(ShippitOptions.StagingHost, StringComparison.OrdinalIgnoreCase))
            throw new ShippitConfigurationException($"Shippit BaseUrl host must be {ShippitOptions.StagingHost}; refusing '{uri.Host}'.");
        if (uri.UserInfo.Length > 0 || !string.IsNullOrEmpty(uri.Query))
            throw new ShippitConfigurationException("Shippit BaseUrl must not contain credentials or a query string.");
    }

    /// <summary>The normalised base address (always ends with '/'), after EnsureStaging.</summary>
    public static Uri BaseAddress(ShippitOptions o)
    {
        EnsureStaging(o);
        return new Uri(o.BaseUrl.TrimEnd('/') + "/");
    }

    /// <summary>Webhook URLs must be public HTTPS (Shippit documents http/https; we only ever register https).</summary>
    public static bool IsAcceptableWebhookUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0
        && !u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
}
