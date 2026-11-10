using System.IO.Compression;
using DocCollect.Core.Documents;
using DocCollect.Core.Notifications;
using DocCollect.Core.Security;
using DocCollect.Domain;
using DocCollect.Infrastructure.Delivery;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocCollect.Api.Services;

public sealed class NotFoundException(string message) : Exception(message);

/// <summary>Création d'invitations (fournisseurs, membres d'équipe, responsables de demandeur).</summary>
public sealed class InvitationService(AppDbContext db, IEmailSender email, IOptions<AppOptions> app, TimeProvider time)
{
    public async Task<string> CreateAsync(string address, string role, Guid? tenantId, Guid? dossierId, CancellationToken ct = default)
    {
        var token = Hashing.NewToken();
        db.Invitations.Add(new Invitation
        {
            Email = address,
            Role = role,
            TenantId = tenantId,
            DossierId = dossierId,
            TokenHash = Hashing.Sha256Hex(token),
            ExpiresAt = time.GetUtcNow().AddDays(app.Value.InvitationValidityDays)
        });
        await db.SaveChangesAsync(ct);
        return $"{app.Value.PublicBaseUrl.TrimEnd('/')}/invitation/{token}";
    }

    public async Task InviteTeamMemberAsync(string address, string role, Tenant tenant, CancellationToken ct = default)
    {
        var link = await CreateAsync(address, role, tenant.Id, null, ct);
        await email.SendAsync(EmailTemplates.UserInvitation(address, tenant.Name,
            role == Roles.Admin ? "administrateur" : "demandeur", link), ct);
    }
}

