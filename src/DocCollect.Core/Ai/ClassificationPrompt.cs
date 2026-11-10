using System.Text.Json;
using System.Text.RegularExpressions;
using DocCollect.Shared;

namespace DocCollect.Core.Ai;

/// <summary>Prompt commun à tous les fournisseurs et analyse tolérante de la réponse JSON.</summary>
public static partial class ClassificationPrompt
{
    public const string System =
        "Tu es un assistant de contrôle documentaire pour un service de collecte de pièces justificatives. " +
        "On te montre un document déposé par un particulier ou une entreprise et le type de pièce attendu. " +
        "Détermine si le document correspond au type attendu. Ne retranscris aucune donnée personnelle " +
        "(nom, numéro, adresse, montant) dans ta réponse. Réponds uniquement par un objet JSON de la forme : " +
        "{\"verdict\":\"match|mismatch|uncertain\",\"detected_type\":\"type de document reconnu, en français\"," +
        "\"confidence\":0.0 à 1.0,\"comment\":\"une phrase courte, sans donnée personnelle\"}.";

    public static string User(ClassificationRequest r) =>
        $"Pièce attendue : {r.ExpectedDocumentType}" +
        (string.IsNullOrWhiteSpace(r.ItemLabel) ? "" : $" (libellé dans la checklist : {r.ItemLabel})") +
        $". Nom du fichier déposé : {r.FileName}. Le document est joint.";

    public static ClassificationResult Parse(string? text, long inputTokens = 0, long outputTokens = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new(AiVerdict.Error, null, null, "Réponse vide du fournisseur d'IA.", inputTokens, outputTokens);

        var match = JsonObjectRegex().Match(text);
        if (!match.Success)
            return new(AiVerdict.Uncertain, null, null, "Réponse de l'IA non structurée.", inputTokens, outputTokens);
        try
        {
            using var doc = JsonDocument.Parse(match.Value);
            var root = doc.RootElement;
            var verdict = (Str(root, "verdict") ?? "").Trim().ToLowerInvariant() switch
            {
                "match" => AiVerdict.Match,
                "mismatch" => AiVerdict.Mismatch,
                _ => AiVerdict.Uncertain
            };
            double? confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                ? Math.Clamp(c.GetDouble(), 0, 1) : null;
            return new(verdict, Truncate(Str(root, "detected_type"), 200), confidence, Truncate(Str(root, "comment"), 500),
                inputTokens, outputTokens);
        }
        catch (JsonException)
        {
            return new(AiVerdict.Uncertain, null, null, "Réponse JSON de l'IA illisible.", inputTokens, outputTokens);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"\{[\s\S]*\}")]
    private static partial Regex JsonObjectRegex();
}
