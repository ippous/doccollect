using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocCollect.Shared;

namespace DocCollect.Api.Tests;

public class EndToEndTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public EndToEndTests(ApiFactory factory) => _f = factory;

    private static byte[] Pdf(string marker)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes($"%PDF-1.7\n% {marker}\n" + new string('x', 2000));
        return bytes;
    }

    private static MultipartFormDataContent File(byte[] content, string name, string contentType = "application/pdf")
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, "file", name);
        return form;
    }

    private async Task<HttpClient> AdminAsync()
    {
        if (_f.AdminTotpKey is not null)
            return await _f.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword, _f.AdminTotpKey);
        var restricted = await _f.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var (client, key) = await _f.EnrollAuthenticatorAsync(restricted);
        _f.AdminTotpKey = key;
        return client;
    }

    private async Task<HttpClient> NewTenantRequesterAsync(HttpClient admin, string slug)
    {
        var owner = $"owner@{slug}.test";
        (await admin.PostAsJsonAsync("/api/admin/tenants", new CreateTenantRequest($"Étude {slug}", slug, owner, null))).EnsureSuccessStatusCode();
        var restricted = await _f.AcceptInvitationAsync(owner, "Requester-Passw0rd!");
        return await _f.EnrollEmailAsync(restricted, owner);
    }

    [Fact]
    public async Task Without_two_factor_only_enrollment_endpoints_are_reachable()
    {
        var admin = await AdminAsync();
        const string owner = "owner@etude-sans2fa.test";
        (await admin.PostAsJsonAsync("/api/admin/tenants", new CreateTenantRequest("Étude sans 2FA", "etude-sans2fa", owner, null))).EnsureSuccessStatusCode();
        var restricted = await _f.AcceptInvitationAsync(owner, "Requester-Passw0rd!");

        var me = await restricted.GetFromJsonAsync<MeResponse>("/api/auth/me");
        Assert.False(me!.TwoFactorEnabled);
        Assert.Equal(HttpStatusCode.Forbidden, (await restricted.GetAsync("/api/requester/dossiers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await restricted.PostAsync("/api/auth/2fa/recovery-codes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await restricted.PostAsync("/api/auth/2fa/authenticator/setup", null)).StatusCode);
    }

    [Fact]
    public async Task Login_requires_second_factor_once_enabled()
    {
        var admin = await AdminAsync();
        const string owner = "owner@etude-login.test";
        (await admin.PostAsJsonAsync("/api/admin/tenants", new CreateTenantRequest("Étude login", "etude-login", owner, null))).EnsureSuccessStatusCode();
        var (_, key) = await _f.EnrollAuthenticatorAsync(await _f.AcceptInvitationAsync(owner, "Requester-Passw0rd!"));

        var anon = _f.CreateClient();
        var first = await (await anon.PostAsJsonAsync("/api/auth/login", new LoginRequest(owner, "Requester-Passw0rd!")))
            .Content.ReadFromJsonAsync<LoginResponse>();
        Assert.True(first!.RequiresTwoFactor);
        Assert.Equal(TwoFactorMethod.Authenticator, first.Method);
        Assert.Null(first.AccessToken);

        var wrong = await anon.PostAsJsonAsync("/api/auth/login", new LoginRequest(owner, "Requester-Passw0rd!", "000000"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var ok = await _f.LoginAsync(owner, "Requester-Passw0rd!", key);
        Assert.Equal(HttpStatusCode.OK, (await ok.GetAsync("/api/requester/dossiers")).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_calls_are_rejected()
    {
        var anon = _f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/requester/dossiers")).StatusCode);
        var bad = await anon.PostAsJsonAsync("/api/auth/login", new LoginRequest("inconnu@x.test", "mauvais"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    [Fact]
    public async Task Full_collection_flow_from_request_to_delivery()
    {
        var admin = await AdminAsync();
        var requester = await NewTenantRequesterAsync(admin, "etude-flux");

        // Le demandeur crée une demande à partir d'un modèle public.
        var templates = await requester.GetFromJsonAsync<List<TemplateDto>>("/api/requester/templates");
        var template = templates!.First(t => t.Name.StartsWith("Dossier de location"));
        var create = await requester.PostAsJsonAsync("/api/requester/dossiers", new CreateDossierRequest(
            "Location Durand", "LOC-001", template.Id, null, "Locataire@Example.test", "M. Durand",
            DeliveryMode.AllAtOnce, false, DateOnly.FromDateTime(DateTime.Today.AddDays(10)), "Merci de déposer vos pièces."));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var dossier = await create.Content.ReadFromJsonAsync<DossierDto>();
        Assert.Equal(DossierStatus.Open, dossier!.Status);
        Assert.Equal("locataire@example.test", dossier.ProviderEmail);
        Assert.Equal(4, dossier.MissingRequiredItems.Count);

        // Le fournisseur reçoit l'invitation, crée son accès, active la 2FA.
        var provider = await _f.EnrollEmailAsync(await _f.AcceptInvitationAsync("locataire@example.test", "Provider-Passw0rd!"), "locataire@example.test");
        var mine = await provider.GetFromJsonAsync<DossierDto>($"/api/provider/dossiers/{dossier.Id}");
        Assert.Equal("Étude etude-flux", mine!.TenantName);

        // Dépôts : un fichier par pièce obligatoire.
        foreach (var item in mine.Items.Where(i => i.Required))
        {
            var resp = await provider.PostAsync($"/api/provider/dossiers/{dossier.Id}/items/{item.Id}/documents", File(Pdf(item.Label), $"{item.Label}.pdf"));
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        }

        var after = await requester.GetFromJsonAsync<DossierDto>($"/api/requester/dossiers/{dossier.Id}");
        Assert.Empty(after!.MissingRequiredItems);
        Assert.Equal(DossierStatus.Delivered, after.Status); // aucun canal : disponible dans l'espace demandeur
        Assert.All(after.Items.Where(i => i.Required).SelectMany(i => i.Documents), d => Assert.True(d.Encrypted));

        // Téléchargement : le contenu déchiffré est identique au fichier déposé.
        var firstItem = after.Items.First(i => i.Required);
        var doc = firstItem.Documents.Single();
        var content = await requester.GetByteArrayAsync($"/api/requester/dossiers/{dossier.Id}/documents/{doc.Id}/content");
        Assert.Equal(Pdf(firstItem.Label), content);

        // Archive zip de tout le dossier.
        var zipBytes = await requester.GetByteArrayAsync($"/api/requester/dossiers/{dossier.Id}/zip");
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        Assert.Equal(after.Items.Sum(i => i.Documents.Count), zip.Entries.Count);
    }

    [Fact]
    public async Task Rejected_document_is_removed_and_provider_notified()
    {
        var admin = await AdminAsync();
        var requester = await NewTenantRequesterAsync(admin, "etude-refus");
        var create = await requester.PostAsJsonAsync("/api/requester/dossiers", new CreateDossierRequest(
            "Pièce unique", null, null, [new TemplateItemDto(null, "RIB", null, true, ["application/pdf"], 5, 1, null, 0)],
            "client-refus@example.test", null, DeliveryMode.Incremental, true, null, null));
        var dossier = await create.Content.ReadFromJsonAsync<DossierDto>();
        var provider = await _f.EnrollEmailAsync(await _f.AcceptInvitationAsync("client-refus@example.test", "Provider-Passw0rd!"), "client-refus@example.test");
        var itemId = dossier!.Items[0].Id;

        (await provider.PostAsync($"/api/provider/dossiers/{dossier.Id}/items/{itemId}/documents", File(Pdf("rib"), "rib.pdf"))).EnsureSuccessStatusCode();
        var submitted = await requester.GetFromJsonAsync<DossierDto>($"/api/requester/dossiers/{dossier.Id}");
        Assert.Equal(ItemStatus.Submitted, submitted!.Items[0].Status);
        Assert.Equal(DossierStatus.Open, submitted.Status); // validation requise : pas encore complet

        (await requester.PostAsJsonAsync($"/api/requester/dossiers/{dossier.Id}/items/{itemId}/reject", new RejectItemRequest("Illisible"))).EnsureSuccessStatusCode();
        var rejected = await provider.GetFromJsonAsync<DossierDto>($"/api/provider/dossiers/{dossier.Id}");
        Assert.Equal(ItemStatus.Rejected, rejected!.Items[0].Status);
        Assert.Empty(rejected.Items[0].Documents);
        Assert.Contains(_f.Emails.Sent, m => m.To.Contains("client-refus@example.test") && m.Subject.StartsWith("Document à remplacer"));

        (await provider.PostAsync($"/api/provider/dossiers/{dossier.Id}/items/{itemId}/documents", File(Pdf("rib2"), "rib.pdf"))).EnsureSuccessStatusCode();
        (await requester.PostAsync($"/api/requester/dossiers/{dossier.Id}/items/{itemId}/accept", null)).EnsureSuccessStatusCode();
        var done = await requester.GetFromJsonAsync<DossierDto>($"/api/requester/dossiers/{dossier.Id}");
        Assert.Equal(DossierStatus.Delivered, done!.Status);
    }

    [Fact]
    public async Task Tenants_are_isolated_from_each_other()
    {
        var admin = await AdminAsync();
        var a = await NewTenantRequesterAsync(admin, "etude-a");
        var b = await NewTenantRequesterAsync(admin, "etude-b");
        var created = await (await a.PostAsJsonAsync("/api/requester/dossiers", new CreateDossierRequest(
            "Confidentiel", null, null, [new TemplateItemDto(null, "Pièce", null, true, [], 5, 1, null, 0)],
            "x@example.test", null, DeliveryMode.AllAtOnce, false, null, null))).Content.ReadFromJsonAsync<DossierDto>();

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/requester/dossiers/{created!.Id}")).StatusCode);
        var listB = await b.GetFromJsonAsync<List<DossierSummaryDto>>("/api/requester/dossiers");
        Assert.DoesNotContain(listB!, d => d.Id == created.Id);
        // Un demandeur n'a pas accès à l'administration.
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync("/api/admin/tenants")).StatusCode);
    }

    [Fact]
    public async Task Disguised_executable_is_refused()
    {
        var admin = await AdminAsync();
        var requester = await NewTenantRequesterAsync(admin, "etude-exe");
        var dossier = await (await requester.PostAsJsonAsync("/api/requester/dossiers", new CreateDossierRequest(
            "Test", null, null, [new TemplateItemDto(null, "Photo", null, true, ["image/*"], 5, 1, null, 0)],
            "exe@example.test", null, DeliveryMode.AllAtOnce, false, null, null))).Content.ReadFromJsonAsync<DossierDto>();
        var provider = await _f.EnrollEmailAsync(await _f.AcceptInvitationAsync("exe@example.test", "Provider-Passw0rd!"), "exe@example.test");

        byte[] exe = [(byte)'M', (byte)'Z', .. new byte[500]];
        var resp = await provider.PostAsync($"/api/provider/dossiers/{dossier!.Id}/items/{dossier.Items[0].Id}/documents", File(exe, "photo.jpg", "image/jpeg"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = await resp.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("content_type_not_allowed", error!.Code);
    }

    [Fact]
    public async Task Provider_cannot_see_dossiers_of_someone_else()
    {
        var admin = await AdminAsync();
        var requester = await NewTenantRequesterAsync(admin, "etude-prov");
        var d1 = await (await requester.PostAsJsonAsync("/api/requester/dossiers", new CreateDossierRequest(
            "Pour P1", null, null, [new TemplateItemDto(null, "Pièce", null, true, [], 5, 1, null, 0)],
            "p1@example.test", null, DeliveryMode.AllAtOnce, false, null, null))).Content.ReadFromJsonAsync<DossierDto>();
        await requester.PostAsJsonAsync("/api/requester/dossiers", new CreateDossierRequest(
            "Pour P2", null, null, [new TemplateItemDto(null, "Pièce", null, true, [], 5, 1, null, 0)],
            "p2@example.test", null, DeliveryMode.AllAtOnce, false, null, null));

        var p2 = await _f.EnrollEmailAsync(await _f.AcceptInvitationAsync("p2@example.test", "Provider-Passw0rd!"), "p2@example.test");
        Assert.Equal(HttpStatusCode.NotFound, (await p2.GetAsync($"/api/provider/dossiers/{d1!.Id}")).StatusCode);
    }

    [Fact]
    public async Task Embed_page_is_frameable_only_by_declared_origins()
    {
        var admin = await AdminAsync();
        var requester = await NewTenantRequesterAsync(admin, "etude-embed");
        (await requester.PutAsJsonAsync("/api/requester/tenant/settings", new UpdateTenantSettingsRequest(
            null, true, false, null, ["https://www.etude-embed.fr"]))).EnsureSuccessStatusCode();

        var anon = _f.CreateClient();
        var embed = await anon.GetAsync("/embed/etude-embed");
        Assert.Contains("https://www.etude-embed.fr", embed.Headers.GetValues("Content-Security-Policy").Single());
        var normal = await anon.GetAsync("/login");
        Assert.Equal("frame-ancestors 'self'", normal.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Embed_origins_must_be_https()
    {
        var admin = await AdminAsync();
        var requester = await NewTenantRequesterAsync(admin, "etude-http");
        var resp = await requester.PutAsJsonAsync("/api/requester/tenant/settings", new UpdateTenantSettingsRequest(
            null, true, false, null, ["http://insecure.example"]));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Webhook_channel_secret_is_never_returned()
    {
        var admin = await AdminAsync();
        var requester = await NewTenantRequesterAsync(admin, "etude-hook");
        var resp = await requester.PostAsJsonAsync("/api/requester/channels", new SaveDeliveryChannelRequest(
            DeliveryChannelType.Webhook, "Mon logiciel", new() { ["Url"] = "https://hooks.example/doccollect", ["Secret"] = "tres-secret" }, true));
        resp.EnsureSuccessStatusCode();
        var channels = await requester.GetFromJsonAsync<List<DeliveryChannelDto>>("/api/requester/channels");
        Assert.Equal("••••••", channels!.Single().Settings["Secret"]);
    }

    [Fact]
    public async Task Storage_profile_can_be_deleted_only_when_empty_and_unused()
    {
        var admin = await AdminAsync();
        var requester = await NewTenantRequesterAsync(admin, "etude-stock");
        var root = Path.Combine(Path.GetTempPath(), "dc-stock-" + Guid.NewGuid().ToString("N"));

        async Task<StorageProfileDto> CreateAsync(string name) =>
            (await (await requester.PostAsJsonAsync("/api/requester/storage/profiles",
                new SaveStorageProfileRequest(name, "local", new() { ["RootPath"] = Path.Combine(root, name) }, false)))
                .Content.ReadFromJsonAsync<StorageProfileDto>())!;

        // 1. Vide et inutilisé : supprimable.
        var empty = await CreateAsync("vide");
        var listed = await requester.GetFromJsonAsync<List<StorageProfileDto>>("/api/requester/storage/profiles");
        Assert.Null(listed!.Single(p => p.Id == empty.Id).DeleteBlockedReason);
        Assert.Equal(HttpStatusCode.NoContent, (await requester.DeleteAsync($"/api/requester/storage/profiles/{empty.Id}")).StatusCode);

        // 2. Utilisé comme stockage du demandeur, puis contenant un document : refusé avec le motif.
        var used = await CreateAsync("utilise");
        (await requester.PutAsJsonAsync("/api/requester/tenant/settings", new UpdateTenantSettingsRequest(used.Id, true, false, null, []))).EnsureSuccessStatusCode();
        var refused = await requester.DeleteAsync($"/api/requester/storage/profiles/{used.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("stockage de", (await refused.Content.ReadFromJsonAsync<ApiError>())!.Message);

        var dossier = await (await requester.PostAsJsonAsync("/api/requester/dossiers", new CreateDossierRequest(
            "Stock", null, null, [new TemplateItemDto(null, "Pièce", null, true, [], 5, 1, null, 0)],
            "stock@example.test", null, DeliveryMode.AllAtOnce, false, null, null))).Content.ReadFromJsonAsync<DossierDto>();
        var provider = await _f.EnrollEmailAsync(await _f.AcceptInvitationAsync("stock@example.test", "Provider-Passw0rd!"), "stock@example.test");
        (await provider.PostAsync($"/api/provider/dossiers/{dossier!.Id}/items/{dossier.Items[0].Id}/documents", File(Pdf("stock"), "p.pdf"))).EnsureSuccessStatusCode();

        // Même après avoir rendu le stockage de la plateforme au demandeur, le document bloque la suppression.
        (await requester.PutAsJsonAsync("/api/requester/tenant/settings", new UpdateTenantSettingsRequest(null, true, false, null, []))).EnsureSuccessStatusCode();
        var withDoc = (await requester.GetFromJsonAsync<List<StorageProfileDto>>("/api/requester/storage/profiles"))!.Single(p => p.Id == used.Id);
        Assert.Equal(1, withDoc.DocumentCount);
        Assert.Contains("1 document", withDoc.DeleteBlockedReason);
        Assert.Equal(HttpStatusCode.BadRequest, (await requester.DeleteAsync($"/api/requester/storage/profiles/{used.Id}")).StatusCode);

        // 3. Cible d'un canal « copie vers mon stockage » : refusé.
        var target = await CreateAsync("copie");
        (await requester.PostAsJsonAsync("/api/requester/channels", new SaveDeliveryChannelRequest(
            DeliveryChannelType.StorageCopy, "Copie NAS", new() { ["StorageProfileId"] = target.Id.ToString() }, true))).EnsureSuccessStatusCode();
        var byChannel = await requester.DeleteAsync($"/api/requester/storage/profiles/{target.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, byChannel.StatusCode);
        Assert.Contains("Copie NAS", (await byChannel.Content.ReadFromJsonAsync<ApiError>())!.Message);

        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public async Task Platform_default_storage_cannot_be_deleted_but_others_can()
    {
        var admin = await AdminAsync();
        var root = Path.Combine(Path.GetTempPath(), "dc-plat-" + Guid.NewGuid().ToString("N"));
        var def = await (await admin.PostAsJsonAsync("/api/admin/storage/profiles",
            new SaveStorageProfileRequest("Défaut", "local", new() { ["RootPath"] = Path.Combine(root, "a") }, true))).Content.ReadFromJsonAsync<StorageProfileDto>();
        var other = await (await admin.PostAsJsonAsync("/api/admin/storage/profiles",
            new SaveStorageProfileRequest("Autre", "local", new() { ["RootPath"] = Path.Combine(root, "b") }, false))).Content.ReadFromJsonAsync<StorageProfileDto>();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/admin/storage/profiles/{def!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/storage/profiles/{other!.Id}")).StatusCode);
        // Remise en état : un autre profil devient le défaut, l'ancien peut alors être supprimé s'il est vide.
        var replacement = await (await admin.PostAsJsonAsync("/api/admin/storage/profiles",
            new SaveStorageProfileRequest("Nouveau défaut", "local", new() { ["RootPath"] = Path.Combine(root, "c") }, true))).Content.ReadFromJsonAsync<StorageProfileDto>();
        var listed = await admin.GetFromJsonAsync<List<StorageProfileDto>>("/api/admin/storage/profiles");
        Assert.False(listed!.Single(p => p.Id == def.Id).IsDefault);
        var deleteOld = await admin.DeleteAsync($"/api/admin/storage/profiles/{def.Id}");
        // Les documents des autres tests peuvent être sur le stockage de secours, pas sur ce profil : il est vide.
        Assert.Equal(HttpStatusCode.NoContent, deleteOld.StatusCode);
        Assert.NotNull(replacement);
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public async Task Requester_can_check_and_test_email_sending()
    {
        var admin = await AdminAsync();
        var requester = await NewTenantRequesterAsync(admin, "etude-mail");
        var status = await requester.GetFromJsonAsync<EmailStatusDto>("/api/requester/email/status");
        Assert.NotNull(status);
        Assert.Null(status!.LastFailureDetail); // jamais de détail technique pour un demandeur

        var test = await (await requester.PostAsync("/api/requester/email/test", null)).Content.ReadFromJsonAsync<EmailTestResult>();
        Assert.True(test!.Succeeded);
        Assert.Contains(_f.Emails.Sent, m => m.Subject == "Test d'envoi DocCollect" && m.To.Contains("owner@etude-mail.test"));
    }

    [Fact]
    public async Task Admin_can_read_logs_but_requester_cannot()
    {
        var admin = await AdminAsync();
        var logs = await admin.GetFromJsonAsync<List<LogEntryDto>>("/api/admin/logs?level=Information&take=50");
        Assert.NotNull(logs);
        Assert.NotEmpty(logs!); // au minimum les journaux de démarrage
        var requester = await NewTenantRequesterAsync(admin, "etude-logs");
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/admin/logs")).StatusCode);
    }
}
