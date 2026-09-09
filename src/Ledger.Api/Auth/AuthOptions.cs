using System.ComponentModel.DataAnnotations;

namespace Ledger.Api.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>The placeholder shipped in appsettings.Development.json. Refused outside Development.</summary>
    public const string DevelopmentSigningKey = "development-only-signing-key-do-not-use-in-production";

    [Required]
    public string Issuer { get; init; } = "ledger-api";

    [Required]
    public string Audience { get; init; } = "ledger-api";

    /// <summary>
    /// HMAC signing key. Supply it through user-secrets or the environment
    /// (<c>Auth__SigningKey</c>) — never through a file that is committed.
    /// </summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; init; } = string.Empty;

    [Range(1, 1440)]
    public int TokenLifetimeMinutes { get; init; } = 60;

    public IReadOnlyList<ApiClient> Clients { get; init; } = [];
}

public sealed class ApiClient
{
    [Required]
    public string ClientId { get; init; } = string.Empty;

    [Required]
    public string ClientSecret { get; init; } = string.Empty;

    public IReadOnlyList<string> Scopes { get; init; } = [];
}

public static class LedgerScopes
{
    public const string Read = "ledger.read";
    public const string Write = "ledger.write";
    public const string ClaimType = "scope";
}
