using System.Text;
using System.Text.Json.Serialization;
using Ledger.Api;
using Ledger.Api.Auth;
using Ledger.Api.Endpoints;
using Ledger.Api.Idempotency;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Reporting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- configuration & options

// Nothing below reads configuration eagerly. Every setting is resolved from DI at the moment it is
// needed, which is what lets an integration test host this exact Program with its own connection
// string and secrets — a value read straight off `builder.Configuration` here would be fixed
// before the test host ever got a chance to supply one.
var isDevelopment = builder.Environment.IsDevelopment();

builder.Services.AddOptions<AuthOptions>()
    .BindConfiguration(AuthOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(
        options => isDevelopment
            || !string.Equals(options.SigningKey, AuthOptions.DevelopmentSigningKey, StringComparison.Ordinal),
        "The development signing key cannot be used outside Development. "
        + "Set Auth__SigningKey through user-secrets, an environment variable or your secret store.")
    .ValidateOnStart();

// ---------------------------------------------------------------- services

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<LedgerDbContext>((serviceProvider, options) =>
{
    var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Ledger");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("ConnectionStrings:Ledger is not configured.");
    }

    options.UseNpgsql(
        connectionString,
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", LedgerDbContext.Schema));
});

builder.Services.AddScoped<LedgerReports>();
builder.Services.AddSingleton<TokenIssuer>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Enums travel as names ("Debit", not 1). A numeric enum in a payments API is the kind of
    // detail that silently breaks when someone reorders a declaration.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<LedgerExceptionHandler>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>>((jwt, auth) =>
    {
        var options = auth.Value;

        // Keep claim names exactly as they were issued instead of the legacy SOAP-era mapping.
        jwt.MapInboundClaims = false;

        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(
        LedgerScopes.Read,
        policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(LedgerScopes.ClaimType, LedgerScopes.Read, LedgerScopes.Write))
    .AddPolicy(
        LedgerScopes.Write,
        policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(LedgerScopes.ClaimType, LedgerScopes.Write));

builder.Services.AddOpenApi();

var app = builder.Build();

// ---------------------------------------------------------------- pipeline

app.UseExceptionHandler();
app.UseStatusCodePages();

// Must run before routing: endpoint filters see the body only after model binding has consumed it.
app.UseIdempotencyRequestBuffering();

if (app.Environment.IsDevelopment())
{
    await using (var scope = app.Services.CreateAsyncScope())
    {
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();
    }

    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }))
    .WithTags("Health")
    .AllowAnonymous()
    .ExcludeFromDescription();

app.MapGet(
        "/health/ready",
        async (LedgerDbContext context, CancellationToken cancellationToken) =>
            await context.Database.CanConnectAsync(cancellationToken)
                ? Results.Ok(new { status = "ready" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .WithTags("Health")
    .AllowAnonymous()
    .ExcludeFromDescription();

app.MapAuth();
app.MapAccounts();
app.MapJournal();
app.MapReports();

await app.RunAsync();

/// <summary>Exposed so the integration tests can host the real application through WebApplicationFactory.</summary>
public partial class Program;
