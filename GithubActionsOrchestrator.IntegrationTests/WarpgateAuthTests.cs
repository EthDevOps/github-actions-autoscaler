using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using GithubActionsOrchestrator.Auth;
using GithubActionsOrchestrator.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GithubActionsOrchestrator.IntegrationTests;

/// <summary>
/// Self-contained tests for the Warpgate-based authorization of mutating dashboard calls.
/// Covers the policy logic directly and the full chain (header -> identity -> CanMutate
/// policy) against a minimal in-process web app - no external service required.
/// </summary>
public class WarpgateAuthTests
{
    // ---------- policy logic (unit) ----------

    private static WarpgateAuthConfiguration Cfg(Action<WarpgateAuthConfiguration> mutate = null)
    {
        var cfg = new WarpgateAuthConfiguration { Enabled = true };
        mutate?.Invoke(cfg);
        return cfg;
    }

    private static ClaimsPrincipal Principal(string username, string authType = WarpgateAuth.AuthTypeUser)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("username", username),
            new Claim(WarpgateAuth.AuthTypeClaim, authType),
        }, WarpgateAuth.SchemeName, nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void Unauthenticated_user_cannot_mutate()
    {
        Assert.False(WarpgateAuth.IsAuthorizedToMutate(null, Cfg()));
        var anon = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.False(WarpgateAuth.IsAuthorizedToMutate(anon, Cfg()));
    }

    [Fact]
    public void Any_user_can_mutate_when_allowlist_empty()
    {
        Assert.True(WarpgateAuth.IsAuthorizedToMutate(Principal("markus"), Cfg()));
    }

    [Fact]
    public void Allowlist_is_case_insensitive_and_filters_users()
    {
        var cfg = Cfg(c => c.AuthorizedUsers.Add("Markus"));
        Assert.True(WarpgateAuth.IsAuthorizedToMutate(Principal("markus"), cfg));
        Assert.False(WarpgateAuth.IsAuthorizedToMutate(Principal("someoneelse"), cfg));
    }

    [Fact]
    public void Ticket_session_cannot_mutate_by_default()
    {
        Assert.False(WarpgateAuth.IsAuthorizedToMutate(Principal("markus", WarpgateAuth.AuthTypeTicket), Cfg()));
    }

    [Fact]
    public void Ticket_session_can_mutate_when_require_user_auth_type_off()
    {
        var cfg = Cfg(c => c.RequireUserAuthType = false);
        Assert.True(WarpgateAuth.IsAuthorizedToMutate(Principal("markus", WarpgateAuth.AuthTypeTicket), cfg));
    }

    [Fact]
    public void Auth_type_defaults_to_user_when_header_absent_in_principal_is_impossible()
    {
        // The handler always sets the claim; a principal without it is "unknown" and must
        // be treated as not-a-user-session when RequireUserAuthType is on.
        var identity = new ClaimsIdentity(new[] { new Claim("username", "markus") }, WarpgateAuth.SchemeName, nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);
        Assert.False(WarpgateAuth.IsAuthorizedToMutate(new ClaimsPrincipal(identity), Cfg()));
    }

    // ---------- full chain (in-process web app) ----------

    private sealed class TestApp : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public string Base { get; }

        public TestApp(WarpgateAuthConfiguration cfg)
        {
            int port = GetFreePort();
            Base = $"http://127.0.0.1:{port}";

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(Base);
            builder.AddWarpgateAuth(cfg);

            _app = builder.Build();
            _app.UseAuthentication();
            _app.UseAuthorization();
            _app.MapGet("/probe", [Authorize(Policy = WarpgateAuth.CanMutatePolicy)] (ClaimsPrincipal user) =>
                Results.Ok(new
                {
                    user = WarpgateAuth.GetUsername(user),
                    authType = WarpgateAuth.GetAuthType(user),
                }));

            _app.StartAsync().GetAwaiter().GetResult();
        }

        public async Task<(int Status, JsonElement? Body)> Probe(string username = null, string authType = null)
        {
            using var client = new HttpClient { BaseAddress = new Uri(Base) };
            var req = new HttpRequestMessage(HttpMethod.Get, "/probe");
            if (username != null)
                req.Headers.Add("X-Warpgate-Username", username);
            if (authType != null)
                req.Headers.Add("X-Warpgate-Authentication-Types", authType);
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode, string.IsNullOrWhiteSpace(body) ? null : System.Text.Json.JsonDocument.Parse(body).RootElement.Clone());
        }

        public async ValueTask DisposeAsync()
        {
            try { await _app.StopAsync(); } catch { /* best effort */ }
            await _app.DisposeAsync();
        }

        private static int GetFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }

    [Fact]
    public async Task Request_without_headers_is_rejected_when_enabled()
    {
        await using var app = new TestApp(new WarpgateAuthConfiguration { Enabled = true });
        var (status, _) = await app.Probe();
        Assert.Equal(401, status);
    }

    [Fact]
    public async Task User_session_can_mutate_when_allowlist_empty()
    {
        await using var app = new TestApp(new WarpgateAuthConfiguration { Enabled = true });
        var (status, body) = await app.Probe("markus");
        Assert.Equal(200, status);
        Assert.Equal("markus", body.Value.GetProperty("user").GetString());
        Assert.Equal("user", body.Value.GetProperty("authType").GetString());
    }

    [Fact]
    public async Task Allowlist_blocks_other_users()
    {
        await using var app = new TestApp(new WarpgateAuthConfiguration
        {
            Enabled = true,
            AuthorizedUsers = { "markus" },
        });
        Assert.Equal(200, (await app.Probe("markus")).Status);
        Assert.Equal(403, (await app.Probe("intruder")).Status);
    }

    [Fact]
    public async Task Ticket_session_is_rejected_by_default()
    {
        await using var app = new TestApp(new WarpgateAuthConfiguration { Enabled = true });
        Assert.Equal(403, (await app.Probe("markus", "ticket")).Status);
    }

    [Fact]
    public async Task Ticket_session_allowed_when_require_user_auth_type_off()
    {
        await using var app = new TestApp(new WarpgateAuthConfiguration
        {
            Enabled = true,
            RequireUserAuthType = false,
        });
        var (status, body) = await app.Probe("markus", "ticket");
        Assert.Equal(200, status);
        Assert.Equal("ticket", body.Value.GetProperty("authType").GetString());
    }

    [Fact]
    public async Task Auth_disabled_falls_back_to_api_key_gate_only()
    {
        await using var app = new TestApp(new WarpgateAuthConfiguration { Enabled = false });
        // No header, auth disabled: the CanMutate policy is a pass-through
        // (the API key middleware is not part of this minimal app).
        Assert.Equal(200, (await app.Probe()).Status);
    }
}
