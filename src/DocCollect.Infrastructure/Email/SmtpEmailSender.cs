using DocCollect.Core.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DocCollect.Infrastructure.Email;

public sealed class SmtpOptions
{
    public const string Section = "Smtp";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    /// <summary>None, StartTls, SslOnConnect, Auto.</summary>
    public string Security { get; set; } = "Auto";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "no-reply@jaume-eric.com";
    public string FromName { get; set; } = "DocCollect";
    /// <summary>Préfixe ajouté au sujet hors production (ex. « [TEST] »).</summary>
    public string? SubjectPrefix { get; set; }
}

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var o = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        foreach (var to in message.To) mime.To.Add(MailboxAddress.Parse(to));
        mime.Subject = string.IsNullOrWhiteSpace(o.SubjectPrefix) ? message.Subject : $"{o.SubjectPrefix} {message.Subject}";

        var body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody };
        foreach (var a in message.Attachments ?? [])
            body.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        mime.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        var security = o.Security.ToLowerInvariant() switch
        {
            "none" => SecureSocketOptions.None,
            "starttls" => SecureSocketOptions.StartTls,
            "sslonconnect" => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.Auto
        };
        await client.ConnectAsync(o.Host, o.Port, security, ct);
        if (!string.IsNullOrEmpty(o.Username))
            await client.AuthenticateAsync(o.Username, o.Password, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
        logger.LogInformation("E-mail « {Subject} » envoyé à {Count} destinataire(s).", message.Subject, message.To.Count);
    }
}
