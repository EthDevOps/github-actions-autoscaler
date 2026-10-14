using System.Net;
using System.Text.Json;
using GithubActionsOrchestrator.Models;

namespace GithubActionsOrchestrator.Warpgate;

/// <summary>
/// Thrown when the Warpgate admin API rejects a request. Message is safe to surface
/// to the dashboard operator (no credentials included).
/// </summary>
public class WarpgateException : Exception
{
    public WarpgateException(string message) : base(message)
    {
    }
}

/// <summary>
/// Minimal client for the Warpgate admin API (targets + roles model).
/// Used by the "hold for inspection" feature to register a runner VM as an SSH
/// target in Warpgate and grant the configured access role access to it.
///
/// The admin API is mounted at /@warpgate/admin/api (alias: /_warpgate/admin/api)
/// on the Warpgate base URL - there is no /api/v1 prefix (verified against the
/// warpgate-protocol-http route mounting and the Terraform provider). Routes:
/// GET/POST /targets, GET/PUT/DELETE /targets/{id}, GET /roles?search=,
/// POST /targets/{id}/roles/{roleId}.
///
/// Auth: admin API token sent as the X-Warpgate-Token header (stable across
/// Warpgate v0.21+; the older "apps"/"groups" API is not supported).
/// </summary>
public class WarpgateClient
{
    /// <summary>Mount point of the Warpgate admin API relative to the base URL.</summary>
    private const string AdminApi = "/@warpgate/admin/api";

    private readonly WarpgateConfiguration _config;
    private readonly HttpClient _http;
    private readonly ILogger<WarpgateClient> _logger;

