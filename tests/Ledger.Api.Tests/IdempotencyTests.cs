using System.Net;
using System.Net.Http.Json;
using Ledger.Api.Contracts;
using Ledger.Domain.Accounts;
using Xunit;

namespace Ledger.Api.Tests;

/// <summary>
/// The behaviour that matters most in a payments-shaped API: a retried request must not move money
/// twice. These tests are the reason the endpoint takes an Idempotency-Key at all.
/// </summary>
public class IdempotencyTests(LedgerApiFactory factory) : LedgerTestBase(factory)
{
    [Fact]
    public async Task ReplaysTheFirstResponseWhenTheSameKeyIsRetried()
    {
        var client = await WriterAsync();
        var (cash, revenue) = await AccountPairAsync(client);
        var request = Transfer(cash.Id, revenue.Id, 1_500m);
        var key = Guid.NewGuid().ToString();

        var first = await client.SendAsync(PostEntry(request, key));
        var second = await client.SendAsync(PostEntry(request, key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replay"));

        var firstEntry = (await first.Content.ReadFromJsonAsync<JournalEntryResponse>(Json))!;
        var secondEntry = (await second.Content.ReadFromJsonAsync<JournalEntryResponse>(Json))!;

        Assert.Equal(firstEntry.Id, secondEntry.Id);
        Assert.Equal(1, await CountEntriesAsync(client, request.Reference));
    }

    [Fact]
    public async Task RejectsTheSameKeyCarryingADifferentPayload()
    {
        var client = await WriterAsync();
        var (cash, revenue) = await AccountPairAsync(client);
        var key = Guid.NewGuid().ToString();

        await client.SendAsync(PostEntry(Transfer(cash.Id, revenue.Id, 100m), key));
        var second = await client.SendAsync(PostEntry(Transfer(cash.Id, revenue.Id, 999m), key));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task RefusesAWriteWithoutAnIdempotencyKey()
    {
        var client = await WriterAsync();
        var (cash, revenue) = await AccountPairAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/v1/journal-entries",
            Transfer(cash.Id, revenue.Id, 10m),
            Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreatesExactlyOneEntryWhenRetriesArriveAtTheSameMoment()
    {
        var client = await WriterAsync();
        var (cash, revenue) = await AccountPairAsync(client);
        var request = Transfer(cash.Id, revenue.Id, 250m);
        var key = Guid.NewGuid().ToString();

        // Eight simultaneous retries, exactly as a mobile client behind a flaky connection would
        // produce them. One must win; the rest must replay or be told to wait — never post again.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => client.SendAsync(PostEntry(request, key))));

        var accepted = responses.Count(response => response.StatusCode == HttpStatusCode.Created);
        var deferred = responses.Count(response => response.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(responses.Length, accepted + deferred);
        Assert.True(accepted >= 1);
        Assert.Equal(1, await CountEntriesAsync(client, request.Reference));
    }

    private async Task<(AccountResponse Cash, AccountResponse Revenue)> AccountPairAsync(HttpClient client) =>
        (await CreateAccountAsync(client, AccountType.Asset),
            await CreateAccountAsync(client, AccountType.Revenue));

    private static async Task<int> CountEntriesAsync(HttpClient client, string reference)
    {
        var response = await client.GetAsync($"/api/v1/journal-entries?reference={Uri.EscapeDataString(reference)}");
        response.EnsureSuccessStatusCode();

        var page = (await response.Content.ReadFromJsonAsync<EntryPage>(Json))!;

        return page.Total;
    }

    private sealed record EntryPage(int Page, int PageSize, int Total, IReadOnlyList<JournalEntryResponse> Items);
}
