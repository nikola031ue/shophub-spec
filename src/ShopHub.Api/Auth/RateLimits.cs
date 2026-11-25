using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ShopHub.Api.Auth;

/// <summary>
/// Per-client-address limits (built-in ASP.NET Core rate limiting), ported from shop 0.2.0's order
/// limit. Over the limit: 429 problem details with <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// The window is per replica (in memory), so with N replicas a client gets up to N × the limit. The
/// key is <see cref="ClientAddress"/>: the address Traefik forwarded, otherwise the TCP peer.
/// </remarks>
public static class RateLimits
{
    /// <summary>Registration: 10 per hour per client address (research R7).</summary>
    public const string Registration = "registration";

    /// <summary>Optional override, e.g. for tests; default <see cref="DefaultRegistrationsPerHour"/>.</summary>
    public const string RegistrationSetting = "REGISTRATION_RATE_LIMIT_PER_HOUR";

    public const int DefaultRegistrationsPerHour = 10;

    public static IServiceCollection AddRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IConfiguration>((o, configuration) =>
        {
            var permits = configuration.GetValue(RegistrationSetting, DefaultRegistrationsPerHour);
            if (permits < 1)
            {
                throw new SettingsException($"Invalid configuration: {RegistrationSetting} must be 1 or more.");
            }

            o.AddPolicy(Registration, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress.Of(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                }));
            o.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter =
                        Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails = { Status = 429, Title = "Too many requests from your address. Try again later." },
                });
            };
        });
        return services;
    }
}
