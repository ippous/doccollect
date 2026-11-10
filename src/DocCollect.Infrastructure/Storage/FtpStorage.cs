using DocCollect.Core.Storage;
using DocCollect.Shared;
using FluentFTP;

namespace DocCollect.Infrastructure.Storage;

/// <summary>
/// Stockage FTP / FTPS (FluentFTP). Compatible avec le serveur FTP d'un NAS Synology.
/// Une connexion par opération : simple et robuste pour des volumes de dépôt modérés.
/// </summary>
public sealed class FtpStorage(StorageSettings settings) : IDocumentStorage
{
    public string ProviderType => FtpStorageProvider.TypeName;

    private readonly string? _root = settings.Get("RootPath");

    private async Task<AsyncFtpClient> ConnectAsync(CancellationToken ct)
    {
        var client = new AsyncFtpClient(
            settings.Require("Host"),
            settings.Require("Username"),
            settings.Get("Password") ?? "",
            settings.GetInt("Port", 21));

        client.Config.EncryptionMode = (settings.Get("Encryption") ?? "Explicit").ToLowerInvariant() switch
        {
            "none" => FtpEncryptionMode.None,
            "implicit" => FtpEncryptionMode.Implicit,
            _ => FtpEncryptionMode.Explicit
        };
        client.Config.ConnectTimeout = settings.GetInt("TimeoutSeconds", 30) * 1000;
        client.Config.DataConnectionType = settings.GetBool("PassiveMode", true) ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive;

        // Certificat auto-signé (fréquent sur un NAS) : on épingle son empreinte plutôt que de tout accepter.
        var pinned = settings.Get("CertificateThumbprint")?.Replace(":", "").Replace(" ", "");
        var acceptAny = settings.GetBool("AcceptAnyCertificate", false);
        client.ValidateCertificate += (_, e) =>
        {
            if (acceptAny) { e.Accept = true; return; }
            if (pinned is not null)
            {
                e.Accept = string.Equals(e.Certificate.GetCertHashString(), pinned, StringComparison.OrdinalIgnoreCase);
                return;
            }
            e.Accept = e.PolicyErrors == System.Net.Security.SslPolicyErrors.None;
        };

        await client.Connect(ct);
        return client;
    }

    private string Remote(string key) => "/" + StorageKeys.Combine(_root, key);

    public async Task WriteAsync(string key, Stream content, CancellationToken ct = default)
    {
        await using var client = await ConnectAsync(ct);
        var status = await client.UploadStream(content, Remote(key), FtpRemoteExists.Overwrite, createRemoteDir: true, token: ct);
        if (status != FtpStatus.Success)
            throw new StorageException($"FTP : échec de l'envoi ({status}).");
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        // Téléchargement dans un fichier temporaire : la connexion est libérée immédiatement.
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), "doccollect-ftp-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await using var client = await ConnectAsync(ct);
            if (!await client.DownloadStream(temp, Remote(key), token: ct))
                throw new StorageException($"FTP : objet introuvable ({key}).");
            temp.Position = 0;
            return temp;
        }
        catch
        {
            await temp.DisposeAsync();
            throw;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        await using var client = await ConnectAsync(ct);
        return await client.FileExists(Remote(key), ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var client = await ConnectAsync(ct);
        if (await client.FileExists(Remote(key), ct))
            await client.DeleteFile(Remote(key), ct);
    }

    public Task<StorageTestResult> TestAsync(CancellationToken ct = default) => StorageTester.RunAsync(this, ct);
}

public sealed class FtpStorageProvider : IStorageProvider
{
    public const string TypeName = "ftp";
    public string Type => TypeName;
    public string DisplayName => "FTP / FTPS";
    public IReadOnlyList<StorageSettingField> Fields { get; } =
    [
        new("Host", "Serveur", true, false, Help: "Nom d'hôte ou IP (ex. nas.mondomaine.fr)."),
        new("Port", "Port", false, false, "21"),
        new("Username", "Utilisateur", true, false),
        new("Password", "Mot de passe", false, true),
        new("RootPath", "Dossier racine", false, false, "/doccollect"),
        new("Encryption", "Chiffrement", false, false, "Explicit", "None, Explicit (FTPES, recommandé) ou Implicit (FTPS port 990)."),
        new("CertificateThumbprint", "Empreinte du certificat", false, false, Help: "SHA-1 du certificat à épingler (NAS en auto-signé)."),
        new("AcceptAnyCertificate", "Accepter tout certificat", false, false, "false", "Déconseillé hors développement."),
        new("PassiveMode", "Mode passif", false, false, "true"),
        new("TimeoutSeconds", "Délai (s)", false, false, "30")
    ];

    public IDocumentStorage Create(StorageSettings settings) => new FtpStorage(settings);
}
