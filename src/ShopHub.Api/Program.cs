// ShopHub (spec 004). One image serves the API under /api and the Angular app from /.
using ShopHub.Api;
using ShopHub.Api.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => Settings.Load(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddProblemDetails();
builder.Services.AddTrustedProxies();
builder.Services.AddRateLimits();
builder.Services.Configure<CookiePolicyOptions>(o => o.Secure = CookieSecurePolicy.Always);
// Sessions (Auth/Sessions.cs) and the lockout store are wired with the account endpoints (T015).

var app = builder.Build();

// First, so everything after it (rate limits, lockout, cookies) sees the real client (research R7).
app.UseTrustedForwardedHeaders();
app.UseSecurityHeaders();
app.UseCookiePolicy();
app.UseShopHubErrorHandling();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Unknown API paths are 404s; every other path is a client route of the Angular app.
app.MapFallback("/api/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
