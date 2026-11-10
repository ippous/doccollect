using DocCollect.Core.Delivery;
using DocCollect.Core.Documents;
using DocCollect.Core.Notifications;
using DocCollect.Core.Security;
using DocCollect.Core.Storage;
using DocCollect.Domain;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocCollect.Infrastructure.Delivery;

public sealed class AppOptions
{
    public const string Section = "App";
    /// <summary>URL publique (ex. https://doccollect.jaume-eric.com). Sert aux liens des e-mails et webhooks.</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:5001";
    /// <summary>Libellé d'environnement affiché dans l'interface (Dev, Test ; vide en production).</summary>
    public string? EnvironmentLabel { get; set; }
    public int InvitationValidityDays { get; set; } = 14;
    public int ReminderIntervalDays { get; set; } = 7;
    public int MaxReminders { get; set; } = 5;
}

/// <summary>Réveille le worker de livraison dès qu'un envoi est planifié.</summary>
public sealed class DeliverySignal
{
    private readonly SemaphoreSlim _semaphore = new(0);
    public void Notify() { if (_semaphore.CurrentCount == 0) _semaphore.Release(); }
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);
}

/// <summary>
/// Planifie les envois : calcule les documents prêts (selon le mode du dossier), crée un enregistrement
/// de livraison par canal actif du demandeur et marque les documents comme « mis en livraison ».
/// Sans canal configuré, les documents restent simplement disponibles dans l'espace du demandeur.
/// </summary>
public sealed class DeliveryScheduler(AppDbContext db, DeliverySignal signal, TimeProvider clock)
{
    public async Task<int> ScheduleAsync(Dossier dossier, bool manual = false, CancellationToken ct = default)
    {
        var docs = manual ? dossier.DocumentsForManualDelivery() : dossier.DocumentsReadyForDelivery();
        if (docs.Count == 0) return 0;

        var now = clock.GetUtcNow();
        var channels = await db.DeliveryChannels.Where(c => c.TenantId == dossier.TenantId && c.Enabled).ToListAsync(ct);
        foreach (var channel in channels)
        {
            db.Deliveries.Add(new DeliveryRecord
            {
                DossierId = dossier.Id,
                ChannelId = channel.Id,
                DocumentIds = docs.Select(d => d.Id).ToList(),
                NextAttemptAt = now
            });
        }
        dossier.MarkDelivered(docs.Select(d => d.Id), now);
        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = dossier.TenantId, DossierId = dossier.Id, Action = manual ? "delivery.manual" : "delivery.scheduled",
            Details = $"{docs.Count} document(s), {channels.Count} canal(aux)"
        });
        await db.SaveChangesAsync(ct);
        if (channels.Count > 0) signal.Notify();
        return docs.Count;
    }
}

public sealed class DeliveryWorker(
    IServiceScopeFactory scopes,
    DeliverySignal signal,
    TimeProvider clock,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Erreur du worker de livraison.");
            }
            try { await signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task ProcessDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();

        var due = await db.Deliveries
            .Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt).Take(20).ToListAsync(ct);

        foreach (var record in due)
        {
            try
            {
                await DeliverOneAsync(sp, db, record, ct);
                record.Status = DeliveryStatus.Succeeded;
                record.CompletedAt = clock.GetUtcNow();
                record.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                record.Attempts++;
                record.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                record.NextAttemptAt = DeliveryRetryPolicy.NextAttempt(record.Attempts, clock.GetUtcNow());
                if (record.NextAttemptAt is null) record.Status = DeliveryStatus.Failed;
                logger.LogWarning(ex, "Livraison {Id} en échec (tentative {Attempts}).", record.Id, record.Attempts);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task DeliverOneAsync(IServiceProvider sp, AppDbContext db, DeliveryRecord record, CancellationToken ct)
    {
        var dossier = await db.Dossiers.WithDetails().FirstAsync(d => d.Id == record.DossierId, ct);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == dossier.TenantId, ct);
        var channel = await db.DeliveryChannels.FirstOrDefaultAsync(c => c.Id == record.ChannelId, ct)
                      ?? throw new InvalidOperationException("Canal de livraison supprimé.");
        var handler = sp.GetServices<IDeliveryHandler>().FirstOrDefault(h => h.Type == channel.Type)
                      ?? throw new InvalidOperationException($"Aucun gestionnaire pour {channel.Type}.");
        var ingestion = sp.GetRequiredService<DocumentIngestionService>();
        var protector = sp.GetRequiredService<ISecretProtector>();
        var app = sp.GetRequiredService<IOptions<AppOptions>>().Value;

        var ids = record.DocumentIds.ToHashSet();
        var docs = dossier.Items
            .SelectMany(i => i.Documents.Where(d => ids.Contains(d.Id)).Select(d => new DeliveryDocument(d, i.Label)))
            .ToList();
        if (docs.Count == 0) return; // documents retirés entre-temps

        await handler.DeliverAsync(new DeliveryContext
        {
            Tenant = tenant,
            Dossier = dossier,
            Channel = channel,
            Settings = ChannelSettings.Read(channel, protector),
            Documents = docs,
            OpenDocument = ingestion.OpenAsync,
            PublicBaseUrl = app.PublicBaseUrl,
            DossierComplete = dossier.IsComplete
        }, ct);
    }
}

