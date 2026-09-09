using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Ledger.Api;
using Ledger.Api.Auth;
using Ledger.Api.Endpoints;
using Ledger.Api.Idempotency;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Reporting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Container platforms (Render, Cloud Run, Fly.io) hand the port over in PORT rather than letting
// the app choose one. Reading it here keeps the Dockerfile free of a shell wrapper.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// ---------------------------------------------------------------- configuration & options

// Two values are read eagerly, and only two: the hosting environment and the CORS origins. Both
// decide which middleware exists at all, so they cannot wait for DI. Everything else — connection
// string, signing key, clients — is resolved from the container at the moment it is needed. That
// is what lets an integration test host this exact Program with its own settings; a value read
// straight off `builder.Configuration` is fixed before a test host can supply one.
var isDevelopment = builder.Environment.IsDevelopment();
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

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

// The deployed instance is a public demo with a write scope, so it needs a ceiling. Partitioning
// by client IP (which is meaningful because forwarded headers are honoured behind Render's proxy)
// keeps one noisy caller from spending the whole free tier.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// Only configured when origins are supplied, so a local run stays free of CORS entirely.
if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders("Idempotent-Replay")));
}

var app = builder.Build();

// ---------------------------------------------------------------- pipeline

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

if (allowedOrigins.Length > 0)
{
    app.UseCors();
}

// Must run before routing: endpoint filters see the body only after model binding has consumed it.
app.UseIdempotencyRequestBuffering();

// Deliberately a flag rather than "if Development". Migrating on start is right for a container
// that owns its database and wrong the moment two instances start at once, so it should be a
// decision someone made, not a side effect of which environment name happens to be set.
if (app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
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
