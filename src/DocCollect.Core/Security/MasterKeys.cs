using System.Security.Cryptography;

namespace DocCollect.Core.Security;

/// <summary>
/// Fournit les clés maîtres (KEK) qui scellent les clés de chaque document.
/// Plusieurs clés peuvent coexister pour permettre la rotation : les nouvelles écritures
/// utilisent <see cref="CurrentKeyId"/>, les anciennes restent lisibles avec leur identifiant.
/// </summary>
public interface IMasterKeyProvider
{
    string CurrentKeyId { get; }
    byte[] GetKey(string keyId);
}

public sealed class EncryptionOptions
{
    public const string Section = "Encryption";
    /// <summary>Identifiant de la clé utilisée pour les nouvelles écritures.</summary>
    public string CurrentKeyId { get; set; } = "";
    /// <summary>Clés maîtres en base64 (32 octets). À fournir par variables d'environnement / secrets, jamais dans le dépôt.</summary>
    public Dictionary<string, string> Keys { get; set; } = [];
    /// <summary>Taille des blocs chiffrés (octets).</summary>
    public int ChunkSize { get; set; } = 64 * 1024;
}

public sealed class ConfiguredMasterKeyProvider : IMasterKeyProvider
{
    private readonly Dictionary<string, byte[]> _keys;

    public ConfiguredMasterKeyProvider(EncryptionOptions options)
    {
        _keys = new(StringComparer.Ordinal);
        foreach (var (id, b64) in options.Keys)
        {
            byte[] key;
            try { key = Convert.FromBase64String(b64); }
            catch (FormatException) { throw new InvalidOperationException($"Clé maître « {id} » : base64 invalide."); }
            if (key.Length != 32) throw new InvalidOperationException($"Clé maître « {id} » : 32 octets attendus.");
            _keys[id] = key;
        }
        if (string.IsNullOrWhiteSpace(options.CurrentKeyId) || !_keys.ContainsKey(options.CurrentKeyId))
            throw new InvalidOperationException("Encryption:CurrentKeyId doit désigner une clé présente dans Encryption:Keys.");
        CurrentKeyId = options.CurrentKeyId;
    }

    public string CurrentKeyId { get; }

    public byte[] GetKey(string keyId) =>
        _keys.TryGetValue(keyId, out var k) ? k : throw new CryptographicException($"Clé maître inconnue : {keyId}.");

    /// <summary>Génère une nouvelle clé maître en base64 (outil d'exploitation).</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
