# ProxmoxCloudController Integration Tests

This project contains integration tests for the ProxmoxCloudController that interact with a real Proxmox cluster.

## Safety Measures

The integration tests are designed with several safety measures to prevent interference with production:

1. **Isolated VM ID Range**: Tests use VM IDs starting from 20000 (configurable via `TEST_MIN_VM_ID`)
2. **Test Runner Prefix**: Uses `ghr-test` prefix instead of production `ghr` (configurable via `TEST_RUNNER_PREFIX`)
3. **Automatic Cleanup**: Test VMs are automatically deleted after test completion
4. **Configuration Validation**: Prevents running with production settings

## Prerequisites

1. Access to a Proxmox cluster
2. Valid Proxmox credentials with VM management permissions
3. Network connectivity to the Proxmox API (typically port 8006)
4. Docker and Docker Compose (for test PostgreSQL database)
5. .NET 9.0 SDK

## Environment Variables

Set the following environment variables before running tests:

### Required
- `PVE_HOST`: Proxmox host/IP address
- `PVE_USERNAME`: Proxmox username (e.g., `root@pam`)
- `PVE_PASSWORD`: Proxmox password

### Optional (with defaults)
- `TEST_RUNNER_PREFIX`: VM name prefix (default: `ghr-test`)
- `TEST_MIN_VM_ID`: Starting VM ID range (default: `20000`)
- `TEST_PVE_TEMPLATE`: Template VM ID for cloning (default: `100`)
- `TEST_PROVISION_URL`: Provision script base URL (default: `https://example.com/scripts`)
- `TEST_METRIC_USER`: Metrics username (default: `test-user`)
- `TEST_METRIC_PASSWORD`: Metrics password (default: `test-password`)
- `TEST_CONTROLLER_URL`: Controller URL (default: `http://localhost:5000`)
- `TEST_GITHUB_AGENT_VERSION`: GitHub agent version (default: `2.0.0`)
- `TEST_DB_CONNECTION_STRING`: PostgreSQL connection (default: uses local Docker database on port 5433)

## Database Setup

The integration tests require a PostgreSQL database. A Docker Compose configuration is provided for convenience.

### Automatic Setup (Recommended)
The test runner script (`run-tests.sh`) automatically starts the PostgreSQL database:
```bash
./run-tests.sh  # Automatically starts database if needed
```

### Manual Setup
```bash
# Start PostgreSQL test database
docker-compose -f docker-compose.test.yml up -d

# Verify database is running
docker-compose -f docker-compose.test.yml exec postgres-test pg_isready -U test_user

# Stop database when done
docker-compose -f docker-compose.test.yml down
```

### Custom Database
To use a different PostgreSQL instance, set the connection string:
```bash
export TEST_DB_CONNECTION_STRING="Host=myhost;Port=5432;Database=test_db;Username=user;Password=pass"
```

## Running Tests

### Command Line

```bash
# Set environment variables
export PVE_HOST="your-proxmox-host.com"
export PVE_USERNAME="root@pam"
export PVE_PASSWORD="your-password"

# Navigate to test project
cd GithubActionsOrchestrator.IntegrationTests

# Run all tests
dotnet test

# Run specific test
dotnet test --filter "CreateNewRunner_ShouldCreateVmWithCorrectProperties"

# Run with detailed output
dotnet test --logger "console;verbosity=detailed"
```

### Using PowerShell (Windows)

```powershell
$env:PVE_HOST = "your-proxmox-host.com"
$env:PVE_USERNAME = "root@pam"
$env:PVE_PASSWORD = "your-password"
dotnet test
```

## Test Coverage

The integration tests cover the following scenarios:

### Core Operations
- ✅ `GetAllServersFromCsp` - Retrieves only test VMs with correct prefix
- ✅ `GetServerCountFromCsp` - Returns accurate count of test VMs
- ✅ VM ID allocation within specified range (20000+)
- ✅ Configuration validation and safety checks

### VM Lifecycle
- ✅ `CreateNewRunner` - Full VM creation with correct properties
- ✅ Custom profile support with proper configuration
- ✅ Multiple VM creation with unique IDs and names
- ✅ `DeleteRunner` - VM cleanup functionality
- ✅ Provision payload generation and validation

### Error Handling
- ✅ Invalid machine type handling
- ✅ Configuration validation
- ✅ VM ID range exhaustion

## Safety Validations

The tests include several built-in safety checks:

1. **Production Collision Prevention**: 
   - Runner prefix must not be `ghr` (production prefix)
   - VM ID range must be >= 20000

2. **Configuration Validation**:
   - Required environment variables must be set
   - MinVmId must be >= 1000
   - PVE credentials cannot be empty

3. **Cleanup Automation**:
   - All created VMs are tracked and automatically deleted
   - Test fixture disposal ensures cleanup even if tests fail

## Troubleshooting

### Common Issues

1. **Environment Variables Not Set**
   ```
   Missing required environment variables: PVE_HOST, PVE_USERNAME, PVE_PASSWORD
   ```
   Solution: Set all required environment variables

2. **Production Settings Detected**
   ```
   Cannot run integration tests with production runner prefix 'ghr'
   ```
   Solution: Use `TEST_RUNNER_PREFIX=ghr-test`

3. **VM ID Range Collision**
   ```
   Test MinVmId (5000) must be >= 20000 to avoid production VM ID range collision
   ```
   Solution: Set `TEST_MIN_VM_ID=20000` or higher

4. **Proxmox Connection Issues**
   ```
   Authentication failed: Unauthorized
   ```
   Solution: Verify PVE credentials and network connectivity

5. **Template Not Found**
   ```
   API call failed: 500 - Template VM not found
   ```
   Solution: Ensure the template VM ID exists in Proxmox

6. **Database Connection Issues**
   ```
   Failed to initialize test database: Connection refused
   ```
   Solution: Start PostgreSQL database with `docker-compose -f docker-compose.test.yml up -d`

7. **Database Already Exists Errors**
   ```
   Database creation failed
   ```
   Solution: Reset test database:
   ```bash
   docker-compose -f docker-compose.test.yml down -v
   docker-compose -f docker-compose.test.yml up -d
   ```

## CI/CD Integration

For automated testing in CI/CD pipelines, consider:

1. **Secure Secret Management**: Store PVE credentials as encrypted secrets
2. **Test Environment**: Use a dedicated test Proxmox cluster if possible
3. **Cleanup Monitoring**: Monitor for orphaned test VMs
4. **Resource Limits**: Consider VM creation rate limits and quotas

## Best Practices

1. **Run Tests Sequentially**: Some tests may interfere if run in parallel
2. **Monitor Test VMs**: Check for orphaned VMs if tests are interrupted
3. **Resource Cleanup**: Tests automatically clean up, but manual verification is recommended
4. **Network Isolation**: Ensure test VMs cannot access production networks
5. **Template Maintenance**: Keep test template VMs updated and secure