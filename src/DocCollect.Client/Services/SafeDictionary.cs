namespace DocCollect.Client.Services;

/// <summary>Dictionnaire de paramètres dont l'indexeur renvoie null pour une clé absente (liaisons de formulaires dynamiques).</summary>
public sealed class SafeDictionary : Dictionary<string, string?>
{
    public SafeDictionary() : base(StringComparer.OrdinalIgnoreCase) { }

    public SafeDictionary(IEnumerable<KeyValuePair<string, string?>> values) : base(StringComparer.OrdinalIgnoreCase)
    {
        foreach (var (k, v) in values) base[k] = v;
    }

    public new string? this[string key]
    {
        get => TryGetValue(key, out var v) ? v : null;
        set => base[key] = value;
    }
}
