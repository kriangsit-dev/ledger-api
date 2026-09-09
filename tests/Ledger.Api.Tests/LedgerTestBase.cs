using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Api.Contracts;
using Ledger.Domain;
using Ledger.Domain.Accounts;
using Xunit;

namespace Ledger.Api.Tests;

[Collection(LedgerApiCollection.Name)]
public abstract class LedgerTestBase(LedgerApiFactory factory)
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected LedgerApiFactory Factory { get; } = factory;

    protected static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>Unique per test run so tests sharing one container never collide on account codes.</summary>
    protected static string UniqueCode(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    protected async Task<HttpClient> WriterAsync() =>
        await AuthenticatedAsync(LedgerApiFactory.WriterClientId, LedgerApiFactory.WriterSecret);

    protected async Task<HttpClient> ReaderAsync() =>
        await AuthenticatedAsync(LedgerApiFactory.ReaderClientId, LedgerApiFactory.ReaderSecret);

    protected HttpClient Anonymous() => Factory.CreateClient();

    protected async Task<AccountResponse> CreateAccountAsync(
        HttpClient client,
        AccountType type,
        string currency = "THB")
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/accounts",
            new CreateAccountRequest(UniqueCode("AC"), $"{type} account", type, currency),
            Json);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<AccountResponse>(Json))!;
    }

    protected static PostJournalEntryRequest Transfer(
        Guid debit,
        Guid credit,
        decimal amount,
        DateOnly? on = null,
        string? reference = null) =>
        new(
            reference ?? $"REF-{Guid.NewGuid():N}"[..16],
            "Integration test entry",
            "THB",
            on ?? Today,
            [
                new JournalLineRequest(debit, EntryDirection.Debit, amount, null),
                new JournalLineRequest(credit, EntryDirection.Credit, amount, null),
            ]);

    protected static HttpRequestMessage PostEntry(PostJournalEntryRequest request, string idempotencyKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/journal-entries")
        {
            Content = JsonContent.Create(request, options: Json),
        };

        message.Headers.Add("Idempotency-Key", idempotencyKey);

        return message;
    }

    private async Task<HttpClient> AuthenticatedAsync(string clientId, string secret)
    {
        var client = Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new TokenRequest(clientId, secret),
            Json);

        response.EnsureSuccessStatusCode();

        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        return client;
    }
}
