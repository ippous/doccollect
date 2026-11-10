using System.Security.Cryptography;
using DocCollect.Api.Services;
using DocCollect.Core.Storage;
using DocCollect.Domain;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Shared;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DocCollect.Api.Infrastructure;

/// <summary>Traduit les exceptions en réponses JSON homogènes (ApiError).</summary>
public static class ErrorHandling
{
    public static void UseApiErrors(this IApplicationBuilder app) => app.UseExceptionHandler(e => e.Run(async ctx =>
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DocCollect.Errors");
        var (status, error) = ex switch
        {
            DomainException d => (StatusCodes.Status400BadRequest, new ApiError(d.Code, d.Message)),
            NotFoundException n => (StatusCodes.Status404NotFound, new ApiError("not_found", n.Message)),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, new ApiError("forbidden", "Accès refusé.")),
            DocCollect.Core.Notifications.EmailSendException m => (StatusCodes.Status502BadGateway, new ApiError("email_unavailable",
                $"{m.Message} L'opération a été enregistrée, mais le destinataire n'a pas été prévenu.")),
            StorageException s => (StatusCodes.Status502BadGateway, new ApiError("storage_unavailable", $"Stockage indisponible : {s.Message}")),
            CryptographicException => (StatusCodes.Status500InternalServerError, new ApiError("integrity_error", "Le document stocké est illisible ou altéré.")),
            BadHttpRequestException b => (b.StatusCode, new ApiError("bad_request",
                b.StatusCode == StatusCodes.Status413PayloadTooLarge ? "Fichier trop volumineux." : "Requête invalide.")),
            _ => (StatusCodes.Status500InternalServerError, new ApiError("internal_error", "Erreur interne. Elle a été journalisée."))
        };
        if (status >= 500) logger.LogError(ex, "Erreur sur {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(error);
    }));
}

/// <summary>
/// En-têtes de sécurité. L'interface ne peut être affichée en iframe que sous /embed/{demandeur}/…,
/// et uniquement par les origines déclarées par ce demandeur (CSP frame-ancestors).
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IMemoryCache cache)
{
    public async Task InvokeAsync(HttpContext ctx, AppDbContext db)
    {
        var frameAncestors = "'self'";
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase))
        {
            var slug = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)?.ToLowerInvariant();
            if (slug is not null)
            {
                var origins = await cache.GetOrCreateAsync($"embed:{slug}", async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                    return await db.Tenants.Where(t => t.Slug == slug && !t.Disabled)
                        .Select(t => t.AllowedEmbedOrigins).FirstOrDefaultAsync() ?? [];
                });
                if (origins is { Count: > 0 }) frameAncestors += " " + string.Join(' ', origins);
            }
        }

        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["Content-Security-Policy"] = $"frame-ancestors {frameAncestors}";
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=()";
            return Task.CompletedTask;
        });
        await next(ctx);
    }
}
