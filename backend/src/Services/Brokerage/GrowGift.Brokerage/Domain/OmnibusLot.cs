using GrowGift.SharedKernel.Domain;

namespace GrowGift.Brokerage.Domain;

public enum LotStatus { Held = 0, TransferPending = 1, Transferred = 2, Liquidated = 3 }

/// <summary>
/// Our ledger of what GrowGift holds in the ViewTrade omnibus account, keyed
/// by gift order. This is the reconciliation source of truth: a nightly job
/// compares the sum of Held lots per symbol against the broker's omnibus
/// position and alerts on any drift.
/// </summary>
public sealed class OmnibusLot : AggregateRoot<Guid>
{
    private OmnibusLot() { }

    public Guid OrderId { get; private set; }
    public string ProviderKey { get; private set; } = default!;
    public string BrokerLotRef { get; private set; } = default!;
    public string Symbol { get; private set; } = default!;
    public decimal Quantity { get; private set; }
    public decimal AverageFillPrice { get; private set; }
    public LotStatus Status { get; private set; }
    public string? TransferRef { get; private set; }
    public string? DestinationAccountRef { get; private set; }
    public DateTimeOffset AcquiredAtUtc { get; private set; }
    public DateTimeOffset? TransferredAtUtc { get; private set; }

    public static OmnibusLot Record(Guid orderId, string providerKey, AcquisitionResult acquisition, string symbol) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            ProviderKey = providerKey,
            BrokerLotRef = acquisition.OmnibusLotRef,
            Symbol = symbol,
            Quantity = acquisition.Quantity,
            AverageFillPrice = acquisition.AverageFillPrice,
            Status = LotStatus.Held,
            AcquiredAtUtc = DateTimeOffset.UtcNow
        };

    public void BeginTransfer(string destinationAccountRef)
    {
        if (Status != LotStatus.Held)
            throw new InvalidOperationException($"Lot {Id} is {Status}; only Held lots can be transferred.");
        Status = LotStatus.TransferPending;
        DestinationAccountRef = destinationAccountRef;
    }

    public void CompleteTransfer(string transferRef)
    {
        Status = LotStatus.Transferred;
        TransferRef = transferRef;
        TransferredAtUtc = DateTimeOffset.UtcNow;
    }
}
