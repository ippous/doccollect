using DocCollect.Infrastructure.Persistence;
using DocCollect.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DocCollect.Api.Auth;

/// <summary>
/// Émet les jetons d'accès / de rafraîchissement au même format que le gestionnaire « Bearer » d'ASP.NET Identity,
/// mais en réponse JSON maîtrisée (LoginResponse). Les jetons fonctionnent en iframe et sur mobile (pas de cookie tiers).
/// </summary>
public sealed class TokenIssuer(
    IOptionsMonitor<BearerTokenOptions> bearerOptions,
    SignInManager<AppUser> signIn,
    TimeProvider time)
{
    private BearerTokenOptions Options => bearerOptions.Get(IdentityConstants.BearerScheme);

    public async Task<LoginResponse> IssueAsync(AppUser user)
    {
        var principal = await signIn.CreateUserPrincipalAsync(user);
        var o = Options;
        var now = time.GetUtcNow();
        var access = o.BearerTokenProtector.Protect(new AuthenticationTicket(principal,
            new AuthenticationProperties { ExpiresUtc = now + o.BearerTokenExpiration }, $"{IdentityConstants.BearerScheme}:AccessToken"));
        var refresh = o.RefreshTokenProtector.Protect(new AuthenticationTicket(principal,
            new AuthenticationProperties { ExpiresUtc = now + o.RefreshTokenExpiration }, $"{IdentityConstants.BearerScheme}:RefreshToken"));
        return new LoginResponse(
            Succeeded: true,
            MustEnrollTwoFactor: !user.TwoFactorEnabled,
            AccessToken: access,
            RefreshToken: refresh,
            ExpiresInSeconds: (int)o.BearerTokenExpiration.TotalSeconds);
    }

    /// <summary>Valide un jeton de rafraîchissement (expiration + empreinte de sécurité) et renvoie l'utilisateur.</summary>
    public async Task<AppUser?> ValidateRefreshAsync(string refreshToken)
    {
        var ticket = Options.RefreshTokenProtector.Unprotect(refreshToken);
        if (ticket?.Properties.ExpiresUtc is not { } expires || time.GetUtcNow() >= expires)
            return null;
        return await signIn.ValidateSecurityStampAsync(ticket.Principal);
    }
}
