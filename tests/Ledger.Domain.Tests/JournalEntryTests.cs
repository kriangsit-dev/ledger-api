using Ledger.Domain.Journal;
using Ledger.Domain.Primitives;
using Xunit;

namespace Ledger.Domain.Tests;

public class JournalEntryTests
{
    private static readonly Currency Thb = new("THB");
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static readonly Guid Cash = Guid.CreateVersion7();
    private static readonly Guid Revenue = Guid.CreateVersion7();

    [Fact]
    public void PostsABalancedEntry()
    {
        var entry = Sale(1_000m);

        Assert.Equal(JournalEntryStatus.Posted, entry.Status);
        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(entry.TotalDebits, entry.TotalCredits);
        Assert.Equal(1_000m, entry.TotalDebits.Amount);
    }

    [Fact]
    public void RejectsAnEntryWhereDebitsDoNotEqualCredits()
    {
        var exception = Assert.Throws<LedgerRuleViolationException>(() => JournalEntry.Post(
            "INV-1",
            "Broken",
            Thb,
            Today,
            [
                new JournalLineDraft(Cash, EntryDirection.Debit, 1_000m),
                new JournalLineDraft(Revenue, EntryDirection.Credit, 900m),
            ],
            Now));

        Assert.Contains("does not balance", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsASingleSidedEntry() =>
        Assert.Throws<LedgerRuleViolationException>(() => JournalEntry.Post(
            "INV-1",
            "One-legged",
            Thb,
            Today,
            [new JournalLineDraft(Cash, EntryDirection.Debit, 1_000m)],
            Now));

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void RejectsNonPositiveLineAmounts(decimal amount) =>
        Assert.Throws<LedgerRuleViolationException>(() => JournalEntry.Post(
            "INV-1",
            "Bad amount",
            Thb,
            Today,
            [
                new JournalLineDraft(Cash, EntryDirection.Debit, amount),
                new JournalLineDraft(Revenue, EntryDirection.Credit, amount),
            ],
            Now));

    [Fact]
    public void RejectsAnEntryDatedInTheFuture() =>
        Assert.Throws<LedgerRuleViolationException>(() => JournalEntry.Post(
            "INV-1",
            "Tomorrow",
            Thb,
            Today.AddDays(1),
            [
                new JournalLineDraft(Cash, EntryDirection.Debit, 10m),
                new JournalLineDraft(Revenue, EntryDirection.Credit, 10m),
            ],
            Now));

    [Fact]
    public void ReversalMirrorsEverySide()
    {
        var original = Sale(1_000m);

        var reversal = original.Reverse("INV-1-REV", Today, Now);

        Assert.Equal(JournalEntryStatus.Reversed, original.Status);
        Assert.Equal(reversal.Id, original.ReversedByEntryId);
        Assert.Equal(original.Id, reversal.ReversalOfEntryId);
        Assert.True(reversal.IsReversal);

        var originalCash = original.Lines.Single(line => line.AccountId == Cash);
        var reversedCash = reversal.Lines.Single(line => line.AccountId == Cash);

        Assert.Equal(EntryDirection.Debit, originalCash.Direction);
        Assert.Equal(EntryDirection.Credit, reversedCash.Direction);
        Assert.Equal(originalCash.Amount, reversedCash.Amount);
    }

    [Fact]
    public void ReversingLeavesTheTwoEntriesNettingToZero()
    {
        var original = Sale(1_000m);
        var reversal = original.Reverse("INV-1-REV", Today, Now);

        var net = original.Lines.Concat(reversal.Lines).Sum(line => line.SignedAmount);

        Assert.Equal(0m, net);
    }

    [Fact]
    public void RefusesToReverseTheSameEntryTwice()
    {
        var original = Sale(1_000m);
        original.Reverse("INV-1-REV", Today, Now);

        Assert.Throws<LedgerRuleViolationException>(() => original.Reverse("INV-1-REV-2", Today, Now));
    }

    [Fact]
    public void RefusesToReverseAReversal()
    {
        var reversal = Sale(1_000m).Reverse("INV-1-REV", Today, Now);

        Assert.Throws<LedgerRuleViolationException>(() => reversal.Reverse("INV-1-REV-REV", Today, Now));
    }

    private static JournalEntry Sale(decimal amount) => JournalEntry.Post(
        "INV-1",
        "Cash sale",
        Thb,
        Today,
        [
            new JournalLineDraft(Cash, EntryDirection.Debit, amount),
            new JournalLineDraft(Revenue, EntryDirection.Credit, amount),
        ],
        Now);
}
