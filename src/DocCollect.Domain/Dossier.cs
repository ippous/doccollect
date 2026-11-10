using DocCollect.Shared;

namespace DocCollect.Domain;

/// <summary>
/// Une demande de pièces adressée à un fournisseur. Porte les règles de complétude
/// et détermine quels documents sont prêts à être transmis au demandeur.
/// </summary>
public class Dossier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Title { get; set; } = "";
    public string? Reference { get; set; }
    public Guid? TemplateId { get; set; }
    public string ProviderEmail { get; set; } = "";
    public string? ProviderName { get; set; }
    public string? ProviderUserId { get; set; }
    public DeliveryMode DeliveryMode { get; set; } = DeliveryMode.AllAtOnce;
    /// <summary>Si vrai, une pièce ne compte qu'une fois acceptée par le demandeur.</summary>
    public bool RequireRequesterValidation { get; set; }
    public DossierStatus Status { get; set; } = DossierStatus.Draft;
    public DateOnly? DueDate { get; set; }
    public string? MessageToProvider { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastReminderAt { get; set; }
    public int RemindersSent { get; set; }
    public List<DossierItem> Items { get; set; } = [];

    // ---------- Construction ----------

    public static Dossier FromTemplate(Guid tenantId, string title, ChecklistTemplate? template, IEnumerable<TemplateItem>? extraItems = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new DomainException(ErrorCodes.Validation, "Le titre du dossier est obligatoire.");
        if (template is not null && !template.IsVisibleTo(tenantId))
            throw new DomainException(ErrorCodes.Validation, "Ce modèle n'est pas accessible à ce demandeur.");

        var dossier = new Dossier
        {
            TenantId = tenantId,
            Title = title.Trim(),
            TemplateId = template?.Id,
            DeliveryMode = template?.DefaultDeliveryMode ?? DeliveryMode.AllAtOnce
        };
        var sources = (template?.Items ?? []).OrderBy(i => i.Order).Concat(extraItems ?? []);
        var order = 0;
        foreach (var source in sources)
            dossier.Items.Add(DossierItem.FromTemplateItem(dossier.Id, source, order++));

        if (dossier.Items.Count == 0)
            throw new DomainException(ErrorCodes.Validation, "Un dossier doit contenir au moins une pièce attendue.");
        return dossier;
    }

    public void Open()
    {
        if (Status != DossierStatus.Draft)
            throw new DomainException(ErrorCodes.InvalidState, "Seul un brouillon peut être ouvert.");
        Status = DossierStatus.Open;
    }

    public void Cancel()
    {
        if (Status is DossierStatus.Closed or DossierStatus.Cancelled)
            throw new DomainException(ErrorCodes.InvalidState, "Ce dossier est déjà clos.");
        Status = DossierStatus.Cancelled;
    }

    public void Close()
    {
        if (Status is DossierStatus.Cancelled)
            throw new DomainException(ErrorCodes.InvalidState, "Ce dossier est annulé.");
        Status = DossierStatus.Closed;
    }

    public bool AcceptsUploads => Status is DossierStatus.Open or DossierStatus.Complete or DossierStatus.Delivered;

    // ---------- Dépôt / validation ----------

    public DossierItem GetItem(Guid itemId) =>
        Items.FirstOrDefault(i => i.Id == itemId)
        ?? throw new DomainException(ErrorCodes.ItemNotFound, "Pièce attendue introuvable.");

    /// <summary>Contrôle qu'un fichier peut être déposé sur une pièce, avant de l'écrire dans le stockage.</summary>
    public void EnsureCanUpload(Guid itemId, string contentType, long sizeBytes)
    {
        if (!AcceptsUploads)
            throw new DomainException(ErrorCodes.InvalidState, "Ce dossier n'accepte plus de dépôt.");
        GetItem(itemId).EnsureCanAccept(contentType, sizeBytes);
    }

    public void AddDocument(Guid itemId, StoredDocument document, DateTimeOffset now)
    {
        EnsureCanUpload(itemId, document.ContentType, document.SizeBytes);
        var item = GetItem(itemId);
        document.DossierItemId = item.Id;
        item.Documents.Add(document);
        item.Status = RequireRequesterValidation ? ItemStatus.Submitted : ItemStatus.Accepted;
        item.RejectionReason = null;
        RefreshCompletion(now);
    }

    public void RemoveDocument(Guid documentId, DateTimeOffset now)
    {
        var item = Items.FirstOrDefault(i => i.Documents.Any(d => d.Id == documentId))
                   ?? throw new DomainException(ErrorCodes.DocumentNotFound, "Document introuvable.");
        var doc = item.Documents.First(d => d.Id == documentId);
        if (doc.DeliveredAt is not null)
            throw new DomainException(ErrorCodes.AlreadyDelivered, "Ce document a déjà été transmis et ne peut plus être retiré.");
        item.Documents.Remove(doc);
        if (item.Documents.Count == 0)
            item.Status = ItemStatus.Missing;
        RefreshCompletion(now);
    }

    public void AcceptItem(Guid itemId, DateTimeOffset now)
    {
        var item = GetItem(itemId);
        if (item.Documents.Count == 0)
            throw new DomainException(ErrorCodes.InvalidState, "Aucun document à accepter pour cette pièce.");
        item.Status = ItemStatus.Accepted;
        item.RejectionReason = null;
        RefreshCompletion(now);
    }

    public void RejectItem(Guid itemId, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException(ErrorCodes.Validation, "Le motif de refus est obligatoire.");
        var item = GetItem(itemId);
        item.Status = ItemStatus.Rejected;
        item.RejectionReason = reason.Trim();
        // Les documents non transmis sont retirés : le fournisseur doit en redéposer.
        item.Documents.RemoveAll(d => d.DeliveredAt is null);
        RefreshCompletion(now);
    }

    // ---------- Complétude ----------

    public bool IsSatisfied(DossierItem item) =>
        item.Documents.Count > 0 && (RequireRequesterValidation
            ? item.Status == ItemStatus.Accepted
            : item.Status is ItemStatus.Accepted or ItemStatus.Submitted);

    public IReadOnlyList<DossierItem> MissingRequiredItems() =>
        Items.Where(i => i.Required && !IsSatisfied(i)).OrderBy(i => i.Order).ToList();

    public bool IsComplete => MissingRequiredItems().Count == 0;

    private void RefreshCompletion(DateTimeOffset now)
    {
        if (Status is DossierStatus.Cancelled or DossierStatus.Closed or DossierStatus.Draft)
            return;
        if (IsComplete)
        {
            if (Status == DossierStatus.Open)
            {
                Status = DossierStatus.Complete;
                CompletedAt ??= now;
            }
            if (Status == DossierStatus.Delivered && AllSatisfiedDocuments().Any(d => d.DeliveredAt is null))
                Status = DossierStatus.Complete;
        }
        else if (Status is DossierStatus.Complete or DossierStatus.Delivered)
        {
            Status = DossierStatus.Open;
            CompletedAt = null;
        }
    }

    // ---------- Livraison ----------

    private IEnumerable<StoredDocument> AllSatisfiedDocuments() =>
        Items.Where(IsSatisfied).SelectMany(i => i.Documents);

    /// <summary>
    /// Documents à transmettre maintenant :
    /// - Incremental : toute pièce satisfaite dont les documents n'ont pas encore été transmis ;
    /// - AllAtOnce : rien tant que le dossier n'est pas complet, puis tout ce qui n'a pas été transmis.
    /// </summary>
    public IReadOnlyList<StoredDocument> DocumentsReadyForDelivery()
    {
        if (Status is DossierStatus.Cancelled or DossierStatus.Draft)
            return [];
        if (DeliveryMode == DeliveryMode.AllAtOnce && !IsComplete)
            return [];
        return AllSatisfiedDocuments().Where(d => d.DeliveredAt is null).ToList();
    }

    /// <summary>Envoi manuel décidé par le demandeur (ex. dossier incomplet mais urgent).</summary>
    public IReadOnlyList<StoredDocument> DocumentsForManualDelivery() =>
        AllSatisfiedDocuments().Where(d => d.DeliveredAt is null).ToList();

    public void MarkDelivered(IEnumerable<Guid> documentIds, DateTimeOffset now)
    {
        var ids = documentIds.ToHashSet();
        foreach (var doc in Items.SelectMany(i => i.Documents).Where(d => ids.Contains(d.Id)))
            doc.DeliveredAt ??= now;

        if (IsComplete && AllSatisfiedDocuments().All(d => d.DeliveredAt is not null)
            && Status is DossierStatus.Complete or DossierStatus.Open)
            Status = DossierStatus.Delivered;
    }
}

