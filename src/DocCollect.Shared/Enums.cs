using System.Text.Json.Serialization;

namespace DocCollect.Shared;

/// <summary>Rôles applicatifs. Tous exigent la double authentification.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Requester = "Requester";
    public const string Provider = "Provider";

    public static readonly IReadOnlyList<string> All = [Admin, Requester, Provider];
}

/// <summary>Visibilité d'un modèle de checklist.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TemplateVisibility>))]
public enum TemplateVisibility
{
    /// <summary>Visible uniquement par le demandeur (tenant) propriétaire.</summary>
    Private = 0,
    /// <summary>Visible par tous les demandeurs (catalogue public, géré par les admins).</summary>
    Public = 1
}

/// <summary>Quand transmettre les documents au demandeur.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeliveryMode>))]
public enum DeliveryMode
{
    /// <summary>Un seul envoi lorsque toutes les pièces obligatoires sont présentes.</summary>
    AllAtOnce = 0,
    /// <summary>Chaque pièce est transmise dès qu'elle est déposée (et validée si la validation est requise).</summary>
    Incremental = 1
}

[JsonConverter(typeof(JsonStringEnumConverter<DossierStatus>))]
public enum DossierStatus
{
    Draft = 0,
    Open = 1,
    Complete = 2,
    Delivered = 3,
    Closed = 4,
    Cancelled = 5
}

[JsonConverter(typeof(JsonStringEnumConverter<ItemStatus>))]
public enum ItemStatus
{
    Missing = 0,
    Submitted = 1,
    Accepted = 2,
    Rejected = 3
}

/// <summary>Résultat de la vérification IA d'un document.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AiVerdict>))]
public enum AiVerdict
{
    NotChecked = 0,
    Match = 1,
    Mismatch = 2,
    Uncertain = 3,
    Error = 4
}

[JsonConverter(typeof(JsonStringEnumConverter<DeliveryChannelType>))]
public enum DeliveryChannelType
{
    /// <summary>Notification e-mail ; le demandeur télécharge depuis son espace.</summary>
    EmailNotification = 0,
    /// <summary>POST HTTP signé (HMAC) vers une URL du demandeur.</summary>
    Webhook = 1,
    /// <summary>Copie vers un stockage du demandeur (son SFTP, son Blob…).</summary>
    StorageCopy = 2
}

[JsonConverter(typeof(JsonStringEnumConverter<DeliveryStatus>))]
public enum DeliveryStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2
}

[JsonConverter(typeof(JsonStringEnumConverter<TwoFactorMethod>))]
public enum TwoFactorMethod
{
    None = 0,
    Authenticator = 1,
    Email = 2
}
