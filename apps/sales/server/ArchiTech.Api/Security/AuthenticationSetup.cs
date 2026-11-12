using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

namespace ArchiTech.Api.Security;

public static class DeploymentRoles
{
    public const string Sales = "Sales";
    public const string Management = "Management";
    public const string Engineer = "DeploymentEngineer";
    public const string Approver = "DeploymentApprover";
    public const string Operator = "DeploymentOperator";
    public const string Administrator = "DeploymentAdmin";
}

public sealed record AuthenticationMode(bool IsLocalDevelopment);

public static class AuthenticationSetup
{
    public static void AddWorkspaceAuthentication(this WebApplicationBuilder builder)
    {
        var localDevelopment = builder.Environment.IsDevelopment() &&
            string.Equals(
                builder.Configuration["Authentication:Mode"] ?? "Development",
                "Development",
                StringComparison.OrdinalIgnoreCase);
        builder.Services.AddSingleton(new AuthenticationMode(localDevelopment));

        if (localDevelopment)
        {
            builder.Services
                .AddAuthentication("LocalDevelopment")
                .AddScheme<AuthenticationSchemeOptions, LocalDevelopmentAuthenticationHandler>(
                    "LocalDevelopment",
                    _ => { });
        }
        else
        {
            var tenantId = builder.Configuration["Authentication:TenantId"];
            var clientId = builder.Configuration["Authentication:ClientId"];
            var clientSecret = builder.Configuration["Authentication:ClientSecret"];
            if (!Guid.TryParse(tenantId, out _) ||
                !Guid.TryParse(clientId, out _) ||
                string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new InvalidOperationException(
                    "Production authentication requires Authentication:TenantId, ClientId, and ClientSecret. " +
                    "Supply the client secret through the host's secret store.");
            }

            var groupIds = builder.Configuration.GetSection("Authorization:Groups")
                .GetChildren()
                .ToDictionary(item => item.Key, item => item.Value ?? string.Empty, StringComparer.Ordinal);
            foreach (var requiredGroup in new[] { "Sales", "Deployment" })
            {
                if (!Guid.TryParse(groupIds.GetValueOrDefault(requiredGroup), out _))
                {
                    throw new InvalidOperationException(
                        $"Authorization:Groups:{requiredGroup} must be the Entra security group's object ID.");
                }
            }

            builder.Services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                })
                .AddCookie(options =>
                {
                    options.Cookie.Name = "__Host-architech-auth";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.SlidingExpiration = true;
                    options.Events.OnRedirectToLogin = context =>
                    {
                        if (context.Request.Path.StartsWithSegments("/api"))
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        }
                        context.Response.Redirect(context.RedirectUri);
                        return Task.CompletedTask;
                    };
                    options.Events.OnRedirectToAccessDenied = context =>
                    {
                        if (context.Request.Path.StartsWithSegments("/api"))
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return Task.CompletedTask;
                        }
                        context.Response.Redirect(context.RedirectUri);
                        return Task.CompletedTask;
                    };
                })
                .AddOpenIdConnect(options =>
                {
                    options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
                    options.ClientId = clientId;
                    options.ClientSecret = clientSecret;
                    options.CallbackPath = "/signin-oidc";
                    options.ResponseType = OpenIdConnectResponseType.Code;
                    options.UsePkce = true;
                    options.SaveTokens = true;
                    options.GetClaimsFromUserInfoEndpoint = true;
                    options.MapInboundClaims = false;
                    options.Scope.Clear();
                    options.Scope.Add("openid");
                    options.Scope.Add("profile");
                    options.Scope.Add("email");
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        NameClaimType = "name"
                    };
                    options.Events.OnTokenValidated = context =>
                    {
                        if (context.Principal?.Identity is ClaimsIdentity identity)
                        {
                            foreach (var (groupName, role) in ConfiguredGroupRoles())
                            {
                                var groupId = groupIds.GetValueOrDefault(groupName);
                                if (Guid.TryParse(groupId, out _) &&
                                    identity.FindAll("groups").Any(claim =>
                                        string.Equals(claim.Value, groupId, StringComparison.OrdinalIgnoreCase)))
                                {
                                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
                                }
                            }
                        }
                        return Task.CompletedTask;
                    };
                });
        }

        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole(
                    DeploymentRoles.Sales,
                    DeploymentRoles.Management,
                    DeploymentRoles.Engineer,
                    DeploymentRoles.Approver,
                    DeploymentRoles.Operator,
                    DeploymentRoles.Administrator)
                .Build();
            options.AddPolicy("ManagementAccess", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(DeploymentRoles.Management, DeploymentRoles.Administrator));
            options.AddPolicy("DeploymentAccess", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(
                    DeploymentRoles.Engineer,
                    DeploymentRoles.Approver,
                    DeploymentRoles.Operator,
                    DeploymentRoles.Administrator));
            options.AddPolicy("DeploymentAuthor", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(DeploymentRoles.Engineer, DeploymentRoles.Administrator));
            options.AddPolicy("DeploymentApproval", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(DeploymentRoles.Approver, DeploymentRoles.Administrator));
            options.AddPolicy("DeploymentExecution", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(DeploymentRoles.Operator, DeploymentRoles.Administrator));
        });
    }

    private static IEnumerable<(string GroupName, string Role)> ConfiguredGroupRoles()
    {
        yield return ("Sales", DeploymentRoles.Sales);
        yield return ("Management", DeploymentRoles.Management);
        yield return ("Deployment", DeploymentRoles.Engineer);
        yield return ("Approvers", DeploymentRoles.Approver);
        yield return ("Operators", DeploymentRoles.Operator);
        yield return ("Administrators", DeploymentRoles.Administrator);
    }
}

public sealed class LocalDevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var roles = (configuration["Authentication:DevelopmentRoles"] ??
                     "Sales,Management,DeploymentEngineer,DeploymentApprover,DeploymentOperator,DeploymentAdmin")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(role => role is DeploymentRoles.Sales or DeploymentRoles.Management or DeploymentRoles.Engineer or
                DeploymentRoles.Approver or DeploymentRoles.Operator or DeploymentRoles.Administrator)
            .Distinct(StringComparer.Ordinal)
            .Select(role => new Claim(ClaimTypes.Role, role));
        var claims = new List<Claim>
        {
            new(
                ClaimTypes.NameIdentifier,
                configuration["Authentication:DevelopmentUserId"] ?? "local-development-user"),
            new(ClaimTypes.Name, configuration["Authentication:DevelopmentUser"] ?? "Local developer")
        };
        claims.AddRange(roles);
        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
