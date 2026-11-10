using DocCollect.Shared;

namespace DocCollect.Core.Ai;

/// <summary>
/// Construit les classifieurs à partir de la configuration « Ai:Providers ».
/// Chaque demandeur choisit sa clé de fournisseur (contraintes de localisation, coût…).
/// </summary>
public sealed class AiClassifierRegistry
{
    private readonly Dictionary<string, IDocumentClassifier> _classifiers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _pdf = new(StringComparer.OrdinalIgnoreCase);

    public AiClassifierRegistry(AiOptions options, Func<string, HttpClient> httpFactory, IEnumerable<IDocumentClassifier>? extra = null)
    {
        foreach (var (key, p) in options.Providers.Where(kv => kv.Value.Enabled))
        {
            IDocumentClassifier classifier = p.Kind.ToLowerInvariant() switch
            {
                "anthropic" => new AnthropicClassifier(key, p, httpFactory(key)),
                "openai-compatible" or "openai" or "azure-openai" or "mistral" or "ollama" =>
                    new OpenAiCompatibleClassifier(key, p, httpFactory(key)),
                _ => throw new InvalidOperationException($"Ai:Providers:{key}:Kind inconnu : {p.Kind}.")
            };
            _classifiers[key] = classifier;
            _pdf[key] = p.Kind.Equals("anthropic", StringComparison.OrdinalIgnoreCase) || p.SupportsPdf;
        }
        foreach (var c in extra ?? [])
        {
            _classifiers[c.Key] = c;
            _pdf[c.Key] = c.SupportsContentType("application/pdf");
        }
    }

    public IDocumentClassifier? Find(string? key) =>
        key is not null && _classifiers.TryGetValue(key, out var c) ? c : null;

    public IReadOnlyList<AiProviderInfo> Describe() =>
        _classifiers.Values.OrderBy(c => c.DisplayName)
            .Select(c => new AiProviderInfo(c.Key, c.DisplayName, c.Region, _pdf[c.Key])).ToList();
}
