# Vérification IA des documents

Option **payante** activée par demandeur. À chaque dépôt, si la pièce a un « type de document attendu », le fichier est envoyé au fournisseur d'IA choisi par le demandeur, qui répond `match`, `mismatch` ou `uncertain`, avec le type reconnu et une confiance. Le résultat est affiché au fournisseur (pour qu'il corrige) et au demandeur ; il **ne bloque jamais** le dépôt.

Le prompt (`ClassificationPrompt`) demande explicitement de ne recopier aucune donnée personnelle dans la réponse. Seuls le verdict, le type reconnu et un commentaire court sont stockés.

## Configuration

Section `Ai:Providers` (clé = identifiant choisi par le demandeur). Clés d'API par variables d'environnement, jamais dans le dépôt.

```json
"Ai": {
  "MaxDocumentSizeMb": 10,
  "Providers": {
    "mistral-eu": {
      "Kind": "openai-compatible",
      "DisplayName": "Mistral AI",
      "Region": "UE (France)",
      "BaseUrl": "https://api.mistral.ai/v1",
      "Model": "mistral-small-latest",
      "Enabled": true
    }
  }
}
```

`Ai__Providers__mistral-eu__ApiKey=...` en variable d'environnement.

| `Kind` | Couvre | Notes |
|---|---|---|
| `openai-compatible` | OpenAI, **Azure OpenAI**, **Mistral**, **Scaleway Generative APIs**, **Ollama / vLLM** en local | Images toujours ; PDF si `SupportsPdf: true` (dépend du fournisseur). Azure : `AuthHeader: "api-key"` et `QueryString: "api-version=…"`, `BaseUrl` = URL du déploiement. |
| `anthropic` | Claude (API Messages) | Images et PDF nativement. |

`Region` est affiché au demandeur pour qu'il choisisse selon ses contraintes (données en UE, hébergement local…). Un modèle local (Ollama sur le VPS ou le NAS) garde les documents chez vous.

## Facturation

Chaque vérification crée un `AiUsageRecord` (demandeur, document, fournisseur, jetons d'entrée/sortie). Rapports : Admin › IA et facturation, et Équipe (côté demandeur). L'activation se fait dans Paramètres (demandeur) ou dans la liste des demandeurs (admin).

## Ajouter un fournisseur

- Compatible « chat/completions » : uniquement de la configuration.
- Autre API : implémenter `IDocumentClassifier` (`Key`, `DisplayName`, `Region`, `SupportsContentType`, `ClassifyAsync`) en réutilisant `ClassificationPrompt.System/User/Parse`, puis l'enregistrer : `services.AddSingleton<IDocumentClassifier, MonClassifieur>()` — `AiClassifierRegistry` l'ajoute automatiquement.
