using DocCollect.Shared;

namespace DocCollect.Core.Storage;

/// <summary>Registre des types de stockage disponibles (alimenté par l'injection de dépendances).</summary>
public sealed class StorageProviderRegistry
{
    private readonly Dictionary<string, IStorageProvider> _providers;

    public StorageProviderRegistry(IEnumerable<IStorageProvider> providers)
    {
        _providers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var p in providers)
        {
            if (!_providers.TryAdd(p.Type, p))
                throw new InvalidOperationException($"Type de stockage enregistré deux fois : {p.Type}.");
        }
    }

    public IReadOnlyCollection<IStorageProvider> Providers => _providers.Values;

    public IReadOnlyList<StorageProviderInfo> Describe() =>
        _providers.Values.OrderBy(p => p.DisplayName)
            .Select(p => new StorageProviderInfo(p.Type, p.DisplayName, p.Fields)).ToList();

    public IStorageProvider Get(string type) =>
        _providers.TryGetValue(type, out var p) ? p : throw new StorageException($"Type de stockage inconnu : {type}.");

    public IDocumentStorage Create(string type, StorageSettings settings)
    {
        var provider = Get(type);
        var missing = provider.Fields.Where(f => f.Required && settings.Get(f.Name) is null && f.DefaultValue is null)
            .Select(f => f.Label).ToList();
        if (missing.Count > 0)
            throw new StorageException($"Paramètres obligatoires manquants : {string.Join(", ", missing)}.");
        return provider.Create(settings);
    }
}
