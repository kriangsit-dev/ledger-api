using Ledger.Domain.Primitives;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Stores <see cref="Currency"/> as its three-letter code. Reading the column back through the
/// constructor means invalid data in the database fails loudly instead of spreading.
/// </summary>
public sealed class CurrencyConverter : ValueConverter<Currency, string>
{
    public CurrencyConverter()
        : base(currency => currency.Code, code => new Currency(code))
    {
    }
}
