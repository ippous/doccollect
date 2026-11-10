using System.Net;
using System.Text.Json;
using DocCollect.Core.Delivery;
using DocCollect.Core.Notifications;
using DocCollect.Core.Storage;
using DocCollect.Domain;
using DocCollect.Shared;

namespace DocCollect.Core.Tests;

public class DeliveryTests
{
    private static DeliveryContext Context(Dossier dossier, DeliveryChannelType type, Dictionary<string, string?> settings, bool complete = false)
    {
        var docs = dossier.Items.SelectMany(i => i.Documents.Select(d => new DeliveryDocument(d, i.Label))).ToList();
        return new DeliveryContext
        {
            Tenant = TestData.Tenant(),
            Dossier = dossier,
            Channel = new DeliveryChannel { Type = type, Name = "test", TenantId = TestData.TenantId },
            Settings = new StorageSettings(settings),
            Documents = docs,
            OpenDocument = (doc, output, ct) => output.WriteAsync(System.Text.Encoding.UTF8.GetBytes("contenu " + doc.FileName), ct).AsTask(),
            PublicBaseUrl = "https://doccollect.jaume-eric.com/",
            DossierComplete = complete
        };
    }

    private static Dossier DossierWithDocs()
    {
        var d = TestData.OpenDossier(DeliveryMode.Incremental);
        d.Reference = "2026-VTE-042";
        d.AddDocument(d.Items[0].Id, new StoredDocument { FileName = "cni.pdf", ContentType = "application/pdf", SizeBytes = 10, Sha256 = "aa", UploadedAt = TestData.Now }, TestData.Now);
        return d;
    }

    [Fact]
    public async Task Webhook_posts_signed_payload()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var d = DossierWithDocs();
        await new WebhookDeliveryHandler(new HttpClient(handler)).DeliverAsync(
            Context(d, DeliveryChannelType.Webhook, new() { ["Url"] = "https://client.example/hook", ["Secret"] = "s3cr3t", ["IncludeContent"] = "true" }));