public class DossierItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DossierId { get; set; }
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public bool Required { get; set; } = true;
    public List<string> AcceptedContentTypes { get; set; } = [];
    public int MaxSizeMb { get; set; } = 20;
    public int MaxFiles { get; set; } = 1;
    public string? ExpectedDocumentType { get; set; }
    public int Order { get; set; }
    public ItemStatus Status { get; set; } = ItemStatus.Missing;
    public string? RejectionReason { get; set; }
    public List<StoredDocument> Documents { get; set; } = [];

    public static DossierItem FromTemplateItem(Guid dossierId, TemplateItem source, int order) => new()
    {
        DossierId = dossierId,
        Label = source.Label,
        Description = source.Description,
        Required = source.Required,
        AcceptedContentTypes = [.. source.AcceptedContentTypes],
        MaxSizeMb = source.MaxSizeMb <= 0 ? 20 : source.MaxSizeMb,
        MaxFiles = source.MaxFiles <= 0 ? 1 : source.MaxFiles,
        ExpectedDocumentType = source.ExpectedDocumentType,
        Order = order
    };

    public void EnsureCanAccept(string contentType, long sizeBytes)
    {
        if (!ContentTypeMatcher.IsAllowed(contentType, AcceptedContentTypes))
            throw new DomainException(ErrorCodes.ContentTypeNotAllowed,
                $"Type de fichier non accepté pour « {Label} » ({contentType}).");
        if (sizeBytes <= 0)
            throw new DomainException(ErrorCodes.Validation, "Fichier vide.");
        if (sizeBytes > (long)MaxSizeMb * 1024 * 1024)
            throw new DomainException(ErrorCodes.FileTooLarge, $"Fichier trop volumineux (max {MaxSizeMb} Mo).");
        if (Documents.Count >= MaxFiles)
            throw new DomainException(ErrorCodes.TooManyFiles, $"Nombre maximal de fichiers atteint pour « {Label} » ({MaxFiles}).");
    }
}

public static class ContentTypeMatcher
{
    /// <summary>Liste vide = tout est accepté. Gère les jokers du type « image/* ».</summary>
    public static bool IsAllowed(string contentType, IReadOnlyCollection<string> accepted)
    {
        if (accepted.Count == 0) return true;
        var ct = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        foreach (var pattern in accepted)
        {
            var p = pattern.Trim().ToLowerInvariant();
            if (p == "*/*" || p == ct) return true;
            if (p.EndsWith("/*") && ct.StartsWith(p[..^1], StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
