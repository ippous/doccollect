using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DocCollect.Core.Notifications;
using DocCollect.Core.Security;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocCollect.Api.Tests;

/// <summary>
/// Hôte de test : base EF en mémoire, stockage disque temporaire, e-mails capturés, workers désactivés.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@doccollect.test";
    public const string AdminPassword = "Admin-Passw0rd!";
    public CapturingEmailSender Emails { get; } = new();
    /// <summary>Clé TOTP de l'admin une fois sa 2FA activée (partagée entre les tests de la classe).</summary>
    public string? AdminTotpKey { get; set; }
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "dc-api-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbName = "dc-" + Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("ConnectionStrings:Default", "Host=unused");
        builder.UseSetting("Workers:Enabled", "false");
        builder.UseSetting("App:PublicBaseUrl", "https://doccollect.test");
        builder.UseSetting("Storage:FallbackLocalPath", _storage);
        builder.UseSetting("Encryption:CurrentKeyId", "test1");
        builder.UseSetting("Encryption:Keys:test1", ConfiguredMasterKeyProvider.GenerateKey());
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        builder.UseSetting("Seed:DemoRequesterEmail", "");
        builder.UseSetting("Auth:RateLimitPerMinute", "100000");

        builder.ConfigureServices(services =>
        {
            // Remplace PostgreSQL par une base en mémoire (on retire aussi la configuration accumulée par AddDbContext).
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll(typeof(IDbContextOptionsConfiguration<AppDbContext>));
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_storage, true); } catch { /* ignoré */ }
    }

    // ---------- Aides de scénario ----------

    public async Task<HttpClient> LoginAsync(string email, string password, string? totpKey = null, bool emailCode = false)
    {
        var anon = CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        var login = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        if (login!.RequiresTwoFactor)
        {
            var code = totpKey is not null ? Totp.Compute(totpKey) : Emails.LastCodeFor(email);
            resp = await anon.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password, code));
            login = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        }
        Assert.True(login!.Succeeded, login.Error);
        return Authorized(login.AccessToken!);
    }

    public HttpClient Authorized(string token)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>Active la 2FA par application d'authentification et renvoie (client complet, clé TOTP).</summary>
    public async Task<(HttpClient Client, string Key)> EnrollAuthenticatorAsync(HttpClient restricted)
    {
        var setup = await (await restricted.PostAsync("/api/auth/2fa/authenticator/setup", null)).Content.ReadFromJsonAsync<AuthenticatorSetupResponse>();
        var key = setup!.SharedKey.Replace(" ", "").ToUpperInvariant();
        var resp = await restricted.PostAsJsonAsync("/api/auth/2fa/enable", new EnableTwoFactorRequest(TwoFactorMethod.Authenticator, Totp.Compute(key)));
        resp.EnsureSuccessStatusCode();
        var enabled = await resp.Content.ReadFromJsonAsync<EnableTwoFactorResponse>();
        Assert.Equal(10, enabled!.RecoveryCodes.Count);
        return (Authorized(enabled.Tokens!.AccessToken!), key);
    }

    public async Task<HttpClient> EnrollEmailAsync(HttpClient restricted, string email)
    {
        (await restricted.PostAsync("/api/auth/2fa/email/send-code", null)).EnsureSuccessStatusCode();
        var resp = await restricted.PostAsJsonAsync("/api/auth/2fa/enable", new EnableTwoFactorRequest(TwoFactorMethod.Email, Emails.LastCodeFor(email)));
        resp.EnsureSuccessStatusCode();
        var enabled = await resp.Content.ReadFromJsonAsync<EnableTwoFactorResponse>();
        return Authorized(enabled!.Tokens!.AccessToken!);
    }

    public async Task<HttpClient> AcceptInvitationAsync(string email, string password)
    {
        var token = Emails.LastInvitationTokenFor(email);
        var resp = await CreateClient().PostAsJsonAsync("/api/auth/invitations/accept", new AcceptInvitationRequest(token, password, null));
        resp.EnsureSuccessStatusCode();
        var login = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.True(login!.MustEnrollTwoFactor);
        return Authorized(login.AccessToken!);
    }
}

public sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];
    public IReadOnlyList<EmailMessage> Sent { get { lock (_sent) return _sent.ToList(); } }

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add(message);
        return Task.CompletedTask;
    }

    private EmailMessage Last(string to, Func<EmailMessage, bool> predicate) =>
        Sent.Last(m => m.To.Contains(to, StringComparer.OrdinalIgnoreCase) && predicate(m));

    public string LastCodeFor(string to) => CodeRegex().Match(Last(to, m => m.Subject.Contains("code")).HtmlBody).Groups[1].Value;

    public string LastInvitationTokenFor(string to) => InvitationRegex().Match(Last(to, m => m.TextBody.Contains("/invitation/")).TextBody).Groups[1].Value;

    [GeneratedRegex(@">(\d{6})<")] private static partial Regex CodeRegex();
    [GeneratedRegex(@"/invitation/([A-Za-z0-9_\-]+)")] private static partial Regex InvitationRegex();
}

/// <summary>TOTP RFC 6238 (SHA-1, 30 s, 6 chiffres) — comme l'application d'authentification de l'utilisateur.</summary>
public static class Totp
{
    public static string Compute(string base32Key, DateTimeOffset? at = null)
    {
        var key = Base32Decode(base32Key);
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var data = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(data);
        var hash = HMACSHA1.HashData(key, data);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. output];
    }
}
