using System.Net;
using System.Net.Http.Json;
using Ledger.Api.Contracts;
using Ledger.Domain;
using Ledger.Domain.Accounts;
using Ledger.Domain.Journal;
using Ledger.Infrastructure.Reporting;
using Xunit;

namespace Ledger.Api.Tests;

public class JournalEndpointTests(LedgerApiFactory factory) : LedgerTestBase(factory)
{
    [Fact]
    public async Task PostsAnEntryAndReadsItBack()
    {
        var client = await WriterAsync();
        var cash = await CreateAccountAsync(client, AccountType.Asset);
        var revenue = await CreateAccountAsync(client, AccountType.Revenue);

        var created = await client.SendAsync(
            PostEntry(Transfer(cash.Id, revenue.Id, 2_000m), Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var entry = (await created.Content.ReadFromJsonAsync<JournalEntryResponse>(Json))!;

        Assert.Equal(JournalEntryStatus.Posted, entry.Status);
        Assert.Equal(entry.TotalDebits, entry.TotalCredits);

        var fetched = await client.GetFromJsonAsync<JournalEntryResponse>(
            $"/api/v1/journal-entries/{entry.Id}",
            Json);

        Assert.Equal(entry.Id, fetched!.Id);
        Assert.Equal(2, fetched.Lines.Count);
    }

    [Fact]
    public async Task RefusesAnEntryWhereDebitsDoNotEqualCredits()
    {
        var client = await WriterAsync();
        var cash = await CreateAccountAsync(client, AccountType.Asset);
        var revenue = await CreateAccountAsync(client, AccountType.Revenue);

        var request = new PostJournalEntryRequest(
            "UNBALANCED",
            "Debits and credits disagree",
            "THB",
            Today,
            [
                new JournalLineRequest(cash.Id, EntryDirection.Debit, 100m, null),
                new JournalLineRequest(revenue.Id, EntryDirection.Credit, 90m, null),
            ]);

        var response = await client.SendAsync(PostEntry(request, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RefusesAnEntryAgainstAnUnknownAccount()
    {
        var client = await WriterAsync();
        var cash = await CreateAccountAsync(client, AccountType.Asset);

        var response = await client.SendAsync(
            PostEntry(Transfer(cash.Id, Guid.CreateVersion7(), 50m), Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RefusesAnEntryAgainstAClosedAccount()
    {
        var client = await WriterAsync();
        var cash = await CreateAccountAsync(client, AccountType.Asset);
        var revenue = await CreateAccountAsync(client, AccountType.Revenue);

        (await client.PostAsync($"/api/v1/accounts/{revenue.Id}/closure", null)).EnsureSuccessStatusCode();

        var response = await client.SendAsync(
            PostEntry(Transfer(cash.Id, revenue.Id, 50m), Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RefusesAnEntryInACurrencyTheAccountDoesNotUse()
    {
        var client = await WriterAsync();
        var thbCash = await CreateAccountAsync(client, AccountType.Asset);
        var usdRevenue = await CreateAccountAsync(client, AccountType.Revenue, "USD");

        var response = await client.SendAsync(
            PostEntry(Transfer(thbCash.Id, usdRevenue.Id, 50m), Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ReversalCancelsTheOriginalWithoutDeletingIt()
    {
        var client = await WriterAsync();
        var cash = await CreateAccountAsync(client, AccountType.Asset);
        var revenue = await CreateAccountAsync(client, AccountType.Revenue);

        var posted = await client.SendAsync(
            PostEntry(Transfer(cash.Id, revenue.Id, 400m), Guid.NewGuid().ToString()));

        var original = (await posted.Content.ReadFromJsonAsync<JournalEntryResponse>(Json))!;

        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/journal-entries/{original.Id}/reversal")
        {
            Content = JsonContent.Create(new ReverseJournalEntryRequest("REV-1", null), options: Json),
        };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var reversed = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.Created, reversed.StatusCode);

        var reversal = (await reversed.Content.ReadFromJsonAsync<JournalEntryResponse>(Json))!;
        Assert.Equal(original.Id, reversal.ReversalOfEntryId);

        // The original is still there, marked — an auditor can see both what happened and the fix.
        var refetched = await client.GetFromJsonAsync<JournalEntryResponse>(
            $"/api/v1/journal-entries/{original.Id}",
            Json);

        Assert.Equal(JournalEntryStatus.Reversed, refetched!.Status);
        Assert.Equal(reversal.Id, refetched.ReversedByEntryId);

        // And the account is back where it started.
        var balance = await client.GetFromJsonAsync<AccountBalanceView>(
            $"/api/v1/accounts/{cash.Id}/balance",
            Json);

        Assert.Equal(0m, balance!.Balance);
    }

    [Fact]
    public async Task BalanceIgnoresEntriesAfterTheAsOfDate()
    {
        var client = await WriterAsync();
        var cash = await CreateAccountAsync(client, AccountType.Asset);
        var revenue = await CreateAccountAsync(client, AccountType.Revenue);

        var yesterday = Today.AddDays(-1);

        await client.SendAsync(PostEntry(Transfer(cash.Id, revenue.Id, 100m, yesterday), Guid.NewGuid().ToString()));
        await client.SendAsync(PostEntry(Transfer(cash.Id, revenue.Id, 250m, Today), Guid.NewGuid().ToString()));

        var asOfYesterday = await client.GetFromJsonAsync<AccountBalanceView>(
            $"/api/v1/accounts/{cash.Id}/balance?asOf={yesterday:yyyy-MM-dd}",
            Json);

        var asOfToday = await client.GetFromJsonAsync<AccountBalanceView>(
            $"/api/v1/accounts/{cash.Id}/balance",
            Json);

        Assert.Equal(100m, asOfYesterday!.Balance);
        Assert.Equal(350m, asOfToday!.Balance);
    }

    [Fact]
    public async Task ShowsALiabilityBalanceOnItsCreditSide()
    {
        var client = await WriterAsync();
        var expense = await CreateAccountAsync(client, AccountType.Expense);
        var payable = await CreateAccountAsync(client, AccountType.Liability);

        await client.SendAsync(PostEntry(Transfer(expense.Id, payable.Id, 700m), Guid.NewGuid().ToString()));

        var balance = await client.GetFromJsonAsync<AccountBalanceView>(
            $"/api/v1/accounts/{payable.Id}/balance",
            Json);

        // 700 credited to a liability is a balance of +700, not -700.
        Assert.Equal(EntryDirection.Credit, balance!.NormalSide);
        Assert.Equal(700m, balance.Balance);
    }
}
