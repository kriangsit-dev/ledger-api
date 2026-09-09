using Ledger.Domain.Accounts;
using Ledger.Domain.Primitives;
using Xunit;

namespace Ledger.Domain.Tests;

public class AccountTests
{
    private static readonly Currency Thb = new("THB");
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AccountType.Asset, EntryDirection.Debit)]
    [InlineData(AccountType.Expense, EntryDirection.Debit)]
    [InlineData(AccountType.Liability, EntryDirection.Credit)]
    [InlineData(AccountType.Equity, EntryDirection.Credit)]
    [InlineData(AccountType.Revenue, EntryDirection.Credit)]
    public void KnowsWhichSideEachAccountClassIncreasesOn(AccountType type, EntryDirection expected) =>
        Assert.Equal(expected, type.NormalBalance());

    [Fact]
    public void UpperCasesTheAccountCode() =>
        Assert.Equal("1000-CASH", Account.Open(" 1000-cash ", "Cash", AccountType.Asset, Thb, Now).Code);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsABlankCode(string code) =>
        Assert.Throws<LedgerRuleViolationException>(() =>
            Account.Open(code, "Cash", AccountType.Asset, Thb, Now));

    [Fact]
    public void OpensActive() =>
        Assert.True(Account.Open("1000", "Cash", AccountType.Asset, Thb, Now).IsActive);

    [Fact]
    public void RefusesToCloseATwiceClosedAccount()
    {
        var account = Account.Open("1000", "Cash", AccountType.Asset, Thb, Now);
        account.Close();

        Assert.Throws<LedgerRuleViolationException>(account.Close);
    }

    [Fact]
    public void IssuesVersion7Identifiers()
    {
        var account = Account.Open("1000", "Cash", AccountType.Asset, Thb, Now);

        // Version 7 GUIDs carry a timestamp in their leading bits, so primary-key inserts land at
        // the right edge of the index instead of scattering random pages the way v4 does.
        var version = account.Id.ToByteArray(bigEndian: true)[6] >> 4;

        Assert.Equal(7, version);
    }
}
