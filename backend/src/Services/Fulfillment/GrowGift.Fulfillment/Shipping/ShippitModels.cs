namespace GrowGift.Fulfillment.Shipping;

public enum ShippitOutcome
{
    Ok,
    /// <summary>Definitely NOT processed (e.g. 429, or a failed connect) — safe to send again.</summary>
    RetrySafe,
    /// <summary>Label not ready yet (422 label_failed_*processing/generating/not_processed) — poll again.</summary>
    StillProcessing,
    /// <summary>Rejected for a reason a retry won't fix (400/403/422 validation, missing courier…). Needs a person or a data fix.</summary>
    Permanent,
    /// <summary>For writes only: we can't tell whether Shippit acted (timeout, 5xx, dropped connection). NEVER resend blindly.</summary>
    Unknown,
    NotFound
}

/// <summary>Result of one Shippit call. <see cref="Code"/> is a short sanitised label — never a response body, address or secret.</summary>
public sealed record ShippitResult<T>(ShippitOutcome Outcome, T? Value, string? Code = null, int? HttpStatus = null, TimeSpan? RetryAfter = null)
{
    public bool IsOk => Outcome == ShippitOutcome.Ok;
    public static ShippitResult<T> Ok(T v, int status = 200) => new(ShippitOutcome.Ok, v, null, status);
    public static ShippitResult<T> Fail(ShippitOutcome o, string code, int? status = null, TimeSpan? retryAfter = null) => new(o, default, code, status, retryAfter);
}

public sealed record ShippitAddress(string Name, string Line1, string? Line2, string City, string State, string Postcode, string Country, string? Phone, string Email);

public sealed record ShippitCreateRequest(
    string Reference,          // retailer_invoice
    string? BoxSku,
    ShippitAddress To,
    string CourierType,
    string AuthorityToLeave,
    ParcelSpec Parcel);

public sealed record ShippitCreated(string TrackingNumber, string? CourierName, string? State, string? TrackingUrl);

public sealed record ShippitLabel(string? LabelUrl, string? CourierName, string? ManifestNumber, string? ManifestId, string? State);

public sealed record ShippitBooked(string? ManifestNumber, string? ManifestId, string? ManifestPdfUrl);

public sealed record ShippitTrackingEvent(string State, DateTimeOffset? Time);
public sealed record ShippitTracking(string? State, IReadOnlyList<ShippitTrackingEvent> History, string? CourierName, string? TrackingUrl);

public sealed record ShippitWebhookInfo(string Id, string? Url, bool Enabled);

public interface IShippitClient
{
    Task<ShippitResult<ShippitCreated>> CreateOrderAsync(ShippitCreateRequest req, CancellationToken ct);
    Task<ShippitResult<ShippitLabel>> GetLabelAsync(string trackingNumber, CancellationToken ct);
    Task<ShippitResult<ShippitBooked>> BookAsync(string trackingNumber, CancellationToken ct);
    Task<ShippitResult<ShippitTracking>> GetTrackingAsync(string trackingNumber, CancellationToken ct);
    Task<ShippitResult<IReadOnlyList<ShippitWebhookInfo>>> ListWebhooksAsync(CancellationToken ct);
    Task<ShippitResult<ShippitWebhookInfo>> CreateWebhookAsync(string url, string token, CancellationToken ct);
}
