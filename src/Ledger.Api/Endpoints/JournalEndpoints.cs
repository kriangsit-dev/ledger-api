using Ledger.Api.Auth;
using Ledger.Api.Contracts;
using Ledger.Api.Idempotency;
using Ledger.Domain;
using Ledger.Domain.Journal;
using Ledger.Domain.Primitives;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Api.Endpoints;

public static class JournalEndpoints
{
    public static RouteGroupBuilder MapJournal(this IEndpointRouteBuilder routes)
    {
        var group = routes
            .MapGroup("/api/v1/journal-entries")
            .WithTags("Journal");

        // Only the money-moving endpoints demand an Idempotency-Key. Opening an account is already
        // protected by the unique code, and forcing a key on every read-shaped write would be
        // ceremony without a payoff.
        group.MapPost("/", PostAsync)
            .RequireAuthorization(LedgerScopes.Write)
            .AddEndpointFilter<IdempotencyFilter>()
            .WithSummary("Post a balanced journal entry")
            .Produces<JournalEntryResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{id:guid}/reversal", ReverseAsync)
            .RequireAuthorization(LedgerScopes.Write)
            .AddEndpointFilter<IdempotencyFilter>()
            .WithSummary("Reverse an entry by posting its mirror image")
            .Produces<JournalEntryResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(LedgerScopes.Read)
            .WithSummary("Get one entry with its lines");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(LedgerScopes.Read)
            .WithSummary("List entries, newest accounting date first");

        return group;
    }

    private static async Task<IResult> PostAsync(
        PostJournalEntryRequest request,
        LedgerDbContext context,
        TimeProvider clock,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Problems.RuleViolation(http, "A journal entry needs at least two lines.");
        }

        var currency = new Currency(request.Currency);

        var drafts = request.Lines
            .Select(line => new JournalLineDraft(line.AccountId, line.Direction, line.Amount, line.Memo))
            .ToList();

        var rejection = await ValidateAccountsAsync(context, drafts, currency, cancellationToken);

        if (rejection is not null)
        {
            return Problems.RuleViolation(http, rejection);
        }

        var entry = JournalEntry.Post(
            request.Reference,
            request.Description,
            currency,
            request.OccurredOn,
            drafts,
            clock.GetUtcNow());

        context.JournalEntries.Add(entry);
        await context.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/v1/journal-entries/{entry.Id}", entry.ToResponse());
    }

    private static async Task<IResult> ReverseAsync(
        Guid id,
        ReverseJournalEntryRequest request,
        LedgerDbContext context,
        TimeProvider clock,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var original = await context.JournalEntries
            .Include(entry => entry.Lines)
            .SingleOrDefaultAsync(entry => entry.Id == id, cancellationToken);

        if (original is null)
        {
            return Problems.NotFound(http, $"No journal entry with id {id}.");
        }

        var now = clock.GetUtcNow();
        var reversal = original.Reverse(
            request.Reference,
            request.OccurredOn ?? DateOnly.FromDateTime(now.UtcDateTime),
            now);

        context.JournalEntries.Add(reversal);
        await context.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/v1/journal-entries/{reversal.Id}", reversal.ToResponse());
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        LedgerDbContext context,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var entry = await context.JournalEntries
            .AsNoTracking()
            .Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return entry is null
            ? Problems.NotFound(http, $"No journal entry with id {id}.")
            : Results.Ok(entry.ToResponse());
    }

    private static async Task<IResult> ListAsync(
        LedgerDbContext context,
        CancellationToken cancellationToken,
        string? reference = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int page = 1,
        int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = context.JournalEntries
            .AsNoTracking()
            .Include(entry => entry.Lines)
            .Where(entry => reference == null || entry.Reference == reference)
            .Where(entry => !from.HasValue || entry.OccurredOn >= from.Value)
            .Where(entry => !to.HasValue || entry.OccurredOn <= to.Value);

        var total = await query.CountAsync(cancellationToken);

        var entries = await query
            .OrderByDescending(entry => entry.OccurredOn)
            .ThenByDescending(entry => entry.PostedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return Results.Ok(new
        {
            page,
            pageSize,
            total,
            items = entries.Select(entry => entry.ToResponse()).ToList(),
        });
    }

    /// <summary>
    /// Checks the things the domain cannot see: that every named account exists, is still open,
    /// and is denominated in the entry's currency.
    /// </summary>
    private static async Task<string?> ValidateAccountsAsync(
        LedgerDbContext context,
        IReadOnlyList<JournalLineDraft> drafts,
        Currency currency,
        CancellationToken cancellationToken)
    {
        var ids = drafts.Select(draft => draft.AccountId).Distinct().ToList();

        var accounts = await context.Accounts
            .AsNoTracking()
            .Where(account => ids.Contains(account.Id))
            .ToListAsync(cancellationToken);

        var missing = ids.Except(accounts.Select(account => account.Id)).ToList();

        if (missing.Count > 0)
        {
            return $"Unknown account(s): {string.Join(", ", missing)}.";
        }

        var closed = accounts.Where(account => !account.IsActive).Select(account => account.Code).ToList();

        if (closed.Count > 0)
        {
            return $"Account(s) closed to new entries: {string.Join(", ", closed)}.";
        }

        var mismatched = accounts
            .Where(account => account.Currency != currency)
            .Select(account => $"{account.Code} ({account.Currency})")
            .ToList();

        return mismatched.Count > 0
            ? $"Entry is in {currency} but these accounts are not: {string.Join(", ", mismatched)}."
            : null;
    }
}
