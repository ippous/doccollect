using DocCollect.Shared;

namespace DocCollect.Client.Services;

/// <summary>Libellés français et petites aides d'affichage.</summary>
public static class Ui
{
    public static string Label(this DossierStatus s) => s switch
    {
        DossierStatus.Draft => "Brouillon",
        DossierStatus.Open => "En attente de pièces",
        DossierStatus.Complete => "Complet",
        DossierStatus.Delivered => "Transmis",
        DossierStatus.Closed => "Clos",
        DossierStatus.Cancelled => "Annulé",
        _ => s.ToString()
    };

    public static string Css(this DossierStatus s) => s switch
    {
        DossierStatus.Open => "badge warn",
        DossierStatus.Complete => "badge ok",
        DossierStatus.Delivered => "badge ok",
        DossierStatus.Cancelled => "badge muted",
        DossierStatus.Closed => "badge muted",
        _ => "badge"
    };

    public static string Label(this ItemStatus s) => s switch
    {
        ItemStatus.Missing => "Manquant",
        ItemStatus.Submitted => "Déposé, à valider",
        ItemStatus.Accepted => "Reçu",
        ItemStatus.Rejected => "Refusé",
        _ => s.ToString()
    };

    public static string Css(this ItemStatus s) => s switch
    {
        ItemStatus.Missing => "badge",
        ItemStatus.Submitted => "badge info",
        ItemStatus.Accepted => "badge ok",
        ItemStatus.Rejected => "badge err",
        _ => "badge"
    };

    public static string Label(this DeliveryMode m) => m == DeliveryMode.AllAtOnce ? "En une fois, quand tout est là" : "Au fil de l'eau";

    public static string Label(this DeliveryChannelType t) => t switch
    {
        DeliveryChannelType.EmailNotification => "Notification e-mail",
        DeliveryChannelType.Webhook => "Webhook (API)",
        DeliveryChannelType.StorageCopy => "Copie vers mon stockage",
        _ => t.ToString()
    };

    public static string? AiText(this DocumentDto d) => d.AiVerdict switch
    {
        AiVerdict.Match => $"Conforme ({d.AiDetectedType})",
        AiVerdict.Mismatch => $"Ne semble pas correspondre : {d.AiDetectedType}",
        AiVerdict.Uncertain => "Vérification incertaine",
        AiVerdict.Error => "Vérification indisponible",
        _ => null
    };

    public static string AiCss(this AiVerdict v) => v switch
    {
        AiVerdict.Match => "badge ok",
        AiVerdict.Mismatch => "badge err",
        AiVerdict.Uncertain => "badge warn",
        _ => "badge muted"
    };

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} o",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} Ko",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} Mo"
    };

    /// <summary>Attribut accept du sélecteur de fichier, à partir des types acceptés de la pièce.</summary>
    public static string Accept(IReadOnlyList<string> types) => types.Count == 0 ? "" : string.Join(',', types);

    public static readonly IReadOnlyList<(string Value, string Label)> CommonTypes =
    [
        ("application/pdf", "PDF"),
        ("image/*", "Photos / images"),
        ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "Word"),
        ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Excel")
    ];
}
