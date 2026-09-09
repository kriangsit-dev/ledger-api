using Ledger.Domain.Accounts;
using Ledger.Domain.Journal;
using Ledger.Domain.Primitives;
using Ledger.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure.Persistence;

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public const string Schema = "ledger";

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();

    public DbSet<JournalLine> JournalLines => Set<JournalLine>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Currency>()
            .HaveConversion<CurrencyConverter>()
            .HaveMaxLength(3)
            .AreFixedLength();

        // One place decides money precision. numeric(19,4) holds every realistic balance without
        // the rounding drift a floating-point column would introduce.
        configurationBuilder.Properties<decimal>()
            .HavePrecision(19, 4);
    }
}
