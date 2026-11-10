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

    public async Task<StorageTestResult> TestAsync(StorageProfile profile, CancellationToken ct)
    {
        try { return await Build(profile).TestAsync(ct); }
        catch (Exception ex) { return new StorageTestResult(false, ex.Message); }
    }
}
