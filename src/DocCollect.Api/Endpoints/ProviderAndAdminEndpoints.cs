using DocCollect.Api.Auth;
using DocCollect.Api.Services;
using DocCollect.Core.Ai;
using DocCollect.Core.Storage;
using DocCollect.Domain;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Infrastructure.Storage;
using DocCollect.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocCollect.Api.Endpoints;

/// <summary>Espace fournisseur de documents : uniquement les dossiers qui lui sont adressés.</summary>
public static class ProviderEndpoints
{
    public static void MapProviderEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/provider").WithTags("Fournisseur").RequireAuthorization(Policies.Provider);

        g.MapGet("/dossiers", async (HttpContext http, AppDbContext db) =>
        {
            var userId = http.User.UserId();
            var mail = DossierService.NormalizeEmail(http.User.Email());
            var list = await db.Dossiers.WithDetails()
                .Where(d => d.Status != DossierStatus.Draft && (d.ProviderUserId == userId || (d.ProviderUserId == null && d.ProviderEmail == mail)))
                .OrderByDescending(d => d.CreatedAt).ToListAsync();
            var ids = list.Select(d => d.TenantId).Distinct().ToList();
            var tenants = await db.Tenants.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
            return list.Select(d => new { Summary = d.ToSummary(), TenantName = tenants[d.TenantId].Name, TenantSlug = tenants[d.TenantId].Slug });
        });

        g.MapGet("/dossiers/{id:guid}", async (Guid id, HttpContext http, DossierService svc, AppDbContext db) =>
        {
            var d = await svc.GetForProviderAsync(http.User.UserId(), http.User.Email(), id);
            var name = await db.Tenants.Where(t => t.Id == d.TenantId).Select(t => t.Name).FirstAsync();
            return d.ToDto(name);
        });

        g.MapPost("/dossiers/{id:guid}/items/{itemId:guid}/documents", async (Guid id, Guid itemId, IFormFile file, HttpContext http, DossierService svc, CancellationToken ct) =>
        {
            var d = await svc.GetForProviderAsync(http.User.UserId(), http.User.Email(), id, ct);
            await using var stream = file.OpenReadStream();
            var doc = await svc.UploadAsync(d, itemId, file.FileName, file.ContentType, stream, http.User.UserId(), ct);
            return Results.Ok(doc.ToDto());
        }).DisableAntiforgery();

        g.MapDelete("/dossiers/{id:guid}/documents/{docId:guid}", async (Guid id, Guid docId, HttpContext http, DossierService svc) =>
        {
            var d = await svc.GetForProviderAsync(http.User.UserId(), http.User.Email(), id);
            await svc.RemoveDocumentAsync(d, docId, http.User.UserId());
            return Results.NoContent();
        });

        g.MapGet("/dossiers/{id:guid}/documents/{docId:guid}/content", async (Guid id, Guid docId, HttpContext http, DossierService svc, CancellationToken ct) =>
            await svc.DownloadAsync(await svc.GetForProviderAsync(http.User.UserId(), http.User.Email(), id, ct), docId, http.User.UserId(), ct));
    }
}

