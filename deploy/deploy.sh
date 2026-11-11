#!/usr/bin/env bash
# Deploy/atualização do stack no Docker Swarm com as imagens do GHCR.
#   ./deploy.sh                # deploy (baixa as imagens novas da TAG do .env e faz rolling update)
#   STACK=outro ./deploy.sh    # nome do stack (padrão: arconet)
set -euo pipefail
cd "$(dirname "$0")"

STACK="${STACK:-arconet}"
[ -f .env ] || { echo "Crie o .env a partir do .env.example."; exit 1; }

# Variáveis usadas no docker-stack.yml (REGISTRY, TAG, FRONTEND_ORIGIN, PUBLIC_GATEWAY_URL, SMB_*...)
set -a
. ./.env
set +a

docker stack deploy -c docker-stack.yml --with-registry-auth --resolve-image always --prune "$STACK"
docker stack services "$STACK"
