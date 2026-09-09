using Ledger.Api.Auth;
using Ledger.Api.Contracts;
using Ledger.Domain.Accounts;
using Ledger.Domain.Primitives;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Api.Endpoints;

public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccounts(this IEndpointRouteBuilder routes)
    {
        var group = routes
            .MapGroup("/api/v1/accounts")
            .WithTags("Accounts");

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(LedgerScopes.Write)
            .WithSummary("Open an account in the chart of accounts")
            .Produces<AccountResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/", ListAsync)
            .RequireAuthorization(LedgerScopes.Read)
            .WithSummary("List accounts");

        group.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(LedgerScopes.Read)
            .WithSummary("Get one account");

        group.MapPost("/{id:guid}/closure", CloseAsync)
            .RequireAuthorization(LedgerScopes.Write)
            .WithSummary("Close an account so no further entries can name it");

        group.MapGet("/{id:guid}/balance", BalanceAsync)
            .RequireAuthorization(LedgerScopes.Read)
            .WithSummary("Balance, optionally as at an accounting date");

        group.MapGet("/{id:guid}/statement", StatementAsync)
            .RequireAuthorization(LedgerScopes.Read)
            .WithSummary("Paged statement with a running balance");

        return group;
    }

    private static async Task<IResult> CreateAsync(
        CreateAccountRequest request,
        LedgerDbContext context,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var account = Account.Open(
            request.Code,
            request.Name,
            request.Type,
            new Currency(request.Currency),
            clock.GetUtcNow());

        context.Accounts.Add(account);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            // The unique index on code is the authority here, not a pre-check: a pre-check would
            // still lose the race against a second request arriving microseconds later.
            return Results.Conflict(new { code = "account-code-taken", detail = $"Account code '{account.Code}' already exists." });
        }

        return Results.Created($"/api/v1/accounts/{account.Id}", account.ToResponse());
    }

    private static async Task<IResult> ListAsync(
        LedgerDbContext context,
        CancellationToken cancellationToken,
        AccountType? type = null,
        bool? active = null)
    {
        var accounts = await context.Accounts
            .AsNoTracking()
            .Where(account => !type.HasValue || account.Type == type.Value)
            .Where(account => !active.HasValue || account.IsActive == active.Value)
            .OrderBy(account => account.Code)
            .ToListAsync(cancellationToken);

        return Results.Ok(accounts.Select(account => account.ToResponse()).ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        LedgerDbContext context,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var account = await context.Accounts
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return account is null
            ? Problems.NotFound(http, $"No account with id {id}.")
            : Results.Ok(account.ToResponse());
    }

    private static async Task<IResult> CloseAsync(
        Guid id,
        LedgerDbContext context,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var account = await context.Accounts
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (account is null)
        {
            return Problems.NotFound(http, $"No account with id {id}.");
        }

        account.Close();
        await context.SaveChangesAsync(cancellationToken);

        return Results.Ok(account.ToResponse());
    }

    private static async Task<IResult> BalanceAsync(
        Guid id,
        LedgerReports reports,
        HttpContext http,
        CancellationToken cancellationToken,
        DateOnly? asOf = null)
    {
        var balance = await reports.GetAccountBalanceAsync(id, asOf, cancellationToken);

        return balance is null
            ? Problems.NotFound(http, $"No account with id {id}.")
            : Results.Ok(balance);
    }

    private static async Task<IResult> StatementAsync(
        Guid id,
        LedgerReports reports,
        HttpContext http,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        int page = 1,
        int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var statement = await reports.GetStatementAsync(id, from, to, page, pageSize, cancellationToken);

        return statement is null
            ? Problems.NotFound(http, $"No account with id {id}.")
            : Results.Ok(statement);
    }
}
