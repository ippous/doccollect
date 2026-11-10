using DocCollect.Shared;

namespace DocCollect.Core.Ai;

public sealed record ClassificationRequest(
    string ExpectedDocumentType,
    string? ItemLabel,
    string FileName,
    string ContentType,
    byte[] Content);

public sealed record ClassificationResult(
    AiVerdict Verdict,
    string? DetectedType,
    double? Confidence,
    string? Comment,
    long InputTokens = 0,
    long OutputTokens = 0);

/// <summary>Vérifie qu'un document correspond au type attendu. Une implémentation par fournisseur d'IA.</summary>
public interface IDocumentClassifier
{
    /// <summary>Clé de configuration (ex. « mistral-eu », « anthropic », « azure-openai-fr »).</summary>
    string Key { get; }
    string DisplayName { get; }
    /// <summary>Localisation déclarée des traitements (affichée aux demandeurs, ex. « UE », « US »).</summary>
    string? Region { get; }
    bool SupportsContentType(string contentType);
    Task<ClassificationResult> ClassifyAsync(ClassificationRequest request, CancellationToken ct = default);
}

/// <summary>Configuration d'un fournisseur d'IA (section « Ai:Providers:{clé} »).</summary>
public sealed class AiProviderOptions
{
    /// <summary>« openai-compatible » ou « anthropic ».</summary>
    public string Kind { get; set; } = "openai-compatible";
    public string DisplayName { get; set; } = "";
    public string? Region { get; set; }
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "";
    /// <summary>Nom de l'en-tête d'authentification ; « api-key » pour Azure OpenAI, « Authorization » (Bearer) sinon.</summary>
    public string? AuthHeader { get; set; }
    /// <summary>Paramètre de requête ajouté à l'URL (ex. « api-version=2024-10-21 » pour Azure).</summary>
    public string? QueryString { get; set; }
    public bool SupportsPdf { get; set; }
    public int MaxOutputTokens { get; set; } = 400;
    public int TimeoutSeconds { get; set; } = 30;
    public bool Enabled { get; set; } = true;
}

public sealed class AiOptions
{
    public const string Section = "Ai";
    /// <summary>Taille maximale envoyée à l'IA (Mo). Au-delà, le document n'est pas vérifié.</summary>
    public int MaxDocumentSizeMb { get; set; } = 10;
    public Dictionary<string, AiProviderOptions> Providers { get; set; } = [];
}
