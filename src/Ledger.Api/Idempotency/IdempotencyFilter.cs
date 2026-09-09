using System.Security.Cryptography;
using System.Text;
using Ledger.Infrastructure.Idempotency;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Api.Idempotency;

/// <summary>
/// Makes a write endpoint safe to retry: the same <c>Idempotency-Key</c> replays the first
/// response instead of posting a second entry.
/// </summary>
/// <remarks>
/// <para>
/// A mobile client on a flaky connection will retry a POST it never saw the response to. Without
/// this, the customer is debited twice. The reservation row's composite primary key is what makes
/// the guarantee hold under genuine concurrency: two simultaneous retries both try to INSERT, the
/// database lets exactly one through, and the loser replays.
/// </para>
/// <para>
/// Reusing a key with a <em>different</em> body is a client bug, not a retry, so it is rejected
/// with 409 rather than silently returning someone else's result.
/// </para>
/// </remarks>
public sealed class IdempotencyFilter(
    LedgerDbContext context,
    TimeProvider clock,
    ILogger<IdempotencyFilter> logger) : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayHeaderName = "Idempotent-Replay";

    /// <summary>
    /// A process that dies mid-request leaves its reservation behind. After this long, a retry is
    /// allowed to take the reservation over rather than being locked out forever.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var http = invocation.HttpContext;

        if (!http.Request.Headers.TryGetValue(HeaderName, out var header) ||
            string.IsNullOrWhiteSpace(header.ToString()))
        {
            return Problems.MissingIdempotencyKey(http);
        }

        var key = header.ToString().Trim();

        if (key.Length > 128)
        {
            return Problems.IdempotencyKeyTooLong(http);
        }

        var endpoint = $"{http.Request.Method} {http.Request.Path.Value}";
        var requestHash = await HashRequestBodyAsync(http);
        var now = clock.GetUtcNow();

        var reserved = await TryReserveAsync(endpoint, key, requestHash, now, http.RequestAborted);

        if (reserved is ReservationOutcome.Conflict conflict)
        {
            return conflict.Result(http);
        }

        if (reserved is ReservationOutcome.Replay replay)
        {
            logger.LogInformation("Replaying idempotent response for {Endpoint} key {Key}.", endpoint, key);
            http.Response.Headers[ReplayHeaderName] = "true";
            return replay.ToResult();
        }

        try
        {
            var result = await next(invocation) as IResult;

            if (result is null)
            {
                await ReleaseAsync(endpoint, key, http.RequestAborted);
                return Results.Empty;
            }

            var captured = await ExecuteAndCaptureAsync(result, http);

            await CompleteAsync(endpoint, key, captured, clock.GetUtcNow(), http.RequestAborted);

            // This filter has already executed the result and written it to the wire, so it hands
            // back an empty result. Returning null would make the framework treat it as a value to
            // serialise and it would try to set headers on a response that has already started.
            return Results.Empty;
        }
        catch
        {
            // Free the key so the caller's retry can actually be retried.
            await ReleaseAsync(endpoint, key, CancellationToken.None);
            throw;
        }
    }

    private async Task<ReservationOutcome> TryReserveAsync(
        string endpoint,
        string key,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        context.IdempotencyRecords.Add(IdempotencyRecord.Reserve(endpoint, key, requestHash, now));

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return new ReservationOutcome.Reserved();
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            context.ChangeTracker.Clear();
        }

        var existing = await context.IdempotencyRecords
            .SingleOrDefaultAsync(record => record.Endpoint == endpoint && record.Key == key, cancellationToken);

        if (existing is null)
        {
            // Raced with a delete; the caller may simply try again.
            return new ReservationOutcome.Conflict(ConflictReason.InProgress);
        }

        if (!existing.Matches(requestHash))
        {
            return new ReservationOutcome.Conflict(ConflictReason.DifferentPayload);
        }

        if (existing.State == IdempotencyState.Completed)
        {
            return new ReservationOutcome.Replay(existing.StatusCode ?? StatusCodes.Status200OK, existing.ResponseBody);
        }

        if (now - existing.CreatedAt > StaleAfter)
        {
            logger.LogWarning(
                "Taking over a stale in-flight idempotency reservation for {Endpoint} key {Key}.",
                endpoint,
                key);

            existing.Complete(StatusCodes.Status200OK, null, null, now);
            context.Entry(existing).State = EntityState.Modified;
            await context.SaveChangesAsync(cancellationToken);

            return new ReservationOutcome.Reserved();
        }

        return new ReservationOutcome.Conflict(ConflictReason.InProgress);
    }

    private async Task CompleteAsync(
        string endpoint,
        string key,
        CapturedResponse captured,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var record = await context.IdempotencyRecords
            .SingleOrDefaultAsync(candidate => candidate.Endpoint == endpoint && candidate.Key == key, cancellationToken);

        if (record is null)
        {
            return;
        }

        // Only successful responses are worth replaying. A 4xx should be re-evaluated on retry,
        // because the client has probably fixed whatever was wrong.
        if (captured.StatusCode is >= 200 and < 300)
        {
            record.Complete(captured.StatusCode, captured.Body, captured.Location, now);
            await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            context.IdempotencyRecords.Remove(record);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ReleaseAsync(string endpoint, string key, CancellationToken cancellationToken)
    {
        try
        {
            var record = await context.IdempotencyRecords
                .SingleOrDefaultAsync(
                    candidate => candidate.Endpoint == endpoint && candidate.Key == key,
                    cancellationToken);

            if (record is { State: IdempotencyState.InFlight })
            {
                context.IdempotencyRecords.Remove(record);
                await context.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            // Releasing is best-effort; the stale-takeover window is the real safety net.
            logger.LogWarning(exception, "Could not release idempotency reservation {Endpoint} {Key}.", endpoint, key);
        }
    }

    /// <summary>
    /// Runs the endpoint's result against a buffered body so the exact bytes sent to the client
    /// can be stored and replayed byte-for-byte on the next retry.
    /// </summary>
    private static async Task<CapturedResponse> ExecuteAndCaptureAsync(IResult result, HttpContext http)
    {
        var originalBody = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;

        try
        {
            await result.ExecuteAsync(http);
        }
        finally
        {
            http.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var bytes = buffer.ToArray();

        if (bytes.Length > 0)
        {
            await http.Response.Body.WriteAsync(bytes, http.RequestAborted);
        }

        http.Response.Headers.TryGetValue("Location", out var location);

        return new CapturedResponse(
            http.Response.StatusCode,
            bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes),
            location.ToString() is { Length: > 0 } value ? value : null);
    }

    private static async Task<string> HashRequestBodyAsync(HttpContext http)
    {
        var body = http.Request.Body;

        if (!body.CanSeek)
        {
            // Fail loudly. Hashing an unbuffered — and by now fully consumed — stream would give
            // every request the same fingerprint, and the "same key, different payload" check
            // would quietly pass everything.
            throw new InvalidOperationException(
                $"Idempotent endpoints need a re-readable request body. Call "
                + $"{nameof(IdempotencyExtensions.UseIdempotencyRequestBuffering)}() before routing.");
        }

        body.Position = 0;

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(body, http.RequestAborted);

        body.Position = 0;

        return Convert.ToHexStringLower(hash);
    }

    private enum ConflictReason
    {
        DifferentPayload,
        InProgress,
    }

    private abstract record ReservationOutcome
    {
        public sealed record Reserved : ReservationOutcome;

        public sealed record Replay(int StatusCode, string? Body) : ReservationOutcome
        {
            public IResult ToResult() =>
                Body is null
                    ? Results.StatusCode(StatusCode)
                    : Results.Content(Body, "application/json", Encoding.UTF8, StatusCode);
        }

        public sealed record Conflict(ConflictReason Reason) : ReservationOutcome
        {
            public IResult Result(HttpContext http) =>
                Reason == ConflictReason.DifferentPayload
                    ? Problems.IdempotencyKeyReused(http)
                    : Problems.RequestInProgress(http);
        }
    }

    private sealed record CapturedResponse(int StatusCode, string? Body, string? Location);
}
