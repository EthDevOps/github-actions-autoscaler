#!/usr/bin/env bash
set -euo pipefail

echo "Grabbing secrets from bao..."
export BAO_ADDR="https://tyrell.ethquokkaops.io"
export DO_TOKEN=$(bao kv get -field=DO_TOKEN secret/ethquokkaops/github-runners/production/app)
export HETZNER_TOKEN=$(bao kv get -field=HCLOUD_TOKEN secret/ethquokkaops/github-runners/production/app)
export PVE_USER=$(bao kv get -field=PVE_USER secret/ethquokkaops/github-runners/production/app)
export PVE_PASSWORD=$(bao kv get -field=PVE_PASSWORD secret/ethquokkaops/github-runners/production/app)
export GITHUB_TOKEN=$(bao kv get -field=GH_ETHDEVOPS secret/ethquokkaops/github-runners/production/app)

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

CONFIG_FILE="${CONFIG_FILE:-${PROJECT_DIR}/config.dev.json}"
COMPOSE_FILE="${PROJECT_DIR}/GithubActionsOrchestrator.IntegrationTests/docker-compose.test.yml"

# --- Validate config exists ---
if [[ ! -f "$CONFIG_FILE" ]]; then
    echo "ERROR: Config file not found: $CONFIG_FILE"
    echo "       Copy config.dev.json and fill in your values:"
    echo "       cp config.dev.json config.dev.json  # edit with your secrets"
    exit 1
fi

# --- Safety: check prefix is not production ---
PREFIX=$(grep -o '"RunnerPrefix"[[:space:]]*:[[:space:]]*"[^"]*"' "$CONFIG_FILE" | grep -o '"[^"]*"$' | tr -d '"')
if [[ "$PREFIX" == "ghr" ]]; then
    echo "ERROR: RunnerPrefix in $CONFIG_FILE is 'ghr' (production)."
    echo "       Change it to 'ghr-dev' or another non-production prefix."
    exit 1
fi
echo "RunnerPrefix: $PREFIX"

# --- Start test database if not running ---
if ! docker ps --format '{{.Names}}' | grep -q 'github-actions-orchestrator-test-db'; then
    echo "Starting test database..."
    docker compose -f "$COMPOSE_FILE" up -d
    echo "Waiting for database to be ready..."
    for i in {1..30}; do
        if docker exec github-actions-orchestrator-test-db pg_isready -U test_user -d github_actions_orchestrator_test > /dev/null 2>&1; then
            echo "Database is ready."
            break
        fi
        sleep 1
    done
else
    echo "Test database already running."
fi

# --- Start cloudflared tunnel for runner callbacks ---
LISTEN_PORT=$(grep -o '"ListenUrl"[[:space:]]*:[[:space:]]*"[^"]*"' "$CONFIG_FILE" | grep -oE '[0-9]+' | tail -1)
LISTEN_PORT="${LISTEN_PORT:-5050}"

echo "Starting cloudflared tunnel to expose port $LISTEN_PORT..."
TUNNEL_LOG=$(mktemp)
cloudflared tunnel --url "http://localhost:${LISTEN_PORT}" > "$TUNNEL_LOG" 2>&1 &
CLOUDFLARED_PID=$!

# Clean up cloudflared on exit
cleanup() {
    echo ""
    echo "Shutting down cloudflared (PID $CLOUDFLARED_PID)..."
    kill "$CLOUDFLARED_PID" 2>/dev/null || true
    rm -f "$TUNNEL_LOG"
}
trap cleanup EXIT

# Wait for cloudflared to print the tunnel URL
TUNNEL_URL=""
for i in {1..30}; do
    TUNNEL_URL=$(grep -oE 'https://[a-zA-Z0-9-]+\.trycloudflare\.com' "$TUNNEL_LOG" 2>/dev/null | head -1 || true)
    if [[ -n "$TUNNEL_URL" ]]; then
        break
    fi
    sleep 1
done

if [[ -z "$TUNNEL_URL" ]]; then
    echo "ERROR: Failed to get tunnel URL from cloudflared after 30s."
    echo "       Log output:"
    cat "$TUNNEL_LOG"
    exit 1
fi

export CONTROLLER_URL="$TUNNEL_URL"
echo "Tunnel URL: $CONTROLLER_URL"
echo "Runners will call back to: $CONTROLLER_URL/runner-state"

# --- Run the orchestrator ---
echo ""
echo "Starting orchestrator with config: $CONFIG_FILE"
echo "Listening on http://0.0.0.0:$LISTEN_PORT (tunneled via $CONTROLLER_URL)"
echo ""

export CONFIG_FILE
exec dotnet run --project "$PROJECT_DIR"
