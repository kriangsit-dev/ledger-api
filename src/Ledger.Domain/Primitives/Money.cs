namespace Ledger.Domain.Primitives;

/// <summary>
/// An amount in a single currency.
/// </summary>
/// <remarks>
/// Money is <see cref="decimal"/>, never <see cref="double"/>: binary floating point cannot
/// represent 0.10 exactly, so a few thousand additions silently drift away from the true total.
/// Arithmetic across two different currencies is rejected rather than silently coerced.
/// </remarks>
public readonly record struct Money
{
    /// <summary>Storage is numeric(19,4), so anything finer than four decimal places is rejected.</summary>
    public const int MaximumScale = 4;

    public Money(decimal amount, Currency currency)
    {
        if (decimal.Round(amount, MaximumScale) != amount)
        {
            throw new LedgerRuleViolationException(
                $"Amount {amount} has more than {MaximumScale} decimal places.");
        }

        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    public bool IsZero => Amount == 0m;

    public static Money Zero(Currency currency) => new(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator -(Money value) => new(-value.Amount, value.Currency);

    public override string ToString() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{Amount:0.0000} {Currency}");

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new LedgerRuleViolationException(
                $"Cannot combine {left.Currency} and {right.Currency} amounts.");
        }
    }
}