        var (req, body) = handler.Requests.Single();
        var ts = req.Headers.GetValues("X-DocCollect-Timestamp").Single();
        var sig = req.Headers.GetValues("X-DocCollect-Signature").Single();
        Assert.Equal("sha256=" + WebhookDeliveryHandler.Sign("s3cr3t", ts, body), sig);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("documents.received", json.RootElement.GetProperty("event").GetString());
        var doc = json.RootElement.GetProperty("documents")[0];
        Assert.Equal("Pièce d'identité", doc.GetProperty("item").GetString());
        Assert.Equal("contenu cni.pdf", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(doc.GetProperty("contentBase64").GetString()!)));
        Assert.StartsWith("https://doccollect.jaume-eric.com/api/requester/dossiers/", doc.GetProperty("downloadUrl").GetString());
    }

    [Fact]
    public async Task Webhook_failure_throws_for_retry()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(() => new WebhookDeliveryHandler(new HttpClient(handler)).DeliverAsync(
            Context(DossierWithDocs(), DeliveryChannelType.Webhook, new() { ["Url"] = "https://x", ["Secret"] = "s" })));
    }

    [Fact]
    public async Task Storage_copy_writes_plain_files_with_template_path()
    {
        using var dir = new TempDir();
        var target = new LocalFileSystemStorage(dir.Path);
        var targetId = Guid.NewGuid();
        var resolver = new FixedStorageResolver(new LocalFileSystemStorage(Path.Combine(dir.Path, "_platform")));
        resolver.Others[targetId] = target;

        await new StorageCopyDeliveryHandler(resolver).DeliverAsync(
            Context(DossierWithDocs(), DeliveryChannelType.StorageCopy, new() { ["StorageProfileId"] = targetId.ToString() }));

        var file = Path.Combine(dir.Path, "2026-VTE-042", "Pièce d'identité", "20261110-090000_cni.pdf");
        Assert.Equal("contenu cni.pdf", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public void Storage_copy_path_cannot_escape()
    {
        var d = DossierWithDocs();
        d.Reference = "../../etc";
        var path = StorageCopyDeliveryHandler.BuildPath("{reference}/{file}", d, new DeliveryDocument(d.Items[0].Documents[0], "x"));
        Assert.DoesNotContain("..", path.Split('/'));
    }

    [Fact]
    public async Task Email_notification_lists_documents_and_link()
    {
        var sender = new CapturingEmailSender();
        var d = DossierWithDocs();
        await new EmailNotificationDeliveryHandler(sender).DeliverAsync(
            Context(d, DeliveryChannelType.EmailNotification, new() { ["To"] = "notaire@etude.fr; clerc@etude.fr" }, complete: true));
        var m = sender.Sent.Single();
        Assert.Equal(["notaire@etude.fr", "clerc@etude.fr"], m.To);
        Assert.StartsWith("Dossier complet", m.Subject);
        Assert.Contains($"/requester/dossiers/{d.Id}", m.TextBody);
        Assert.Null(m.Attachments);
    }

    [Fact]
    public async Task Email_notification_can_attach_documents()
    {
        var sender = new CapturingEmailSender();
        await new EmailNotificationDeliveryHandler(sender).DeliverAsync(
            Context(DossierWithDocs(), DeliveryChannelType.EmailNotification, new() { ["To"] = "n@e.fr", ["AttachDocuments"] = "true" }));
        Assert.Single(sender.Sent.Single().Attachments!);
    }

    [Fact]
    public void Retry_policy_backs_off_then_gives_up()
    {
        Assert.Equal(TestData.Now.AddMinutes(1), DeliveryRetryPolicy.NextAttempt(1, TestData.Now));
        Assert.Equal(TestData.Now.AddHours(12), DeliveryRetryPolicy.NextAttempt(5, TestData.Now));
        Assert.Null(DeliveryRetryPolicy.NextAttempt(6, TestData.Now));
    }

    [Fact]
    public void Email_templates_encode_html()
    {
        var d = TestData.OpenDossier();
        d.Title = "<script>alert(1)</script>";
        var m = EmailTemplates.ProviderInvitation("a@b.fr", "Étude", d, "https://x/invite");
        Assert.DoesNotContain("<script>", m.HtmlBody);
        Assert.Contains("https://x/invite", m.TextBody);
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) { Sent.Add(message); return Task.CompletedTask; }
    }
}

public class ReminderPolicyTests
{
    private readonly ReminderPolicy _policy = new() { IntervalDays = 7, MaxReminders = 3 };

    [Fact]
    public void No_reminder_before_interval()
    {
        var d = TestData.OpenDossier();
        d.CreatedAt = TestData.Now.AddDays(-3);
        Assert.False(_policy.ShouldRemind(d, null, 0, TestData.Now));
    }

    [Fact]
    public void Reminder_after_interval()
    {
        var d = TestData.OpenDossier();
        d.CreatedAt = TestData.Now.AddDays(-8);
        Assert.True(_policy.ShouldRemind(d, null, 0, TestData.Now));
    }

    [Fact]
    public void Reminder_two_days_before_due_date()
    {
        var d = TestData.OpenDossier();
        d.CreatedAt = TestData.Now.AddDays(-1);
        d.DueDate = DateOnly.FromDateTime(TestData.Now.UtcDateTime).AddDays(2);
        Assert.True(_policy.ShouldRemind(d, d.CreatedAt, 0, TestData.Now));
        Assert.False(_policy.ShouldRemind(d, TestData.Now.AddHours(-1), 1, TestData.Now));
    }

    [Fact]
    public void No_reminder_when_complete_or_limit_reached()
    {
        var d = TestData.OpenDossier();
        d.CreatedAt = TestData.Now.AddDays(-30);
        Assert.False(_policy.ShouldRemind(d, null, 3, TestData.Now));
        d.AddDocument(d.Items[0].Id, TestData.Doc(), TestData.Now);
        d.AddDocument(d.Items[1].Id, TestData.Doc(), TestData.Now);
        Assert.False(_policy.ShouldRemind(d, null, 0, TestData.Now));
    }
}
