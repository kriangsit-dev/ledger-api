namespace Ledger.Domain.Primitives;

/// <summary>
/// An ISO 4217 currency code. Wrapping the string stops a currency from being passed where an
/// account code (or any other string) was meant, and gives one place to validate the format.
/// </summary>
public readonly record struct Currency
{
    private readonly string? _code;

    public Currency(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new LedgerRuleViolationException("Currency code is required.");
        }

        var normalised = code.Trim().ToUpperInvariant();

        if (normalised.Length != 3 || !normalised.All(char.IsAsciiLetterUpper))
        {
            throw new LedgerRuleViolationException(
                $"'{code}' is not a valid ISO 4217 currency code (expected three letters, e.g. THB).");
        }

        _code = normalised;
    }

    public string Code =>
        _code ?? throw new InvalidOperationException("Currency was never initialised.");

    public override string ToString() => Code;
}
