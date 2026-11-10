using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocCollect.Shared;

namespace DocCollect.Core.Ai;

/// <summary>
/// Fournisseur compatible avec l'API « chat/completions » d'OpenAI : OpenAI, Azure OpenAI,
/// Mistral (La Plateforme, hébergé en UE), Scaleway Generative APIs, Ollama / vLLM en local, etc.
/// </summary>
public sealed class OpenAiCompatibleClassifier(string key, AiProviderOptions options, HttpClient http) : IDocumentClassifier
{
    public string Key => key;
    public string DisplayName => string.IsNullOrWhiteSpace(options.DisplayName) ? key : options.DisplayName;
    public string? Region => options.Region;

    public bool SupportsContentType(string contentType) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || (options.SupportsPdf && contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase));

    internal JsonObject BuildBody(ClassificationRequest r)
    {
        var dataUri = $"data:{r.ContentType};base64,{Convert.ToBase64String(r.Content)}";
        JsonObject attachment = r.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUri } }
            : new JsonObject { ["type"] = "file", ["file"] = new JsonObject { ["filename"] = r.FileName, ["file_data"] = dataUri } };

        var body = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = ClassificationPrompt.System },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = ClassificationPrompt.User(r) },
                        attachment
                    }
                }
            },
            ["max_tokens"] = options.MaxOutputTokens,
            ["temperature"] = 0
        };
        if (!string.IsNullOrWhiteSpace(options.Model)) body["model"] = options.Model;
        return body;
    }

    public async Task<ClassificationResult> ClassifyAsync(ClassificationRequest request, CancellationToken ct = default)
    {
        if (!SupportsContentType(request.ContentType))
            return new(AiVerdict.NotChecked, null, null, "Type de fichier non pris en charge par ce fournisseur d'IA.");

        var url = options.BaseUrl.TrimEnd('/') + "/chat/completions"
                  + (string.IsNullOrWhiteSpace(options.QueryString) ? "" : "?" + options.QueryString.TrimStart('?'));
        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            if (string.IsNullOrWhiteSpace(options.AuthHeader) || options.AuthHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            else
                msg.Headers.TryAddWithoutValidation(options.AuthHeader, options.ApiKey);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));
        try
        {
            using var resp = await http.SendAsync(msg, cts.Token);
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
                return new(AiVerdict.Error, null, null, $"Fournisseur d'IA : HTTP {(int)resp.StatusCode}.");
            return ParseResponse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(AiVerdict.Error, null, null, $"Fournisseur d'IA indisponible : {ex.GetType().Name}.");
        }
    }

    internal static ClassificationResult ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var text = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            ? choices[0].GetProperty("message").GetProperty("content").GetString() : null;
        long inTok = 0, outTok = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var p)) inTok = p.GetInt64();
            if (usage.TryGetProperty("completion_tokens", out var c)) outTok = c.GetInt64();
        }
        return ClassificationPrompt.Parse(text, inTok, outTok);
    }
}
