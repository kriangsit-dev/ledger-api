using System.Net.Http.Json;
using Ledger.Domain.Accounts;
using Ledger.Infrastructure.Reporting;
using Xunit;

namespace Ledger.Api.Tests;

public class TrialBalanceTests(LedgerApiFactory factory) : LedgerTestBase(factory)
{
    [Fact]
    public async Task TotalDebitsAlwaysEqualTotalCredits()
    {
        var client = await WriterAsync();

        var accounts = new[]
        {
            await CreateAccountAsync(client, AccountType.Asset),
            await CreateAccountAsync(client, AccountType.Liability),
            await CreateAccountAsync(client, AccountType.Revenue),
            await CreateAccountAsync(client, AccountType.Expense),
        };

        // Randomised amounts and pairings: whatever combination comes out, the ledger must still
        // balance. This is the single assertion that would catch almost any posting bug.
        var random = new Random(20260909);

        for (var i = 0; i < 25; i++)
        {
            var debit = accounts[random.Next(accounts.Length)];
            var credit = accounts[random.Next(accounts.Length)];

            if (debit.Id == credit.Id)
            {
                continue;
            }

            var amount = Math.Round((decimal)random.NextDouble() * 10_000m, 2);

            if (amount <= 0m)
            {
                continue;
            }

            var response = await client.SendAsync(
                PostEntry(Transfer(debit.Id, credit.Id, amount), Guid.NewGuid().ToString()));

            response.EnsureSuccessStatusCode();
        }

        var trialBalance = await client.GetFromJsonAsync<TrialBalanceView>(
            "/api/v1/reports/trial-balance",
            Json);

        Assert.Equal(trialBalance!.TotalDebits, trialBalance.TotalCredits);
        Assert.True(trialBalance.IsBalanced);
        Assert.True(trialBalance.TotalDebits > 0m);
    }

    [Fact]
    public async Task StatementRunsTheBalanceForwardThroughTheLines()
    {
        var client = await WriterAsync();
        var cash = await CreateAccountAsync(client, AccountType.Asset);
        var revenue = await CreateAccountAsync(client, AccountType.Revenue);

        // Different accounting dates so the expected order is fixed by the data, not by how fast
        // three requests happened to hit the clock.
        foreach (var (amount, daysAgo) in new[] { (100m, 2), (200m, 1), (300m, 0) })
        {
            (await client.SendAsync(
                PostEntry(Transfer(cash.Id, revenue.Id, amount, Today.AddDays(-daysAgo)), Guid.NewGuid().ToString())))
                .EnsureSuccessStatusCode();
        }

        var statement = await client.GetFromJsonAsync<StatementView>(
            $"/api/v1/accounts/{cash.Id}/statement",
            Json);

        Assert.Equal(3, statement!.TotalLines);
        Assert.Equal(600m, statement.ClosingBalance);
        Assert.Equal(
            new[] { 100m, 300m, 600m },
            statement.Lines.Select(line => line.RunningBalance).ToArray());
    }
}
