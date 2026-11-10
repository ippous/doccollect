using DocCollect.Core.Diagnostics;
using DocCollect.Core.Notifications;

namespace DocCollect.Core.Tests;

public class EmailMonitoringTests
{
    private static EmailMessage Msg() => new(["a@b.fr"], "Sujet", "<p>x</p>", "x");

    private sealed class FlakySender : IEmailSender
    {
        public bool Fail { get; set; }
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) =>
            Fail ? throw new InvalidOperationException("535 auth refused") : Task.CompletedTask;
    }

    [Fact]
    public void Status_is_unknown_before_any_send()
    {
        Assert.Null(new EmailHealth().Snapshot().Working);
    }

    [Fact]
    public async Task Success_then_failure_then_success_is_tracked()
    {
        var health = new EmailHealth();
        var inner = new FlakySender();
        var sender = new MonitoredEmailSender(inner, health, TimeProvider.System, _ => "authentification refusée");

        await sender.SendAsync(Msg());
        Assert.True(health.Snapshot().Working);

        inner.Fail = true;
        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Msg()));
        Assert.Equal("authentification refusée", ex.Category);
        var failed = health.Snapshot();
        Assert.False(failed.Working);
        Assert.Equal("authentification refusée", failed.LastFailureCategory);
        Assert.Contains("535", failed.LastFailureDetail);
        Assert.Equal(1, failed.SentCount);
        Assert.Equal(1, failed.FailedCount);

        inner.Fail = false;
        await sender.SendAsync(Msg());
        Assert.True(health.Snapshot().Working);
    }
}

public class LogStoreTests
{
    [Fact]
    public void Query_returns_newest_first_with_filters()
    {
        var store = new InMemoryLogStore(100);
        store.Add(TestData.Now, LogSeverity.Information, "App", "démarrage");
        store.Add(TestData.Now, LogSeverity.Warning, "DocCollect.Smtp", "envoi lent");
        store.Add(TestData.Now, LogSeverity.Error, "App", "échec SMTP", "System.Exception: boom");

        var all = store.Query();
        Assert.Equal(["échec SMTP", "envoi lent", "démarrage"], all.Select(r => r.Message).ToList());
        Assert.Equal(2, store.Query(LogSeverity.Warning).Count);
        Assert.Equal(2, store.Query(search: "smtp").Count); // message ou catégorie
        Assert.Single(store.Query(search: "boom"));     // texte de l'exception
    }

    [Fact]
    public void Ring_buffer_keeps_the_latest_entries()
    {
        var store = new InMemoryLogStore(10);
        for (var i = 1; i <= 25; i++) store.Add(TestData.Now, LogSeverity.Information, "App", $"m{i}");
        var all = store.Query(take: 100);
        Assert.Equal(10, all.Count);
        Assert.Equal("m25", all[0].Message);
        Assert.Equal("m16", all[^1].Message);
    }

    [Fact]
    public void After_id_returns_only_newer_entries()
    {
        var store = new InMemoryLogStore(50);
        store.Add(TestData.Now, LogSeverity.Information, "App", "a");
        var lastSeen = store.Query().Single().Id;
        store.Add(TestData.Now, LogSeverity.Information, "App", "b");
        store.Add(TestData.Now, LogSeverity.Information, "App", "c");
        Assert.Equal(["c", "b"], store.Query(afterId: lastSeen).Select(r => r.Message).ToList());
    }
}
