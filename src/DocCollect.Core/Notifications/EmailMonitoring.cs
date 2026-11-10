namespace DocCollect.Core.Notifications;

/// <summary>Échec d'envoi d'un e-mail. <see cref="Category"/> est un libellé lisible sans détail technique.</summary>
public sealed class EmailSendException(string category, Exception inner)
    : Exception($"Envoi de l'e-mail impossible : {category}.", inner)
{
    public string Category { get; } = category;
}

public sealed record EmailHealthSnapshot(
    bool? Working,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastFailureCategory,
    string? LastFailureDetail,
    int SentCount,
    int FailedCount);

/// <summary>
/// État de l'envoi des e-mails depuis le démarrage (en mémoire).
/// « Fonctionne » = le dernier envoi a réussi ; null tant qu'aucun envoi n'a été tenté.
/// </summary>
public sealed class EmailHealth
{
    private readonly object _lock = new();
    private DateTimeOffset? _lastSuccess, _lastFailure;
    private string? _category, _detail;
    private int _sent, _failed;

    public void RecordSuccess(DateTimeOffset at)
    {
        lock (_lock) { _lastSuccess = at; _sent++; }
    }

    public void RecordFailure(DateTimeOffset at, string category, string detail)
    {
        lock (_lock)
        {
            _lastFailure = at;
            _category = category;
            _detail = detail.Length > 1000 ? detail[..1000] : detail;
            _failed++;
        }
    }

    public EmailHealthSnapshot Snapshot()
    {
        lock (_lock)
        {
            bool? working = (_lastSuccess, _lastFailure) switch
            {
                (null, null) => null,
                (not null, null) => true,
                (null, not null) => false,
                var (s, f) => s > f
            };
            return new EmailHealthSnapshot(working, _lastSuccess, _lastFailure, _category, _detail, _sent, _failed);
        }
    }
}

/// <summary>
/// Décorateur : enregistre le résultat de chaque envoi et convertit les erreurs en <see cref="EmailSendException"/>
/// (catégorie lisible fournie par <paramref name="classify"/>, propre au client SMTP utilisé).
/// </summary>
public sealed class MonitoredEmailSender(IEmailSender inner, EmailHealth health, TimeProvider clock, Func<Exception, string> classify) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        try
        {
            await inner.SendAsync(message, ct);
            health.RecordSuccess(clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not EmailSendException)
        {
            var category = classify(ex);
            health.RecordFailure(clock.GetUtcNow(), category, $"{ex.GetType().Name}: {ex.Message}");
            throw new EmailSendException(category, ex);
        }
    }
}
