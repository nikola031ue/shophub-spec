using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ShopHub.Api.Auth;

/// <summary>
/// Server-side sessions (data-model.md <c>owner_sessions</c>). The cookie carries the owner and the
/// session id; every API request checks the session still exists and hasn't expired.
/// </summary>
public interface ISessionStore
{
    /// <summary>True while the session exists, belongs to the owner and hasn't expired.</summary>
    Task<bool> IsActiveAsync(Guid ownerId, Guid sessionId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Sign-out: ends only this session (FR-002).</summary>
    Task EndAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Password change: ends every session of the owner except <paramref name="keep"/>.</summary>
    Task EndOthersAsync(Guid ownerId, Guid keep, CancellationToken ct);
}

/// <summary>
/// Cookie session, antiforgery and cookie policy for the panel API, ported from shop 0.2.0's
/// <c>AdminAuthSetup</c> and adapted to many accounts: instead of one admin session version, each
/// cookie names its own session row, so sign-out ends that session only and a password change ends
/// the owner's other sessions.
/// </summary>
public static class Sessions
{
    public const string Cookie = "shophub.session";
    public const string XsrfCookie = "XSRF-TOKEN";
    public const string XsrfHeader = "X-XSRF-TOKEN";
    public const string Policy = "owner";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    /// <summary>Claim holding the session id (the owner id is <see cref="ClaimTypes.NameIdentifier"/>).</summary>
    public const string SessionClaim = "shophub:session";

    public static Guid? OwnerId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static Guid? SessionId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(SessionClaim), out var id) ? id : null;

    /// <summary>Issues the session cookie for an existing <paramref name="sessionId"/> row, then the XSRF cookie.</summary>
    public static async Task SignInAsync(HttpContext context, Guid ownerId, Guid sessionId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ownerId.ToString("D", CultureInfo.InvariantCulture)),
                new Claim(SessionClaim, sessionId.ToString("D", CultureInfo.InvariantCulture)),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        // The XSRF token is bound to the signed-in user, so issue it for the new principal.
        context.User = principal;
        IssueXsrfCookie(context, context.RequestServices.GetRequiredService<IAntiforgery>());
    }

    /// <summary>Sign-out: deletes this session's row and the cookie.</summary>
    public static async Task SignOutAsync(HttpContext context, CancellationToken ct)
    {
        if (SessionId(context.User) is { } session)
        {
            await context.RequestServices.GetRequiredService<ISessionStore>().EndAsync(session, ct);
        }

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>After a password change: every other session of this owner ends; this one stays.</summary>
    public static async Task EndOtherSessionsAsync(HttpContext context, CancellationToken ct)
    {
        if (OwnerId(context.User) is { } owner && SessionId(context.User) is { } session)
        {
            await context.RequestServices.GetRequiredService<ISessionStore>().EndOthersAsync(owner, session, ct);
        }
    }

    /// <summary>
    /// Refuses a cookie whose session row is gone or expired. Checked on API requests only, so
    /// static files don't read the database.
    /// </summary>
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            return;
        }

        var http = context.HttpContext;
        var store = http.RequestServices.GetRequiredService<ISessionStore>();
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var active = context.Principal is { } user && OwnerId(user) is { } owner && SessionId(user) is { } session
            && await store.IsActiveAsync(owner, session, now, http.RequestAborted);
        if (!active)
        {
            context.RejectPrincipal();
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    public static IServiceCollection AddSessions(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = Cookie;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.ExpireTimeSpan = Lifetime;
                o.SlidingExpiration = false;
                o.Events.OnValidatePrincipal = ValidateSessionAsync;
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorizationBuilder().AddPolicy(Policy, p => p.RequireAuthenticatedUser());

        // Every cookie the app writes is Secure, whatever the request scheme (FR-004). Antiforgery
        // keeps SameAsRequest: with Always it throws on the plain-HTTP hop from Traefik (shop 0.2.0).
        services.Configure<CookiePolicyOptions>(o => o.Secure = CookieSecurePolicy.Always);
        services.AddAntiforgery(o =>
        {
            o.HeaderName = XsrfHeader;
            o.Cookie.Name = "shophub.af";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        return services;
    }

    /// <summary>Writes the Angular-readable <c>XSRF-TOKEN</c> cookie for the current user.</summary>
    public static void IssueXsrfCookie(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(XsrfCookie, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = true,
            Path = "/",
        });
    }

    /// <summary>Requires a valid <c>X-XSRF-TOKEN</c> header on state-changing endpoints (400 otherwise).</summary>
    public static TBuilder RequireXsrf<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var antiforgery = ctx.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
            try
            {
                await antiforgery.ValidateRequestAsync(ctx.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing or invalid XSRF token.");
            }

            return await next(ctx);
        });
}
