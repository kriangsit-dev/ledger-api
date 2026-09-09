using Ledger.Api.Auth;
using Ledger.Api.Contracts;

namespace Ledger.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuth(this IEndpointRouteBuilder routes)
    {
        var group = routes
            .MapGroup("/api/v1/auth")
            .WithTags("Auth")
            .AllowAnonymous();

        group.MapPost("/token", Issue)
            .WithSummary("Exchange client credentials for a bearer token")
            .Produces<TokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return group;
    }

    private static IResult Issue(TokenRequest request, TokenIssuer issuer)
    {
        var token = issuer.Issue(request);

        // One generic answer for both "no such client" and "wrong secret": telling them apart is
        // free reconnaissance for anyone guessing credentials.
        return token is null
            ? Results.Problem(
                detail: "Client credentials were not accepted.",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Invalid client credentials")
            : Results.Ok(token);
    }
}
