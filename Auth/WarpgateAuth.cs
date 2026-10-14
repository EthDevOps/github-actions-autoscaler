using System.Security.Claims;
using System.Text.Encodings.Web;
using GithubActionsOrchestrator.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GithubActionsOrchestrator.Auth;

/// <summary>
/// Authorization for mutating dashboard API calls based on Warpgate. The viewer is fronted
/// by a Warpgate HTTP target: Warpgate authenticates the user (SSO) and injects
/// <c>X-Warpgate-Username</c> (+ <c>X-Warpgate-Authentication-Types</c>) on every request it
/// proxies. Warpgate strips any client-supplied <c>x-warpgate-*</c> headers, so these are
/// trustworthy as long as the orchestrator is only reachable through the Warpgate front.
/// The API key gate remains in front of /api as an additional (not sufficient) layer.
///
/// Note: Warpgate does not forward user roles, so authorization here is username-based
/// (an empty allowlist means any authenticated Warpgate user may mutate).
/// </summary>
public static class WarpgateAuth
{
    public const string SchemeName = "Warpgate";
    public const string CanMutatePolicy = "CanMutate";
    public const string AuthTypeClaim = "warpgate-auth-type";
    public const string AuthTypeUser = "user";
    public const string AuthTypeTicket = "ticket";

    public static void AddWarpgateAuth(this WebApplicationBuilder builder, WarpgateAuthConfiguration cfg)
    {
        builder.Services.AddAuthentication(SchemeName)
            .AddScheme<WarpgateAuthOptions, WarpgateAuthenticationHandler>(SchemeName, _ => { });

        builder.Services.AddOptions<WarpgateAuthOptions>()
            .Configure(o =>
            {
                o.UsernameHeader = cfg.UsernameHeader;
                o.AuthTypeHeader = cfg.AuthTypeHeader;
                o.AuthorizedUsers = cfg.AuthorizedUsers;
                o.RequireUserAuthType = cfg.RequireUserAuthType;
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(CanMutatePolicy, policy =>
            {
                if (!cfg.Enabled)
                {
                    // Auth disabled: fall back to the API key gate only.
                    policy.RequireAssertion(_ => true);
                    return;
                }

                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(ctx => IsAuthorizedToMutate(ctx.User, cfg));
            });
    }

    /// <summary>
    /// A user may mutate when no allowlist is configured (any authenticated Warpgate user),
    /// or when their username is explicitly allowed. One-time ticket sessions never count
    /// unless <see cref="WarpgateAuthConfiguration.RequireUserAuthType"/> is off.
    /// </summary>
    public static bool IsAuthorizedToMutate(ClaimsPrincipal user, WarpgateAuthConfiguration cfg)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return false;

        if (cfg.RequireUserAuthType && GetAuthType(user) != AuthTypeUser)
            return false;

        var users = cfg.AuthorizedUsers ?? new List<string>();
        if (users.Count == 0)
            return true;

        return users.Contains(GetUsername(user), StringComparer.OrdinalIgnoreCase);
    }

    public static string GetUsername(ClaimsPrincipal user)
    {
        return user?.FindFirst("username")?.Value ?? user?.Identity?.Name;
    }

    public static string GetAuthType(ClaimsPrincipal user)
    {
        return user?.FindFirst(AuthTypeClaim)?.Value;
    }
}

public class WarpgateAuthOptions : AuthenticationSchemeOptions
{
    public string UsernameHeader { get; set; } = "X-Warpgate-Username";
    public string AuthTypeHeader { get; set; } = "X-Warpgate-Authentication-Types";
    public List<string> AuthorizedUsers { get; set; } = new();
    public bool RequireUserAuthType { get; set; } = true;
}

public class WarpgateAuthenticationHandler : AuthenticationHandler<WarpgateAuthOptions>
{
    public WarpgateAuthenticationHandler(
        IOptionsMonitor<WarpgateAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var username = Request.Headers[Options.UsernameHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(username))
            return Task.FromResult(AuthenticateResult.NoResult());

        var authType = Request.Headers[Options.AuthTypeHeader].FirstOrDefault() ?? WarpgateAuth.AuthTypeUser;
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("username", username),
            new Claim(ClaimTypes.Name, username),
            new Claim(WarpgateAuth.AuthTypeClaim, authType),
        }, Scheme.Name,
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
