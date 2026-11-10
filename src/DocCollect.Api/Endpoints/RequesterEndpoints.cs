using DocCollect.Api.Auth;
using DocCollect.Api.Services;
using DocCollect.Core.Ai;
using DocCollect.Core.Security;
using DocCollect.Core.Storage;
using DocCollect.Domain;
using DocCollect.Infrastructure.Delivery;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Infrastructure.Storage;
using DocCollect.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocCollect.Api.Endpoints;

/// <summary>Espace demandeur : tout est filtré sur le demandeur (tenant) de l'utilisateur connecté.</summary>
public static class RequesterEndpoints
{
    public static void MapRequesterEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/requester").WithTags("Demandeur").RequireAuthorization(Policies.Requester);

        // ---------- Paramètres du demandeur ----------

        g.MapGet("/tenant", async (HttpContext http, AppDbContext db) =>
            (await db.Tenants.FirstAsync(t => t.Id == http.User.TenantId())).ToDto());

        g.MapPut("/tenant/settings", async (UpdateTenantSettingsRequest req, HttpContext http, AppDbContext db, AiClassifierRegistry ai) =>
        {
            var tenantId = http.User.TenantId();
            var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId);
            await TenantSettings.ApplyAsync(tenant, req, db, ai);
            db.AuditEvents.Add(new AuditEvent { TenantId = tenantId, UserId = http.User.UserId(), Action = "tenant.settings-updated" });
            await db.SaveChangesAsync();
            return tenant.ToDto();
        });

        g.MapGet("/ai-providers", (AiClassifierRegistry ai) => ai.Describe());

        g.MapGet("/ai-usage", async (int? year, HttpContext http, AppDbContext db) =>
            await AiUsageReport.ForTenantsAsync(db, http.User.TenantId(), year ?? DateTime.UtcNow.Year));

        // ---------- Stockages du demandeur ----------

        g.MapGet("/storage/providers", (StorageProviderRegistry registry) => registry.Describe());

        g.MapGet("/storage/profiles", async (HttpContext http, AppDbContext db, StorageProfileService svc) =>
        {
            var tenantId = http.User.TenantId();
            var own = await db.StorageProfiles.Where(p => p.TenantId == tenantId).OrderBy(p => p.Name).ToListAsync();
            return await svc.ToDtosAsync(own);
        });

        g.MapPost("/storage/profiles", async (SaveStorageProfileRequest req, HttpContext http, AppDbContext db, StorageProfileService svc) =>
        {
            var profile = new StorageProfile { TenantId = http.User.TenantId(), Name = req.Name.Trim(), ProviderType = req.ProviderType };
            profile.SettingsJson = svc.PrepareSettings(req.ProviderType, req.Settings, null);
            db.StorageProfiles.Add(profile);
            await db.SaveChangesAsync();
            return Results.Created($"/api/requester/storage/profiles/{profile.Id}", svc.ToDto(profile));
        });

        g.MapPut("/storage/profiles/{id:guid}", async (Guid id, SaveStorageProfileRequest req, HttpContext http, AppDbContext db, StorageProfileService svc) =>
        {
            var profile = await db.StorageProfiles.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == http.User.TenantId())
                          ?? throw new NotFoundException("Profil introuvable.");
            profile.SettingsJson = svc.PrepareSettings(req.ProviderType, req.Settings, profile.ProviderType == req.ProviderType ? profile : null);
            profile.Name = req.Name.Trim();
            profile.ProviderType = req.ProviderType;
            await db.SaveChangesAsync();
            return svc.ToDto(profile);
        });

        g.MapPost("/storage/profiles/{id:guid}/test", async (Guid id, HttpContext http, AppDbContext db, StorageProfileService svc, CancellationToken ct) =>
        {
            var profile = await db.StorageProfiles.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == http.User.TenantId(), ct)
                          ?? throw new NotFoundException("Profil introuvable.");
            return await svc.TestAsync(profile, ct);
        });

        // Suppression autorisée seulement si le stockage est vide et inutilisé.
        g.MapDelete("/storage/profiles/{id:guid}", async (Guid id, HttpContext http, AppDbContext db, StorageProfileService svc, CancellationToken ct) =>
        {
            var tenantId = http.User.TenantId();
            var profile = await db.StorageProfiles.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct)
                          ?? throw new NotFoundException("Profil introuvable.");
            await svc.DeleteAsync(profile, ct);
            db.AuditEvents.Add(new AuditEvent { TenantId = tenantId, UserId = http.User.UserId(), Action = "storage.deleted", Details = profile.Name });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---------- Modèles ----------

        g.MapGet("/templates", async (HttpContext http, AppDbContext db) =>
        {
            var tenantId = http.User.TenantId();
            var list = await db.Templates.Where(t => t.TenantId == tenantId || t.Visibility == TemplateVisibility.Public)
                .OrderBy(t => t.Visibility).ThenBy(t => t.Name).ToListAsync();
            return list.Select(t => t.ToDto());
        });

        g.MapGet("/templates/{id:guid}", async (Guid id, HttpContext http, AppDbContext db) =>
        {
            var t = await db.Templates.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("Modèle introuvable.");
            return t.IsVisibleTo(http.User.TenantId()) ? Results.Ok(t.ToDto()) : throw new NotFoundException("Modèle introuvable.");
        });

        g.MapPost("/templates", async (SaveTemplateRequest req, HttpContext http, AppDbContext db) =>
        {
            // Un demandeur ne crée que des modèles privés ; les modèles publics sont gérés par les admins.
            var t = await TemplateStore.CreateAsync(db, req with { Visibility = TemplateVisibility.Private }, http.User.TenantId());
            return Results.Created($"/api/requester/templates/{t.Id}", t.ToDto());
        });

        g.MapPut("/templates/{id:guid}", async (Guid id, SaveTemplateRequest req, HttpContext http, AppDbContext db) =>
        {
            var tenantId = http.User.TenantId();
            var t = await db.Templates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId)
                    ?? throw new NotFoundException("Modèle introuvable (les modèles publics se copient avant modification).");
            await TemplateStore.UpdateAsync(db, t, req with { Visibility = TemplateVisibility.Private });
            return t.ToDto();
        });

        g.MapPost("/templates/{id:guid}/copy", async (Guid id, HttpContext http, AppDbContext db) =>
        {
            var tenantId = http.User.TenantId();
            var source = await db.Templates.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("Modèle introuvable.");
            if (!source.IsVisibleTo(tenantId)) throw new NotFoundException("Modèle introuvable.");
            var dto = source.ToDto();
            var copy = await TemplateStore.CreateAsync(db, new SaveTemplateRequest($"{dto.Name} (copie)", dto.Description,
                TemplateVisibility.Private, dto.DefaultDeliveryMode, dto.Items), tenantId);
            return Results.Created($"/api/requester/templates/{copy.Id}", copy.ToDto());
        });

        g.MapDelete("/templates/{id:guid}", async (Guid id, HttpContext http, AppDbContext db) =>
        {
            var tenantId = http.User.TenantId();
            var t = await db.Templates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId)
                    ?? throw new NotFoundException("Modèle introuvable.");
            db.Templates.Remove(t);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---------- Dossiers ----------

        g.MapGet("/dossiers", async (DossierStatus? status, string? q, HttpContext http, AppDbContext db) =>
        {
            var tenantId = http.User.TenantId();
            var query = db.Dossiers.WithDetails().Where(d => d.TenantId == tenantId);
            if (status is not null) query = query.Where(d => d.Status == status);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                query = query.Where(d => d.Title.ToLower().Contains(term) || (d.Reference != null && d.Reference.ToLower().Contains(term))
                                         || d.ProviderEmail.Contains(term));
            }
            var list = await query.OrderByDescending(d => d.CreatedAt).Take(500).ToListAsync();
            return list.Select(d => d.ToSummary());
        });

        g.MapPost("/dossiers", async (CreateDossierRequest req, HttpContext http, DossierService svc, AppDbContext db, CancellationToken ct) =>
        {
            var d = await svc.CreateAsync(http.User.TenantId(), http.User.UserId(), req, ct);
            var name = await db.Tenants.Where(t => t.Id == d.TenantId).Select(t => t.Name).FirstAsync(ct);
            return Results.Created($"/api/requester/dossiers/{d.Id}", d.ToDto(name));
        });

        g.MapGet("/dossiers/{id:guid}", async (Guid id, HttpContext http, DossierService svc, AppDbContext db) =>
        {
            var d = await svc.GetForTenantAsync(http.User.TenantId(), id);
            var name = await db.Tenants.Where(t => t.Id == d.TenantId).Select(t => t.Name).FirstAsync();
            return d.ToDto(name);
        });

        g.MapPost("/dossiers/{id:guid}/items/{itemId:guid}/accept", async (Guid id, Guid itemId, HttpContext http, DossierService svc) =>
        {
            var d = await svc.GetForTenantAsync(http.User.TenantId(), id);
            await svc.AcceptAsync(d, itemId, http.User.UserId());
            return Results.NoContent();
        });

        g.MapPost("/dossiers/{id:guid}/items/{itemId:guid}/reject", async (Guid id, Guid itemId, RejectItemRequest req, HttpContext http, DossierService svc) =>
        {
            var d = await svc.GetForTenantAsync(http.User.TenantId(), id);
            await svc.RejectAsync(d, itemId, req.Reason, http.User.UserId());
            return Results.NoContent();
        });

        g.MapPost("/dossiers/{id:guid}/deliver", async (Guid id, HttpContext http, DossierService svc) =>
        {
            var d = await svc.GetForTenantAsync(http.User.TenantId(), id);
            return Results.Ok(new { scheduled = await svc.DeliverNowAsync(d) });
        });

        g.MapPost("/dossiers/{id:guid}/remind", async (Guid id, HttpContext http, DossierService svc) =>
        {
            var d = await svc.GetForTenantAsync(http.User.TenantId(), id);
            await svc.RemindNowAsync(d);
            return Results.NoContent();
        });

        g.MapPost("/dossiers/{id:guid}/cancel", async (Guid id, HttpContext http, DossierService svc, AppDbContext db) =>
        {
            var d = await svc.GetForTenantAsync(http.User.TenantId(), id);
            d.Cancel();
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/dossiers/{id:guid}/close", async (Guid id, HttpContext http, DossierService svc, AppDbContext db) =>
        {
            var d = await svc.GetForTenantAsync(http.User.TenantId(), id);
            d.Close();
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/dossiers/{id:guid}/documents/{docId:guid}/content", async (Guid id, Guid docId, HttpContext http, DossierService svc, CancellationToken ct) =>
            await svc.DownloadAsync(await svc.GetForTenantAsync(http.User.TenantId(), id, ct), docId, http.User.UserId(), ct));

        g.MapGet("/dossiers/{id:guid}/zip", async (Guid id, HttpContext http, DossierService svc, CancellationToken ct) =>
            await svc.DownloadZipAsync(await svc.GetForTenantAsync(http.User.TenantId(), id, ct), http.User.UserId(), ct));

        g.MapGet("/dossiers/{id:guid}/deliveries", async (Guid id, HttpContext http, DossierService svc, AppDbContext db) =>
        {
            var d = await svc.GetForTenantAsync(http.User.TenantId(), id);
            return await db.Deliveries.Where(r => r.DossierId == d.Id).OrderByDescending(r => r.CreatedAt)
                .Select(r => new { r.Id, r.ChannelId, r.Status, r.Attempts, r.LastError, r.CreatedAt, r.CompletedAt, Documents = r.DocumentIds.Count })
                .ToListAsync();
        });

        // ---------- Canaux de livraison ----------

        g.MapGet("/channels", async (HttpContext http, AppDbContext db) =>
        {
            var tenantId = http.User.TenantId();
            var list = await db.DeliveryChannels.Where(c => c.TenantId == tenantId).OrderBy(c => c.Name).ToListAsync();
            return list.Select(c => new DeliveryChannelDto(c.Id, c.Type, c.Name, ChannelSettings.Masked(c), c.Enabled));
        });

        g.MapPost("/channels", async (SaveDeliveryChannelRequest req, HttpContext http, AppDbContext db, ISecretProtector protector) =>
        {
            var c = new DeliveryChannel { TenantId = http.User.TenantId(), Type = req.Type, Name = req.Name.Trim(), Enabled = req.Enabled };
            ChannelValidation.Validate(req);
            c.SettingsJson = ChannelSettings.Prepare(req.Settings, null, protector);
            db.DeliveryChannels.Add(c);
            await db.SaveChangesAsync();
            return Results.Created($"/api/requester/channels/{c.Id}", new DeliveryChannelDto(c.Id, c.Type, c.Name, ChannelSettings.Masked(c), c.Enabled));
        });

        g.MapPut("/channels/{id:guid}", async (Guid id, SaveDeliveryChannelRequest req, HttpContext http, AppDbContext db, ISecretProtector protector) =>
        {
            var c = await db.DeliveryChannels.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == http.User.TenantId())
                    ?? throw new NotFoundException("Canal introuvable.");
            ChannelValidation.Validate(req);
            c.SettingsJson = ChannelSettings.Prepare(req.Settings, c, protector);
            c.Name = req.Name.Trim();
            c.Type = req.Type;
            c.Enabled = req.Enabled;
            await db.SaveChangesAsync();
            return new DeliveryChannelDto(c.Id, c.Type, c.Name, ChannelSettings.Masked(c), c.Enabled);
        });

        g.MapDelete("/channels/{id:guid}", async (Guid id, HttpContext http, AppDbContext db) =>
        {
            var c = await db.DeliveryChannels.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == http.User.TenantId())
                    ?? throw new NotFoundException("Canal introuvable.");
            db.DeliveryChannels.Remove(c);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---------- Équipe ----------

        g.MapGet("/users", async (HttpContext http, AppDbContext db, UserManager<AppUser> users) =>
        {
            var tenantId = http.User.TenantId();
            var list = await db.Users.Where(u => u.TenantId == tenantId).OrderBy(u => u.Email).ToListAsync();
            var result = new List<UserDto>();
            foreach (var u in list)
                result.Add(new UserDto(u.Id, u.Email!, u.DisplayName, (await users.GetRolesAsync(u)).ToList(), u.TenantId,
                    u.TwoFactorEnabled, u.LockoutEnd > DateTimeOffset.UtcNow));
            return result;
        });

        g.MapPost("/users/invite", async (InviteUserRequest req, HttpContext http, AppDbContext db, InvitationService invitations, UserManager<AppUser> users) =>
        {
            var tenant = await db.Tenants.FirstAsync(t => t.Id == http.User.TenantId());
            var address = DossierService.NormalizeEmail(req.Email);
            if (await users.FindByEmailAsync(address) is not null)
                throw new DomainException(ErrorCodes.Validation, "Un compte existe déjà pour cette adresse.");
            await invitations.InviteTeamMemberAsync(address, Roles.Requester, tenant);
            return Results.NoContent();
        });
    }
}

