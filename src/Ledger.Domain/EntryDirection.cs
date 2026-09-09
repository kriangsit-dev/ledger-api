namespace Ledger.Domain;

/// <summary>
/// Which side of the ledger a line sits on.
/// </summary>
/// <remarks>
/// The values are deliberately +1 and -1: a signed balance is then just
/// <c>SUM(amount * (int)direction)</c>, which the database can evaluate directly and which makes
/// "the whole ledger nets to zero" a single query rather than a loop.
/// </remarks>
public enum EntryDirection
{
    Credit = -1,
    Debit = 1,
}
