using Ledger.Domain.Primitives;

namespace Ledger.Domain.Accounts;

/// <summary>
/// A line in the chart of accounts. Accounts hold no balance field — balances are derived from
/// journal lines (see <c>BalanceQueries</c>), which is why two concurrent postings can never
/// overwrite each other's running total.
/// </summary>
public sealed class Account
{
    private Account() { }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public AccountType Type { get; private set; }

    public Currency Currency { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>PostgreSQL's <c>xmin</c> system column, used as an optimistic concurrency token.</summary>
    public uint Version { get; private set; }

    public static Account Open(
        string code,
        string name,
        AccountType type,
        Currency currency,
        DateTimeOffset now)
    {
        var account = new Account
        {
            // Version 7 GUIDs are time-ordered, so primary-key inserts stay at the right edge of
            // the B-tree instead of scattering random pages the way v4 does.
            Id = Guid.CreateVersion7(),
            Code = NormaliseCode(code),
            Name = NormaliseName(name),
            Type = Enum.IsDefined(type)
                ? type
                : throw new LedgerRuleViolationException($"'{type}' is not a valid account type."),
            Currency = currency,
            IsActive = true,
            CreatedAt = now,
        };

        return account;
    }

    public void Rename(string name) => Name = NormaliseName(name);

    public void Close()
    {
        if (!IsActive)
        {
            throw new LedgerRuleViolationException($"Account {Code} is already closed.");
        }

        IsActive = false;
    }

    public void Reopen()
    {
        if (IsActive)
        {
            throw new LedgerRuleViolationException($"Account {Code} is already open.");
        }

        IsActive = true;
    }

    private static string NormaliseCode(string code)
    {
        var trimmed = code?.Trim() ?? string.Empty;

        if (trimmed.Length is 0 or > 32)
        {
            throw new LedgerRuleViolationException("Account code must be 1-32 characters.");
        }

        return trimmed.ToUpperInvariant();
    }

    private static string NormaliseName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length is 0 or > 128)
        {
            throw new LedgerRuleViolationException("Account name must be 1-128 characters.");
        }

        return trimmed;
    }
}
