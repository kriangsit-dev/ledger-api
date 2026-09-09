using Ledger.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledger.Infrastructure.Persistence.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts");

        builder.HasKey(account => account.Id);
        builder.Property(account => account.Id).HasColumnName("id");

        builder.Property(account => account.Code)
            .HasColumnName("code")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(account => account.Name)
            .HasColumnName("name")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(account => account.Type)
            .HasColumnName("type")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(account => account.Currency)
            .HasColumnName("currency")
            .IsRequired();

        builder.Property(account => account.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(account => account.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // xmin is PostgreSQL's per-row transaction id. Using it as the concurrency token gives
        // optimistic locking without carrying a version column of our own.
        builder.Property(account => account.Version)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasIndex(account => account.Code)
            .HasDatabaseName("ix_accounts_code")
            .IsUnique();
    }
}
