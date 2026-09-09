using Ledger.Domain.Primitives;
using Xunit;

namespace Ledger.Domain.Tests;

public class MoneyTests
{
    private static readonly Currency Thb = new("THB");
    private static readonly Currency Usd = new("USD");

    [Theory]
    [InlineData("thb", "THB")]
    [InlineData(" usd ", "USD")]
    [InlineData("JPY", "JPY")]
    public void NormalisesCurrencyCodes(string input, string expected) =>
        Assert.Equal(expected, new Currency(input).Code);

    [Theory]
    [InlineData("")]
    [InlineData("TH")]
    [InlineData("THBB")]
    [InlineData("TH1")]
    public void RejectsCurrencyCodesThatAreNotThreeLetters(string input) =>
        Assert.Throws<LedgerRuleViolationException>(() => new Currency(input));

    [Fact]
    public void AddsAmountsInTheSameCurrency() =>
        Assert.Equal(new Money(300.75m, Thb), new Money(100.25m, Thb) + new Money(200.50m, Thb));

    [Fact]
    public void RefusesToCombineDifferentCurrencies() =>
        Assert.Throws<LedgerRuleViolationException>(() => new Money(1m, Thb) + new Money(1m, Usd));

    [Fact]
    public void RejectsAmountsFinerThanTheStoredPrecision() =>
        Assert.Throws<LedgerRuleViolationException>(() => new Money(1.000005m, Thb));

    [Fact]
    public void TreatsTrailingZerosAsTheSameAmount() =>
        Assert.Equal(new Money(100m, Thb), new Money(100.0000m, Thb));

    [Fact]
    public void SurvivesRepeatedAdditionWithoutDrift()
    {
        // The reason money is decimal: this loop returns 0.09999999999999999 with double.
        var total = Money.Zero(Thb);

        for (var i = 0; i < 10; i++)
        {
            total += new Money(0.01m, Thb);
        }

        Assert.Equal(0.10m, total.Amount);
    }
}
