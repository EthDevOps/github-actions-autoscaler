using System.Security.Claims;
using GithubActionsOrchestrator.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace GithubActionsOrchestrator.Auth;

public static class TeleportAuth
{
    public const string CanMutatePolicy = "CanMutate";
    public const string JwtHeader = "Teleport-Jwt-Assertion";

    public static void AddTeleportAuth(this WebApplicationBuilder builder, TeleportAuthConfiguration cfg)
    {
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<TeleportSigningKeyProvider>();

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        // Configure JwtBearer with DI access to the key provider.
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TeleportSigningKeyProvider>((options, keyProvider) =>
            {
                options.MapInboundClaims = false; // keep raw Teleport claim names ("roles", "sub", "username")
                options.RequireHttpsMetadata = false; // we supply keys ourselves, no metadata endpoint
                options.Events = new JwtBearerEvents
                {
                    // Teleport passes the assertion in a custom header, not "Authorization: Bearer".
                    OnMessageReceived = ctx =>
                    {
                        var header = ctx.Request.Headers[JwtHeader].FirstOrDefault();
                        if (!string.IsNullOrEmpty(header))
                            ctx.Token = header;
                        return Task.CompletedTask;
                    }
                };
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = !string.IsNullOrWhiteSpace(cfg.Issuer),
                    ValidIssuer = cfg.Issuer,
                    ValidateAudience = !string.IsNullOrWhiteSpace(cfg.Audience),
                    ValidAudience = cfg.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = "username",
                    RoleClaimType = "roles",
                    ClockSkew = TimeSpan.FromMinutes(1),
                    IssuerSigningKeyResolver = (_, _, kid, _) => keyProvider.ResolveKeys(kid),
                };
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
    /// A user may mutate when no allowlists are configured (any authenticated user), or when
    /// they hold an allowed role, or when their username is explicitly allowed.
    /// </summary>
    public static bool IsAuthorizedToMutate(ClaimsPrincipal user, TeleportAuthConfiguration cfg)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return false;

        var roles = cfg.AuthorizedRoles ?? new List<string>();
        var users = cfg.AuthorizedUsers ?? new List<string>();

        if (roles.Count == 0 && users.Count == 0)
            return true;

        bool hasAllowedRole = roles.Count > 0 && GetRoles(user).Any(r => roles.Contains(r, StringComparer.OrdinalIgnoreCase));
        bool isAllowedUser = users.Count > 0 && users.Contains(GetUsername(user), StringComparer.OrdinalIgnoreCase);

        return hasAllowedRole || isAllowedUser;
    }

    public static string GetUsername(ClaimsPrincipal user)
    {
        return user?.FindFirst("username")?.Value
               ?? user?.FindFirst("sub")?.Value
               ?? user?.Identity?.Name;
    }

    public static IEnumerable<string> GetRoles(ClaimsPrincipal user)
    {
        if (user == null) return Enumerable.Empty<string>();
        return user.Claims
            .Where(c => c.Type is "roles" or ClaimTypes.Role)
            .Select(c => c.Value);
    }
}

/// <summary>
/// Fetches and caches Teleport's JWKS signing keys, refreshing on an interval and on a
/// key-id miss (so key rotation is picked up without a restart).
/// </summary>
public class TeleportSigningKeyProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TeleportSigningKeyProvider> _logger;
    private readonly string _jwksUrl;
    private readonly TimeSpan _minRefreshInterval = TimeSpan.FromMinutes(5);
    private readonly object _lock = new();

    private IList<SecurityKey> _keys = new List<SecurityKey>();
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public TeleportSigningKeyProvider(IHttpClientFactory httpClientFactory, ILogger<TeleportSigningKeyProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _jwksUrl = Program.Config.TeleportAuth?.JwksUrl;
    }

    public IEnumerable<SecurityKey> ResolveKeys(string kid)
    {
        var current = _keys;
        var match = Filter(current, kid);
        if (match.Count > 0)
            return match;

        // Unknown kid (or empty cache): refresh once (throttled) and retry.
        Refresh(force: false);
        return Filter(_keys, kid);
    }

    private static List<SecurityKey> Filter(IList<SecurityKey> keys, string kid)
    {
        if (keys == null || keys.Count == 0)
            return new List<SecurityKey>();
        if (string.IsNullOrEmpty(kid))
            return keys.ToList();
        var byKid = keys.Where(k => string.Equals(k.KeyId, kid, StringComparison.Ordinal)).ToList();
        // If the token has a kid we don't recognise, fall back to trying all keys.
        return byKid.Count > 0 ? byKid : keys.ToList();
    }

    private void Refresh(bool force)
    {
        if (string.IsNullOrWhiteSpace(_jwksUrl))
            return;

        lock (_lock)
        {
            if (!force && DateTime.UtcNow - _lastRefreshUtc < _minRefreshInterval)
                return;

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var json = client.GetStringAsync(_jwksUrl).GetAwaiter().GetResult();
                var keySet = new JsonWebKeySet(json);
                var keys = keySet.GetSigningKeys();
                _keys = keys;
                _lastRefreshUtc = DateTime.UtcNow;
                _logger.LogInformation("Refreshed Teleport JWKS: {Count} signing key(s) from {Url}", keys.Count, _jwksUrl);
            }
            catch (Exception ex)
            {
                _lastRefreshUtc = DateTime.UtcNow; // avoid hammering a broken endpoint
                _logger.LogError(ex, "Failed to fetch Teleport JWKS from {Url}", _jwksUrl);
            }
        }
    }
}
