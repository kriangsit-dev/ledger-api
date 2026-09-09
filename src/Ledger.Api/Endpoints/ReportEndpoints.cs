using Ledger.Api.Auth;
using Ledger.Infrastructure.Reporting;

namespace Ledger.Api.Endpoints;

public static class ReportEndpoints
{
    public static RouteGroupBuilder MapReports(this IEndpointRouteBuilder routes)
    {
        var group = routes
            .MapGroup("/api/v1/reports")
            .WithTags("Reports");

        group.MapGet("/trial-balance", TrialBalanceAsync)
            .RequireAuthorization(LedgerScopes.Read)
            .WithSummary("Trial balance")
            .WithDescription(
                "Total debits and total credits across every account. In a ledger that has not been "
                + "corrupted these two numbers are always equal, which is what the integration tests assert.");

        return group;
    }

    private static async Task<IResult> TrialBalanceAsync(
        LedgerReports reports,
        CancellationToken cancellationToken,
        DateOnly? asOf = null) =>
        Results.Ok(await reports.GetTrialBalanceAsync(asOf, cancellationToken));
}
