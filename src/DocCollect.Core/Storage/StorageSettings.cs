using System.Globalization;
using System.Text.Json;

namespace DocCollect.Core.Storage;

/// <summary>Paramètres d'un profil de stockage (clé/valeur), avec accès typé.</summary>
public sealed class StorageSettings
{
    private readonly Dictionary<string, string?> _values;

    public StorageSettings(IDictionary<string, string?>? values = null) =>
        _values = new Dictionary<string, string?>(values ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string?> Values => _values;

    public string? Get(string name) => _values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public string Require(string name) =>
        Get(name) ?? throw new StorageException($"Paramètre de stockage manquant : {name}.");

    public int GetInt(string name, int defaultValue) =>
        int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;

    public bool GetBool(string name, bool defaultValue) =>
        bool.TryParse(Get(name), out var v) ? v : defaultValue;

    public string Serialize() => JsonSerializer.Serialize(_values);

    public static StorageSettings Deserialize(string? json) =>
        new(string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, string?>>(json));
}

/// <summary>Construction et normalisation des clés de stockage.</summary>
public static class StorageKeys
{
    /// <summary>Chemin d'un document : {tenant}/{année}/{dossier}/{document}.bin (le nom d'origine n'apparaît jamais sur le stockage).</summary>
    public static string ForDocument(string tenantSlug, Guid dossierId, Guid documentId, DateTimeOffset uploadedAt) =>
        $"{Sanitize(tenantSlug)}/{uploadedAt:yyyy}/{dossierId:N}/{documentId:N}.bin";

    /// <summary>Normalise une clé et refuse toute remontée de répertoire.</summary>
    public static string Normalize(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new StorageException("Clé de stockage vide.");
        var parts = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".."))
            throw new StorageException("Clé de stockage invalide.");
        return string.Join('/', parts);
    }

    public static string Combine(string? root, string key)
    {
        var k = Normalize(key);
        var r = (root ?? "").Replace('\\', '/').Trim('/');
        return r.Length == 0 ? k : $"{r}/{k}";
    }

    private static string Sanitize(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray();
        var s = new string(chars).Trim('-');
        return s.Length == 0 ? "tenant" : s;
    }
}
