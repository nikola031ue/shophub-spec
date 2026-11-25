namespace ShopHub.Api;

/// <summary>
/// Response headers on every response (spec 003 T025 finding 4): no MIME sniffing, no framing
/// by other sites, and no full URLs in the Referer header. A full script/style CSP is left out:
/// the Angular build inlines a small critical-CSS loader script.
/// </summary>
public static class SecurityHeaders
{
    public static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>
    {
        ["X-Content-Type-Options"] = "nosniff",
        ["X-Frame-Options"] = "DENY",
        ["Content-Security-Policy"] = "frame-ancestors 'none'",
        ["Referrer-Policy"] = "same-origin",
    };

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                foreach (var (name, value) in Values)
                {
                    context.Response.Headers[name] = value;
                }

                return Task.CompletedTask;
            });
            return next(context);
        });
}
