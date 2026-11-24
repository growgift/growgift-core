using GrowGift.Brokerage.Domain;
using GrowGift.Infrastructure.Common.Outbox;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Brokerage.Infrastructure.Persistence;

/// <summary>
/// Buy-at-redemption ledger: one row per redeemed gift, recording the market
/// purchase made directly into the recipient's disclosed account. This is the
/// reconciliation source of truth against broker statements.
/// </summary>
public sealed class RedemptionPurchase
{
    public Guid GiftId { get; init; }               // PK — one purchase per gift
    public Guid RecipientUserId { get; init; }
    public string AccountRef { get; set; } = default!;
    public string Symbol { get; init; } = default!;
    public decimal NotionalAmount { get; init; }
    public string Currency { get; init; } = default!;
    public decimal FilledQuantity { get; init; }
    public decimal AverageFillPrice { get; init; }
    public string BrokerOrderRef { get; set; } = default!;   // ViewTrade's OrderRef ("" when none was returned)
    /// <summary>accepted | pending | partiallyfilled | filled | rejected | unconfirmed | needs_attention. "accepted" ≠ executed.</summary>
    public string OrderState { get; set; } = "accepted";
    /// <summary>What was actually sent to ViewTrade (USD) and the AUD→USD rate used. NotionalAmount/Currency stay the gift's own.</summary>
    public decimal? UsdAmount { get; set; }
    public decimal? FxRate { get; set; }
    public DateTimeOffset PurchasedAtUtc { get; init; }
}

public sealed class BrokerageDbContext(DbContextOptions<BrokerageDbContext> options) : DbContext(options)
{
    public DbSet<OmnibusLot> OmnibusLots => Set<OmnibusLot>();
    public DbSet<RedemptionPurchase> RedemptionPurchases => Set<RedemptionPurchase>();
    public DbSet<OnboardingApplication> OnboardingApplications => Set<OnboardingApplication>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("brokerage");

        b.Entity<OmnibusLot>(e =>
        {
            e.ToTable("omnibus_lots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Quantity).HasPrecision(18, 8);
            e.Property(x => x.AverageFillPrice).HasPrecision(18, 4);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.HasIndex(x => x.OrderId).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Status });
            e.Ignore(x => x.DomainEvents);
        });

        b.Entity<RedemptionPurchase>(e =>
        {
            e.ToTable("redemption_purchases");
            e.HasKey(x => x.GiftId);
            e.Property(x => x.NotionalAmount).HasPrecision(18, 4);
            e.Property(x => x.FilledQuantity).HasPrecision(18, 8);
            e.Property(x => x.AverageFillPrice).HasPrecision(18, 4);
            e.Property(x => x.UsdAmount).HasPrecision(18, 4);
            e.Property(x => x.FxRate).HasPrecision(18, 8);
            e.HasIndex(x => x.RecipientUserId);
        });

        b.Entity<OnboardingApplication>(e =>
        {
            e.ToTable("onboarding_applications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            // The public Answers/MultiAnswers/DocumentRefs properties are
            // read-only dictionary views for callers — EF can't map those
            // directly, so they're ignored; only the JSON-backing properties
            // below are actually persisted.
            e.Ignore(x => x.Answers);
            e.Ignore(x => x.MultiAnswers);
            e.Ignore(x => x.DocumentRefs);
            e.Ignore(x => x.ViewTradeUploadedDocuments);
            e.Property("AnswersJson").HasColumnName("answers_json").HasColumnType("jsonb");
            e.Property("MultiAnswersJson").HasColumnName("multi_answers_json").HasColumnType("jsonb");
            e.Property("DocumentRefsJson").HasColumnName("document_refs_json").HasColumnType("jsonb");
            e.Property("ViewTradeUploadedDocumentsJson").HasColumnName("viewtrade_uploaded_documents_json").HasColumnType("jsonb");
            e.HasIndex(x => x.UserId).IsUnique(); // one application per user for now
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
        });
    }
}
