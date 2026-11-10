using DocCollect.Domain;

namespace DocCollect.Core.Storage;

public sealed record ResolvedStorage(Guid ProfileId, IDocumentStorage Storage);

/// <summary>
/// Trouve le stockage à utiliser : profil du demandeur s'il en a un, sinon profil plateforme par défaut.
/// Implémenté dans l'infrastructure (lecture des profils en base, déchiffrement des secrets).
/// </summary>
public interface IStorageResolver
{
    Task<ResolvedStorage> ResolveForTenantAsync(Tenant tenant, CancellationToken ct = default);
    Task<IDocumentStorage> ResolveAsync(Guid storageProfileId, CancellationToken ct = default);
}
