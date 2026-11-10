using System.Text;
using DocCollect.Core.Notifications;
using DocCollect.Core.Security;
using DocCollect.Domain;
using DocCollect.Infrastructure.Delivery;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocCollect.Api.Auth;

/// <summary>
/// Connexion, double authentification (obligatoire pour tous), invitations et mot de passe oublié.
/// </summary>
public static class AuthEndpoints
{
    private const string Issuer = "DocCollect";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting("auth");
        g.MapPost("/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting("auth");
        g.MapGet("/me", MeAsync).RequireAuthorization(Policies.Enrolling);

        g.MapPost("/2fa/authenticator/setup", SetupAuthenticatorAsync).RequireAuthorization(Policies.Enrolling);
        g.MapPost("/2fa/email/send-code", SendEmailCodeAsync).RequireAuthorization(Policies.Enrolling).RequireRateLimiting("auth");
        g.MapPost("/2fa/enable", EnableTwoFactorAsync).RequireAuthorization(Policies.Enrolling);
        g.MapPost("/2fa/recovery-codes", async (HttpContext http, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(http.User) ?? throw new UnauthorizedAccessException();
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            return Results.Ok(codes);
        });

        g.MapGet("/invitations/{token}", GetInvitationAsync).AllowAnonymous();
        g.MapPost("/invitations/accept", AcceptInvitationAsync).AllowAnonymous().RequireRateLimiting("auth");
        g.MapPost("/forgot-password", ForgotPasswordAsync).AllowAnonymous().RequireRateLimiting("auth");
        g.MapPost("/reset-password", ResetPasswordAsync).AllowAnonymous().RequireRateLimiting("auth");
    }

    private static IResult Fail(string message, int status = StatusCodes.Status401Unauthorized) =>
        Results.Json(new LoginResponse(false, Error: message), statusCode: status);

    // ---------- Connexion ----------

