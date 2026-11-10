using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using DocCollect.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace DocCollect.Client.Services;

/// <summary>
/// Conserve les jetons. En mode embarqué (iframe), sessionStorage : rien ne persiste après fermeture de l'onglet
/// et aucun cookie tiers n'est nécessaire. Sinon localStorage.
/// </summary>
public sealed class TokenStore(IJSRuntime js, NavigationManager nav)
{
    private const string Key = "doccollect.tokens";
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    private bool _loaded;

    private string Storage => nav.ToBaseRelativePath(nav.Uri).StartsWith("embed/", StringComparison.OrdinalIgnoreCase) ? "session" : "local";

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var raw = await js.InvokeAsync<string?>("dc.storage.get", Storage, Key);
            if (string.IsNullOrEmpty(raw)) return;
            var parts = raw.Split('\n');
            if (parts.Length == 2) (AccessToken, RefreshToken) = (parts[0], parts[1]);
        }
        catch (JSException) { /* stockage indisponible (navigation privée stricte) : session en mémoire */ }
    }

    public async Task SaveAsync(LoginResponse r)
    {
        AccessToken = r.AccessToken;
        RefreshToken = r.RefreshToken;
        try { await js.InvokeVoidAsync("dc.storage.set", Storage, Key, $"{AccessToken}\n{RefreshToken}"); }
        catch (JSException) { }
    }

    public async Task ClearAsync()
    {
        AccessToken = RefreshToken = null;
        try { await js.InvokeVoidAsync("dc.storage.remove", Storage, Key); }
        catch (JSException) { }
    }
}

/// <summary>Ajoute le jeton aux appels d'API et le rafraîchit une fois en cas de 401.</summary>
public sealed class AuthHeaderHandler(TokenStore tokens, IServiceProvider services) : DelegatingHandler
{
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await tokens.LoadAsync();
        var sentWith = tokens.AccessToken;
        if (sentWith is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sentWith);
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized || tokens.RefreshToken is null) return response;

        await RefreshLock.WaitAsync(ct);
        try
        {
            if (tokens.AccessToken == sentWith) // pas déjà rafraîchi par un autre appel
            {
                var auth = services.GetRequiredService<AuthService>();
                if (!await auth.TryRefreshAsync()) return response;
            }
        }
        finally { RefreshLock.Release(); }

        response.Dispose();
        var retry = await CloneAsync(request);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return await base.SendAsync(retry, ct);
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage r)
    {
        var clone = new HttpRequestMessage(r.Method, r.RequestUri);
        if (r.Content is not null)
        {
            var bytes = await r.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(bytes);
            foreach (var h in r.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        foreach (var h in r.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return clone;
    }
}

public sealed class AuthService(IHttpClientFactory factory, TokenStore tokens, AppAuthStateProvider state)
{
    // Client sans gestionnaire d'authentification : évite toute récursion lors du rafraîchissement.
    private HttpClient Anonymous => factory.CreateClient("anonymous");

    public MeResponse? Me => state.Me;

    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        using var resp = await Anonymous.PostAsJsonAsync("api/auth/login", request);
        var result = await resp.Content.ReadFromJsonAsync<LoginResponse>()
                     ?? new LoginResponse(false, Error: "Réponse inattendue du serveur.");
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            return new LoginResponse(false, Error: "Trop de tentatives. Patientez une minute.");
        if (result.Succeeded) await SignedInAsync(result);
        return result;
    }

    public async Task SignedInAsync(LoginResponse tokensResponse)
    {
        await tokens.SaveAsync(tokensResponse);
        await state.ReloadAsync();
    }

    public async Task<bool> TryRefreshAsync()
    {
        if (tokens.RefreshToken is null) return false;
        using var resp = await Anonymous.PostAsJsonAsync("api/auth/refresh", new RefreshRequest(tokens.RefreshToken));
        if (!resp.IsSuccessStatusCode)
        {
            await LogoutAsync();
            return false;
        }
        var result = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        if (result?.AccessToken is null) return false;
        await tokens.SaveAsync(result);
        return true;
    }

    public async Task LogoutAsync()
    {
        await tokens.ClearAsync();
        await state.ReloadAsync();
    }
}

/// <summary>État d'authentification : construit à partir de /api/auth/me (rôles, 2FA activée ou non).</summary>
public sealed class AppAuthStateProvider(IHttpClientFactory factory, TokenStore tokens) : AuthenticationStateProvider
{
    public const string NeedsTwoFactorClaim = "needs2fa";
    public MeResponse? Me { get; private set; }
    private Task<AuthenticationState>? _current;

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _current ??= BuildAsync();

    public async Task ReloadAsync()
    {
        _current = BuildAsync();
        NotifyAuthenticationStateChanged(_current);
        await _current;
    }

    private async Task<AuthenticationState> BuildAsync()
    {
        await tokens.LoadAsync();
        Me = null;
        if (tokens.AccessToken is null) return Anonymous();
        try
        {
            Me = await factory.CreateClient("api").GetFromJsonAsync<MeResponse>("api/auth/me");
        }
        catch (HttpRequestException) { return Anonymous(); }
        if (Me is null) return Anonymous();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Me.UserId),
            new(ClaimTypes.Name, Me.DisplayName ?? Me.Email),
            new(ClaimTypes.Email, Me.Email)
        };
        if (Me.TwoFactorEnabled)
            claims.AddRange(Me.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        else
            claims.Add(new Claim(NeedsTwoFactorClaim, "true")); // aucun rôle tant que la 2FA n'est pas active
        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "doccollect")));
    }

    private static AuthenticationState Anonymous() => new(new ClaimsPrincipal(new ClaimsIdentity()));
}
