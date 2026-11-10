using DocCollect.Api.Auth;
using DocCollect.Core.Diagnostics;
using DocCollect.Core.Notifications;
using DocCollect.Domain;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Shared;
using Microsoft.EntityFrameworkCore;

namespace DocCollect.Api.Endpoints;

/// <summary>Journaux (admin) et état de l'envoi des e-mails (admin et demandeur).</summary>
public static class MonitoringEndpoints
{
    public static void MapMonitoringEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization(Policies.Admin);

        admin.MapGet("/logs", (string? level, string? q, long? afterId, int? take, InMemoryLogStore store) =>
        {
            var min = Enum.TryParse<LogSeverity>(level, true, out var l) ? l : LogSeverity.Information;
            return store.Query(min, q, afterId, take ?? 200)
                .Select(r => new LogEntryDto(r.Id, r.At, r.Level.ToString(), r.Category, r.Message, r.Exception));
        });

        admin.MapGet("/email/status", async (EmailHealth health, AppDbContext db) =>
            await StatusAsync(health, db, tenantId: null, includeDetail: true));

        admin.MapPost("/email/test", async (HttpContext http, IEmailSender email) =>
            await SendTestAsync(http, email)).RequireRateLimiting("auth");

        var requester = app.MapGroup("/api/requester").WithTags("Demandeur").RequireAuthorization(Policies.Requester);

        requester.MapGet("/email/status", async (HttpContext http, EmailHealth health, AppDbContext db) =>
            await StatusAsync(health, db, http.User.TenantId(), includeDetail: false));

        requester.MapPost("/email/test", async (HttpContext http, IEmailSender email) =>
            await SendTestAsync(http, email)).RequireRateLimiting("auth");
    }

    private static async Task<EmailStatusDto> StatusAsync(EmailHealth health, AppDbContext db, Guid? tenantId, bool includeDetail)
    {
        var s = health.Snapshot();
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var channels = await db.DeliveryChannels.Where(c => tenantId == null || c.TenantId == tenantId)
            .ToDictionaryAsync(c => c.Id);
        var channelIds = channels.Keys.ToList();
        var records = await db.Deliveries
            .Where(r => channelIds.Contains(r.ChannelId) && r.CreatedAt >= since
                        && (r.Status == DeliveryStatus.Failed || (r.Status == DeliveryStatus.Pending && r.Attempts > 0)))
            .OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync();
        var dossierIds = records.Select(r => r.DossierId).Distinct().ToList();
        var titles = await db.Dossiers.Where(d => dossierIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Title);

        var failures = records.Select(r => new DeliveryFailureDto(
            r.DossierId, titles.GetValueOrDefault(r.DossierId, "?"), channels[r.ChannelId].Name, channels[r.ChannelId].Type,
            r.Status, r.Attempts, includeDetail ? r.LastError : ShortError(r.LastError), r.CreatedAt, r.NextAttemptAt)).ToList();

        return new EmailStatusDto(s.Working, s.LastSuccessAt, s.LastFailureAt, s.LastFailureCategory,
            includeDetail ? s.LastFailureDetail : null, s.SentCount, s.FailedCount, failures);
    }

    /// <summary>Pour un demandeur : seul le libellé lisible (« Envoi de l'e-mail impossible : … ») est conservé.</summary>
    private static string? ShortError(string? error) =>
        error is null ? null : error.StartsWith("Envoi de l'e-mail impossible", StringComparison.Ordinal) ? error : "échec de la transmission";

    private static async Task<EmailTestResult> SendTestAsync(HttpContext http, IEmailSender email)
    {
        var to = http.User.Email();
        try
        {
            await email.SendAsync(new EmailMessage([to], "Test d'envoi DocCollect",
                "<p>Cet e-mail confirme que DocCollect peut envoyer des messages (invitations, codes de connexion, notifications).</p>",
                "Cet e-mail confirme que DocCollect peut envoyer des messages (invitations, codes de connexion, notifications)."));
            return new EmailTestResult(true, $"E-mail de test envoyé à {to}. Vérifiez votre boîte de réception (et les indésirables).");
        }
        catch (EmailSendException ex)
        {
            return new EmailTestResult(false, $"L'envoi a échoué : {ex.Category}.");
        }
    }
}