/// <summary>Orchestration des dossiers : création, dépôt, validation, livraison, téléchargement.</summary>
public sealed class DossierService(
    AppDbContext db,
    UserManager<AppUser> users,
    DocumentIngestionService ingestion,
    DeliveryScheduler scheduler,
    InvitationService invitations,
    IEmailSender email,
    IOptions<AppOptions> app,
    TimeProvider time)
{
    private string BaseUrl => app.Value.PublicBaseUrl.TrimEnd('/');

    public static string NormalizeEmail(string address) => address.Trim().ToLowerInvariant();

    // ---------- Accès ----------

    public async Task<Dossier> GetForTenantAsync(Guid tenantId, Guid dossierId, CancellationToken ct = default) =>
        await db.Dossiers.WithDetails().FirstOrDefaultAsync(d => d.Id == dossierId && d.TenantId == tenantId, ct)
        ?? throw new NotFoundException("Dossier introuvable.");

    public async Task<Dossier> GetForProviderAsync(string userId, string userEmail, Guid dossierId, CancellationToken ct = default)
    {
        var mail = NormalizeEmail(userEmail);
        return await db.Dossiers.WithDetails().FirstOrDefaultAsync(d => d.Id == dossierId
                   && d.Status != DossierStatus.Draft
                   && (d.ProviderUserId == userId || (d.ProviderUserId == null && d.ProviderEmail == mail)), ct)
               ?? throw new NotFoundException("Dossier introuvable.");
    }

    // ---------- Création ----------

    public async Task<Dossier> CreateAsync(Guid tenantId, string userId, CreateDossierRequest req, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
        if (tenant.Disabled) throw new DomainException(ErrorCodes.InvalidState, "Ce compte demandeur est désactivé.");
        if (string.IsNullOrWhiteSpace(req.ProviderEmail) || !req.ProviderEmail.Contains('@'))
            throw new DomainException(ErrorCodes.Validation, "Adresse e-mail du fournisseur invalide.");

        ChecklistTemplate? template = null;
        if (req.TemplateId is { } templateId)
            template = await db.Templates.FirstOrDefaultAsync(t => t.Id == templateId, ct)
                       ?? throw new NotFoundException("Modèle introuvable.");

        var extra = (req.AdditionalItems ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Label))
            .Select((i, n) => i.ToEntityForDossier(n));
        var dossier = Dossier.FromTemplate(tenantId, req.Title, template, extra);
        dossier.Reference = string.IsNullOrWhiteSpace(req.Reference) ? null : req.Reference.Trim();
        dossier.ProviderEmail = NormalizeEmail(req.ProviderEmail);
        dossier.ProviderName = string.IsNullOrWhiteSpace(req.ProviderName) ? null : req.ProviderName.Trim();
        dossier.DeliveryMode = req.DeliveryMode;
        dossier.RequireRequesterValidation = req.RequireRequesterValidation;
        dossier.DueDate = req.DueDate;
        dossier.MessageToProvider = string.IsNullOrWhiteSpace(req.MessageToProvider) ? null : req.MessageToProvider.Trim();
        dossier.CreatedByUserId = userId;
        dossier.CreatedAt = time.GetUtcNow();
        dossier.Open();

        db.Dossiers.Add(dossier);
        db.AuditEvents.Add(new AuditEvent { TenantId = tenantId, DossierId = dossier.Id, UserId = userId, Action = "dossier.created" });

        var provider = await users.FindByEmailAsync(dossier.ProviderEmail);
        if (provider is not null)
        {
            dossier.ProviderUserId = provider.Id;
            if (!await users.IsInRoleAsync(provider, Roles.Provider))
                await users.AddToRoleAsync(provider, Roles.Provider);
            await db.SaveChangesAsync(ct);
            await email.SendAsync(EmailTemplates.NewDossierForExistingProvider(dossier.ProviderEmail, tenant.Name, dossier,
                $"{BaseUrl}/provider/dossiers/{dossier.Id}"), ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
            var link = await invitations.CreateAsync(dossier.ProviderEmail, Roles.Provider, tenantId, dossier.Id, ct);
            await email.SendAsync(EmailTemplates.ProviderInvitation(dossier.ProviderEmail, tenant.Name, dossier, link), ct);
        }
        return dossier;
    }

    // ---------- Dépôt (fournisseur) ----------

    public async Task<StoredDocument> UploadAsync(Dossier dossier, Guid itemId, string fileName, string contentType, Stream content,
        string userId, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == dossier.TenantId, ct);
        var result = await ingestion.IngestAsync(new IngestRequest(tenant, dossier, itemId, fileName, contentType, content, userId), ct);

        if (result.Classification is { } c && result.Document.AiProviderKey is { } key)
            db.AiUsage.Add(new AiUsageRecord
            {
                TenantId = tenant.Id, DocumentId = result.Document.Id, ProviderKey = key,
                InputTokens = c.InputTokens, OutputTokens = c.OutputTokens, At = time.GetUtcNow()
            });
        db.Documents.Add(result.Document);
        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenant.Id, DossierId = dossier.Id, UserId = userId, Action = "document.uploaded",
            Details = $"{result.Document.FileName} ({result.Document.SizeBytes} o, IA : {result.Document.AiVerdict})"
        });
        await db.SaveChangesAsync(ct);
        await scheduler.ScheduleAsync(dossier, ct: ct);
        return result.Document;
    }

    public async Task RemoveDocumentAsync(Dossier dossier, Guid documentId, string userId, CancellationToken ct = default)
    {
        var doc = dossier.Items.SelectMany(i => i.Documents).FirstOrDefault(d => d.Id == documentId)
                  ?? throw new NotFoundException("Document introuvable.");
        dossier.RemoveDocument(documentId, time.GetUtcNow());
        db.Documents.Remove(doc);
        db.AuditEvents.Add(new AuditEvent { TenantId = dossier.TenantId, DossierId = dossier.Id, UserId = userId, Action = "document.removed", Details = doc.FileName });
        await db.SaveChangesAsync(ct);
        await ingestion.DeleteAsync(doc, ct);
    }

    // ---------- Validation (demandeur) ----------

    public async Task AcceptAsync(Dossier dossier, Guid itemId, string userId, CancellationToken ct = default)
    {
        dossier.AcceptItem(itemId, time.GetUtcNow());
        db.AuditEvents.Add(new AuditEvent { TenantId = dossier.TenantId, DossierId = dossier.Id, UserId = userId, Action = "item.accepted", Details = dossier.GetItem(itemId).Label });
        await db.SaveChangesAsync(ct);
        await scheduler.ScheduleAsync(dossier, ct: ct);
    }

    public async Task RejectAsync(Dossier dossier, Guid itemId, string reason, string userId, CancellationToken ct = default)
    {
        var item = dossier.GetItem(itemId);
        var removed = item.Documents.Where(d => d.DeliveredAt is null).ToList();
        dossier.RejectItem(itemId, reason, time.GetUtcNow());
        db.Documents.RemoveRange(removed);
        db.AuditEvents.Add(new AuditEvent { TenantId = dossier.TenantId, DossierId = dossier.Id, UserId = userId, Action = "item.rejected", Details = $"{item.Label} : {reason}" });
        await db.SaveChangesAsync(ct);
        foreach (var doc in removed) await ingestion.DeleteAsync(doc, ct);
        await email.SendAsync(EmailTemplates.ItemRejected(dossier.ProviderEmail, dossier, item.Label, reason,
            $"{BaseUrl}/provider/dossiers/{dossier.Id}"), ct);
    }

    public Task<int> DeliverNowAsync(Dossier dossier, CancellationToken ct = default) => scheduler.ScheduleAsync(dossier, manual: true, ct);

    public async Task RemindNowAsync(Dossier dossier, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == dossier.TenantId, ct);
        await email.SendAsync(EmailTemplates.Reminder(dossier.ProviderEmail, tenant.Name, dossier,
            dossier.MissingRequiredItems().Select(i => i.Label), $"{BaseUrl}/provider/dossiers/{dossier.Id}"), ct);
        dossier.LastReminderAt = time.GetUtcNow();
        dossier.RemindersSent++;
        await db.SaveChangesAsync(ct);
    }

    // ---------- Téléchargement ----------

    /// <summary>Déchiffre dans un fichier temporaire (supprimé à la fermeture) puis le renvoie : l'intégrité est vérifiée avant l'envoi.</summary>
    public async Task<IResult> DownloadAsync(Dossier dossier, Guid documentId, string userId, CancellationToken ct = default)
    {
        var doc = dossier.Items.SelectMany(i => i.Documents).FirstOrDefault(d => d.Id == documentId)
                  ?? throw new NotFoundException("Document introuvable.");
        var temp = TempFile();
        try
        {
            await ingestion.OpenAsync(doc, temp, ct);
            temp.Position = 0;
        }
        catch
        {
            await temp.DisposeAsync();
            throw;
        }
        db.AuditEvents.Add(new AuditEvent { TenantId = dossier.TenantId, DossierId = dossier.Id, UserId = userId, Action = "document.downloaded", Details = doc.FileName });
        await db.SaveChangesAsync(ct);
        return Results.File(temp, doc.ContentType, doc.FileName, enableRangeProcessing: false);
    }

    public async Task<IResult> DownloadZipAsync(Dossier dossier, string userId, CancellationToken ct = default)
    {
        var temp = TempFile();
        try
        {
            using (var zip = new ZipArchive(temp, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var item in dossier.Items.OrderBy(i => i.Order))
                foreach (var doc in item.Documents.OrderBy(d => d.UploadedAt))
                {
                    var folder = $"{item.Order + 1:00} - {Safe(item.Label)}";
                    var entry = zip.CreateEntry($"{folder}/{doc.UploadedAt:yyyyMMdd-HHmmss}_{doc.FileName}", CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await ingestion.OpenAsync(doc, entryStream, ct);
                }
            }
            temp.Position = 0;
        }
        catch
        {
            await temp.DisposeAsync();
            throw;
        }
        db.AuditEvents.Add(new AuditEvent { TenantId = dossier.TenantId, DossierId = dossier.Id, UserId = userId, Action = "dossier.zip-downloaded" });
        await db.SaveChangesAsync(ct);
        var name = $"{Safe(dossier.Reference ?? dossier.Title)}.zip";
        return Results.File(temp, "application/zip", name);
    }

    private static string Safe(string s) =>
        new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim();

    private static FileStream TempFile() =>
        new(Path.Combine(Path.GetTempPath(), "doccollect-dl-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
}

internal static class DossierItemFactory
{
    public static TemplateItem ToEntityForDossier(this TemplateItemDto dto, int order) =>
        Endpoints.Mapping.ToEntity(dto, 1000 + order);
}
