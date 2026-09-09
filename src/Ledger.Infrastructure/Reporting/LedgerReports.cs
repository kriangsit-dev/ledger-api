using Ledger.Domain;
using Ledger.Domain.Accounts;
using Ledger.Domain.Journal;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure.Reporting;

/// <summary>
/// Read-side queries. Balances are never stored: they are summed from journal lines on demand.
/// </summary>
/// <remarks>
/// Deriving the balance rather than caching it removes an entire class of bug — two concurrent
/// postings cannot overwrite each other's running total, because there is no total to overwrite.
/// The cost is a SUM over the account's lines; the fix at scale is a periodic snapshot row plus
/// the lines posted since it, not a mutable balance column.
/// </remarks>
public sealed class LedgerReports(LedgerDbContext context)
{
    public async Task<AccountBalanceView?> GetAccountBalanceAsync(
        Guid accountId,
        DateOnly? asOf,
        CancellationToken cancellationToken)
    {
        var account = await context.Accounts
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);

        if (account is null)
        {
            return null;
        }

        var totals = await PostedLines(asOf)
            .Where(row => row.Line.AccountId == accountId)
            .GroupBy(row => row.Line.AccountId)
            .Select(group => new
            {
                Debits = group.Sum(row => row.Line.Direction == EntryDirection.Debit ? row.Line.Amount : 0m),
                Credits = group.Sum(row => row.Line.Direction == EntryDirection.Credit ? row.Line.Amount : 0m),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return Present(account, totals?.Debits ?? 0m, totals?.Credits ?? 0m, asOf);
    }

    public async Task<TrialBalanceView> GetTrialBalanceAsync(
        DateOnly? asOf,
        CancellationToken cancellationToken)
    {
        var totals = await PostedLines(asOf)
            .GroupBy(row => row.Line.AccountId)
            .Select(group => new
            {
                AccountId = group.Key,
                Debits = group.Sum(row => row.Line.Direction == EntryDirection.Debit ? row.Line.Amount : 0m),
                Credits = group.Sum(row => row.Line.Direction == EntryDirection.Credit ? row.Line.Amount : 0m),
            })
            .ToListAsync(cancellationToken);

        var byAccount = totals.ToDictionary(row => row.AccountId);

        var accounts = await context.Accounts
            .AsNoTracking()
            .OrderBy(account => account.Code)
            .ToListAsync(cancellationToken);

        var rows = accounts
            .Select(account => byAccount.TryGetValue(account.Id, out var total)
                ? Present(account, total.Debits, total.Credits, asOf)
                : Present(account, 0m, 0m, asOf))
            .ToList();

        return new TrialBalanceView(
            asOf,
            rows,
            rows.Sum(row => row.Debits),
            rows.Sum(row => row.Credits));
    }

    public async Task<StatementView?> GetStatementAsync(
        Guid accountId,
        DateOnly? from,
        DateOnly? to,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var account = await context.Accounts
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);

        if (account is null)
        {
            return null;
        }

        // Presenting the balance on the account's normal side: for a liability, credits increase it.
        var sign = (int)account.Type.NormalBalance();

        var forAccount = PostedLines(null).Where(row => row.Line.AccountId == accountId);

        // Everything before the window is folded into an opening balance, so the running balance
        // on page 2 continues from page 1 instead of restarting at zero.
        var opening = from is null
            ? 0m
            : await SignedSum(forAccount.Where(row => row.Entry.OccurredOn < from.Value), cancellationToken) * sign;

        var windowed = forAccount
            .Where(row => !from.HasValue || row.Entry.OccurredOn >= from.Value)
            .Where(row => !to.HasValue || row.Entry.OccurredOn <= to.Value);

        var totalLines = await windowed.CountAsync(cancellationToken);

        var ordered = windowed
            .OrderBy(row => row.Entry.OccurredOn)
            .ThenBy(row => row.Entry.PostedAt)
            .ThenBy(row => row.Line.Id);

        var skipped = Math.Max(0, (page - 1) * pageSize);

        var carried = skipped == 0
            ? 0m
            : await SignedSum(ordered.Take(skipped), cancellationToken) * sign;

        var pageRows = await ordered
            .Skip(skipped)
            .Take(pageSize)
            .Select(row => new
            {
                row.Entry.Id,
                row.Entry.Reference,
                row.Entry.Description,
                row.Entry.OccurredOn,
                row.Line.Direction,
                row.Line.Amount,
                row.Line.Memo,
            })
            .ToListAsync(cancellationToken);

        var openingBalance = opening + carried;
        var running = openingBalance;
        var lines = new List<StatementLineView>(pageRows.Count);

        foreach (var row in pageRows)
        {
            running += row.Amount * (int)row.Direction * sign;

            lines.Add(new StatementLineView(
                row.Id,
                row.Reference,
                row.Description,
                row.OccurredOn,
                row.Direction,
                row.Amount,
                running,
                row.Memo));
        }

        return new StatementView(
            account.Id,
            account.Code,
            account.Currency.Code,
            openingBalance,
            running,
            page,
            pageSize,
            totalLines,
            lines);
    }

    /// <summary>Lines joined to their entry, optionally cut off at an accounting date.</summary>
    private IQueryable<LineWithEntry> PostedLines(DateOnly? asOf) =>
        context.JournalLines
            .AsNoTracking()
            .Join(
                context.JournalEntries.AsNoTracking(),
                line => line.JournalEntryId,
                entry => entry.Id,
                (line, entry) => new LineWithEntry { Line = line, Entry = entry })
            .Where(row => !asOf.HasValue || row.Entry.OccurredOn <= asOf.Value);

    /// <summary>
    /// Debits add, credits subtract. Expressed as a CASE rather than a cast of the enum, because
    /// the direction is stored as text and PostgreSQL cannot multiply by that.
    /// </summary>
    private static async Task<decimal> SignedSum(
        IQueryable<LineWithEntry> query,
        CancellationToken cancellationToken) =>
        await query.SumAsync(
            row => (decimal?)(row.Line.Direction == EntryDirection.Debit ? row.Line.Amount : -row.Line.Amount),
            cancellationToken) ?? 0m;

    private static AccountBalanceView Present(Account account, decimal debits, decimal credits, DateOnly? asOf)
    {
        var normalSide = account.Type.NormalBalance();

        return new AccountBalanceView(
            account.Id,
            account.Code,
            account.Name,
            account.Type,
            account.Currency.Code,
            debits,
            credits,
            normalSide,
            (debits - credits) * (int)normalSide,
            asOf);
    }

    private sealed class LineWithEntry
    {
        public required JournalLine Line { get; init; }

        public required JournalEntry Entry { get; init; }
    }
}
