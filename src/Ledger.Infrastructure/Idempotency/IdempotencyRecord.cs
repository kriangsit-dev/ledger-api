namespace Ledger.Infrastructure.Idempotency;

public enum IdempotencyState
{
    /// <summary>A request with this key is being processed right now.</summary>
    InFlight = 1,

    /// <summary>The request finished; <see cref="IdempotencyRecord.ResponseBody"/> is the answer to replay.</summary>
    Completed = 2,
}

/// <summary>
/// The reservation a write request takes out on its <c>Idempotency-Key</c>.
/// </summary>
/// <remarks>
/// The primary key is (endpoint, key), so the database — not application code — is what decides
/// which of two simultaneous retries gets to proceed. The loser sees a unique-violation and
/// replays instead of posting a second entry.
/// </remarks>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord() { }

    public string Endpoint { get; private set; } = null!;

    public string Key { get; private set; } = null!;

    /// <summary>SHA-256 of the request body, so the same key with a different payload is rejected.</summary>
    public string RequestHash { get; private set; } = null!;

    public IdempotencyState State { get; private set; }

    public int? StatusCode { get; private set; }

    public string? ResponseBody { get; private set; }

    public string? ResourceLocation { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static IdempotencyRecord Reserve(string endpoint, string key, string requestHash, DateTimeOffset now) =>
        new()
        {
            Endpoint = endpoint,
            Key = key,
            RequestHash = requestHash,
            State = IdempotencyState.InFlight,
            CreatedAt = now,
        };

    public void Complete(int statusCode, string? responseBody, string? resourceLocation, DateTimeOffset now)
    {
        State = IdempotencyState.Completed;
        StatusCode = statusCode;
        ResponseBody = responseBody;
        ResourceLocation = resourceLocation;
        CompletedAt = now;
    }

    public bool Matches(string requestHash) =>
        string.Equals(RequestHash, requestHash, StringComparison.Ordinal);
}
