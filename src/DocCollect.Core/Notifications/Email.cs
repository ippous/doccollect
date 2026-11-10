using System.Net;
using DocCollect.Core.Delivery;
using DocCollect.Domain;
using DocCollect.Shared;

namespace DocCollect.Core.Notifications;

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record EmailMessage(
    IReadOnlyList<string> To,
    string Subject,
    string HtmlBody,
    string TextBody,
    IReadOnlyList<EmailAttachment>? Attachments = null);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Gabarits d'e-mails (français). Le HTML est volontairement simple pour passer partout.</summary>
public static class EmailTemplates
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static EmailMessage Build(IEnumerable<string> to, string subject, string intro, string? linkText = null, string? link = null, string? footer = null)
    {
        var html = $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;max-width:560px;color:#1f2933">
              <p>{intro}</p>
              {(link is null ? "" : $"""<p><a href="{E(link)}" style="display:inline-block;padding:10px 18px;background:#1d4ed8;color:#fff;border-radius:6px;text-decoration:none">{E(linkText)}</a></p>""")}
              {(footer is null ? "" : $"""<p style="color:#6b7280;font-size:13px">{footer}</p>""")}
              <p style="color:#9ca3af;font-size:12px">DocCollect — collecte sécurisée de pièces justificatives.</p>
            </div>
            """;
        var text = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(intro, "<.*?>", ""))
                   + (link is null ? "" : $"\n\n{linkText} : {link}")
                   + (footer is null ? "" : "\n\n" + WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(footer, "<.*?>", "")));
        return new EmailMessage(to.ToList(), subject, html, text);
    }

    public static EmailMessage ProviderInvitation(string email, string tenantName, Dossier dossier, string link) =>
        Build([email], $"{tenantName} vous demande des documents",
            $"Bonjour{(string.IsNullOrWhiteSpace(dossier.ProviderName) ? "" : " " + E(dossier.ProviderName))},<br/><br/>" +
            $"<b>{E(tenantName)}</b> vous demande de transmettre les pièces du dossier « {E(dossier.Title)} »" +
            (dossier.DueDate is { } due ? $" avant le {due:dd/MM/yyyy}" : "") + "." +
            (string.IsNullOrWhiteSpace(dossier.MessageToProvider) ? "" : $"<br/><br/><i>{E(dossier.MessageToProvider)}</i>"),
            "Déposer mes documents", link,
            "Pour protéger vos données, l'accès est sécurisé par un mot de passe et une double authentification.");

    public static EmailMessage NewDossierForExistingProvider(string email, string tenantName, Dossier dossier, string link) =>
        Build([email], $"Nouvelle demande de documents : {dossier.Title}",
            $"<b>{E(tenantName)}</b> vous a adressé une nouvelle demande : « {E(dossier.Title)} »" +
            (dossier.DueDate is { } due ? $", à compléter avant le {due:dd/MM/yyyy}" : "") + ".",
            "Ouvrir le dossier", link);

    public static EmailMessage Reminder(string email, string tenantName, Dossier dossier, IEnumerable<string> missing, string link) =>
        Build([email], $"Rappel : documents manquants — {dossier.Title}",
            $"Il manque encore les pièces suivantes pour le dossier « {E(dossier.Title)} » ({E(tenantName)}) :<ul>" +
            string.Concat(missing.Select(m => $"<li>{E(m)}</li>")) + "</ul>",
            "Compléter le dossier", link);

    public static EmailMessage ItemRejected(string email, Dossier dossier, string itemLabel, string reason, string link) =>
        Build([email], $"Document à remplacer — {dossier.Title}",
            $"Le document fourni pour « {E(itemLabel)} » n'a pas été accepté.<br/>Motif : <i>{E(reason)}</i>",
            "Déposer un nouveau document", link);

    public static EmailMessage DocumentsAvailable(IEnumerable<string> to, Dossier dossier, IReadOnlyList<DeliveryDocument> docs, bool complete, string link) =>
        Build(to,
            complete ? $"Dossier complet : {dossier.Title}" : $"{docs.Count} nouveau(x) document(s) : {dossier.Title}",
            (complete ? $"Toutes les pièces obligatoires du dossier « {E(dossier.Title)} » ont été fournies." :
                        $"De nouveaux documents ont été déposés pour le dossier « {E(dossier.Title)} ».") +
            "<ul>" + string.Concat(docs.Select(d => $"<li>{E(d.ItemLabel)} — {E(d.Document.FileName)}</li>")) + "</ul>",
            "Consulter le dossier", link);

    public static EmailMessage TwoFactorCode(string email, string code) =>
        Build([email], "Votre code de connexion DocCollect",
            $"Votre code de vérification : <b style=\"font-size:20px;letter-spacing:3px\">{E(code)}</b><br/>Il expire dans quelques minutes.",
            footer: "Si vous n'êtes pas à l'origine de cette connexion, changez votre mot de passe.");

    public static EmailMessage UserInvitation(string email, string tenantName, string role, string link) =>
        Build([email], $"Invitation à rejoindre {tenantName} sur DocCollect",
            $"Vous êtes invité à rejoindre <b>{E(tenantName)}</b> sur DocCollect ({E(role)}).",
            "Créer mon accès", link);
}

/// <summary>
/// Canal « notification e-mail » : prévient le demandeur ; les documents se téléchargent depuis son espace.
/// Paramètres : To (adresses séparées par des virgules), AttachDocuments (true/false), MaxAttachmentsMb.
/// </summary>
public sealed class EmailNotificationDeliveryHandler(IEmailSender sender) : IDeliveryHandler
{
    public DeliveryChannelType Type => DeliveryChannelType.EmailNotification;

    public async Task DeliverAsync(DeliveryContext c, CancellationToken ct = default)
    {
        var to = c.Settings.Require("To").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var link = $"{c.PublicBaseUrl.TrimEnd('/')}/requester/dossiers/{c.Dossier.Id}";
        var message = EmailTemplates.DocumentsAvailable(to, c.Dossier, c.Documents, c.DossierComplete, link);

        if (c.Settings.GetBool("AttachDocuments", false))
        {
            var max = (long)c.Settings.GetInt("MaxAttachmentsMb", 15) * 1024 * 1024;
            if (c.Documents.Sum(d => d.Document.SizeBytes) <= max)
            {
                var attachments = new List<EmailAttachment>();
                foreach (var d in c.Documents)
                {
                    using var ms = new MemoryStream();
                    await c.OpenDocument(d.Document, ms, ct);
                    attachments.Add(new EmailAttachment(d.Document.FileName, d.Document.ContentType, ms.ToArray()));
                }
                message = message with { Attachments = attachments };
            }
        }
        await sender.SendAsync(message, ct);
    }
}
