using DocCollect.Shared;

namespace DocCollect.Domain;

/// <summary>Un demandeur (étude notariale, cabinet comptable…). Unité d'isolation des données.</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Identifiant court utilisé dans les URL et les chemins de stockage.</summary>
    public string Slug { get; set; } = "";
    public Guid? StorageProfileId { get; set; }
    public bool EncryptAtRest { get; set; } = true;
    /// <summary>Option payante : vérification du type de document par IA.</summary>
    public bool AiVerificationEnabled { get; set; }
    public string? AiProviderKey { get; set; }
    /// <summary>Origines autorisées à embarquer l'interface (CSP frame-ancestors).</summary>
    public List<string> AllowedEmbedOrigins { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Disabled { get; set; }
}

/// <summary>Configuration d'un stockage (FTP, SFTP, Azure Blob, GCS, local…). TenantId null = profil plateforme.</summary>
public class StorageProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string ProviderType { get; set; } = "";
    /// <summary>Paramètres du fournisseur, sérialisés en JSON. Les champs secrets sont chiffrés (voir ISecretProtector).</summary>
    public string SettingsJson { get; set; } = "{}";
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ChecklistTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>null pour un modèle public de la plateforme.</summary>
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public TemplateVisibility Visibility { get; set; }
    public DeliveryMode DefaultDeliveryMode { get; set; } = DeliveryMode.AllAtOnce;
    public List<TemplateItem> Items { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Un modèle est utilisable par un tenant s'il est public ou s'il lui appartient.</summary>
    public bool IsVisibleTo(Guid tenantId) => Visibility == TemplateVisibility.Public || TenantId == tenantId;
}

public class TemplateItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public bool Required { get; set; } = true;
    public List<string> AcceptedContentTypes { get; set; } = [];
    public int MaxSizeMb { get; set; } = 20;
    public int MaxFiles { get; set; } = 1;
    /// <summary>Type attendu pour la vérification IA (ex. « carte nationale d'identité », « avis d'imposition »).</summary>
    public string? ExpectedDocumentType { get; set; }
    public int Order { get; set; }
}

/// <summary>Document stocké. Le contenu est dans le stockage externe ; ici, les métadonnées.</summary>
public class StoredDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DossierItemId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public Guid StorageProfileId { get; set; }
    public string StorageKey { get; set; } = "";
    public bool Encrypted { get; set; }
    /// <summary>Clé de données chiffrée par la clé maître (enveloppe), en base64.</summary>
    public string? WrappedKey { get; set; }
    public string? MasterKeyId { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UploadedByUserId { get; set; }
    public AiVerdict AiVerdict { get; set; } = AiVerdict.NotChecked;
    public string? AiDetectedType { get; set; }
    public double? AiConfidence { get; set; }
    public string? AiComment { get; set; }
    public string? AiProviderKey { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

public class DeliveryChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DeliveryChannelType Type { get; set; }
    public string Name { get; set; } = "";
    public string SettingsJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
}

/// <summary>Trace d'un envoi au demandeur (un lot de documents, un canal).</summary>
public class DeliveryRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DossierId { get; set; }
    public Guid ChannelId { get; set; }
    public List<Guid> DocumentIds { get; set; } = [];
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
}

/// <summary>Comptage de l'usage IA par tenant et par mois (base de facturation).</summary>
public class AiUsageRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public string ProviderKey { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Invitation (fournisseur de documents ou utilisateur d'un demandeur).</summary>
public class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string Role { get; set; } = Roles.Provider;
    public Guid? TenantId { get; set; }
    public Guid? DossierId { get; set; }
    /// <summary>SHA-256 du jeton envoyé par e-mail (le jeton en clair n'est jamais stocké).</summary>
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;
}

public class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid? TenantId { get; set; }
    public Guid? DossierId { get; set; }
    public string? UserId { get; set; }
    public string Action { get; set; } = "";
    public string? Details { get; set; }
}
