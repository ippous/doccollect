#!/usr/bin/env bash
# Installation / mise à jour de DocCollect sur le VPS, sans registre d'images ni CI.
#   Usage : sudo bash deploy/vps-setup.sh [dev|test|prod|all]   (défaut : dev)
# Prérequis : /opt/doccollect/<env>/.env en place ; lancé depuis un clone du dépôt.
# Le reverse proxy est le Caddy existant de shetland-registre (seul détenteur du port 443).
set -euo pipefail

TARGET="${1:-dev}"
REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
CADDY_CONTAINER="${CADDY_CONTAINER:-shetland-registre-web-1}"
CADDYFILE="${CADDYFILE:-/opt/shetland-registre/Caddyfile}"
PROXY_NETWORK="${PROXY_NETWORK:-shetland-registre_default}"
case "$TARGET" in
  all) ENVS="dev test prod" ;;
  dev|test|prod) ENVS="$TARGET" ;;
  *) echo "Environnement inconnu : $TARGET" >&2; exit 1 ;;
esac

echo "==> Construction de l'image (première compilation : quelques minutes)"
VERSION="0.1.$(git -C "$REPO_DIR" rev-list --count HEAD 2>/dev/null || echo 0)"
TAGS=""
for e in $ENVS; do TAGS="$TAGS -t ghcr.io/ippous/doccollect:$e"; done
docker build --build-arg VERSION="$VERSION" $TAGS "$REPO_DIR"

host_for() { [ "$1" = prod ] && echo "doccollect.jaume-eric.com" || echo "doccollect-$1.jaume-eric.com"; }

echo "==> Configuration du Caddy existant ($CADDYFILE)"
cp -n "$CADDYFILE" "$CADDYFILE.avant-doccollect" 2>/dev/null || true
CHANGED=0
for e in $ENVS; do
  HOST="$(host_for "$e")"
  if ! grep -q "^$HOST " "$CADDYFILE"; then
    # Ajout en fin de fichier (même inode : le montage du fichier dans le conteneur reste valide).
    cat >> "$CADDYFILE" <<CADDY

$HOST {
    encode gzip
    request_body {
        max_size 110MB
    }
    header {
        Strict-Transport-Security "max-age=31536000"
        $( [ "$e" != prod ] && echo 'X-Robots-Tag "noindex, nofollow"' )
    }
    reverse_proxy doccollect-$e-app-1:8080
    tls {
        issuer acme {
            disable_http_challenge
        }
    }
}
CADDY
    CHANGED=1
    echo "    + $HOST"
  else
    echo "    = $HOST déjà présent"
  fi
done

echo "==> Démarrage des environnements : $ENVS"
for e in $ENVS; do
  DIR="/opt/doccollect/$e"
  [ -f "$DIR/.env" ] || { echo "Manquant : $DIR/.env" >&2; exit 1; }
  grep -q '^DC_PROXY_NETWORK=' "$DIR/.env" || echo "DC_PROXY_NETWORK=$PROXY_NETWORK" >> "$DIR/.env"
  cp "$REPO_DIR/deploy/docker-compose.yml" "$DIR/docker-compose.yml"
  PROFILE=""; [ "$e" != prod ] && PROFILE="--profile mailpit"
  (cd "$DIR" && docker compose --env-file .env $PROFILE up -d --remove-orphans)
done

if [ "$CHANGED" = 1 ]; then
  echo "==> Validation puis rechargement de Caddy"
  docker exec "$CADDY_CONTAINER" caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
  docker exec "$CADDY_CONTAINER" caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile
fi

echo "==> Vérification"
for e in $ENVS; do
  for i in $(seq 1 30); do
    if docker run --rm --network "doccollect-${e}_internal" curlimages/curl -fsS http://app:8080/healthz >/dev/null 2>&1; then
      echo "    $e : OK → https://$(host_for "$e")"; continue 2
    fi
    sleep 4
  done
  echo "    $e : l'application ne répond pas. Journaux :"; (cd "/opt/doccollect/$e" && docker compose --env-file .env logs --tail=60 app)
done
