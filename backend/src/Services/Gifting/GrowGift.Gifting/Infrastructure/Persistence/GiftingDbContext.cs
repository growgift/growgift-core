using GrowGift.Gifting.Domain;
using GrowGift.Infrastructure.Common.Outbox;
using GrowGift.SharedKernel.Domain;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Gifting.Infrastructure.Persistence;

public sealed class GiftingDbContext(DbContextOptions<GiftingDbContext> options, IPublisher publisher)
    : DbContext(options)
{
    public DbSet<GiftOrder> GiftOrders => Set<GiftOrder>();
    public DbSet<Gift> Gifts => Set<Gift>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("gifting");

        b.Entity<GiftOrder>(e =>
        {
            e.ToTable("gift_orders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(10);
            e.Property(x => x.GreetingMessage).HasMaxLength(500);
            e.Property(x => x.RecipientEmail).HasColumnName("recipient_email").HasMaxLength(254);
            e.Property(x => x.DeliveryMethod).HasColumnName("delivery_method").HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.OwnsOne(x => x.PurchaseAmount, m =>
            {
                m.Property(p => p.Amount).HasColumnName("purchase_amount").HasPrecision(18, 4);
                m.Property(p => p.Currency).HasColumnName("purchase_currency").HasMaxLength(3);
            });
            e.Property(x => x.BoxSku).HasColumnName("box_sku").HasMaxLength(20);
            e.OwnsOne(x => x.BoxPrice, m =>
            {
                m.Property(p => p.Amount).HasColumnName("box_price_amount").HasPrecision(18, 4);
                m.Property(p => p.Currency).HasColumnName("box_price_currency").HasMaxLength(3);
            });
            e.OwnsOne(x => x.PostalFee, m =>
            {
                m.Property(p => p.Amount).HasColumnName("postal_fee_amount").HasPrecision(18, 4);
                m.Property(p => p.Currency).HasColumnName("postal_fee_currency").HasMaxLength(3);
            });
            e.OwnsOne(x => x.ServiceFee, m =>
            {
                m.Property(p => p.Amount).HasColumnName("service_fee_amount").HasPrecision(18, 4);
                m.Property(p => p.Currency).HasColumnName("service_fee_currency").HasMaxLength(3);
            });
            e.Ignore(x => x.HasPhysicalBox);
            e.Ignore(x => x.TotalAmount);
            e.HasIndex(x => x.BuyerId);
            // Not unique: a combined cart checkout can pay for several
            // orders in one Stripe PaymentIntent (see
            // CreateMultiOrderCheckoutSession), so more than one order
            // legitimately shares the same value here.
            e.HasIndex(x => x.StripePaymentIntentId);
            e.Ignore(x => x.DomainEvents);
        });

        b.Entity<Gift>(e =>
        {
            e.ToTable("gifts");
            e.HasKey(x => x.Id);
            e.Property(x => x.CodeHashHex).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.CodeHashHex).IsUnique();
            e.HasIndex(x => x.OrderId).IsUnique();
            // Postgres system column as optimistic concurrency token.
            e.Property<uint>("xmin").IsRowVersion();
            e.Ignore(x => x.DomainEvents);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PublishedAtUtc, x.OccurredAtUtc });
        });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var events = ChangeTracker.Entries<AggregateRoot<Guid>>()
            .SelectMany(e =>
            {
                var evts = e.Entity.DomainEvents.ToList();
                e.Entity.ClearDomainEvents();
                return evts;
            })
            .ToList();

        var written = await base.SaveChangesAsync(ct);

        // In-process handlers run after commit; cross-service effects go via outbox.
        foreach (var domainEvent in events)
            await publisher.Publish(domainEvent, ct);

        return written;
    }
}
