using Ledger.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledger.Infrastructure.Persistence.Configurations;

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");

        // Composite key: the same key may be reused against a different endpoint, but never twice
        // against the same one. This constraint is the whole concurrency control for replays.
        builder.HasKey(record => new { record.Endpoint, record.Key });

        builder.Property(record => record.Endpoint)
            .HasColumnName("endpoint")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(record => record.Key)
            .HasColumnName("key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(record => record.RequestHash)
            .HasColumnName("request_hash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(record => record.State)
            .HasColumnName("state")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(record => record.StatusCode).HasColumnName("status_code");

        builder.Property(record => record.ResponseBody)
            .HasColumnName("response_body")
            .HasColumnType("jsonb");

        builder.Property(record => record.ResourceLocation)
            .HasColumnName("resource_location")
            .HasMaxLength(512);

        builder.Property(record => record.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(record => record.CompletedAt).HasColumnName("completed_at");
    }
}
