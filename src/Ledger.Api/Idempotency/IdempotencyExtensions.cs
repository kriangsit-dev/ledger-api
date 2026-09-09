namespace Ledger.Api.Idempotency;

public static class IdempotencyExtensions
{
    /// <summary>
    /// Makes the request body re-readable for endpoints that fingerprint it.
    /// </summary>
    /// <remarks>
    /// Endpoint filters run <em>after</em> model binding, by which point the request stream has
    /// already been consumed. Enabling buffering inside the filter is too late: it would hash an
    /// empty stream, every request would produce the same fingerprint, and reusing a key with a
    /// different payload would silently replay someone else's response instead of being rejected.
    /// Buffering therefore has to be switched on out here, before routing.
    /// </remarks>
    public static IApplicationBuilder UseIdempotencyRequestBuffering(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey(IdempotencyFilter.HeaderName))
            {
                context.Request.EnableBuffering();
            }

            await next(context);
        });
}
