using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Keeps the provider-specific error inspection in the layer that already knows about PostgreSQL,
/// so the API project never has to reference Npgsql to understand a failed insert.
/// </summary>
public static class DbUpdateExceptionExtensions
{
    public static bool IsUniqueViolation(this DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
