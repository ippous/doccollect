using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocCollect.Core.Storage;
using DocCollect.Domain;
using DocCollect.Shared;

namespace DocCollect.Core.Delivery;

public sealed record DeliveryDocument(StoredDocument Document, string ItemLabel);

public sealed class DeliveryContext
{
    public required Tenant Tenant { get; init; }
    public required Dossier Dossier { get; init; }
    public required DeliveryChannel Channel { get; init; }
    public required StorageSettings Settings { get; init; }
    public required IReadOnlyList<DeliveryDocument> Documents { get; init; }
    /// <summary>Écrit le contenu en clair d'un document (déchiffré, intégrité vérifiée) dans le flux fourni.</summary>
    public required Func<StoredDocument, Stream, CancellationToken, Task> OpenDocument { get; init; }
    /// <summary>URL publique de l'application (ex. https://doccollect.jaume-eric.com).</summary>
    public required string PublicBaseUrl { get; init; }
    /// <summary>Vrai si l'envoi correspond au dossier complet (sinon envoi au fil de l'eau).</summary>
    public bool DossierComplete { get; init; }
}

/// <summary>Transmet un lot de documents au demandeur par un canal donné.</summary>
public interface IDeliveryHandler
{
    DeliveryChannelType Type { get; }
    Task DeliverAsync(DeliveryContext context, CancellationToken ct = default);
}

public static class DeliveryRetryPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2), TimeSpan.FromHours(12)
    ];

    public static int MaxAttempts => Delays.Length + 1;

    /// <summary>Prochaine tentative après <paramref name="attempts"/> échecs, ou null si on abandonne.</summary>
    public static DateTimeOffset? NextAttempt(int attempts, DateTimeOffset now) =>
        attempts >= 1 && attempts <= Delays.Length ? now + Delays[attempts - 1] : null;
}

// ---------- Webhook ----------

/// <summary>
/// POST JSON signé vers l'URL du demandeur. Signature : en-tête X-DocCollect-Signature = « sha256=hex(HMAC(secret, timestamp + "." + corps)) ».
/// Paramètres : Url (obligatoire), Secret (obligatoire), IncludeContent (true/false), MaxInlineMb.
/// </summary>
public sealed class WebhookDeliveryHandler(HttpClient http) : IDeliveryHandler
{
    public DeliveryChannelType Type => DeliveryChannelType.Webhook;

    public async Task DeliverAsync(DeliveryContext c, CancellationToken ct = default)
    {
        var url = c.Settings.Require("Url");
        var secret = c.Settings.Require("Secret");
        var includeContent = c.Settings.GetBool("IncludeContent", false);
        var maxInline = (long)c.Settings.GetInt("MaxInlineMb", 10) * 1024 * 1024;

        var docs = new List<object>();
        foreach (var d in c.Documents)
        {
            string? content = null;
            if (includeContent && d.Document.SizeBytes <= maxInline)
            {
                using var ms = new MemoryStream();
                await c.OpenDocument(d.Document, ms, ct);
                content = Convert.ToBase64String(ms.ToArray());
            }
            docs.Add(new
            {
                id = d.Document.Id,
                item = d.ItemLabel,
                fileName = d.Document.FileName,
                contentType = d.Document.ContentType,
                size = d.Document.SizeBytes,
                sha256 = d.Document.Sha256,
                aiVerdict = d.Document.AiVerdict.ToString(),
                contentBase64 = content,
                downloadUrl = $"{c.PublicBaseUrl.TrimEnd('/')}/api/requester/dossiers/{c.Dossier.Id}/documents/{d.Document.Id}/content"
            });
        }

        var body = JsonSerializer.Serialize(new
        {
            @event = c.DossierComplete ? "dossier.complete" : "documents.received",
            dossier = new { id = c.Dossier.Id, reference = c.Dossier.Reference, title = c.Dossier.Title, status = c.Dossier.Status.ToString() },
            documents = docs,
            sentAt = DateTimeOffset.UtcNow
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-DocCollect-Timestamp", timestamp);
        req.Headers.Add("X-DocCollect-Signature", "sha256=" + Sign(secret, timestamp, body));
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Webhook : HTTP {(int)resp.StatusCode}.");
    }

    public static string Sign(string secret, string timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
}

// ---------- Copie vers un stockage du demandeur ----------

/// <summary>
/// Copie les documents (en clair) vers un stockage appartenant au demandeur (son SFTP, son NAS, son Blob…).
/// Paramètres : StorageProfileId (obligatoire), PathTemplate (défaut « {reference}/{item}/{file} »).
/// </summary>
public sealed class StorageCopyDeliveryHandler(IStorageResolver resolver) : IDeliveryHandler
{
    public DeliveryChannelType Type => DeliveryChannelType.StorageCopy;

    public async Task DeliverAsync(DeliveryContext c, CancellationToken ct = default)
    {
        if (!Guid.TryParse(c.Settings.Require("StorageProfileId"), out var profileId))
            throw new StorageException("StorageProfileId invalide.");
        var target = await resolver.ResolveAsync(profileId, ct);
        var template = c.Settings.Get("PathTemplate") ?? "{reference}/{item}/{file}";

        foreach (var d in c.Documents)
        {
            var path = BuildPath(template, c.Dossier, d);
            var temp = Path.Combine(Path.GetTempPath(), "doccollect-out-" + Guid.NewGuid().ToString("N"));
            await using var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await c.OpenDocument(d.Document, fs, ct);
            fs.Position = 0;
            await target.WriteAsync(path, fs, ct);
        }
    }

    public static string BuildPath(string template, Dossier dossier, DeliveryDocument d)
    {
        static string Clean(string? s) => string.IsNullOrWhiteSpace(s) ? "_" :
            new string(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\' ? '_' : ch).ToArray()).Trim();
        var path = template
            .Replace("{reference}", Clean(dossier.Reference ?? dossier.Id.ToString("N")[..8]))
            .Replace("{dossier}", dossier.Id.ToString("N"))
            .Replace("{title}", Clean(dossier.Title))
            .Replace("{item}", Clean(d.ItemLabel))
            .Replace("{file}", $"{d.Document.UploadedAt:yyyyMMdd-HHmmss}_{Clean(d.Document.FileName)}");
        return StorageKeys.Normalize(path);
    }
}
