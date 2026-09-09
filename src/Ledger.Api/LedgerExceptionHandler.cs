using Ledger.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Api;

/// <summary>
/// Turns the two exceptions the ledger raises on purpose into proper HTTP answers. Anything else
/// is left alone so it surfaces as a 500 and gets logged rather than being quietly swallowed.
/// </summary>
public sealed class LedgerExceptionHandler(ILogger<LedgerExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        IResult? result = exception switch
        {
            LedgerRuleViolationException violation => Problems.RuleViolation(httpContext, violation.Message),
            DbUpdateConcurrencyException => Problems.ConcurrencyConflict(httpContext),
            _ => null,
        };

        if (result is null)
        {
            return false;
        }

        logger.LogInformation(
            "Rejected {Method} {Path}: {Reason}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            exception.Message);

        await result.ExecuteAsync(httpContext);
        return true;
    }
}
