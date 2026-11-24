using GrowGift.Identity.Domain;
using GrowGift.Infrastructure.Common.Outbox;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Identity.Infrastructure;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<BillingEvent> BillingEvents => Set<BillingEvent>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("identity");
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(254);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Kyc).HasConversion<string>().HasMaxLength(16);
            e.Ignore(x => x.DomainEvents);
            e.OwnsOne(x => x.ShippingAddress, a =>
            {
                a.Property(x => x.FullName).HasColumnName("ShippingFullName").HasMaxLength(120);
                a.Property(x => x.Line1).HasColumnName("ShippingLine1").HasMaxLength(200);
                a.Property(x => x.Line2).HasColumnName("ShippingLine2").HasMaxLength(200);
                a.Property(x => x.City).HasColumnName("ShippingCity").HasMaxLength(100);
                a.Property(x => x.Region).HasColumnName("ShippingRegion").HasMaxLength(100);
                a.Property(x => x.PostalCode).HasColumnName("ShippingPostalCode").HasMaxLength(20);
                a.Property(x => x.CountryCode).HasColumnName("ShippingCountryCode").HasMaxLength(2);
                a.Property(x => x.Phone).HasColumnName("ShippingPhone").HasMaxLength(30);
            });
        });
        b.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(32);
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => x.StripeCustomerId);
            e.HasIndex(x => x.StripeSubscriptionId);
        });
        b.Entity<BillingEvent>(e => { e.ToTable("billing_events"); e.HasKey(x => x.EventId); });
        b.Entity<OutboxMessage>(e => { e.ToTable("outbox_messages"); e.HasKey(x => x.Id); });
    }
}
