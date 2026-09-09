using Ledger.Domain.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledger.Infrastructure.Persistence.Configurations;

public sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> builder)
    {
        builder.ToTable("journal_entries");

        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).HasColumnName("id");

        builder.Property(entry => entry.Reference)
            .HasColumnName("reference")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entry => entry.Description)
            .HasColumnName("description")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entry => entry.Currency)
            .HasColumnName("currency")
            .IsRequired();

        builder.Property(entry => entry.OccurredOn)
            .HasColumnName("occurred_on")
            .HasColumnType("date")
            .IsRequired();

        builder.Property(entry => entry.PostedAt)
            .HasColumnName("posted_at")
            .IsRequired();

        builder.Property(entry => entry.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entry => entry.ReversalOfEntryId).HasColumnName("reversal_of_entry_id");
        builder.Property(entry => entry.ReversedByEntryId).HasColumnName("reversed_by_entry_id");

        builder.HasMany(entry => entry.Lines)
            .WithOne()
            .HasForeignKey(line => line.JournalEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Lines are reached through the aggregate root, so EF writes to the backing field and
        // nothing outside JournalEntry can add a line that skips the balance check.
        builder.Metadata
            .FindNavigation(nameof(JournalEntry.Lines))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(entry => entry.Reference).HasDatabaseName("ix_journal_entries_reference");
        builder.HasIndex(entry => entry.OccurredOn).HasDatabaseName("ix_journal_entries_occurred_on");
    }
}
