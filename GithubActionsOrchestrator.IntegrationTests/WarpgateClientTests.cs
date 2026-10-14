using System.Net;
using System.Text;
using GithubActionsOrchestrator.Models;
using GithubActionsOrchestrator.Warpgate;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GithubActionsOrchestrator.IntegrationTests;

/// <summary>
/// Self-contained tests for <see cref="WarpgateClient"/>. A minimal in-process
/// mock of the Warpgate admin API (targets + roles model) is started on a random
/// local port - no external service, database or network required.
/// </summary>
public class WarpgateClientTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _base;
    private readonly Task _acceptLoop;
    private readonly List<(string Method, string Path)> _requests = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, object>> _targets = new();

    private const string RoleId = "11111111-1111-1111-1111-111111111111";

    // The admin API is mounted at /@warpgate/admin/api on the Warpgate base URL.
    private const string Api = "/@warpgate/admin/api";

    /// <summary>
    /// When true, the mock serves an HTML page with HTTP 200 for role lookups,
    /// mimicking Warpgate's SPA catchall for unknown paths (the production
    /// failure mode that motivated the client's non-JSON guard).
    /// </summary>
    private bool _returnHtmlForRoles;

    public WarpgateClientTests()
    {
        // Find a free port and start the mock.
        int port = GetFreePort();
        _base = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_base + "/");
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoop());
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* best effort */ }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private WarpgateClient NewClient()
    {
        var config = new WarpgateConfiguration
        {
            Enabled = true,
            BaseUrl = _base,
            ApiKey = "test-token",
            AccessRole = "inspectors",
            SshUsername = "devops",
            SshTargetPort = 22,
            GatewaySshHost = "gate.example",
            GatewaySshPort = 22
        };
        return new WarpgateClient(config, NullLogger<WarpgateClient>.Instance);
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Register_CreatesTargetAndGrantsRole()
    {
        var client = NewClient();

        var (targetId, created) = await client.RegisterInspectionTargetAsync("ghr-foo", "10.0.0.5");

        Assert.True(created);
        Assert.False(string.IsNullOrEmpty(targetId));

        // A target should now be stored in the mock.
        lock (_lock)
        {
            Assert.Contains(_targets.Values, t => (string)t["name"] == "ghr-foo"
                && (string)((Dictionary<string, object>)t["options"])["host"] == "10.0.0.5");
        }

        // The role grant must have been called for the returned target id.
        lock (_lock)
        {
            Assert.Contains(_requests, r => r.Method == "POST"
                && r.Path == $"{Api}/targets/{targetId}/roles/{RoleId}");
        }
    }

    [Fact]
    public async Task Register_IsIdempotent_OnSameName()
    {
        var client = NewClient();

        var first = await client.RegisterInspectionTargetAsync("ghr-foo", "10.0.0.5");
        var second = await client.RegisterInspectionTargetAsync("ghr-foo", "10.0.0.5");

        // Second call reuses the existing target (no new creation).
        Assert.False(second.Created);
        Assert.Equal(first.TargetId, second.TargetId);
    }

    [Fact]
    public async Task Register_UpdatesHost_WhenItDrifted()
    {
        var client = NewClient();

        var first = await client.RegisterInspectionTargetAsync("ghr-foo", "10.0.0.5");
        // Same name, different IP: the existing target must be updated in place.
        var second = await client.RegisterInspectionTargetAsync("ghr-foo", "10.0.0.99");

        Assert.Equal(first.TargetId, second.TargetId);
        Assert.False(second.Created);

        lock (_lock)
        {
            var t = _targets.Values.First(x => (string)x["name"] == "ghr-foo");
            Assert.Equal("10.0.0.99", (string)((Dictionary<string, object>)t["options"])["host"]);
            Assert.Contains(_requests, r => r.Method == "PUT" && r.Path == $"{Api}/targets/{first.TargetId}");
        }
    }

    [Fact]
    public async Task Register_Throws_WhenRoleMissing()
    {
        // Point the client at a role that the mock does not know.
        var config = new WarpgateConfiguration
        {
            Enabled = true,
            BaseUrl = _base,
            ApiKey = "test-token",
            AccessRole = "does-not-exist",
            SshUsername = "devops",
            SshTargetPort = 22
        };
        var client = new WarpgateClient(config, NullLogger<WarpgateClient>.Instance);

        await Assert.ThrowsAsync<WarpgateException>(
            () => client.RegisterInspectionTargetAsync("ghr-foo", "10.0.0.5"));
    }

    [Fact]
    public async Task Register_Throws_WhenServerReturnsHtmlInsteadOfJson()
    {
        // Warpgate's SPA catchall answers unknown paths with index.html + HTTP 200.
        // The client must turn that into a descriptive WarpgateException (which
        // InspectRunner surfaces as a clean 502) instead of a JsonReaderException.
        _returnHtmlForRoles = true;
        var client = NewClient();

        WarpgateException ex = await Assert.ThrowsAsync<WarpgateException>(
            () => client.RegisterInspectionTargetAsync("ghr-foo", "10.0.0.5"));

        Assert.Contains("non-JSON", ex.Message);
    }

    [Fact]
    public async Task Remove_DeletesExistingTarget()
    {
        var client = NewClient();
        var (targetId, _) = await client.RegisterInspectionTargetAsync("ghr-foo", "10.0.0.5");

        bool removed = await client.RemoveInspectionTargetAsync("ghr-foo");

        Assert.True(removed);
        lock (_lock)
        {
            Assert.DoesNotContain(_targets, t => t.Value.ContainsKey("name")
                && (string)t.Value["name"] == "ghr-foo");
        }
    }

    [Fact]
    public async Task Remove_ReturnsFalse_WhenNoTarget()
    {
        var client = NewClient();
        bool removed = await client.RemoveInspectionTargetAsync("ghr-never-created");
        Assert.False(removed);
    }

    [Fact]
    public async Task Register_Throws_WhenApiKeyMissing()
    {
        var config = new WarpgateConfiguration
        {
            Enabled = true,
            BaseUrl = _base,
            ApiKey = "",
            AccessRole = "inspectors",
            SshUsername = "devops",
            SshTargetPort = 22
        };
        var client = new WarpgateClient(config, NullLogger<WarpgateClient>.Instance);

        await Assert.ThrowsAsync<WarpgateException>(
            () => client.RegisterInspectionTargetAsync("ghr-foo", "10.0.0.5"));
    }

    // ------------------------------------------------------------------
    // In-process mock of the Warpgate admin API
    // ------------------------------------------------------------------

    private void AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; }

            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = req.Url.AbsolutePath;
        lock (_lock) { _requests.Add((req.HttpMethod, path)); }

        string body = "";
        using (var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            body = sr.ReadToEnd();

        int code;
        string respJson;
        string contentType = "application/json";
        if (req.HttpMethod == "GET" && path.StartsWith(Api + "/roles"))
        {
            if (_returnHtmlForRoles)
            {
                // Mimic Warpgate's SPA catchall: unknown paths get index.html + 200.
                respJson = "<!doctype html><html><head><title>Warpgate</title></head><body>app</body></html>";
                contentType = "text/html";
                code = 200;
            }
            else
            {
                // Roles: only "inspectors" exists.
                respJson = "[{\"id\":\"" + RoleId + "\",\"name\":\"inspectors\",\"description\":\"ops\",\"is_default\":false}]";
                code = 200;
            }
        }
        else if (req.HttpMethod == "GET" && path == Api + "/targets")
        {
            // Targets list (search).
            respJson = ListTargetsJson();
            code = 200;
        }
        else if (req.HttpMethod == "GET" && path.StartsWith(Api + "/targets/"))
        {
            // Single target.
            (code, respJson) = GetTarget(path);
        }
        else if (req.HttpMethod == "POST" && path == Api + "/targets")
        {
            // Create target.
            (code, respJson) = CreateTarget(body);
        }
        else if (req.HttpMethod == "PUT" && path.StartsWith(Api + "/targets/"))
        {
            // Update target.
            (code, respJson) = UpdateTarget(path, body);
        }
        else if (req.HttpMethod == "POST" && path.Contains("/roles/"))
        {
            // Grant role.
            (code, respJson) = GrantRole(path);
        }
        else if (req.HttpMethod == "DELETE" && path.StartsWith(Api + "/targets/"))
        {
            // Delete target.
            (code, respJson) = DeleteTarget(path);
        }
        else
        {
            code = 404;
            respJson = "\"not found\"";
        }

        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = contentType;
        byte[] data = Encoding.UTF8.GetBytes(respJson);
        ctx.Response.ContentLength64 = data.Length;
        ctx.Response.OutputStream.Write(data, 0, data.Length);
        ctx.Response.Close();
    }

    private string ListTargetsJson()
    {
        lock (_lock)
        {
            if (_targets.Count == 0) return "[]";
            var parts = _targets.Values.Select(TargetToJson).ToArray();
            return "[" + string.Join(",", parts) + "]";
        }
    }

    private (int, string) GetTarget(string path)
    {
        string id = path.Substring((Api + "/targets/").Length);
        lock (_lock)
        {
            if (_targets.TryGetValue(id, out var t)) return (200, TargetToJson(t));
            return (404, "\"not found\"");
        }
    }

    private (int, string) CreateTarget(string body)
    {
        if (string.IsNullOrEmpty(body)) return (400, "\"bad body\"");

        // Minimal parse: pull name + options.host out of the JSON the client sent.
        string name = ExtractString(body, "name");
        string host = ExtractString(body, "host");
        Assert.NotNull(name);
        Assert.NotNull(host);

        string id = Guid.NewGuid().ToString();
        var t = new Dictionary<string, object>
        {
            ["id"] = id,
            ["name"] = name,
            ["description"] = ExtractString(body, "description") ?? "",
            ["options"] = new Dictionary<string, object>
            {
                ["kind"] = "Ssh",
                ["host"] = host,
                ["port"] = 22,
                ["username"] = "devops",
                ["allow_insecure_algos"] = false
            }
        };
        lock (_lock) { _targets[id] = t; }
        return (201, TargetToJson(t));
    }

    private (int, string) UpdateTarget(string path, string body)
    {
        string id = path.Substring((Api + "/targets/").Length);
        lock (_lock)
        {
            if (!_targets.TryGetValue(id, out var t)) return (404, "\"not found\"");
            string host = ExtractString(body, "host");
            if (host != null) ((Dictionary<string, object>)t["options"])["host"] = host;
            return (200, TargetToJson(t));
        }
    }

    private (int, string) GrantRole(string path)
    {
        // /@warpgate/admin/api/targets/{id}/roles/{roleId}
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // segs: @warpgate, admin, api, targets, {id}, roles, {roleId}
        string targetId = segs[4];
        lock (_lock)
        {
            if (!_targets.ContainsKey(targetId)) return (404, "\"not found\"");
            // Repeat grants would be 409 in real Warpgate; the client treats 409
            // as success (idempotency is covered by the reuse/update tests).
        }
        return (201, "");
    }

    private (int, string) DeleteTarget(string path)
    {
        string id = path.Substring((Api + "/targets/").Length);
        lock (_lock)
        {
            if (_targets.Remove(id)) return (204, "");
            return (404, "\"not found\"");
        }
    }

    private string TargetToJson(Dictionary<string, object> t)
    {
        var opts = (Dictionary<string, object>)t["options"];
        return "{\"id\":\"" + t["id"] + "\",\"name\":\"" + t["name"] + "\","
            + "\"description\":\"" + t["description"] + "\","
            + "\"options\":{\"kind\":\"Ssh\",\"host\":\"" + opts["host"]
            + "\",\"port\":22,\"username\":\"devops\",\"allow_insecure_algos\":false}}";
    }

    private static string ExtractString(string json, string key)
    {
        // Very small helper: finds "key":"value". Good enough for the mock.
        int i = json.IndexOf("\"" + key + "\":", StringComparison.Ordinal);
        if (i < 0) return null;
        int start = json.IndexOf('"', i + key.Length + 3);
        if (start < 0) return null;
        int end = json.IndexOf('"', start + 1);
        if (end < 0) return null;
        return json.Substring(start + 1, end - start - 1);
    }
}
