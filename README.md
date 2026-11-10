# DocCollect

Service de **collecte sécurisée de pièces justificatives**. Un demandeur (étude notariale, cabinet comptable, agence…) envoie une liste de documents à fournir ; le fournisseur (son client) les dépose depuis un ordinateur ou un téléphone ; DocCollect indique ce qui manque, relance, et transmet les pièces au demandeur **en une fois quand tout est là** ou **au fil de l'eau**.

| | |
|---|---|
| Dépôt GitHub | `ippous/doccollect` |
| Production | https://doccollect.jaume-eric.com |
| Test | https://doccollect-test.jaume-eric.com |
| Dev | https://doccollect-dev.jaume-eric.com |

## Fonctionnalités

- Modèles de listes de pièces **privés** (par demandeur) ou **publics** (catalogue de la plateforme), copiables et adaptables.
- Suivi de complétude, validation/refus pièce par pièce, relances automatiques, échéances.
- Transmission configurable : notification e-mail, **webhook signé** (HMAC), **copie vers le stockage du demandeur**.
- **Stockage générique** configurable dans l'application : disque local/NAS monté, **FTP/FTPS**, **SFTP**, **Azure Blob**, **Google Cloud Storage** (ajout d'un fournisseur = une classe).
- **Chiffrement optionnel** des documents au repos (AES-256-GCM par enveloppe, rotation des clés) et contrôle d'intégrité (SHA-256) à chaque lecture.
- **Double authentification obligatoire** pour tous (admins, demandeurs, fournisseurs) : application TOTP ou code par e-mail, codes de récupération.
- **Vérification IA** du type de document — option payante, fournisseur au choix (Mistral UE, Azure OpenAI France, Anthropic, modèle local Ollama…), consommation comptée par demandeur.
- **Intégration dans le site du demandeur** (`embed.js`, iframe, origines autorisées par CSP) et **application mobile installable** (PWA, prise de photo directe).
- Trois environnements **dev / test / prod** sur le VPS, déploiement continu GitHub Actions.

## Pile technique

.NET 10 LTS · ASP.NET Core (API minimales) · Blazor WebAssembly · EF Core + PostgreSQL · ASP.NET Identity · Docker + Caddy.

```
src/
  DocCollect.Shared          contrats partagés API ↔ client
  DocCollect.Domain          modèle métier et règles (complétude, livraison) — aucune dépendance
  DocCollect.Core            stockage générique, chiffrement, IA, livraison — BCL uniquement
  DocCollect.Infrastructure  EF Core, FTP/SFTP/Azure/GCS, SMTP, tâches de fond
  DocCollect.Api             API, authentification 2FA, hébergement du client
  DocCollect.Client          Blazor WebAssembly (PWA, mode embarqué)
tests/
  DocCollect.Core.Tests      tests unitaires (domaine, chiffrement, stockage, IA, livraison)
  DocCollect.Api.Tests       tests d'intégration de bout en bout
deploy/                      docker compose (dev/test/prod), Caddy, .env d'exemple
docs/                        documentation développeur et utilisateur
```

## Démarrage rapide

```bash
docker compose -f docker-compose.dev.yml up -d     # PostgreSQL + Mailpit
dotnet run --project src/DocCollect.Api            # https://localhost:5001
dotnet test DocCollect.slnx
```

Comptes de développement : `admin@doccollect.local` / `Admin-Local-2026` et `demo@doccollect.local` / `Demo-Local-2026` (la 2FA est demandée à la première connexion ; les codes e-mail arrivent dans Mailpit, http://localhost:8025).

## Documentation

- Développeur : [architecture](docs/dev/architecture.md) · [démarrage](docs/dev/getting-started.md) · [stockages](docs/dev/storage-providers.md) · [IA](docs/dev/ai-providers.md) · [sécurité](docs/dev/security.md) · [déploiement](docs/dev/deployment.md) · [intégration web](docs/dev/embedding.md) · [API et webhooks](docs/dev/api.md)
- Utilisateur : [demandeur](docs/user/guide-demandeur.md) · [fournisseur de documents](docs/user/guide-fournisseur.md) · [administrateur](docs/user/guide-administrateur.md)
