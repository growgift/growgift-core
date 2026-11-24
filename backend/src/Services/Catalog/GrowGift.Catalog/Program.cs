using Microsoft.EntityFrameworkCore.Infrastructure;
using GrowGift.Catalog;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<CatalogDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("catalog")));
builder.Services.AddHealthChecks();

var app = builder.Build();

// Development only: create this service's tables if EF migrations haven't
// been applied. EnsureCreated() can't be used here — it no-ops when the
// database already contains ANY tables, and init.sql pre-creates audit_log.
// Production applies real migrations in the deploy pipeline instead.
if (app.Environment.IsDevelopment())
{
    using var bootstrapScope = app.Services.CreateScope();
    var bootstrapDb = bootstrapScope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    try
    {
        bootstrapDb.Database.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>().CreateTables();
    }
    catch (Npgsql.PostgresException e) when (e.SqlState == "42P07")
    {
        // Tables already exist — nothing to do.
    }
}

// Seed on first run (idempotent).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    if (db.Database.CanConnect() && !db.Collections.Any())
    {
        db.Collections.AddRange(CatalogSeed.Collections());
        db.SaveChanges();
    }
}

app.MapGet("/api/v1/collections", async (CatalogDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Collections.AsNoTracking()
        .Include(c => c.Entries)
        .OrderBy(c => c.SortOrder)
        .ToListAsync(ct)));

app.MapGet("/api/v1/collections/{slug}", async (string slug, CatalogDbContext db, CancellationToken ct) =>
{
    var collection = await db.Collections.AsNoTracking()
        .Include(c => c.Entries)
        .FirstOrDefaultAsync(c => c.Slug == slug, ct);
    return collection is null ? Results.NotFound() : Results.Ok(collection);
});

app.MapHealthChecks("/healthz");
app.Run();

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Collection> Collections => Set<Collection>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("catalog");
        b.Entity<Collection>(e =>
        {
            e.ToTable("collections");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.CollectionId);
        });
        b.Entity<CollectionEntry>(e => { e.ToTable("collection_entries"); e.HasKey(x => x.Id); });
    }
}
