# Stockages de documents

Les fichiers ne sont jamais en base : ils vont dans un **profil de stockage**, configuré dans l'application.

- Profils **plateforme** (admin) : celui marqué « par défaut » reçoit les documents des demandeurs sans stockage propre.
- Profils **demandeur** : un demandeur peut stocker ses documents chez lui, ou y recevoir une copie (canal « Copie vers mon stockage »).
- Sans aucun profil : disque du serveur, `Storage:FallbackLocalPath` (`/data/storage`, volume Docker).

Les champs secrets (mots de passe, clés) sont chiffrés en base (`SecretProtector`) et masqués dans l'interface. Le bouton **Tester la connexion** écrit, relit puis supprime un fichier témoin.

## Types disponibles

| Type | Clé | Usage conseillé |
|---|---|---|
| Disque local / partage monté | `local` | Dev, ou NAS monté sur le VPS |
| FTP / FTPS | `ftp` | NAS ou hébergement existant ; préférer FTPES (Explicit) |
| SFTP | `sftp` | **Recommandé pour le NAS** |
| Azure Blob Storage | `azure-blob` | Cloud, région France Central possible |
| Google Cloud Storage | `gcs` | Cloud, région europe-west9 (Paris) possible |

### NAS Synology (DS920+) en SFTP via Tailscale

Exposer le NAS sur Internet n'est pas nécessaire :

1. Installer **Tailscale** sur le NAS (Centre de paquets) et sur le VPS (`curl -fsSL https://tailscale.com/install.sh | sh`), sur le même réseau Tailscale.
2. Sur le NAS : Panneau de configuration › Terminal et SNMP › activer SSH ; Services de fichiers › FTP › onglet SFTP › activer.
3. Créer un utilisateur dédié `doccollect`, avec accès en écriture à un dossier partagé `doccollect` uniquement.
4. Récupérer l'empreinte de la clé d'hôte depuis le VPS : `ssh-keyscan -t ed25519 100.x.y.z | ssh-keygen -lf -` (valeur `SHA256:...`).
5. Dans DocCollect › Stockages : type SFTP, hôte `100.x.y.z` (IP Tailscale du NAS), utilisateur `doccollect`, clé privée de préférence, empreinte, dossier racine `/doccollect`.

Les conteneurs Docker du VPS atteignent l'IP Tailscale via l'hôte (routage par défaut).

### FTP

Avec un certificat auto-signé (cas du NAS), renseigner l'**empreinte SHA-1** du certificat plutôt que « Accepter tout certificat ».

## Ajouter un type de stockage

1. Implémenter `IDocumentStorage` (écriture, lecture, existence, suppression, `TestAsync` → `StorageTester.RunAsync(this, ct)`).
2. Implémenter `IStorageProvider` : `Type` (clé stable enregistrée en base), `DisplayName`, `Fields` (le formulaire de l'interface est généré à partir de cette liste ; `Secret = true` pour les champs à chiffrer), `Create(StorageSettings)`.
3. L'enregistrer dans `DependencyInjection.AddDocCollectInfrastructure` :

```csharp
services.AddSingleton<IStorageProvider, S3StorageProvider>();
```

Rien d'autre à modifier : l'API, l'interface et la résolution des stockages sont génériques. Piste naturelle : un fournisseur **S3 compatible** (OVH, Scaleway, AWS) pour des stockages objet hébergés en France.

Règles à respecter : clés relatives normalisées par `StorageKeys.Normalize` (refus de `..`), écriture atomique si le protocole le permet, `OpenReadAsync` doit renvoyer un flux indépendant de la connexion (fichier temporaire si besoin).
