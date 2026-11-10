# Démarrer en développement

## Prérequis

- SDK .NET 10 (`global.json` accepte toute version 10.0.1xx et suivantes)
- Docker (PostgreSQL et Mailpit)
- Visual Studio 2022 17.14+, Rider ou VS Code (solution au format `.slnx`)

## Première installation

```bash
git clone https://github.com/ippous/doccollect.git
cd doccollect
docker compose -f docker-compose.dev.yml up -d
dotnet restore DocCollect.slnx
dotnet list package --outdated   # les versions de Directory.Packages.props n'ont pas pu être vérifiées à la génération
```

### Migration initiale de la base

Le dépôt ne contient pas encore de migration EF Core. Tant qu'il n'y en a pas, l'application crée le schéma au démarrage (`EnsureCreated`). **Avant le premier déploiement en test**, générer la migration initiale pour passer en mode migrations :

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add Initial -p src/DocCollect.Infrastructure -s src/DocCollect.Api -o Persistence/Migrations
```

Ensuite, l'application applique automatiquement les migrations au démarrage (`Database:InitializeOnStartup`).

## Lancer

```bash
dotnet run --project src/DocCollect.Api
```

- Application : https://localhost:5001 (le client Blazor est servi par l'API)
- Documentation OpenAPI : https://localhost:5001/openapi/v1.json (hors production)
- E-mails (invitations, codes 2FA) : http://localhost:8025

Comptes créés au démarrage en développement (`appsettings.Development.json`, section `Seed`) :

| Compte | Mot de passe | Rôle |
|---|---|---|
| admin@doccollect.local | Admin-Local-2026 | Administrateur |
| demo@doccollect.local | Demo-Local-2026 | Demandeur « Étude de démonstration » (`demo`) |

La double authentification est demandée à la première connexion. Pour tester le parcours fournisseur, créez une demande vers une adresse quelconque : l'invitation arrive dans Mailpit.

Les paramètres propres à votre poste vont dans `src/DocCollect.Api/appsettings.Local.json` (ignoré par git).

## Tests

```bash
dotnet test DocCollect.slnx
```

- `DocCollect.Core.Tests` : domaine, chiffrement (formats, troncature, altération, rotation), stockage, ingestion, IA, livraison, relances.
- `DocCollect.Api.Tests` : scénarios de bout en bout via `WebApplicationFactory`, base EF en mémoire, e-mails capturés, codes TOTP calculés : 2FA obligatoire, parcours complet demande → dépôt → transmission → téléchargement, refus/redépôt, isolation des demandeurs et des fournisseurs, CSP d'intégration, masquage des secrets.

## Conventions

- Interface et messages en français ; code et identifiants en anglais.
- Règles métier dans `Domain`, techniques sans dépendance externe dans `Core`, tout ce qui touche à une bibliothèque tierce dans `Infrastructure`.
- Les endpoints n'accèdent aux dossiers que par `DossierService.GetForTenantAsync` / `GetForProviderAsync` (contrôle d'appartenance).
- Toute action sensible écrit un `AuditEvent`.
