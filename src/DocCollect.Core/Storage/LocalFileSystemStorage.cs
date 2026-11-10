using DocCollect.Shared;

namespace DocCollect.Core.Storage;

/// <summary>Stockage sur disque local (ou un partage NAS monté sur le serveur). Utile en dev et en test.</summary>
public sealed class LocalFileSystemStorage : IDocumentStorage
{
    private readonly string _root;

    public LocalFileSystemStorage(string rootPath)
    {
        // Séparateur final pour que « /data/x » ne valide pas « /data/xyz » dans le contrôle de racine.
        _root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_root);
    }

    public string ProviderType => LocalFileSystemStorageProvider.TypeName;

    private string PathFor(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, StorageKeys.Normalize(key)));
        if (!full.StartsWith(_root, StringComparison.Ordinal))
            throw new StorageException("Clé de stockage hors de la racine.");
        return full;
    }

    public async Task WriteAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await content.CopyToAsync(fs, ct);
        File.Move(temp, path, overwrite: true);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) throw new StorageException($"Objet introuvable : {key}.");
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(PathFor(key)));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<StorageTestResult> TestAsync(CancellationToken ct = default) => StorageTester.RunAsync(this, ct);
}

public sealed class LocalFileSystemStorageProvider : IStorageProvider
{
    public const string TypeName = "local";
    public string Type => TypeName;
    public string DisplayName => "Disque local / partage monté";
    public IReadOnlyList<StorageSettingField> Fields { get; } =
    [
        new("RootPath", "Répertoire racine", Required: true, Secret: false, DefaultValue: null,
            Help: "Chemin absolu sur le serveur (ex. /data/doccollect ou un partage NAS monté).")
    ];

    public IDocumentStorage Create(StorageSettings settings) => new LocalFileSystemStorage(settings.Require("RootPath"));
}

/// <summary>Test générique : écrit, relit, compare puis supprime un fichier témoin.</summary>
public static class StorageTester
{
    public static async Task<StorageTestResult> RunAsync(IDocumentStorage storage, CancellationToken ct)
    {
        var key = $"_doccollect-healthcheck/{Guid.NewGuid():N}.txt";
        var payload = System.Text.Encoding.UTF8.GetBytes($"doccollect {DateTimeOffset.UtcNow:O}");
        try
        {
            await storage.WriteAsync(key, new MemoryStream(payload), ct);
            await using (var read = await storage.OpenReadAsync(key, ct))
            using (var ms = new MemoryStream())
            {
                await read.CopyToAsync(ms, ct);
                if (!ms.ToArray().AsSpan().SequenceEqual(payload))
                    return new StorageTestResult(false, "Le contenu relu ne correspond pas au contenu écrit.");
            }
            await storage.DeleteAsync(key, ct);
            return new StorageTestResult(true, "Écriture, lecture et suppression réussies.");
        }
        catch (Exception ex)
        {
            return new StorageTestResult(false, $"Échec : {ex.Message}");
        }
    }
}
