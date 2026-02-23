#!/usr/bin/env bash
set -euo pipefail

# Webhook simulator for local GitHub Actions Orchestrator testing.
# Sends properly formatted GitHub webhook_job payloads to the local instance.
#
# Usage:
#   ./scripts/simulate-webhook.sh queued      --org MyOrg --repo MyOrg/MyRepo --size small
#   ./scripts/simulate-webhook.sh in_progress --job-id 12345 --runner-name ghr-dev-abc123
#   ./scripts/simulate-webhook.sh completed   --job-id 12345

URL="${WEBHOOK_URL:-http://localhost:5050/github-webhook}"
PREFIX="${RUNNER_PREFIX:-ghr-dev}"

# --- Safety check ---
if [[ "$PREFIX" == "ghr" ]]; then
    echo "ERROR: RUNNER_PREFIX is 'ghr' (production). Refusing to send webhook."
    echo "       Use 'ghr-dev' (default) or set RUNNER_PREFIX to a non-production value."
    exit 1
fi

usage() {
    cat <<EOF
Usage: $0 <action> [options]

Actions:
  queued        Simulate a job queued event (triggers runner provisioning)
  in_progress   Simulate a job in_progress event (runner picked up the job)
  completed     Simulate a job completed event (triggers runner deletion)

Options for 'queued':
  --org  <name>       GitHub organization name (required)
  --repo <owner/repo> Repository full name (required)
  --size <size>       Runner size label, e.g. small (required)
  --job-id <id>       Job ID (default: random)
  --custom            Add custom runner label
  --profile <name>    Profile name for custom runs (default: default)

Options for 'in_progress':
  --job-id <id>         Job ID (required)
  --runner-name <name>  Runner hostname (required)
  --org <name>          Organization name (default: TestOrg)
  --repo <owner/repo>   Repository full name (default: TestOrg/TestRepo)

Options for 'completed':
  --job-id <id>         Job ID (required)
  --runner-name <name>  Runner hostname (default: unknown)
  --org <name>          Organization name (default: TestOrg)
  --repo <owner/repo>   Repository full name (default: TestOrg/TestRepo)

Environment:
  WEBHOOK_URL     Target URL (default: http://localhost:5050/github-webhook)
  RUNNER_PREFIX   Runner prefix (default: ghr-dev)
EOF
    exit 1
}

send_webhook() {
    local payload="$1"
    echo "Sending $ACTION webhook to $URL"
    echo "$payload" | jq . 2>/dev/null || true
    echo ""
    HTTP_CODE=$(curl -s -o /dev/stderr -w "%{http_code}" \
        -X POST "$URL" \
        -H "Content-Type: application/json" \
        -d "$payload" 2>&1)
    echo ""
    echo "Response: HTTP $HTTP_CODE"
}

# --- Parse action ---
ACTION="${1:-}"
if [[ -z "$ACTION" ]]; then
    usage
fi
shift

# --- Parse options ---
ORG=""
REPO=""
SIZE=""
JOB_ID=""
RUNNER_NAME=""
CUSTOM=false
PROFILE="default"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --org)        ORG="$2"; shift 2 ;;
        --repo)       REPO="$2"; shift 2 ;;
        --size)       SIZE="$2"; shift 2 ;;
        --job-id)     JOB_ID="$2"; shift 2 ;;
        --runner-name) RUNNER_NAME="$2"; shift 2 ;;
        --custom)     CUSTOM=true; shift ;;
        --profile)    PROFILE="$2"; shift 2 ;;
        *)            echo "Unknown option: $1"; usage ;;
    esac
done

# Generate a random job ID if not provided
if [[ -z "$JOB_ID" ]]; then
    JOB_ID=$((RANDOM * RANDOM))
fi

case "$ACTION" in
    queued)
        if [[ -z "$ORG" || -z "$REPO" || -z "$SIZE" ]]; then
            echo "ERROR: --org, --repo, and --size are required for 'queued'"
            usage
        fi

        LABELS="[\"self-hosted\", \"self-hosted-${PREFIX}\", \"self-hosted-${PREFIX}-${SIZE}\", \"${SIZE}\"]"
        if [[ "$CUSTOM" == true ]]; then
            LABELS="[\"self-hosted\", \"self-hosted-${PREFIX}\", \"self-hosted-${PREFIX}-custom\", \"self-hosted-${PREFIX}-${SIZE}\", \"${SIZE}\", \"profile-${PROFILE}\"]"
        fi

        PAYLOAD=$(cat <<EOF
{
  "action": "queued",
  "workflow_job": {
    "id": ${JOB_ID},
    "labels": ${LABELS},
    "url": "https://api.github.com/repos/${REPO}/actions/jobs/${JOB_ID}",
    "runner_name": null
  },
  "repository": {
    "full_name": "${REPO}"
  },
  "organization": {
    "login": "${ORG}"
  }
}
EOF
)
        send_webhook "$PAYLOAD"
        echo "Job ID: $JOB_ID (use this for in_progress/completed)"
        ;;

    in_progress)
        if [[ -z "$JOB_ID" || -z "$RUNNER_NAME" ]]; then
            echo "ERROR: --job-id and --runner-name are required for 'in_progress'"
            usage
        fi
        ORG="${ORG:-TestOrg}"
        REPO="${REPO:-TestOrg/TestRepo}"

        PAYLOAD=$(cat <<EOF
{
  "action": "in_progress",
  "workflow_job": {
    "id": ${JOB_ID},
    "labels": ["self-hosted", "self-hosted-${PREFIX}"],
    "url": "https://api.github.com/repos/${REPO}/actions/jobs/${JOB_ID}",
    "runner_name": "${RUNNER_NAME}"
  },
  "repository": {
    "full_name": "${REPO}"
  },
  "organization": {
    "login": "${ORG}"
  }
}
EOF
)
        send_webhook "$PAYLOAD"
        ;;

    completed)
        if [[ -z "$JOB_ID" ]]; then
            echo "ERROR: --job-id is required for 'completed'"
            usage
        fi
        ORG="${ORG:-TestOrg}"
        REPO="${REPO:-TestOrg/TestRepo}"
        RUNNER_NAME="${RUNNER_NAME:-unknown}"

        PAYLOAD=$(cat <<EOF
{
  "action": "completed",
  "conclusion": "success",
  "workflow_job": {
    "id": ${JOB_ID},
    "labels": ["self-hosted", "self-hosted-${PREFIX}"],
    "url": "https://api.github.com/repos/${REPO}/actions/jobs/${JOB_ID}",
    "runner_name": "${RUNNER_NAME}"
  },
  "repository": {
    "full_name": "${REPO}"
  },
  "organization": {
    "login": "${ORG}"
  }
}
EOF
)
        send_webhook "$PAYLOAD"
        ;;

    *)
        echo "Unknown action: $ACTION"
        usage
        ;;
esac
