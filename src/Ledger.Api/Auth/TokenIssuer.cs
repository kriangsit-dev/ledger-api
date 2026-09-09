using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Ledger.Api.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ledger.Api.Auth;

/// <summary>
/// Issues short-lived bearer tokens for configured machine clients.
/// </summary>
/// <remarks>
/// A real deployment would delegate this to an identity provider (Entra ID, Keycloak, Auth0).
/// It lives here so the API can be run and demonstrated with nothing else installed; the
/// authorisation model — scope claims checked by policy — is identical either way.
/// </remarks>
public sealed class TokenIssuer(IOptions<AuthOptions> options, TimeProvider clock)
{
    private readonly AuthOptions _options = options.Value;

    public TokenResponse? Issue(TokenRequest request)
    {
        var client = _options.Clients.FirstOrDefault(candidate =>
            string.Equals(candidate.ClientId, request.ClientId, StringComparison.Ordinal));

        // The comparison runs whether or not the client id matched, and in constant time, so an
        // attacker cannot tell "no such client" apart from "wrong secret" by timing the response.
        var expected = client?.ClientSecret ?? string.Empty;
        var secretMatches = FixedTimeEquals(expected, request.ClientSecret);

        if (client is null || !secretMatches)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_options.TokenLifetimeMinutes);

        var identity = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, client.ClientId),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            .. client.Scopes.Select(scope => new Claim(LedgerScopes.ClaimType, scope)),
        ]);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = identity,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
                SecurityAlgorithms.HmacSha256),
        };

        var accessToken = new JsonWebTokenHandler().CreateToken(descriptor);

        return new TokenResponse(
            accessToken,
            "Bearer",
            _options.TokenLifetimeMinutes * 60,
            string.Join(' ', client.Scopes));
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
}
