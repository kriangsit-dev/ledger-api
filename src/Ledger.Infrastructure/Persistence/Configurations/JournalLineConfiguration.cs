using Ledger.Domain.Accounts;
using Ledger.Domain.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledger.Infrastructure.Persistence.Configurations;

public sealed class JournalLineConfiguration : IEntityTypeConfiguration<JournalLine>
{
    public void Configure(EntityTypeBuilder<JournalLine> builder)
    {
        builder.ToTable(
            "journal_lines",
            table => table.HasCheckConstraint("ck_journal_lines_amount_positive", "amount > 0"));

        builder.HasKey(line => line.Id);
        builder.Property(line => line.Id).HasColumnName("id");

        builder.Property(line => line.JournalEntryId).HasColumnName("journal_entry_id").IsRequired();
        builder.Property(line => line.AccountId).HasColumnName("account_id").IsRequired();

        builder.Property(line => line.Direction)
            .HasColumnName("direction")
            .HasConversion<string>()
            .HasMaxLength(8)
            .IsRequired();

        builder.Property(line => line.Amount)
            .HasColumnName("amount")
            .IsRequired();

        builder.Property(line => line.Memo)
            .HasColumnName("memo")
            .HasMaxLength(256);

        // SignedAmount is computed in C# from Amount and Direction; it is not a column.
        builder.Ignore(line => line.SignedAmount);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(line => line.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // The statement and balance queries always filter by account and order by date, so the
        // account column carries the index rather than relying on the entry-level one.
        builder.HasIndex(line => line.AccountId).HasDatabaseName("ix_journal_lines_account_id");
    }
}
