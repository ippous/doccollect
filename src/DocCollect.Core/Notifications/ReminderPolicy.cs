using DocCollect.Domain;
using DocCollect.Shared;

namespace DocCollect.Core.Notifications;

/// <summary>
/// Quand relancer le fournisseur : dossier ouvert avec des pièces manquantes,
/// au plus une relance tous les <see cref="IntervalDays"/> jours, et à J-2 de l'échéance.
/// </summary>
public sealed class ReminderPolicy
{
    public int IntervalDays { get; init; } = 7;
    public int MaxReminders { get; init; } = 5;

    public bool ShouldRemind(Dossier dossier, DateTimeOffset? lastReminderAt, int remindersSent, DateTimeOffset now)
    {
        if (dossier.Status != DossierStatus.Open || dossier.IsComplete || remindersSent >= MaxReminders)
            return false;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var last = lastReminderAt ?? dossier.CreatedAt;
        if (dossier.DueDate is { } due && today >= due.AddDays(-2) && today <= due
            && DateOnly.FromDateTime(last.UtcDateTime) < due.AddDays(-2))
            return true;
        return now - last >= TimeSpan.FromDays(IntervalDays);
    }
}
