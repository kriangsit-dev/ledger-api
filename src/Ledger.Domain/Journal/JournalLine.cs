using Ledger.Domain.Primitives;

namespace Ledger.Domain.Journal;

/// <summary>One side of one entry: this much, on this side, against this account.</summary>
public sealed class JournalLine
{
    private JournalLine() { }

    public Guid Id { get; private set; }

    public Guid JournalEntryId { get; private set; }

    public Guid AccountId { get; private set; }

    public EntryDirection Direction { get; private set; }

    /// <summary>Always positive. The side lives in <see cref="Direction"/>, never in the sign.</summary>
    public decimal Amount { get; private set; }

    public string? Memo { get; private set; }

    /// <summary>The amount as it contributes to a running balance: debits add, credits subtract.</summary>
    public decimal SignedAmount => Amount * (int)Direction;

    internal static JournalLine Create(Guid journalEntryId, JournalLineDraft draft, Currency currency)
    {
        if (draft.AccountId == Guid.Empty)
        {
            throw new LedgerRuleViolationException("Every journal line must name an account.");
        }

        if (!Enum.IsDefined(draft.Direction))
        {
            throw new LedgerRuleViolationException($"'{draft.Direction}' is not a valid entry direction.");
        }

        if (draft.Amount <= 0m)
        {
            throw new LedgerRuleViolationException(
                "Journal line amounts must be greater than zero; use the opposite direction instead of a negative amount.");
        }

        // Constructing Money validates the scale against the currency's storage precision.
        _ = new Money(draft.Amount, currency);

        if (draft.Memo is { Length: > 256 })
        {
            throw new LedgerRuleViolationException("Journal line memo must be 256 characters or fewer.");
        }

        return new JournalLine
        {
            Id = Guid.CreateVersion7(),
            JournalEntryId = journalEntryId,
            AccountId = draft.AccountId,
            Direction = draft.Direction,
            Amount = draft.Amount,
            Memo = string.IsNullOrWhiteSpace(draft.Memo) ? null : draft.Memo.Trim(),
        };
    }
}

/// <summary>The caller's requested line, before it becomes part of a posted entry.</summary>
public readonly record struct JournalLineDraft(
    Guid AccountId,
    EntryDirection Direction,
    decimal Amount,
    string? Memo = null);
