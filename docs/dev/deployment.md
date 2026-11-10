# Déploiement sur le VPS (dev, test, prod)

## Principe

| Environnement | Branche / déclencheur | URL | Dossier VPS | E-mails |
|---|---|---|---|---|
| dev | push sur `develop` | https://doccollect-dev.jaume-eric.com | `/opt/doccollect/dev` | capturés (Mailpit) |
| test | push sur `main` | https://doccollect-test.jaume-eric.com | `/opt/doccollect/test` | capturés (Mailpit) |
| prod | publication d'une release `vX.Y.Z` (approbation manuelle) | https://doccollect.jaume-eric.com | `/opt/doccollect/prod` | SMTP Hostinger |

Une seule image (`ghcr.io/ippous/doccollect`), taguée `dev`, `test`, `prod` et par version. Chaque environnement a sa propre base PostgreSQL, son volume de documents et sa clé de chiffrement. Un Caddy partagé fait le HTTPS (Let's Encrypt).

## 1. DNS (hPanel Hostinger › Domaines › jaume-eric.com › DNS)

Trois enregistrements **A** vers l'IP du VPS : `doccollect`, `doccollect-test`, `doccollect-dev`.

## 2. Préparer le VPS (une fois)

```bash
# Docker (si absent)
curl -fsSL https://get.docker.com | sh
# Utilisateur de déploiement
sudo adduser --disabled-password deploy && sudo usermod -aG docker deploy
sudo mkdir -p /opt/doccollect/{dev,test,prod,caddy} && sudo chown -R deploy /opt/doccollect
docker network create web
```

Si un autre reverse proxy occupe déjà les ports 80/443 sur le VPS, ne lancez pas Caddy : ajoutez les trois sous-domaines à ce proxy, vers les conteneurs `doccollect-<env>-app-1:8080` sur le réseau `web`.

Sinon :

```bash
scp deploy/caddy/* deploy@VPS:/opt/doccollect/caddy/
ssh deploy@VPS 'cd /opt/doccollect/caddy && docker compose up -d'
```

## 3. Fichiers .env

Pour chaque environnement, copier `deploy/.env.example` en `/opt/doccollect/<env>/.env` et compléter :

- `DC_ENV`, `DC_IMAGE_TAG` (= `dev`, `test` ou `prod`), `DC_PUBLIC_URL`, `DC_ENV_LABEL` (vide en prod)
- `ASPNETCORE_ENVIRONMENT` : `Staging` pour dev/test, `Production` pour prod
- `POSTGRES_PASSWORD`, `DC_ENCRYPTION_KEY` : `openssl rand -base64 32` (**différents par environnement, sauvegardés hors du VPS**)
- `DC_ADMIN_EMAIL` / `DC_ADMIN_PASSWORD` : premier administrateur (ignoré ensuite)
- prod : SMTP Hostinger (`smtp.hostinger.com`, 465, `SslOnConnect`, boîte `no-reply@jaume-eric.com` à créer dans hPanel)

`chmod 600 .env`.

## 4. GitHub

Dépôt `ippous/doccollect` :

- Secrets : `VPS_HOST`, `VPS_USER` (`deploy`), `VPS_SSH_KEY` (clé privée dont la publique est dans `~deploy/.ssh/authorized_keys`).
- Environnements : `dev`, `test`, `prod` ; sur `prod`, règle **Required reviewers** (vous).
- Packages : après le premier push, rendre l'image GHCR accessible au VPS (`docker login ghcr.io` sur le VPS avec un jeton `read:packages`, ou image publique).
- Branches : `develop` (intégration), `main` (recette) ; protection de `main` avec le workflow CI obligatoire.

Le workflow `Deploy` : tests → image → copie du compose → `docker compose pull && up -d` → sonde `/healthz`.

## 5. Mise en production

1. Fusionner `develop` → `main` : déploiement automatique en test, recette.
2. Créer une release `v1.2.0` sur `main` : le déploiement prod attend votre approbation.

Retour arrière : relancer le workflow (« Run workflow ») sur la release précédente, ou sur le VPS `DC_IMAGE_TAG=1.1.0 docker compose --env-file .env up -d`.

## Sauvegardes

- Base : `docker compose exec -T db pg_dump -U doccollect doccollect | gzip > /backup/doccollect-$(date +%F).sql.gz` (cron quotidien) ; copie vers le NAS (Hyper Backup ou rsync via Tailscale).
- Documents : volume `doccollect-prod_storage` si stockage local, sinon sauvegarde du NAS / cloud.
- Clés : `.env` de prod dans un gestionnaire de mots de passe.
