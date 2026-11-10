using DocCollect.Shared;

namespace DocCollect.Core.Storage;

/// <summary>
/// Stockage d'objets binaires. Toutes les implémentations (FTP, SFTP, Azure Blob, GCS, local…)
/// exposent ce même contrat ; les clés sont des chemins relatifs avec « / » comme séparateur.
/// </summary>
public interface IDocumentStorage
{
    string ProviderType { get; }
    Task WriteAsync(string key, Stream content, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    /// <summary>Test de connexion utilisé par l'écran d'administration (écriture, lecture, suppression d'un fichier témoin).</summary>
    Task<StorageTestResult> TestAsync(CancellationToken ct = default);
}

/// <summary>
/// Décrit un type de stockage et sait créer une instance à partir de paramètres.
/// Ajouter un nouveau stockage = implémenter cette interface et l'enregistrer dans le conteneur DI.
/// </summary>
public interface IStorageProvider
{
    /// <summary>Identifiant stable, enregistré en base (ex. « ftp », « sftp », « azure-blob »).</summary>
    string Type { get; }
    string DisplayName { get; }
    IReadOnlyList<StorageSettingField> Fields { get; }
    IDocumentStorage Create(StorageSettings settings);
}

public sealed class StorageException(string message, Exception? inner = null) : Exception(message, inner);
