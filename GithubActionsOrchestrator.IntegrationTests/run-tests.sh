#!/bin/bash

# ProxmoxCloudController Integration Test Runner
# This script sets up the test environment and runs integration tests

set -e

echo "🔧 ProxmoxCloudController Integration Tests"
echo "==========================================="

# Check if required environment variables are set
if [ -z "$PVE_HOST" ]; then
    echo "❌ ERROR: PVE_HOST environment variable is required"
    echo "   Example: export PVE_HOST=\"your-proxmox-host.com\""
    exit 1
fi

if [ -z "$PVE_USERNAME" ]; then
    echo "❌ ERROR: PVE_USERNAME environment variable is required"
    echo "   Example: export PVE_USERNAME=\"root@pam\""
    exit 1
fi

if [ -z "$PVE_PASSWORD" ]; then
    echo "❌ ERROR: PVE_PASSWORD environment variable is required"
    echo "   Example: export PVE_PASSWORD=\"your-password\""
    exit 1
fi

# Set default test configuration if not provided
export TEST_RUNNER_PREFIX="${TEST_RUNNER_PREFIX:-ghr-test}"
export TEST_MIN_VM_ID="${TEST_MIN_VM_ID:-20000}"
export TEST_PVE_TEMPLATE="${TEST_PVE_TEMPLATE:-170}"

echo "✅ Environment Configuration:"
echo "   PVE Host: $PVE_HOST"
echo "   PVE Username: $PVE_USERNAME"
echo "   Test Runner Prefix: $TEST_RUNNER_PREFIX"
echo "   Test VM ID Range: $TEST_MIN_VM_ID+"
echo "   Test Template: $TEST_PVE_TEMPLATE"
echo ""

# Check if PostgreSQL test database is available
echo "🗄️  Checking PostgreSQL test database..."
if ! docker ps | grep -q "github-actions-orchestrator-test-db"; then
    echo "⚠️  PostgreSQL test database not found. Starting it..."
    echo "   Running: docker-compose -f docker-compose.test.yml up -d"
    docker-compose -f docker-compose.test.yml up -d
    
    echo "   Waiting for database to be ready..."
    timeout=30
    while [ $timeout -gt 0 ]; do
        if docker-compose -f docker-compose.test.yml exec -T postgres-test pg_isready -U test_user -d github_actions_orchestrator_test > /dev/null 2>&1; then
            echo "   ✅ PostgreSQL test database is ready!"
            break
        fi
        sleep 1
        timeout=$((timeout - 1))
    done
    
    if [ $timeout -eq 0 ]; then
        echo "   ❌ ERROR: PostgreSQL test database failed to start within 30 seconds"
        exit 1
    fi
else
    echo "   ✅ PostgreSQL test database is already running"
fi
echo ""

# Safety checks
if [ "$TEST_RUNNER_PREFIX" = "ghr" ]; then
    echo "❌ ERROR: Cannot run tests with production runner prefix 'ghr'"
    echo "   Use: export TEST_RUNNER_PREFIX=\"ghr-test\""
    exit 1
fi

if [ "$TEST_MIN_VM_ID" -lt 20000 ]; then
    echo "❌ ERROR: Test VM ID range must start at 20000 or higher"
    echo "   Use: export TEST_MIN_VM_ID=\"20000\""
    exit 1
fi

echo "🚀 Running Integration Tests..."
echo ""

# Build the project first (ensure we're in the right directory)
dotnet build

# Run the tests
if [ "$1" = "--verbose" ]; then
    dotnet test --logger "console;verbosity=detailed"
elif [ "$1" = "--filter" ] && [ -n "$2" ]; then
    dotnet test --filter "$2"
else
    dotnet test
fi

echo ""
echo "✅ Integration tests completed!"
echo ""
echo "ℹ️  If tests failed, check:"
echo "   - Proxmox connectivity and credentials"
echo "   - Template VM exists and is accessible"
echo "   - No orphaned test VMs (prefix: $TEST_RUNNER_PREFIX)"
echo "   - VM ID range availability ($TEST_MIN_VM_ID+)"