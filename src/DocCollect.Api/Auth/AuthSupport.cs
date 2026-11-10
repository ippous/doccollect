using System.Security.Claims;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DocCollect.Api.Auth;

public static class DcClaims
{
    /// <summary>Présent (= « true ») seulement si l'utilisateur a activé la double authentification.</summary>
    public const string TwoFactor = "dc:2fa";
    public const string Tenant = "dc:tenant";
}

public static class Policies
{
    /// <summary>Utilisateur connecté, même sans 2FA : réservé aux écrans d'activation de la 2FA.</summary>
    public const string Enrolling = "enrolling";
    public const string Admin = "admin";
    public const string Requester = "requester";
    public const string Provider = "provider";
}

/// <summary>Ajoute au jeton l'indicateur 2FA et le demandeur de rattachement.</summary>
public sealed class AppClaimsPrincipalFactory(
    UserManager<AppUser> users,
    RoleManager<IdentityRole> roles,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.TwoFactorEnabled) identity.AddClaim(new Claim(DcClaims.TwoFactor, "true"));
        if (user.TenantId is { } t) identity.AddClaim(new Claim(DcClaims.Tenant, t.ToString()));
        if (!string.IsNullOrEmpty(user.DisplayName)) identity.AddClaim(new Claim("name", user.DisplayName));
        return identity;
    }
}

public static class ClaimsPrincipalExtensions
{
    public static string UserId(this ClaimsPrincipal p) =>
        p.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();

    public static Guid TenantId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(DcClaims.Tenant), out var id) ? id : throw new UnauthorizedAccessException("Aucun demandeur rattaché.");

    public static string Email(this ClaimsPrincipal p) =>
        p.FindFirstValue(ClaimTypes.Email) ?? p.FindFirstValue(ClaimTypes.Name) ?? "";
}

/// <summary>Génère l'URI otpauth:// pour les applications d'authentification (Google Authenticator, Microsoft Authenticator…).</summary>
public static class AuthenticatorUri
{
    public static string Build(string issuer, string email, string key) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(email)}?secret={key}&issuer={Uri.EscapeDataString(issuer)}&digits=6";

    /// <summary>Clé affichée par groupes de 4 caractères, plus lisible pour une saisie manuelle.</summary>
    public static string Format(string key) =>
        string.Join(' ', Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4)))).ToLowerInvariant();
}
