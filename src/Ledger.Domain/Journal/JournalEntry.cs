using Ledger.Domain.Primitives;

namespace Ledger.Domain.Journal;

/// <summary>
/// One balanced accounting transaction: a set of lines whose debits equal their credits.
/// </summary>
/// <remarks>
/// A posted entry is immutable. There is no <c>Update</c> and no <c>Delete</c> — a mistake is
/// corrected by <see cref="Reverse"/>, which writes a second, mirrored entry. That is what an
/// auditor expects, and it means history can always be replayed exactly as it happened.
/// </remarks>
public sealed class JournalEntry
{
    private readonly List<JournalLine> _lines = [];

    private JournalEntry() { }

    public Guid Id { get; private set; }

    /// <summary>The caller's own identifier for the business event (invoice no., payment id, ...).</summary>
    public string Reference { get; private set; } = null!;

    public string Description { get; private set; } = null!;

    public Currency Currency { get; private set; }

    /// <summary>The accounting date. Distinct from <see cref="PostedAt"/>, which is wall-clock.</summary>
    public DateOnly OccurredOn { get; private set; }

    public DateTimeOffset PostedAt { get; private set; }

    public JournalEntryStatus Status { get; private set; }

    /// <summary>Set on a reversing entry, pointing at the entry it cancels.</summary>
    public Guid? ReversalOfEntryId { get; private set; }

    /// <summary>Set on the original entry once it has been reversed.</summary>
    public Guid? ReversedByEntryId { get; private set; }

    public IReadOnlyList<JournalLine> Lines => _lines;

    public Money TotalDebits => Total(EntryDirection.Debit);

    public Money TotalCredits => Total(EntryDirection.Credit);

    public bool IsReversal => ReversalOfEntryId is not null;

    public static JournalEntry Post(
        string reference,
        string description,
        Currency currency,
        DateOnly occurredOn,
        IReadOnlyList<JournalLineDraft> lines,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var entry = new JournalEntry
        {
            Id = Guid.CreateVersion7(),
            Reference = NormaliseReference(reference),
            Description = NormaliseDescription(description),
            Currency = currency,
            OccurredOn = EnsureNotInTheFuture(occurredOn, now),
            PostedAt = now,
            Status = JournalEntryStatus.Posted,
        };

        if (lines.Count < 2)
        {
            throw new LedgerRuleViolationException(
                "A journal entry needs at least two lines: something is debited and something is credited.");
        }

        foreach (var draft in lines)
        {
            entry._lines.Add(JournalLine.Create(entry.Id, draft, currency));
        }

        entry.EnsureBalanced();

        return entry;
    }

    /// <summary>
    /// Produces the mirrored entry that cancels this one and marks this one reversed.
    /// Both entries stay in the ledger; nothing is erased.
    /// </summary>
    public JournalEntry Reverse(string reference, DateOnly occurredOn, DateTimeOffset now)
    {
        if (Status == JournalEntryStatus.Reversed)
        {
            throw new LedgerRuleViolationException($"Entry {Reference} has already been reversed.");
        }

        if (IsReversal)
        {
            throw new LedgerRuleViolationException(
                "A reversing entry cannot itself be reversed; post a fresh entry instead.");
        }

        var mirrored = _lines
            .Select(line => new JournalLineDraft(line.AccountId, Opposite(line.Direction), line.Amount, line.Memo))
            .ToList();

        var reversal = Post(reference, $"Reversal of {Reference}", Currency, occurredOn, mirrored, now);

        reversal.ReversalOfEntryId = Id;
        Status = JournalEntryStatus.Reversed;
        ReversedByEntryId = reversal.Id;

        return reversal;
    }

    private static EntryDirection Opposite(EntryDirection direction) =>
        direction == EntryDirection.Debit ? EntryDirection.Credit : EntryDirection.Debit;

    private void EnsureBalanced()
    {
        var debits = TotalDebits;
        var credits = TotalCredits;

        if (debits != credits)
        {
            throw new LedgerRuleViolationException(
                $"Entry does not balance: debits {debits} but credits {credits}.");
        }

        if (debits.IsZero)
        {
            throw new LedgerRuleViolationException("An entry that moves nothing cannot be posted.");
        }
    }

    private Money Total(EntryDirection direction)
    {
        var sum = _lines
            .Where(line => line.Direction == direction)
            .Sum(line => line.Amount);

        return new Money(sum, Currency);
    }

    private static DateOnly EnsureNotInTheFuture(DateOnly occurredOn, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        return occurredOn <= today
            ? occurredOn
            : throw new LedgerRuleViolationException(
                $"Cannot post to {occurredOn:yyyy-MM-dd}: the accounting date is in the future.");
    }

    private static string NormaliseReference(string reference)
    {
        var trimmed = reference?.Trim() ?? string.Empty;

        return trimmed.Length is 0 or > 64
            ? throw new LedgerRuleViolationException("Entry reference must be 1-64 characters.")
            : trimmed;
    }

    private static string NormaliseDescription(string description)
    {
        var trimmed = description?.Trim() ?? string.Empty;

        return trimmed.Length is 0 or > 256
            ? throw new LedgerRuleViolationException("Entry description must be 1-256 characters.")
            : trimmed;
    }
}