/// <summary>Paramètres de canal : les champs nommés « Secret » ou « Password » sont chiffrés en base.</summary>
public static class ChannelSettings
{
    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase) { "Secret", "Password" };
    public const string Mask = "••••••";

    public static StorageSettings Read(DeliveryChannel channel, ISecretProtector protector) =>
        new(StorageSettings.Deserialize(channel.SettingsJson).Values.ToDictionary(kv => kv.Key,
            kv => kv.Value is not null && SecretNames.Contains(kv.Key) ? protector.Unprotect(kv.Value) : kv.Value));

    public static string Prepare(IDictionary<string, string?> incoming, DeliveryChannel? existing, ISecretProtector protector)
    {
        var previous = existing is null ? new StorageSettings() : StorageSettings.Deserialize(existing.SettingsJson);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in incoming)
        {
            var value = v;
            if (SecretNames.Contains(k))
                value = value == Mask ? previous.Get(k) : string.IsNullOrEmpty(value) ? null : protector.Protect(value);
            if (!string.IsNullOrWhiteSpace(value)) result[k] = value;
        }
        return new StorageSettings(result).Serialize();
    }

    public static IReadOnlyDictionary<string, string?> Masked(DeliveryChannel channel) =>
        StorageSettings.Deserialize(channel.SettingsJson).Values.ToDictionary(kv => kv.Key,
            kv => SecretNames.Contains(kv.Key) && !string.IsNullOrEmpty(kv.Value) ? Mask : kv.Value);
}

/// <summary>Relance périodique des fournisseurs pour les pièces manquantes.</summary>
public sealed class ReminderWorker(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    IOptions<AppOptions> options,
    ILogger<ReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Erreur du worker de relance."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var o = options.Value;
        var policy = new ReminderPolicy { IntervalDays = o.ReminderIntervalDays, MaxReminders = o.MaxReminders };
        var now = clock.GetUtcNow();

        var open = await db.Dossiers.WithDetails().Where(d => d.Status == DossierStatus.Open).ToListAsync(ct);
        var tenantNames = await db.Tenants.ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        foreach (var dossier in open.Where(d => policy.ShouldRemind(d, d.LastReminderAt, d.RemindersSent, now)))
        {
            var missing = dossier.MissingRequiredItems().Select(i => i.Label);
            var link = $"{o.PublicBaseUrl.TrimEnd('/')}/provider/dossiers/{dossier.Id}";
            await email.SendAsync(EmailTemplates.Reminder(dossier.ProviderEmail, tenantNames.GetValueOrDefault(dossier.TenantId, ""), dossier, missing, link), ct);
            dossier.LastReminderAt = now;
            dossier.RemindersSent++;
            db.AuditEvents.Add(new AuditEvent { TenantId = dossier.TenantId, DossierId = dossier.Id, Action = "reminder.sent" });
            await db.SaveChangesAsync(ct);
        }
    }
}
