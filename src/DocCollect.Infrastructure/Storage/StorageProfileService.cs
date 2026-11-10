using DocCollect.Core.Security;
using DocCollect.Core.Storage;
using DocCollect.Domain;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocCollect.Infrastructure.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>
    /// Stockage de secours si aucun profil plateforme n'est défini en base (ex. au premier démarrage).
    /// Par défaut : disque local dans /data/storage.
    /// </summary>
    public string FallbackLocalPath { get; set; } = "/data/storage";
}

/// <summary>
/// Gestion des profils de stockage : chiffrement des champs secrets à l'enregistrement,
/// masquage à la lecture, et résolution du stockage d'un demandeur.
/// </summary>
public sealed class StorageProfileService(
    AppDbContext db,
    StorageProviderRegistry registry,
    ISecretProtector protector,
    IOptions<StorageOptions> options) : IStorageResolver
{
    public const string Mask = "••••••";
    /// <summary>Identifiant fixe du stockage de secours (documents écrits avant toute configuration).</summary>
    public static readonly Guid FallbackProfileId = Guid.Parse("00000000-0000-0000-0000-00000000f00d");

    public async Task<ResolvedStorage> ResolveForTenantAsync(Tenant tenant, CancellationToken ct = default)
    {
        var profile = tenant.StorageProfileId is { } id
            ? await db.StorageProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            : null;
        profile ??= await db.StorageProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == null && p.IsDefault, ct);
        return profile is null
            ? new ResolvedStorage(FallbackProfileId, Fallback())
            : new ResolvedStorage(profile.Id, Build(profile));
    }

    public async Task<IDocumentStorage> ResolveAsync(Guid storageProfileId, CancellationToken ct = default)
    {
        if (storageProfileId == FallbackProfileId) return Fallback();
        var profile = await db.StorageProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == storageProfileId, ct)
                      ?? throw new StorageException("Profil de stockage introuvable.");
        return Build(profile);
    }

    private IDocumentStorage Fallback() => new LocalFileSystemStorage(options.Value.FallbackLocalPath);

    public IDocumentStorage Build(StorageProfile profile) =>
        registry.Create(profile.ProviderType, Unprotect(profile.ProviderType, StorageSettings.Deserialize(profile.SettingsJson)));

    private StorageSettings Unprotect(string type, StorageSettings settings)
    {
        var secrets = registry.Get(type).Fields.Where(f => f.Secret).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new StorageSettings(settings.Values.ToDictionary(kv => kv.Key,
            kv => kv.Value is not null && secrets.Contains(kv.Key) ? protector.Unprotect(kv.Value) : kv.Value));
    }

    /// <summary>
    /// Prépare les paramètres à enregistrer : les secrets sont chiffrés ; une valeur masquée
    /// renvoyée par l'interface conserve le secret déjà enregistré.
    /// </summary>
    public string PrepareSettings(string type, IDictionary<string, string?> incoming, StorageProfile? existing)
    {
        var provider = registry.Get(type);
        var previous = existing is null ? new StorageSettings() : StorageSettings.Deserialize(existing.SettingsJson);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in provider.Fields)
        {
            incoming.TryGetValue(field.Name, out var value);
            if (field.Secret)
            {
                if (value == Mask) value = previous.Get(field.Name);
                else if (!string.IsNullOrEmpty(value)) value = protector.Protect(value);
            }
            if (!string.IsNullOrWhiteSpace(value)) result[field.Name] = value;
        }
        return new StorageSettings(result).Serialize();
    }

    public StorageProfileDto ToDto(StorageProfile p)
    {
        var secrets = registry.Get(p.ProviderType).Fields.Where(f => f.Secret).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = StorageSettings.Deserialize(p.SettingsJson).Values
            .ToDictionary(kv => kv.Key, kv => secrets.Contains(kv.Key) && !string.IsNullOrEmpty(kv.Value) ? Mask : kv.Value);
        return new StorageProfileDto(p.Id, p.TenantId, p.Name, p.ProviderType, settings, p.IsDefault);
    }

    // ---------- Suppression d'un profil vide ----------

    /// <summary>
    /// Raisons empêchant de supprimer le profil (vide = supprimable) : documents stockés, demandeur qui l'utilise
    /// comme stockage, canal « copie vers mon stockage » qui le cible, ou profil par défaut de la plateforme.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetDeleteBlockersAsync(StorageProfile profile, CancellationToken ct = default)
    {
        var reasons = new List<string>();
        var documents = await db.Documents.CountAsync(d => d.StorageProfileId == profile.Id, ct);
        if (documents > 0)
            reasons.Add($"il contient {documents} document(s)");

        var tenants = await db.Tenants.Where(t => t.StorageProfileId == profile.Id).Select(t => t.Name).ToListAsync(ct);
        if (tenants.Count > 0)
            reasons.Add($"il est le stockage de : {string.Join(", ", tenants)}");

        var channels = await db.DeliveryChannels
            .Where(c => c.Type == DeliveryChannelType.StorageCopy && (profile.TenantId == null || c.TenantId == profile.TenantId))
            .ToListAsync(ct);
        var targeting = channels
            .Where(c => string.Equals(StorageSettings.Deserialize(c.SettingsJson).Get("StorageProfileId"), profile.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(c => $"« {c.Name} »").ToList();
        if (targeting.Count > 0)
            reasons.Add($"il est la destination du canal {string.Join(", ", targeting)}");

        if (profile.TenantId is null && profile.IsDefault)
            reasons.Add("c'est le stockage par défaut de la plateforme (désignez-en un autre d'abord)");
        return reasons;
    }

    public async Task DeleteAsync(StorageProfile profile, CancellationToken ct = default)
    {
        var reasons = await GetDeleteBlockersAsync(profile, ct);
        if (reasons.Count > 0)
            throw new DomainException(ErrorCodes.InvalidState,
                $"Le stockage « {profile.Name} » ne peut pas être supprimé : {string.Join(" ; ", reasons)}.");
        db.StorageProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>DTO enrichis (nombre de documents, motif empêchant la suppression) pour l'écran de gestion.</summary>
    public async Task<List<StorageProfileDto>> ToDtosAsync(IReadOnlyList<StorageProfile> profiles, CancellationToken ct = default)
    {
        var ids = profiles.Select(p => p.Id).ToList();
        var counts = (await db.Documents.Where(d => ids.Contains(d.StorageProfileId))
                .GroupBy(d => d.StorageProfileId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);
        var result = new List<StorageProfileDto>();
        foreach (var p in profiles)
        {
            var reasons = await GetDeleteBlockersAsync(p, ct);
            result.Add(ToDto(p) with
            {
                DocumentCount = counts.GetValueOrDefault(p.Id),
                DeleteBlockedReason = reasons.Count == 0 ? null : string.Join(" ; ", reasons)
            });
        }
        return result;
    }

    public async Task<StorageTestResult> TestAsync(StorageProfile profile, CancellationToken ct)
    {
        try { return await Build(profile).TestAsync(ct); }
        catch (Exception ex) { return new StorageTestResult(false, ex.Message); }
    }
}