/// <summary>Administration de la plateforme.</summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization(Policies.Admin);

        // ---------- Demandeurs ----------

        g.MapGet("/tenants", async (AppDbContext db) =>
            (await db.Tenants.OrderBy(t => t.Name).ToListAsync()).Select(t => t.ToDto()));

        g.MapPost("/tenants", async (CreateTenantRequest req, AppDbContext db, InvitationService invitations, UserManager<AppUser> users) =>
        {
            var slug = req.Slug.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(req.Name) || !System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9][a-z0-9-]{1,58}[a-z0-9]$"))
                throw new DomainException(ErrorCodes.Validation, "Nom obligatoire ; identifiant court en minuscules, chiffres et tirets (3 à 60 caractères).");
            if (await db.Tenants.AnyAsync(t => t.Slug == slug))
                throw new DomainException(ErrorCodes.Validation, "Cet identifiant court est déjà utilisé.");
            var owner = DossierService.NormalizeEmail(req.OwnerEmail);
            if (await users.FindByEmailAsync(owner) is not null)
                throw new DomainException(ErrorCodes.Validation, "Un compte existe déjà pour l'adresse du responsable.");

            var tenant = new Tenant { Name = req.Name.Trim(), Slug = slug };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            await invitations.InviteTeamMemberAsync(owner, Roles.Requester, tenant);
            return Results.Created($"/api/admin/tenants/{tenant.Id}", tenant.ToDto());
        });

        g.MapPut("/tenants/{id:guid}/settings", async (Guid id, UpdateTenantSettingsRequest req, AppDbContext db, AiClassifierRegistry ai) =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id) ?? throw new NotFoundException("Demandeur introuvable.");
            await TenantSettings.ApplyAsync(tenant, req, db, ai);
            await db.SaveChangesAsync();
            return tenant.ToDto();
        });

        g.MapPost("/tenants/{id:guid}/disable", async (Guid id, bool disabled, AppDbContext db) =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id) ?? throw new NotFoundException("Demandeur introuvable.");
            tenant.Disabled = disabled;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/users", async (AppDbContext db, UserManager<AppUser> users) =>
        {
            var list = await db.Users.OrderBy(u => u.Email).Take(1000).ToListAsync();
            var result = new List<UserDto>();
            foreach (var u in list)
                result.Add(new UserDto(u.Id, u.Email!, u.DisplayName, (await users.GetRolesAsync(u)).ToList(), u.TenantId,
                    u.TwoFactorEnabled, u.LockoutEnd > DateTimeOffset.UtcNow));
            return result;
        });

        g.MapPost("/users/{id}/unlock", async (string id, UserManager<AppUser> users) =>
        {
            var u = await users.FindByIdAsync(id) ?? throw new NotFoundException("Utilisateur introuvable.");
            await users.SetLockoutEndDateAsync(u, null);
            await users.ResetAccessFailedCountAsync(u);
            return Results.NoContent();
        });

        // Réinitialise la 2FA (perte du téléphone et des codes de récupération) : l'utilisateur devra la réactiver.
        g.MapPost("/users/{id}/reset-2fa", async (string id, UserManager<AppUser> users, AppDbContext db, HttpContext http) =>
        {
            var u = await users.FindByIdAsync(id) ?? throw new NotFoundException("Utilisateur introuvable.");
            await users.SetTwoFactorEnabledAsync(u, false);
            await users.ResetAuthenticatorKeyAsync(u);
            u.PreferredTwoFactorMethod = TwoFactorMethod.None;
            await users.UpdateAsync(u);
            db.AuditEvents.Add(new AuditEvent { UserId = http.User.UserId(), Action = "admin.reset-2fa", Details = u.Email });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/admins/invite", async (InviteUserRequest req, InvitationService invitations, AppDbContext db, IServiceProvider sp) =>
        {
            var address = DossierService.NormalizeEmail(req.Email);
            var link = await invitations.CreateAsync(address, Roles.Admin, null, null);
            await sp.GetRequiredService<DocCollect.Core.Notifications.IEmailSender>().SendAsync(
                DocCollect.Core.Notifications.EmailTemplates.UserInvitation(address, "la plateforme DocCollect", "administrateur", link));
            return Results.NoContent();
        });

        // ---------- Stockages plateforme ----------

        g.MapGet("/storage/providers", (StorageProviderRegistry registry) => registry.Describe());

        g.MapGet("/storage/profiles", async (AppDbContext db, StorageProfileService svc) =>
            await svc.ToDtosAsync(await db.StorageProfiles.OrderBy(p => p.TenantId).ThenBy(p => p.Name).ToListAsync()));

        g.MapPost("/storage/profiles", async (SaveStorageProfileRequest req, AppDbContext db, StorageProfileService svc) =>
        {
            var profile = new StorageProfile { TenantId = null, Name = req.Name.Trim(), ProviderType = req.ProviderType, IsDefault = req.IsDefault };
            profile.SettingsJson = svc.PrepareSettings(req.ProviderType, req.Settings, null);
            if (req.IsDefault)
                await ClearPlatformDefaultAsync(db);
            db.StorageProfiles.Add(profile);
            await db.SaveChangesAsync();
            return Results.Created($"/api/admin/storage/profiles/{profile.Id}", svc.ToDto(profile));
        });

        g.MapPut("/storage/profiles/{id:guid}", async (Guid id, SaveStorageProfileRequest req, AppDbContext db, StorageProfileService svc) =>
        {
            var profile = await db.StorageProfiles.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == null)
                          ?? throw new NotFoundException("Profil introuvable.");
            if (req.IsDefault && !profile.IsDefault)
                await ClearPlatformDefaultAsync(db);
            profile.SettingsJson = svc.PrepareSettings(req.ProviderType, req.Settings, profile.ProviderType == req.ProviderType ? profile : null);
            profile.Name = req.Name.Trim();
            profile.ProviderType = req.ProviderType;
            profile.IsDefault = req.IsDefault;
            await db.SaveChangesAsync();
            return svc.ToDto(profile);
        });

        g.MapPost("/storage/profiles/{id:guid}/test", async (Guid id, AppDbContext db, StorageProfileService svc, CancellationToken ct) =>
        {
            var profile = await db.StorageProfiles.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Profil introuvable.");
            return await svc.TestAsync(profile, ct);
        });

        // Suppression autorisée seulement si le stockage est vide et inutilisé (plateforme ou demandeur).
        g.MapDelete("/storage/profiles/{id:guid}", async (Guid id, AppDbContext db, StorageProfileService svc, HttpContext http, CancellationToken ct) =>
        {
            var profile = await db.StorageProfiles.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Profil introuvable.");
            await svc.DeleteAsync(profile, ct);
            db.AuditEvents.Add(new AuditEvent { TenantId = profile.TenantId, UserId = http.User.UserId(), Action = "storage.deleted", Details = profile.Name });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---------- Modèles publics ----------

        g.MapGet("/templates", async (AppDbContext db) =>
            (await db.Templates.Where(t => t.Visibility == TemplateVisibility.Public).OrderBy(t => t.Name).ToListAsync()).Select(t => t.ToDto()));

        g.MapPost("/templates", async (SaveTemplateRequest req, AppDbContext db) =>
        {
            var t = await TemplateStore.CreateAsync(db, req with { Visibility = TemplateVisibility.Public }, null);
            return Results.Created($"/api/admin/templates/{t.Id}", t.ToDto());
        });

        g.MapPut("/templates/{id:guid}", async (Guid id, SaveTemplateRequest req, AppDbContext db) =>
        {
            var t = await db.Templates.FirstOrDefaultAsync(x => x.Id == id && x.Visibility == TemplateVisibility.Public)
                    ?? throw new NotFoundException("Modèle introuvable.");
            await TemplateStore.UpdateAsync(db, t, req with { Visibility = TemplateVisibility.Public });
            return t.ToDto();
        });

        g.MapDelete("/templates/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var t = await db.Templates.FirstOrDefaultAsync(x => x.Id == id && x.Visibility == TemplateVisibility.Public)
                    ?? throw new NotFoundException("Modèle introuvable.");
            db.Templates.Remove(t);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---------- IA et facturation ----------

        g.MapGet("/ai-providers", (AiClassifierRegistry ai) => ai.Describe());
        g.MapGet("/ai-usage", async (int? year, AppDbContext db) => await AiUsageReport.ForTenantsAsync(db, null, year ?? DateTime.UtcNow.Year));

        static async Task ClearPlatformDefaultAsync(AppDbContext db)
        {
            foreach (var p in await db.StorageProfiles.Where(p => p.TenantId == null && p.IsDefault).ToListAsync())
                p.IsDefault = false;
        }

        g.MapGet("/audit", async (Guid? tenantId, int? take, AppDbContext db) =>
            await db.AuditEvents.Where(a => tenantId == null || a.TenantId == tenantId)
                .OrderByDescending(a => a.Id).Take(Math.Clamp(take ?? 200, 1, 1000)).ToListAsync());
    }
}

/// <summary>Données publiques nécessaires au mode embarqué (nom du demandeur), sans authentification.</summary>
public static class PublicEndpoints
{
    public static void MapPublicEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/public/tenants/{slug}", async (string slug, AppDbContext db) =>
        {
            var t = await db.Tenants.FirstOrDefaultAsync(x => x.Slug == slug && !x.Disabled);
            return t is null ? Results.NotFound() : Results.Ok(new { t.Name, t.Slug });
        }).AllowAnonymous();

        app.MapGet("/healthz", async (AppDbContext db) =>
            await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503)).AllowAnonymous();
    }
}
