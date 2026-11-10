using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocCollect.Shared;

namespace DocCollect.Core.Ai;

/// <summary>Fournisseur Anthropic (API Messages). Gère nativement les images et les PDF.</summary>
public sealed class AnthropicClassifier(string key, AiProviderOptions options, HttpClient http) : IDocumentClassifier
{
    private const string ApiVersion = "2023-06-01";

    public string Key => key;
    public string DisplayName => string.IsNullOrWhiteSpace(options.DisplayName) ? "Anthropic Claude" : options.DisplayName;
    public string? Region => options.Region;

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/png", "image/gif", "image/webp" };

    public bool SupportsContentType(string contentType) =>
        ImageTypes.Contains(contentType) || contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

    internal JsonObject BuildBody(ClassificationRequest r)
    {
        var isPdf = r.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
        var attachment = new JsonObject
        {
            ["type"] = isPdf ? "document" : "image",
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = r.ContentType.ToLowerInvariant(),
                ["data"] = Convert.ToBase64String(r.Content)
            }
        };
        return new JsonObject
        {
            ["model"] = options.Model,
            ["max_tokens"] = options.MaxOutputTokens,
            ["temperature"] = 0,
            ["system"] = ClassificationPrompt.System,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        attachment,
                        new JsonObject { ["type"] = "text", ["text"] = ClassificationPrompt.User(r) }
                    }
                }
            }
        };
    }

    public async Task<ClassificationResult> ClassifyAsync(ClassificationRequest request, CancellationToken ct = default)
    {
        if (!SupportsContentType(request.ContentType))
            return new(AiVerdict.NotChecked, null, null, "Type de fichier non pris en charge par ce fournisseur d'IA.");

        var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? "https://api.anthropic.com/v1" : options.BaseUrl.TrimEnd('/');
        using var msg = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/messages")
        {
            Content = new StringContent(BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json")
        };
        msg.Headers.TryAddWithoutValidation("x-api-key", options.ApiKey);
        msg.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);

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
        var text = new StringBuilder();
        if (root.TryGetProperty("content", out var content))
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text")
                    text.Append(block.GetProperty("text").GetString());
        long inTok = 0, outTok = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var i)) inTok = i.GetInt64();
            if (usage.TryGetProperty("output_tokens", out var o)) outTok = o.GetInt64();
        }
        return ClassificationPrompt.Parse(text.ToString(), inTok, outTok);
    }
}
