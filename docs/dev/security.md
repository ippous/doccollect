# Sécurité

## Double authentification (tous les rôles)

- Politique de repli de l'API : utilisateur connecté **et** claim `dc:2fa=true`. Les politiques de rôle (`admin`, `requester`, `provider`) exigent aussi ce claim.
- Un utilisateur sans 2FA reçoit un jeton restreint : seuls `/api/auth/me` et les endpoints d'activation répondent (politique `enrolling`). L'interface l'amène directement à l'écran d'activation.
- Méthodes : **TOTP** (application d'authentification, recommandé) ou **code e-mail** (plus simple pour un fournisseur occasionnel). 10 codes de récupération à l'activation, régénérables.
- La 2FA ne peut pas être désactivée par l'utilisateur, seulement changée. Un admin peut la réinitialiser (perte du téléphone) : l'utilisateur devra la réactiver.
- Verrouillage 15 min après 5 échecs (mot de passe ou code), limitation de débit sur les endpoints d'authentification (`Auth:RateLimitPerMinute`).
- Jetons : accès 30 min, rafraîchissement 7 jours, invalidés à chaque changement de l'empreinte de sécurité (mot de passe, 2FA).

## Chiffrement des documents

- **Enveloppe** : une clé AES-256 aléatoire par document, scellée par une clé maître (AES-GCM, données associées = identifiant du document + identifiant de clé).
- **Format « DCE1 »** : contenu chiffré par blocs de 64 Ko, nonce dérivé d'un compteur, indicateur « dernier bloc » authentifié → toute modification, troncature, réordonnancement ou ajout est détecté.
- **Intégrité** : SHA-256 du clair vérifié à chaque lecture, chiffré ou non.
- Le stockage ne voit ni le nom d'origine ni le contenu.

### Clés maîtres et rotation

Configuration `Encryption:CurrentKeyId` + `Encryption:Keys:{id}` (32 octets en base64, `openssl rand -base64 32`), fournies par variables d'environnement.

Rotation : ajouter `Encryption__Keys__k2027`, passer `Encryption__CurrentKeyId=k2027`, **garder** l'ancienne clé tant que des documents l'utilisent (`StoredDocument.MasterKeyId`). Les secrets en base (mots de passe de stockage, secrets de webhook) suivent la même clé.

**Sauvegardez les clés maîtres hors du VPS** (gestionnaire de mots de passe) : sans elles, les documents chiffrés sont irrécupérables.

## Isolation

- Chaque requête « demandeur » est filtrée par le claim `dc:tenant` ; les dossiers sont chargés uniquement via `DossierService.GetForTenantAsync`.
- Un fournisseur ne voit que les dossiers qui lui sont adressés (`ProviderUserId` ou adresse e-mail).
- Les invitations stockent seulement l'empreinte SHA-256 du jeton ; validité 14 jours, usage unique.

## Fichiers déposés

- Type réel détecté par signature (le type déclaré par le navigateur est ignoré s'il ment) ; exécutables refusés.
- Taille limitée par pièce et à 110 Mo par requête (Kestrel, Caddy).
- Noms de fichiers assainis ; jamais utilisés comme chemin de stockage.

## Intégration et navigateur

- `Content-Security-Policy: frame-ancestors 'self'` partout, sauf `/embed/{demandeur}/…` où s'ajoutent les origines déclarées par ce demandeur (https uniquement).
- `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, HSTS (Caddy).
- En iframe, les jetons sont en `sessionStorage` : aucun cookie tiers, rien ne persiste après fermeture.
- Le service worker ne met jamais en cache `/api/` ni les documents.

## Traçabilité

`AuditEvent` : création de dossier, dépôts, retraits, téléchargements, validations/refus, livraisons, relances, activation/réinitialisation de 2FA, paramètres. Consultable par l'admin (`/api/admin/audit`).

## RGPD (points d'attention pour la commercialisation)

- DocCollect est **sous-traitant** des demandeurs : prévoir un contrat (article 28), un registre, et une politique de conservation (purge des dossiers clos après N mois — à implémenter).
- Localisation : PostgreSQL et stockage par défaut sur le VPS (vérifier la région Hostinger), IA désactivée par défaut et fournisseur choisi par le demandeur.
