using System.Net;
using System.Net.Http.Json;
using Ledger.Api.Contracts;
using Ledger.Domain.Accounts;
using Xunit;

namespace Ledger.Api.Tests;

public class AuthorizationTests(LedgerApiFactory factory) : LedgerTestBase(factory)
{
    [Fact]
    public async Task RejectsAnonymousReads()
    {
        var response = await Anonymous().GetAsync("/api/v1/accounts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RejectsAWriteFromAReadOnlyToken()
    {
        var writer = await WriterAsync();
        var cash = await CreateAccountAsync(writer, AccountType.Asset);
        var revenue = await CreateAccountAsync(writer, AccountType.Revenue);

        var reader = await ReaderAsync();
        var response = await reader.SendAsync(
            PostEntry(Transfer(cash.Id, revenue.Id, 10m), Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AllowsAReadOnlyTokenToRead()
    {
        var reader = await ReaderAsync();
        var response = await reader.GetAsync("/api/v1/accounts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RejectsWrongClientCredentials()
    {
        var response = await Anonymous().PostAsJsonAsync(
            "/api/v1/auth/token",
            new TokenRequest(LedgerApiFactory.WriterClientId, "not-the-secret"),
            Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RejectsAnUnknownClient()
    {
        var response = await Anonymous().PostAsJsonAsync(
            "/api/v1/auth/token",
            new TokenRequest("no-such-client", "whatever"),
            Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
