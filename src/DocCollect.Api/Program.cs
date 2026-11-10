using System.Threading.RateLimiting;
using DocCollect.Api.Auth;
using DocCollect.Api.Endpoints;
using DocCollect.Api.Infrastructure;
using DocCollect.Api.Services;
using DocCollect.Infrastructure;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.AddMemoryLogs(); // derniers journaux consultables par l'admin

const long MaxUploadBytes = 110L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUploadBytes);

// ---------- Infrastructure ----------
builder.Services.AddDocCollectInfrastructure(builder.Configuration);
if (builder.Configuration.GetValue("Workers:Enabled", true))
    builder.Services.AddDocCollectWorkers();
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.Section));
builder.Services.AddMemoryCache();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<DossierService>();
builder.Services.AddScoped<TokenIssuer>();

// ---------- Identité : jetons « bearer » + 2FA obligatoire ----------
builder.Services.AddAuthentication(IdentityConstants.BearerScheme)
    .AddBearerToken(IdentityConstants.BearerScheme, o =>
    {
        o.BearerTokenExpiration = TimeSpan.FromMinutes(builder.Configuration.GetValue("Auth:AccessTokenMinutes", 30));
        o.RefreshTokenExpiration = TimeSpan.FromDays(builder.Configuration.GetValue("Auth:RefreshTokenDays", 7));
    })
    .AddIdentityCookies(); // requis par SignInManager (contrôle « appareil mémorisé »), non utilisé pour l'API

builder.Services.AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
        o.SignIn.RequireConfirmedEmail = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();

builder.Services.AddAuthorizationBuilder()
    // Par défaut : connecté ET 2FA activée. Seuls les écrans d'activation de la 2FA dérogent (politique « enrolling »).
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireClaim(DcClaims.TwoFactor, "true").Build())
    .AddPolicy(Policies.Enrolling, p => p.RequireAuthenticatedUser())
    .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin).RequireClaim(DcClaims.TwoFactor, "true"))
    .AddPolicy(Policies.Requester, p => p.RequireRole(Roles.Requester).RequireClaim(DcClaims.TwoFactor, "true").RequireClaim(DcClaims.Tenant))
    .AddPolicy(Policies.Provider, p => p.RequireRole(Roles.Provider).RequireClaim(DcClaims.TwoFactor, "true"));

var authPerMinute = builder.Configuration.GetValue("Auth:RateLimitPerMinute", 20);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

// Derrière Caddy (reverse proxy dans le même réseau Docker).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseApiErrors();
if (!app.Environment.IsDevelopment()) app.UseHsts();

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (!app.Environment.IsProduction())
    app.MapOpenApi().AllowAnonymous();

app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapRequesterEndpoints();
app.MapProviderEndpoints();
app.MapPublicEndpoints();
app.MapMonitoringEndpoints();
app.MapGet("/api/config", (IConfiguration c) => new { environment = c["App:EnvironmentLabel"] }).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

// Échec immédiat si les clés de chiffrement sont absentes ou invalides (plutôt qu'au premier dépôt).
app.Services.GetRequiredService<DocCollect.Core.Security.IMasterKeyProvider>();

if (app.Configuration.GetValue("Database:InitializeOnStartup", true))
{
    var seed = app.Configuration.GetSection(SeedOptions.Section).Get<SeedOptions>() ?? new SeedOptions();
    await DataSeeder.InitializeAsync(app.Services, seed, app.Logger);
}

app.Run();

/// <summary>Point d'entrée exposé aux tests d'intégration (WebApplicationFactory).</summary>
public partial class Program;
