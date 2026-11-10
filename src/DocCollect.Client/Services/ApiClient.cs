using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocCollect.Shared;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace DocCollect.Client.Services;

public sealed class ApiException(string message, string? code = null, int status = 0) : Exception(message)
{
    public string? Code { get; } = code;
    public int Status { get; } = status;
}

/// <summary>Accès typé à l'API. Les erreurs serveur (ApiError) deviennent des ApiException lisibles.</summary>
public sealed class ApiClient(IHttpClientFactory factory, TokenStore tokens, IJSRuntime js)
{
    private HttpClient Http => factory.CreateClient("api");

    private static async Task EnsureOkAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        ApiError? error = null;
        try { error = await resp.Content.ReadFromJsonAsync<ApiError>(); } catch { /* corps non JSON */ }
        var message = error?.Message ?? (int)resp.StatusCode switch
        {
            401 => "Session expirée : reconnectez-vous.",
            403 => "Accès refusé.",
            404 => "Élément introuvable.",
            413 => "Fichier trop volumineux.",
            429 => "Trop de requêtes, réessayez dans un instant.",
            _ => $"Erreur serveur ({(int)resp.StatusCode})."
        };
        throw new ApiException(message, error?.Code, (int)resp.StatusCode);
    }

    public async Task<T> GetAsync<T>(string url)
    {
        using var resp = await Http.GetAsync(url);
        await EnsureOkAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<T>())!;
    }

    public async Task<TRes> PostAsync<TRes>(string url, object? body = null)
    {
        using var resp = await Http.PostAsJsonAsync(url, body);
        await EnsureOkAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<TRes>())!;
    }

    public async Task PostAsync(string url, object? body = null)
    {
        using var resp = await Http.PostAsJsonAsync(url, body);
        await EnsureOkAsync(resp);
    }

    public async Task<TRes> PutAsync<TRes>(string url, object body)
    {
        using var resp = await Http.PutAsJsonAsync(url, body);
        await EnsureOkAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<TRes>())!;
    }

    public async Task DeleteAsync(string url)
    {
        using var resp = await Http.DeleteAsync(url);
        await EnsureOkAsync(resp);
    }

    public async Task<DocumentDto> UploadAsync(string url, IBrowserFile file, long maxBytes)
    {
        using var content = new MultipartFormDataContent();
        var stream = new StreamContent(file.OpenReadStream(maxBytes));
        stream.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType);
        content.Add(stream, "file", file.Name);
        using var resp = await Http.PostAsync(url, content);
        await EnsureOkAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
    }

    /// <summary>Téléchargement authentifié (le jeton ne peut pas passer dans un simple lien).</summary>
    public async Task DownloadAsync(string url, string fileName)
    {
        await tokens.LoadAsync();
        var ok = await js.InvokeAsync<bool>("dc.download", url, tokens.AccessToken, fileName);
        if (!ok) throw new ApiException("Téléchargement impossible (session expirée ou document indisponible).");
    }
}
