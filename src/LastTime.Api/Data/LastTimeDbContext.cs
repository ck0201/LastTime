using LastTime.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace LastTime.Api.Data;

public class LastTimeDbContext(DbContextOptions<LastTimeDbContext> options) : DbContext(options)
{
    public DbSet<Item> Items => Set<Item>();
    public DbSet<HistoryEntry> HistoryEntries => Set<HistoryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Item>(e =>
        {
            e.ToTable("items");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.Name);
        });

        modelBuilder.Entity<HistoryEntry>(e =>
        {
            e.ToTable("history_entries");
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.Cost).HasPrecision(12, 2);
            e.Property(x => x.Provider).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Item).WithMany(i => i.History)
                .HasForeignKey(x => x.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ItemId, x.Date }).IsDescending(false, true);
        });
    }
}

