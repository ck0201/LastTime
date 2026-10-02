using LastTime.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace LastTime.Api.Data;

public class LastTimeDbContext(DbContextOptions<LastTimeDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<HistoryEntry> HistoryEntries => Set<HistoryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<OtpCode>(e =>
        {
            e.ToTable("otp_codes");
            e.Property(x => x.Purpose).HasMaxLength(32).IsRequired();
            e.Property(x => x.CodeHash).IsRequired(); // like PasswordHash: an unbounded text column holding a hasher's output
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.FailedAttempts).HasDefaultValue(0);
            e.HasOne(x => x.User).WithMany(u => u.OtpCodes)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // One current code per user and purpose; a new code replaces the old row.
            e.HasIndex(x => new { x.UserId, x.Purpose }).IsUnique();
        });

        modelBuilder.Entity<Item>(e =>
        {
            e.ToTable("items");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.Name);
            e.HasOne(x => x.User).WithMany(u => u.Items)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
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

