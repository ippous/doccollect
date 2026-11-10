using Azure.Storage.Blobs;
using DocCollect.Core.Storage;
using DocCollect.Shared;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;

namespace DocCollect.Infrastructure.Storage;

// ---------- Azure Blob Storage ----------

public sealed class AzureBlobStorage : IDocumentStorage
{
    private readonly BlobContainerClient _container;
    private readonly string? _prefix;
    private bool _ensured;

    public AzureBlobStorage(StorageSettings settings)
    {
        var container = settings.Require("Container");
        _container = settings.Get("ConnectionString") is { } cs
            ? new BlobContainerClient(cs, container)
            : new BlobContainerClient(new Uri(settings.Require("ContainerSasUrl")));
        _prefix = settings.Get("Prefix");
        _ensured = !settings.GetBool("CreateContainer", true);
    }

    public string ProviderType => AzureBlobStorageProvider.TypeName;

    private async Task<BlobClient> Blob(string key, CancellationToken ct)
    {
        if (!_ensured)
        {
            await _container.CreateIfNotExistsAsync(cancellationToken: ct);
            _ensured = true;
        }
        return _container.GetBlobClient(StorageKeys.Combine(_prefix, key));
    }

    public async Task WriteAsync(string key, Stream content, CancellationToken ct = default) =>
        await (await Blob(key, ct)).UploadAsync(content, overwrite: true, ct);

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var blob = await Blob(key, ct);
        if (!await blob.ExistsAsync(ct)) throw new StorageException($"Blob introuvable ({key}).");
        return await blob.OpenReadAsync(cancellationToken: ct);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        await (await Blob(key, ct)).ExistsAsync(ct);

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await (await Blob(key, ct)).DeleteIfExistsAsync(cancellationToken: ct);

    public Task<StorageTestResult> TestAsync(CancellationToken ct = default) => StorageTester.RunAsync(this, ct);
}

public sealed class AzureBlobStorageProvider : IStorageProvider
{
    public const string TypeName = "azure-blob";
    public string Type => TypeName;
    public string DisplayName => "Azure Blob Storage";
    public IReadOnlyList<StorageSettingField> Fields { get; } =
    [
        new("ConnectionString", "Chaîne de connexion", false, true, Help: "Ou bien une URL SAS de conteneur."),
        new("ContainerSasUrl", "URL SAS du conteneur", false, true),
        new("Container", "Conteneur", true, false, "doccollect"),
        new("Prefix", "Préfixe", false, false),
        new("CreateContainer", "Créer le conteneur s'il n'existe pas", false, false, "true")
    ];

    public IDocumentStorage Create(StorageSettings settings)
    {
        if (settings.Get("ConnectionString") is null && settings.Get("ContainerSasUrl") is null)
            throw new StorageException("Azure Blob : renseigner une chaîne de connexion ou une URL SAS.");
        return new AzureBlobStorage(settings);
    }
}

// ---------- Google Cloud Storage ----------

public sealed class GoogleCloudStorage(StorageSettings settings) : IDocumentStorage
{
    private readonly string _bucket = settings.Require("Bucket");
    private readonly string? _prefix = settings.Get("Prefix");
    private StorageClient? _client;

    public string ProviderType => GoogleCloudStorageProvider.TypeName;

    private async Task<StorageClient> Client()
    {
        if (_client is not null) return _client;
        var json = settings.Get("ServiceAccountJson");
#pragma warning disable CS0612, CS0618 // FromJson reste la voie la plus simple pour une clé de compte de service stockée en configuration.
        var credential = json is null ? await GoogleCredential.GetApplicationDefaultAsync() : GoogleCredential.FromJson(json);
#pragma warning restore CS0612, CS0618
        return _client = await StorageClient.CreateAsync(credential);
    }

    private string Name(string key) => StorageKeys.Combine(_prefix, key);

    public async Task WriteAsync(string key, Stream content, CancellationToken ct = default) =>
        await (await Client()).UploadObjectAsync(_bucket, Name(key), "application/octet-stream", content, cancellationToken: ct);

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), "doccollect-gcs-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await (await Client()).DownloadObjectAsync(_bucket, Name(key), temp, cancellationToken: ct);
            temp.Position = 0;
            return temp;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await temp.DisposeAsync();
            throw new StorageException($"Objet GCS introuvable ({key}).", ex);
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await (await Client()).GetObjectAsync(_bucket, Name(key), cancellationToken: ct);
            return true;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        try { await (await Client()).DeleteObjectAsync(_bucket, Name(key), cancellationToken: ct); }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound) { }
    }

    public Task<StorageTestResult> TestAsync(CancellationToken ct = default) => StorageTester.RunAsync(this, ct);
}

public sealed class GoogleCloudStorageProvider : IStorageProvider
{
    public const string TypeName = "gcs";
    public string Type => TypeName;
    public string DisplayName => "Google Cloud Storage";
    public IReadOnlyList<StorageSettingField> Fields { get; } =
    [
        new("Bucket", "Bucket", true, false),
        new("Prefix", "Préfixe", false, false),
        new("ServiceAccountJson", "Clé JSON du compte de service", false, true,
            Help: "Vide = identifiants par défaut de l'environnement (Workload Identity, GOOGLE_APPLICATION_CREDENTIALS).")
    ];

    public IDocumentStorage Create(StorageSettings settings) => new GoogleCloudStorage(settings);
}
