using System.Security.Claims;
using ArchiTech.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;

namespace ArchiTech.Api.Endpoints;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth").AllowAnonymous();
        group.MapGet("/me", (ClaimsPrincipal user, AuthenticationMode mode) =>
        {
            if (user.Identity?.IsAuthenticated != true)
            {
                return Results.Ok(new
                {
                    authenticated = false,
                    name = (string?)null,
                    roles = Array.Empty<string>(),
                    localDevelopment = mode.IsLocalDevelopment
                });
            }

            var roles = user.FindAll(ClaimTypes.Role).Select(claim => claim.Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(role => role, StringComparer.Ordinal)
                .ToArray();
            return Results.Ok(new
            {
                authenticated = true,
                name = user.Identity.Name,
                roles,
                localDevelopment = mode.IsLocalDevelopment
            });
        });

        group.MapGet("/login", (
            string? returnUrl,
            AuthenticationMode mode,
            HttpContext context) =>
        {
            var destination = returnUrl is { Length: > 0 } candidate &&
                candidate.StartsWith("/", StringComparison.Ordinal) &&
                !candidate.StartsWith("//", StringComparison.Ordinal) &&
                !candidate.Contains('\\')
                    ? candidate
                    : "/";

            if (mode.IsLocalDevelopment)
            {
                return Results.Redirect(destination);
            }

            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = destination },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        });

        group.MapPost("/logout", async (
            AuthenticationMode mode,
            HttpContext context) =>
        {
            if (mode.IsLocalDevelopment)
            {
                return Results.NoContent();
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.SignOut(
                new AuthenticationProperties { RedirectUri = "/" },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        }).RequireAuthorization();

        return endpoints;
    }
}
