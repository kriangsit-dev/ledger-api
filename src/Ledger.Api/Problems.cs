namespace Ledger.Api;

/// <summary>
/// Every failure leaves through here, so clients get one predictable shape
/// (RFC 9457 <c>application/problem+json</c>) instead of a mixture of bare strings and stack traces.
/// </summary>
public static class Problems
{
    public const string BaseType = "https://github.com/kriangsit-dev/ledger-api/docs/errors";

    public static IResult RuleViolation(HttpContext http, string detail) =>
        Create(
            http,
            StatusCodes.Status422UnprocessableEntity,
            "ledger-rule-violation",
            "The ledger refused this entry",
            detail);

    public static IResult NotFound(HttpContext http, string detail) =>
        Create(http, StatusCodes.Status404NotFound, "not-found", "Resource not found", detail);

    public static IResult MissingIdempotencyKey(HttpContext http) =>
        Create(
            http,
            StatusCodes.Status400BadRequest,
            "idempotency-key-required",
            "Idempotency-Key header is required",
            "Write requests must carry an Idempotency-Key header so they can be retried safely.");

    public static IResult IdempotencyKeyTooLong(HttpContext http) =>
        Create(
            http,
            StatusCodes.Status400BadRequest,
            "idempotency-key-invalid",
            "Idempotency-Key is too long",
            "The Idempotency-Key header must be 128 characters or fewer.");

    public static IResult IdempotencyKeyReused(HttpContext http) =>
        Create(
            http,
            StatusCodes.Status409Conflict,
            "idempotency-key-reused",
            "Idempotency-Key was reused with a different payload",
            "This key has already been used for a different request body. Use a new key for a new request.");

    public static IResult RequestInProgress(HttpContext http) =>
        Create(
            http,
            StatusCodes.Status409Conflict,
            "request-in-progress",
            "An identical request is still being processed",
            "A request with this Idempotency-Key has not finished yet. Retry shortly.");

    public static IResult ConcurrencyConflict(HttpContext http) =>
        Create(
            http,
            StatusCodes.Status409Conflict,
            "concurrency-conflict",
            "The resource changed while you were editing it",
            "Someone else updated this record. Re-read it and apply your change again.");

    private static IResult Create(HttpContext http, int status, string code, string title, string detail) =>
        Results.Problem(
            detail: detail,
            instance: $"{http.Request.Method} {http.Request.Path}",
            statusCode: status,
            title: title,
            type: $"{BaseType}/{code}",
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
