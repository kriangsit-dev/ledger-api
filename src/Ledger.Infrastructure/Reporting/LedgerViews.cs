using Ledger.Domain;
using Ledger.Domain.Accounts;

namespace Ledger.Infrastructure.Reporting;

/// <summary>A single account's position, presented on the side that account normally sits on.</summary>
public sealed record AccountBalanceView(
    Guid AccountId,
    string Code,
    string Name,
    AccountType Type,
    string Currency,
    decimal Debits,
    decimal Credits,
    EntryDirection NormalSide,
    decimal Balance,
    DateOnly? AsOf);

/// <summary>
/// Every account's position at a point in time. In a correct ledger
/// <see cref="TotalDebits"/> always equals <see cref="TotalCredits"/> — the integration tests
/// assert exactly that after posting randomised entries.
/// </summary>
public sealed record TrialBalanceView(
    DateOnly? AsOf,
    IReadOnlyList<AccountBalanceView> Accounts,
    decimal TotalDebits,
    decimal TotalCredits)
{
    public bool IsBalanced => TotalDebits == TotalCredits;
}

public sealed record StatementLineView(
    Guid EntryId,
    string Reference,
    string Description,
    DateOnly OccurredOn,
    EntryDirection Direction,
    decimal Amount,
    decimal RunningBalance,
    string? Memo);

public sealed record StatementView(
    Guid AccountId,
    string Code,
    string Currency,
    decimal OpeningBalance,
    decimal ClosingBalance,
    int Page,
    int PageSize,
    int TotalLines,
    IReadOnlyList<StatementLineView> Lines);
