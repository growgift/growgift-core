using GrowGift.Family.Domain;
using GrowGift.Infrastructure.Common.Outbox;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Family.Infrastructure.Persistence;

public sealed class FamilyDbContext(DbContextOptions<FamilyDbContext> options) : DbContext(options)
{
    public DbSet<Child> Children => Set<Child>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<ChildWallet> Wallets => Set<ChildWallet>();
    public DbSet<ChoreTask> Tasks => Set<ChoreTask>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("family");

        b.Entity<Child>(e =>
        {
            e.ToTable("children");
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(80);
            e.Property(x => x.LoginCode).HasMaxLength(10);
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => x.ParentUserId);
            e.Ignore(x => x.DomainEvents);
        });

        b.Entity<Partner>(e =>
        {
            e.ToTable("partners");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(254);
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => x.UserId).IsUnique(); // one family per partner account
            e.Ignore(x => x.DomainEvents);
        });

        b.Entity<ChildWallet>(e =>
        {
            e.ToTable("child_wallets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => x.ChildId).IsUnique();
            e.HasMany<JarEntry>("_ledger").WithOne().HasForeignKey(j => j.WalletId);
            e.Navigation("_ledger").UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Ignore(x => x.DomainEvents);
            e.Ignore(x => x.Ledger);
        });

        b.Entity<JarEntry>(e =>
        {
            e.ToTable("jar_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Jar).HasConversion<string>().HasMaxLength(16);
            e.OwnsOne(x => x.Amount, m =>
            {
                m.Property(p => p.Amount).HasColumnName("amount").HasPrecision(18, 4);
                m.Property(p => p.Currency).HasColumnName("currency").HasMaxLength(3);
            });
            e.Property(x => x.Reference).HasMaxLength(120);
            e.HasIndex(x => new { x.WalletId, x.Jar });
        });

        b.Entity<ChoreTask>(e =>
        {
            e.ToTable("chore_tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Recurrence).HasConversion<string>().HasMaxLength(16);
            e.OwnsOne(x => x.Reward, m =>
            {
                m.Property(p => p.Amount).HasColumnName("reward_amount").HasPrecision(18, 4);
                m.Property(p => p.Currency).HasColumnName("reward_currency").HasMaxLength(3);
            });
            e.HasIndex(x => new { x.ChildId, x.State });
            e.Ignore(x => x.DomainEvents);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
        });
    }
}
