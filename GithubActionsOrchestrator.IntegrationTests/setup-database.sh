#!/bin/bash

# PostgreSQL Test Database Setup Script
# This script sets up the test database for ProxmoxCloudController integration tests

set -e

echo "🗄️  Setting up PostgreSQL test database..."
echo "==========================================="

# Check if Docker is available
if ! command -v docker &> /dev/null; then
    echo "❌ ERROR: Docker is not installed or not in PATH"
    exit 1
fi

if ! command -v docker-compose &> /dev/null; then
    echo "❌ ERROR: Docker Compose is not installed or not in PATH"
    exit 1
fi

# Check if database is already running
if docker ps | grep -q "github-actions-orchestrator-test-db"; then
    echo "⚠️  PostgreSQL test database is already running"
    echo "   To restart: ./setup-database.sh restart"
    echo "   To stop: docker-compose -f docker-compose.test.yml down"
    if [ "$1" != "restart" ]; then
        exit 0
    fi
fi

# Handle restart option
if [ "$1" = "restart" ]; then
    echo "🔄 Restarting PostgreSQL test database..."
    docker-compose -f docker-compose.test.yml down -v
fi

# Start the database
echo "🚀 Starting PostgreSQL test database..."
docker-compose -f docker-compose.test.yml up -d

# Wait for database to be ready
echo "⏳ Waiting for database to be ready..."
timeout=30
while [ $timeout -gt 0 ]; do
    if docker-compose -f docker-compose.test.yml exec -T postgres-test pg_isready -U test_user -d github_actions_orchestrator_test > /dev/null 2>&1; then
        echo "✅ PostgreSQL test database is ready!"
        break
    fi
    sleep 1
    timeout=$((timeout - 1))
done

if [ $timeout -eq 0 ]; then
    echo "❌ ERROR: PostgreSQL test database failed to start within 30 seconds"
    echo "   Check logs: docker-compose -f docker-compose.test.yml logs postgres-test"
    exit 1
fi

# Show connection info
echo ""
echo "✅ Database Setup Complete!"
echo "================================"
echo "Host: localhost"
echo "Port: 5433"
echo "Database: github_actions_orchestrator_test"
echo "Username: test_user"
echo "Password: test_password"
echo ""
echo "Connection String: Host=localhost;Port=5433;Database=github_actions_orchestrator_test;Username=test_user;Password=test_password"
echo ""
echo "Useful commands:"
echo "  Stop database: docker-compose -f docker-compose.test.yml down"
echo "  View logs:     docker-compose -f docker-compose.test.yml logs postgres-test"
echo "  Connect:       docker-compose -f docker-compose.test.yml exec postgres-test psql -U test_user -d github_actions_orchestrator_test"
echo ""
echo "Ready to run integration tests: ./run-tests.sh"