internal static class TenantSettings
{
    public static async Task ApplyAsync(Tenant tenant, UpdateTenantSettingsRequest req, AppDbContext db, AiClassifierRegistry ai)
    {
        if (req.StorageProfileId is { } pid &&
            !await db.StorageProfiles.AnyAsync(p => p.Id == pid && (p.TenantId == tenant.Id || p.TenantId == null)))
            throw new DomainException(ErrorCodes.Validation, "Profil de stockage inconnu.");
        if (req.AiVerificationEnabled && ai.Find(req.AiProviderKey) is null)
            throw new DomainException(ErrorCodes.Validation, "Choisissez un fournisseur d'IA disponible.");

        var origins = new List<string>();
        foreach (var o in req.AllowedEmbedOrigins.Where(o => !string.IsNullOrWhiteSpace(o)))
        {
            if (!Uri.TryCreate(o.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/")
                throw new DomainException(ErrorCodes.Validation, $"Origine invalide : {o} (attendu : https://www.exemple.fr).");
            origins.Add(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant());
        }

        tenant.StorageProfileId = req.StorageProfileId;
        tenant.EncryptAtRest = req.EncryptAtRest;
        tenant.AiVerificationEnabled = req.AiVerificationEnabled;
        tenant.AiProviderKey = req.AiProviderKey;
        tenant.AllowedEmbedOrigins = origins.Distinct().ToList();
    }
}

internal static class ChannelValidation
{
    public static void Validate(SaveDeliveryChannelRequest req)
    {
        string? Get(string k) => req.Settings.TryGetValue(k, out var v) ? v : null;
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new DomainException(ErrorCodes.Validation, "Nom du canal obligatoire.");
        switch (req.Type)
        {
            case DeliveryChannelType.EmailNotification when string.IsNullOrWhiteSpace(Get("To")):
                throw new DomainException(ErrorCodes.Validation, "Indiquez au moins une adresse de destination.");
            case DeliveryChannelType.Webhook:
                if (!Uri.TryCreate(Get("Url"), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                    throw new DomainException(ErrorCodes.Validation, "L'URL du webhook doit être en https.");
                if (string.IsNullOrWhiteSpace(Get("Secret")))
                    throw new DomainException(ErrorCodes.Validation, "Le secret de signature est obligatoire.");
                break;
            case DeliveryChannelType.StorageCopy when !Guid.TryParse(Get("StorageProfileId"), out _):
                throw new DomainException(ErrorCodes.Validation, "Choisissez le stockage de destination.");
        }
    }
}

internal static class TemplateStore
{
    public static async Task<ChecklistTemplate> CreateAsync(AppDbContext db, SaveTemplateRequest req, Guid? tenantId)
    {
        Mapping.ValidateTemplate(req);
        var t = new ChecklistTemplate
        {
            TenantId = req.Visibility == TemplateVisibility.Public ? null : tenantId,
            Name = req.Name.Trim(),
            Description = req.Description,
            Visibility = req.Visibility,
            DefaultDeliveryMode = req.DefaultDeliveryMode
        };
        t.Items = req.Items.Select((i, n) => { var e = i.ToEntity(n); e.TemplateId = t.Id; return e; }).ToList();
        db.Templates.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    public static async Task UpdateAsync(AppDbContext db, ChecklistTemplate t, SaveTemplateRequest req)
    {
        Mapping.ValidateTemplate(req);
        t.Name = req.Name.Trim();
        t.Description = req.Description;
        t.Visibility = req.Visibility;
        t.DefaultDeliveryMode = req.DefaultDeliveryMode;
        t.UpdatedAt = DateTimeOffset.UtcNow;
        db.TemplateItems.RemoveRange(t.Items);
        t.Items = req.Items.Select((i, n) => { var e = i.ToEntity(n); e.TemplateId = t.Id; return e; }).ToList();
        db.TemplateItems.AddRange(t.Items);
        await db.SaveChangesAsync();
    }
}

internal static class AiUsageReport
{
    public static async Task<List<AiUsageDto>> ForTenantsAsync(AppDbContext db, Guid? tenantId, int year)
    {
        var from = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddYears(1);
        var rows = await db.AiUsage.Where(u => u.At >= from && u.At < to && (tenantId == null || u.TenantId == tenantId))
            .Select(u => new { u.TenantId, u.At, u.InputTokens, u.OutputTokens }).ToListAsync();
        return rows.GroupBy(r => new { r.TenantId, r.At.Month })
            .Select(g => new AiUsageDto(g.Key.TenantId, year, g.Key.Month, g.Count(), g.Sum(x => x.InputTokens), g.Sum(x => x.OutputTokens)))
            .OrderBy(x => x.TenantId).ThenBy(x => x.Month).ToList();
    }
}
