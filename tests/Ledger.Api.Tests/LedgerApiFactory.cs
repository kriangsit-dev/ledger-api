using Ledger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Ledger.Api.Tests;

/// <summary>
/// Hosts the real application against a throwaway PostgreSQL container.
/// </summary>
/// <remarks>
/// The tests run against Npgsql, the real migrations and the real HTTP pipeline. An in-memory
/// provider would be faster, but it cannot reproduce the unique-constraint violation the
/// idempotency filter depends on, so it would test a different program than the one that ships.
/// </remarks>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string WriterClientId = "test-writer";
    public const string WriterSecret = "test-writer-secret";
    public const string ReaderClientId = "test-reader";
    public const string ReaderSecret = "test-reader-secret";

    private const string SigningKey = "integration-test-signing-key-at-least-32-chars";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("ledger")
        .WithUsername("ledger")
        .WithPassword("ledger")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Touching Services builds the host, which is why the container must be up first: the
        // connection string below is only known once PostgreSQL has a port.
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: that keeps the startup guard against the sample signing key honest and
        // stops the app applying migrations behind the test's back.
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ledger"] = _postgres.GetConnectionString(),
                ["Auth:SigningKey"] = SigningKey,
                ["Auth:Clients:0:ClientId"] = WriterClientId,
                ["Auth:Clients:0:ClientSecret"] = WriterSecret,
                ["Auth:Clients:0:Scopes:0"] = "ledger.read",
                ["Auth:Clients:0:Scopes:1"] = "ledger.write",
                ["Auth:Clients:1:ClientId"] = ReaderClientId,
                ["Auth:Clients:1:ClientSecret"] = ReaderSecret,
                ["Auth:Clients:1:Scopes:0"] = "ledger.read",
            }));
    }
}

[CollectionDefinition(Name)]
public sealed class LedgerApiCollection : ICollectionFixture<LedgerApiFactory>
{
    public const string Name = "ledger-api";
}
