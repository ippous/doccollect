using System.Text;
using DocCollect.Core.Storage;
using DocCollect.Shared;
using Renci.SshNet;
using SshConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace DocCollect.Infrastructure.Storage;

/// <summary>
/// Stockage SFTP (SSH.NET). Recommandé pour un NAS Synology joint via un VPN (Tailscale/WireGuard)
/// ou un port SSH exposé. L'empreinte de la clé d'hôte doit être épinglée.
/// </summary>
public sealed class SftpStorage(StorageSettings settings) : IDocumentStorage
{
    public string ProviderType => SftpStorageProvider.TypeName;

    private readonly string? _root = settings.Get("RootPath");

    private SftpClient Connect()
    {
        var host = settings.Require("Host");
        var port = settings.GetInt("Port", 22);
        var user = settings.Require("Username");
        var methods = new List<AuthenticationMethod>();
        if (settings.Get("PrivateKey") is { } pem)
        {
            var key = new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(pem)), settings.Get("PrivateKeyPassphrase"));
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }
        if (settings.Get("Password") is { } password)
            methods.Add(new PasswordAuthenticationMethod(user, password));
        if (methods.Count == 0)
            throw new StorageException("SFTP : renseigner un mot de passe ou une clé privée.");

        var connection = new SshConnectionInfo(host, port, user, [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(settings.GetInt("TimeoutSeconds", 30))
        };
        var client = new SftpClient(connection);

        var expected = settings.Get("HostKeyFingerprint")?.Trim();
        var acceptAny = settings.GetBool("AcceptAnyHostKey", false);
        client.HostKeyReceived += (_, e) =>
        {
            if (acceptAny) { e.CanTrust = true; return; }
            // Format attendu : SHA256 en base64, avec ou sans le préfixe « SHA256: » (comme ssh-keygen -lf).
            var normalized = expected?.Replace("SHA256:", "", StringComparison.OrdinalIgnoreCase).TrimEnd('=');
            e.CanTrust = normalized is not null && string.Equals(e.FingerPrintSHA256.TrimEnd('='), normalized, StringComparison.Ordinal);
        };

        client.Connect();
        return client;
    }

    private string Remote(string key) => "/" + StorageKeys.Combine(_root, key);

    private static void EnsureDirectory(SftpClient client, string remoteFilePath)
    {
        var dir = remoteFilePath[..remoteFilePath.LastIndexOf('/')];
        var current = "";
        foreach (var part in dir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            if (!client.Exists(current)) client.CreateDirectory(current);
        }
    }

    public Task WriteAsync(string key, Stream content, CancellationToken ct = default) => Task.Run(() =>
    {
        using var client = Connect();
        var path = Remote(key);
        EnsureDirectory(client, path);
        // Écriture dans un fichier temporaire puis renommage : pas de fichier partiel visible en cas de coupure.
        var temp = path + ".part";
        client.UploadFile(content, temp, canOverride: true);
        if (client.Exists(path)) client.DeleteFile(path);
        client.RenameFile(temp, path);
    }, ct);

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => Task.Run<Stream>(() =>
    {
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), "doccollect-sftp-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        try
        {
            using var client = Connect();
            var path = Remote(key);
            if (!client.Exists(path)) throw new StorageException($"SFTP : objet introuvable ({key}).");
            client.DownloadFile(path, temp);
            temp.Position = 0;
            return temp;
        }
        catch
        {
            temp.Dispose();
            throw;
        }
    }, ct);

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.Run(() =>
    {
        using var client = Connect();
        return client.Exists(Remote(key));
    }, ct);

    public Task DeleteAsync(string key, CancellationToken ct = default) => Task.Run(() =>
    {
        using var client = Connect();
        var path = Remote(key);
        if (client.Exists(path)) client.DeleteFile(path);
    }, ct);

    public Task<StorageTestResult> TestAsync(CancellationToken ct = default) => StorageTester.RunAsync(this, ct);
}

public sealed class SftpStorageProvider : IStorageProvider
{
    public const string TypeName = "sftp";
    public string Type => TypeName;
    public string DisplayName => "SFTP (SSH)";
    public IReadOnlyList<StorageSettingField> Fields { get; } =
    [
        new("Host", "Serveur", true, false, Help: "Ex. l'IP Tailscale du NAS (100.x.y.z)."),
        new("Port", "Port", false, false, "22"),
        new("Username", "Utilisateur", true, false),
        new("Password", "Mot de passe", false, true),
        new("PrivateKey", "Clé privée (PEM / OpenSSH)", false, true, Help: "Préférable au mot de passe."),
        new("PrivateKeyPassphrase", "Phrase de passe de la clé", false, true),
        new("HostKeyFingerprint", "Empreinte de la clé d'hôte", true, false,
            Help: "SHA256 en base64, ex. « SHA256:abc… » (ssh-keyscan -t ed25519 hôte | ssh-keygen -lf -)."),
        new("AcceptAnyHostKey", "Accepter toute clé d'hôte", false, false, "false", "Déconseillé hors développement."),
        new("RootPath", "Dossier racine", false, false, "/doccollect"),
        new("TimeoutSeconds", "Délai (s)", false, false, "30")
    ];

    public IDocumentStorage Create(StorageSettings settings) => new SftpStorage(settings);
}
