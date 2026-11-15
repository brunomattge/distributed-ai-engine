#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
ENV_FILE="$ROOT_DIR/infra/.env"
COMPOSE_FILE="$ROOT_DIR/infra/compose.yaml"

if [[ ! -f "$ENV_FILE" ]]; then
  python3 - "$ENV_FILE" <<'PY'
from pathlib import Path
import secrets
import sys

path = Path(sys.argv[1])
password = secrets.token_urlsafe(32)
path.write_text(
    "POSTGRES_DB=distributed_ai\n"
    "POSTGRES_USER=distributed_ai\n"
    f"POSTGRES_PASSWORD={password}\n"
    'CHAT_DATABASE_URL="Host=127.0.0.1;Port=5432;Database=distributed_ai;'
    f'Username=distributed_ai;Password={password};Timeout=5;Command Timeout=30"\n',
    encoding="utf-8",
)
path.chmod(0o600)
PY
  printf 'Created a private local database configuration at infra/.env.\n'
fi

docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d postgres

for attempt in $(seq 1 40); do
  if docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T postgres \
    pg_isready -U distributed_ai -d distributed_ai >/dev/null 2>&1; then
    docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T postgres \
      psql -v ON_ERROR_STOP=1 -U distributed_ai -d distributed_ai \
      -f /docker-entrypoint-initdb.d/002_agent_memories.sql >/dev/null
    printf 'Project PostgreSQL is ready at 127.0.0.1:5432.\n'
    exit 0
  fi
  sleep 1
done

docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" logs --tail=60 postgres >&2
printf 'PostgreSQL did not become ready within 40 seconds.\n' >&2
exit 1
