using System.Text.RegularExpressions;
using ShopHub.Api.Storage;

namespace ShopHub.Api;

/// <summary>
/// Turns unhandled exceptions into problem details: 503 when the database doesn't answer, otherwise
/// a generic 500 with a correlation id. Details go only to the log, with credentials removed
/// (contracts/http-api.md "Never in responses or logs").
/// </summary>
public static partial class ErrorHandling
{
    public static IApplicationBuilder UseShopHubErrorHandling(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ShopHub.Api.Errors");
                var problems = context.RequestServices.GetRequiredService<IProblemDetailsService>();
                context.Response.Clear();
                if (StoreErrors.IsUnavailable(ex))
                {
                    LogUnavailable(logger, ex.GetType().Name);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await problems.WriteAsync(new ProblemDetailsContext
                    {
                        HttpContext = context,
                        ProblemDetails = { Status = 503, Title = "ShopHub is temporarily unavailable." },
                    });
                    return;
                }

                var correlationId = context.TraceIdentifier;
                LogUnhandled(logger, correlationId, Redaction.Redact(ex.ToString()));
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails =
                    {
                        Status = 500,
                        Title = "Something went wrong.",
                        Extensions = { ["correlationId"] = correlationId },
                    },
                });
            }
        });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database unavailable ({Error})")]
    private static partial void LogUnavailable(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error {CorrelationId}: {Error}")]
    private static partial void LogUnhandled(ILogger logger, string correlationId, string error);
}

/// <summary>Removes credentials from text before it is logged.</summary>
public static partial class Redaction
{
    public static string Redact(string text)
    {
        text = UriCredentials().Replace(text, "$1***@");
        return KeyValueSecret().Replace(text, "$1=***");
    }

    /// <summary><c>scheme://user:password@</c> → <c>scheme://***@</c>.</summary>
    [GeneratedRegex(@"([a-zA-Z][a-zA-Z0-9+.\-]*://)[^\s/@]+@")]
    private static partial Regex UriCredentials();

    /// <summary><c>Password=...;</c> in connection strings.</summary>
    [GeneratedRegex(@"(?i)\b(password|pwd)\s*=\s*[^;,\s]*")]
    private static partial Regex KeyValueSecret();
}
