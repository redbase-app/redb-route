using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace SerialNumbers.Core.Catalog;

/// <summary>One row of the product status journal.</summary>
public sealed class ProductStatusChangeRecord
{
    public long Id { get; set; }

    public string Gtin { get; set; } = "";

    public string FromStatus { get; set; } = "";

    public string ToStatus { get; set; } = "";

    public string ChangedBy { get; set; } = "";

    public DateTime ChangedAt { get; set; }
}

/// <summary>
/// The product status journal as an EF Core model: the part of a data model an existing application
/// keeps in EF Core, next to redb. The table itself is created by <c>Database/schema.sql</c>.
/// </summary>
public sealed class CatalogAuditDbContext(DbContextOptions<CatalogAuditDbContext> options) : DbContext(options)
{
    public DbSet<ProductStatusChangeRecord> StatusChanges => Set<ProductStatusChangeRecord>();

    /// <summary>
    /// A context on an open connection it does not own: disposing the context leaves the connection
    /// to redb. Inside <c>.Transacted()</c> that connection is the one the route's transaction holds,
    /// so what the context saves commits or rolls back together with the redb objects.
    /// </summary>
    public static CatalogAuditDbContext On(DbConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogAuditDbContext>()
            .UseSqlServer(connection, contextOwnsConnection: false)
            .Options);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProductStatusChangeRecord>(entity =>
        {
            entity.ToTable("product_status_changes", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Gtin).HasColumnName("gtin").HasMaxLength(14).IsUnicode(false);
            entity.Property(e => e.FromStatus).HasColumnName("from_status").HasMaxLength(16).IsUnicode(false);
            entity.Property(e => e.ToStatus).HasColumnName("to_status").HasMaxLength(16).IsUnicode(false);
            entity.Property(e => e.ChangedBy).HasColumnName("changed_by").HasMaxLength(128);
            entity.Property(e => e.ChangedAt).HasColumnName("changed_at");
        });
    }
}