    public WarpgateClient(WarpgateConfiguration config, ILogger<WarpgateClient> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;

        _http = new HttpClient
        {
            BaseAddress = new Uri((config.BaseUrl ?? "https://eu.gate.ethdevops.io").TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        if (!string.IsNullOrEmpty(config.ApiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Warpgate-Token", config.ApiKey);
    }

    /// <summary>
    /// Registers the runner (by name) as an SSH target in Warpgate pointing at the
    /// given host, reusing an existing target with the same name (updating its host
    /// when it drifted), and grants the configured access role access to it.
    /// Returns the Warpgate target id and whether the target was newly created.
    /// </summary>
    public async Task<(string TargetId, bool Created)> RegisterInspectionTargetAsync(string name, string host)
    {
        Validate();

        string roleId = await FindRoleIdByNameAsync(_config.AccessRole)
            ?? throw new WarpgateException($"Warpgate role '{_config.AccessRole}' not found. Check the Warpgate.AccessRole configuration.");

        string targetId = await FindTargetIdByNameAsync(name);
        bool created = false;

        if (targetId == null)
        {
            targetId = await CreateSshTargetAsync(name, host,
                "GitHub Actions runner under inspection (held by the orchestrator)");
            created = true;
        }
        else
        {
            // A leftover target from a previous holder of this name may point at a stale IP.
            var existing = await GetTargetAsync(targetId);
            if (existing is { } e && e.Host != host)
            {
                _logger.LogWarning("Warpgate target '{Name}' exists but points at {OldHost} instead of {NewHost} - updating", name, e.Host, host);
                await UpdateSshTargetAsync(targetId, name, host,
                    "GitHub Actions runner under inspection (held by the orchestrator)");
            }
        }

        bool granted = await GrantRoleToTargetAsync(targetId, roleId);
        if (!granted)
            throw new WarpgateException($"Unable to grant role '{_config.AccessRole}' to target '{name}'.");

        return (targetId, created);
    }

    /// <summary>
    /// Removes the Warpgate target with the given runner name (best effort).
    /// Returns true when a target was found and deleted.
    /// </summary>
    public async Task<bool> RemoveInspectionTargetAsync(string name)
    {
        Validate();

        string targetId = await FindTargetIdByNameAsync(name);
        if (targetId == null)
            return false;

        return await DeleteTargetAsync(targetId);
    }

    // ------------------------------------------------------------------
    // Low-level API calls
    // ------------------------------------------------------------------

    private void Validate()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
            throw new WarpgateException("Warpgate is enabled but no API key is configured (Warpgate.ApiKey).");
        if (string.IsNullOrEmpty(_config.AccessRole))
            throw new WarpgateException("Warpgate is enabled but no access role is configured (Warpgate.AccessRole).");
    }

    private async Task<string?> FindRoleIdByNameAsync(string name)
    {
        JsonElement? roles = await GetJsonArrayAsync($"{AdminApi}/roles?search={Uri.EscapeDataString(name)}");
        if (roles == null)
            return null;

        foreach (JsonElement role in roles.Value.EnumerateArray())
        {
            if (role.TryGetProperty("name", out JsonElement n) &&
                string.Equals(n.GetString(), name, StringComparison.Ordinal))
            {
                return role.GetProperty("id").GetString();
            }
        }
        return null;
    }

    private async Task<string?> FindTargetIdByNameAsync(string name)
    {
        JsonElement? targets = await GetJsonArrayAsync($"{AdminApi}/targets?search={Uri.EscapeDataString(name)}");
        if (targets == null)
            return null;

        foreach (JsonElement target in targets.Value.EnumerateArray())
        {
            if (target.TryGetProperty("name", out JsonElement n) &&
                string.Equals(n.GetString(), name, StringComparison.Ordinal))
            {
                return target.GetProperty("id").GetString();
            }
        }
        return null;
    }

    private async Task<(string Host, JsonElement Raw)?> GetTargetAsync(string targetId)
    {
        string path = $"{AdminApi}/targets/{targetId}";
        HttpResponseMessage response = await _http.GetAsync(path);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }

        JsonElement el = await ReadJsonAsync($"GET {path}", () => Task.FromResult(response));
        return (ExtractHost(el), el);
    }

    private static string ExtractHost(JsonElement target)
    {
        // options is a discriminated object; the SSH options carry "host".
        if (target.TryGetProperty("options", out JsonElement options) &&
            options.TryGetProperty("host", out JsonElement host))
        {
            return host.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private async Task<string> CreateSshTargetAsync(string name, string host, string description)
    {
        string path = $"{AdminApi}/targets";
        HttpResponseMessage response = await _http.PostAsJsonAsync(path, BuildTargetBody(name, host, description));
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new WarpgateException($"Creating Warpgate target '{name}' failed: HTTP {(int)response.StatusCode} {body}");

        JsonElement target = ParseJson(body, $"POST {path}");
        return target.GetProperty("id").GetString()
            ?? throw new WarpgateException($"Warpgate target creation for '{name}' returned no id.");
    }

    private async Task UpdateSshTargetAsync(string targetId, string name, string host, string description)
    {
        string path = $"{AdminApi}/targets/{targetId}";
        HttpResponseMessage response = await _http.PutAsJsonAsync(path, BuildTargetBody(name, host, description));
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new WarpgateException($"Updating Warpgate target '{name}' failed: HTTP {(int)response.StatusCode} {body}");
    }

    private object BuildTargetBody(string name, string host, string description)
    {
        return new
        {
            name,
            description,
            options = new
            {
                kind = "Ssh",
                host,
                port = _config.SshTargetPort,
                username = _config.SshUsername,
                allow_insecure_algos = false,
                // Use Warpgate's default client key(s) - the runner images already
                // carry the corresponding public key (installed by the provision script).
                auth = new { kind = "PublicKey" }
            },
            // Required on current Warpgate versions; ignored by older ones.
            ticket_requests_disabled = true,
            ticket_require_approval = false,
            require_approval = false
        };
    }

    /// <summary>Grants the role access to the target. 201 = granted, 409 = already granted.</summary>
    private async Task<bool> GrantRoleToTargetAsync(string targetId, string roleId)
    {
        try
        {
            string path = $"{AdminApi}/targets/{targetId}/roles/{roleId}";
            HttpResponseMessage response = await _http.PostAsync(path, content: null);
            if (response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.Conflict)
            {
                if (response.StatusCode == HttpStatusCode.Conflict)
                    _logger.LogDebug("Warpgate role {Role} already granted to target {Target}", roleId, targetId);
                return true;
            }

            string body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Warpgate role grant failed: HTTP {Status} {Body}", (int)response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Warpgate role grant for target {Target} threw", targetId);
            return false;
        }
    }

    private async Task<bool> DeleteTargetAsync(string targetId)
    {
        try
        {
            string path = $"{AdminApi}/targets/{targetId}";
            HttpResponseMessage response = await _http.DeleteAsync(path);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return false;
            if (response.StatusCode == HttpStatusCode.NoContent)
                return true;

            string body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Warpgate target deletion failed: HTTP {Status} {Body}", (int)response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Warpgate target deletion for {Target} threw", targetId);
            return false;
        }
    }

    private async Task<JsonElement?> GetJsonArrayAsync(string path)
    {
        try
        {
            HttpResponseMessage response = await _http.GetAsync(path);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                throw new WarpgateException($"Warpgate GET {path} failed: HTTP {(int)response.StatusCode} {body}");
            }

            JsonElement el = await ReadJsonAsync($"GET {path}", () => Task.FromResult(response));
            return el.ValueKind == JsonValueKind.Array ? el : null;
        }
        catch (HttpRequestException ex)
        {
            throw new WarpgateException($"Warpgate GET {path} failed: {ex.Message}");
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(string context, Func<Task<HttpResponseMessage>> requestFactory)
    {
        using HttpResponseMessage response = await requestFactory();
        string text = await response.Content.ReadAsStringAsync();
        return ParseJson(text, context);
    }

    /// <summary>
    /// Parses a Warpgate response body as JSON. Warpgate's SPA catchall serves
    /// index.html with HTTP 200 for unknown paths, so a body that doesn't start
    /// with '{' or '[' means the request didn't hit the admin API - surface a
    /// descriptive WarpgateException instead of an unhandled JsonReaderException.
    /// </summary>
    private static JsonElement ParseJson(string text, string context)
    {
        string trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            string snippet = trimmed.Length > 120 ? trimmed.Substring(0, 120) + "…" : trimmed;
            throw new WarpgateException(
                $"Warpgate returned a non-JSON response to {context} " +
                $"(the admin API path may be missing or changed). Response started with: \"{snippet}\"");
        }
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
