namespace Ledger.Domain.Accounts;

/// <summary>The five classical account classes. The class fixes which side is the normal balance.</summary>
public enum AccountType
{
    Asset = 1,
    Liability = 2,
    Equity = 3,
    Revenue = 4,
    Expense = 5,
}

public static class AccountTypeExtensions
{
    /// <summary>
    /// Assets and expenses increase on the debit side; everything else increases on the credit side.
    /// This is what lets the API report a liability of 500 as "credit 500" instead of "-500".
    /// </summary>
    public static EntryDirection NormalBalance(this AccountType type) =>
        type switch
        {
            AccountType.Asset or AccountType.Expense => EntryDirection.Debit,
            AccountType.Liability or AccountType.Equity or AccountType.Revenue => EntryDirection.Credit,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown account type."),
        };
}