    internal static async Task<IResult> LoginAsync(
        LoginRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn, TokenIssuer tokens, IEmailSender email)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is null) return Fail("Identifiants invalides.");

        var check = await signIn.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
        if (check.IsLockedOut) return Fail("Compte temporairement verrouillé après plusieurs échecs. Réessayez plus tard.");
        if (check.IsNotAllowed) return Fail("Compte non activé.");
        if (!check.Succeeded) return Fail("Identifiants invalides.");

        // Pas encore de 2FA : jeton restreint, l'interface oblige à l'activer avant tout autre accès.
        if (!user.TwoFactorEnabled)
            return Results.Ok(await tokens.IssueAsync(user));

        var method = user.PreferredTwoFactorMethod == TwoFactorMethod.Email ? TwoFactorMethod.Email : TwoFactorMethod.Authenticator;

        if (!string.IsNullOrWhiteSpace(req.RecoveryCode))
        {
            var redeemed = await users.RedeemTwoFactorRecoveryCodeAsync(user, req.RecoveryCode.Replace(" ", ""));
            if (!redeemed.Succeeded)
            {
                await users.AccessFailedAsync(user);
                return Fail("Code de récupération invalide.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(req.TwoFactorCode))
        {
            var provider = method == TwoFactorMethod.Email ? TokenOptions.DefaultEmailProvider : TokenOptions.DefaultAuthenticatorProvider;
            if (!await users.VerifyTwoFactorTokenAsync(user, provider, req.TwoFactorCode.Replace(" ", "").Replace("-", "")))
            {
                await users.AccessFailedAsync(user);
                return Fail("Code de vérification invalide.");
            }
        }
        else
        {
            if (method == TwoFactorMethod.Email)
            {
                var code = await users.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider);
                await email.SendAsync(EmailTemplates.TwoFactorCode(user.Email!, code));
            }
            return Results.Ok(new LoginResponse(false, RequiresTwoFactor: true, Method: method));
        }

        await users.ResetAccessFailedCountAsync(user);
        return Results.Ok(await tokens.IssueAsync(user));
    }

    private static async Task<IResult> RefreshAsync(RefreshRequest req, TokenIssuer tokens)
    {
        var user = await tokens.ValidateRefreshAsync(req.RefreshToken);
        return user is null ? Fail("Session expirée.") : Results.Ok(await tokens.IssueAsync(user));
    }

    private static async Task<IResult> MeAsync(HttpContext http, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(http.User);
        if (user is null) return Results.Unauthorized();
        var roles = await users.GetRolesAsync(user);
        return Results.Ok(new MeResponse(user.Id, user.Email!, user.DisplayName, roles.ToList(), user.TenantId,
            user.TwoFactorEnabled, user.PreferredTwoFactorMethod));
    }

    // ---------- Activation de la 2FA ----------

    private static async Task<IResult> SetupAuthenticatorAsync(HttpContext http, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(http.User) ?? throw new UnauthorizedAccessException();
        // Nouvelle clé à chaque configuration : l'ancienne application d'authentification cesse de fonctionner une fois la nouvelle validée.
        if (!user.TwoFactorEnabled || user.PreferredTwoFactorMethod != TwoFactorMethod.Authenticator
            || string.IsNullOrEmpty(await users.GetAuthenticatorKeyAsync(user)))
            await users.ResetAuthenticatorKeyAsync(user);
        var key = await users.GetAuthenticatorKeyAsync(user) ?? throw new InvalidOperationException();
        return Results.Ok(new AuthenticatorSetupResponse(AuthenticatorUri.Format(key), AuthenticatorUri.Build(Issuer, user.Email!, key)));
    }

    private static async Task<IResult> SendEmailCodeAsync(HttpContext http, UserManager<AppUser> users, IEmailSender email)
    {
        var user = await users.GetUserAsync(http.User) ?? throw new UnauthorizedAccessException();
        var code = await users.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider);
        await email.SendAsync(EmailTemplates.TwoFactorCode(user.Email!, code));
        return Results.NoContent();
    }

    internal static async Task<IResult> EnableTwoFactorAsync(
        EnableTwoFactorRequest req, HttpContext http, UserManager<AppUser> users, TokenIssuer tokens, AppDbContext db)
    {
        var user = await users.GetUserAsync(http.User) ?? throw new UnauthorizedAccessException();
        var provider = req.Method switch
        {
            TwoFactorMethod.Authenticator => TokenOptions.DefaultAuthenticatorProvider,
            TwoFactorMethod.Email => TokenOptions.DefaultEmailProvider,
            _ => null
        };
        if (provider is null)
            return Results.BadRequest(new EnableTwoFactorResponse(false, [], "Méthode de double authentification invalide."));
        if (!await users.VerifyTwoFactorTokenAsync(user, provider, req.Code.Replace(" ", "").Replace("-", "")))
            return Results.BadRequest(new EnableTwoFactorResponse(false, [], "Code invalide ou expiré."));

        user.PreferredTwoFactorMethod = req.Method;
        await users.UpdateAsync(user);
        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToList() ?? [];
        db.AuditEvents.Add(new AuditEvent { TenantId = user.TenantId, UserId = user.Id, Action = "2fa.enabled", Details = req.Method.ToString() });
        await db.SaveChangesAsync();
        return Results.Ok(new EnableTwoFactorResponse(true, codes, Tokens: await tokens.IssueAsync(user)));
    }

    // ---------- Invitations ----------

    private static async Task<Invitation?> FindInvitationAsync(AppDbContext db, string token)
    {
        var hash = Hashing.Sha256Hex(token);
        return await db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash);
    }

    private static async Task<IResult> GetInvitationAsync(string token, AppDbContext db, TimeProvider time)
    {
        var inv = await FindInvitationAsync(db, token);
        if (inv is null) return Results.NotFound(new ApiError("invitation_not_found", "Invitation introuvable."));
        var tenantName = inv.TenantId is { } t ? await db.Tenants.Where(x => x.Id == t).Select(x => x.Name).FirstOrDefaultAsync() : null;
        if (tenantName is null && inv.DossierId is { } d)
            tenantName = await db.Dossiers.Where(x => x.Id == d).Join(db.Tenants, x => x.TenantId, x => x.Id, (_, tn) => tn.Name).FirstOrDefaultAsync();
        return Results.Ok(new InvitationInfo(inv.Email, tenantName, inv.Role, inv.IsUsable(time.GetUtcNow())));
    }

    internal static async Task<IResult> AcceptInvitationAsync(
        AcceptInvitationRequest req, AppDbContext db, UserManager<AppUser> users, TokenIssuer tokens, TimeProvider time)
    {
        var inv = await FindInvitationAsync(db, req.Token);
        var now = time.GetUtcNow();
        if (inv is null || !inv.IsUsable(now))
            return Results.BadRequest(new ApiError("invitation_invalid", "Invitation expirée ou déjà utilisée."));

        var user = await users.FindByEmailAsync(inv.Email);
        if (user is not null)
            return Results.Conflict(new ApiError("account_exists", "Un compte existe déjà pour cette adresse : connectez-vous."));

        user = new AppUser
        {
            UserName = inv.Email,
            Email = inv.Email,
            EmailConfirmed = true, // l'invitation a été reçue sur cette adresse
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? null : req.DisplayName.Trim(),
            TenantId = inv.Role == Roles.Provider ? null : inv.TenantId
        };
        var created = await users.CreateAsync(user, req.Password);
        if (!created.Succeeded)
            return Results.BadRequest(new ApiError("password_invalid", string.Join(" ", created.Errors.Select(e => e.Description))));
        await users.AddToRoleAsync(user, inv.Role);

        inv.UsedAt = now;
        if (inv.DossierId is not null)
        {
            // Rattache tous les dossiers déjà adressés à cette adresse.
            var dossiers = await db.Dossiers.Where(d => d.ProviderEmail == inv.Email && d.ProviderUserId == null).ToListAsync();
            foreach (var d in dossiers) d.ProviderUserId = user.Id;
        }
        db.AuditEvents.Add(new AuditEvent { TenantId = inv.TenantId, DossierId = inv.DossierId, UserId = user.Id, Action = "invitation.accepted" });
        await db.SaveChangesAsync();

        // Connexion directe : le jeton est restreint tant que la 2FA n'est pas activée.
        return Results.Ok(await tokens.IssueAsync(user));
    }

    // ---------- Mot de passe oublié ----------

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest req, UserManager<AppUser> users, IEmailSender email, IOptions<AppOptions> app)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is not null && user.EmailConfirmed)
        {
            var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(user)));
            var link = $"{app.Value.PublicBaseUrl.TrimEnd('/')}/reset-password?email={Uri.EscapeDataString(user.Email!)}&code={code}";
            await email.SendAsync(new EmailMessage([user.Email!], "Réinitialisation de votre mot de passe DocCollect",
                $"<p>Pour choisir un nouveau mot de passe : <a href=\"{link}\">réinitialiser</a>.</p><p>Ce lien expire rapidement. La double authentification reste demandée à la connexion.</p>",
                $"Pour choisir un nouveau mot de passe : {link}"));
        }
        // Même réponse que l'adresse existe ou non (pas d'énumération des comptes).
        return Results.NoContent();
    }

    private static async Task<IResult> ResetPasswordAsync(ResetPasswordRequest req, UserManager<AppUser> users)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is null) return Results.BadRequest(new ApiError("reset_invalid", "Lien invalide."));
        string token;
        try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(req.Code)); }
        catch (FormatException) { return Results.BadRequest(new ApiError("reset_invalid", "Lien invalide.")); }
        var result = await users.ResetPasswordAsync(user, token, req.NewPassword);
        return result.Succeeded
            ? Results.NoContent()
            : Results.BadRequest(new ApiError("reset_invalid", string.Join(" ", result.Errors.Select(e => e.Description))));
    }
}
