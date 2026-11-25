// ShopHub (spec 004). One image serves the API under /api and the Angular app from /.
using k8s;
using Npgsql;
using ShopHub.Api;
using ShopHub.Api.Auth;
using ShopHub.Api.Shops;
using ShopHub.Api.Storage.Postgres;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => Settings.Load(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => NpgsqlDataSource.Create(sp.GetRequiredService<Settings>().PostgresConnectionString));
// First hosted service: the schema is in place before anything else starts or the port opens.
builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddSingleton<ILockoutStore, PostgresLockoutStore>();
// In the cluster: the pod's ServiceAccount (RBAC in the shophub chart). Elsewhere: the kubeconfig.
// Created on first use, so the app starts without a cluster.
builder.Services.AddSingleton<IKubernetes>(_ => new Kubernetes(KubernetesClientConfiguration.IsInCluster()
    ? KubernetesClientConfiguration.InClusterConfig()
    : KubernetesClientConfiguration.BuildConfigFromConfigFile()));
builder.Services.AddSingleton<IKubeGateway>(sp =>
    new KubeGateway(sp.GetRequiredService<IKubernetes>(), sp.GetRequiredService<Settings>().ShopsNamespace));
builder.Services.AddProblemDetails();
builder.Services.AddTrustedProxies();
builder.Services.AddRateLimits();
builder.Services.Configure<CookiePolicyOptions>(o => o.Secure = CookieSecurePolicy.Always);
// Sessions (Auth/Sessions.cs) are wired with the account endpoints (T015).

var app = builder.Build();

// First, so everything after it (rate limits, lockout, cookies) sees the real client (research R7).
app.UseTrustedForwardedHeaders();
app.UseSecurityHeaders();
app.UseCookiePolicy();
app.UseShopHubErrorHandling();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

// For local checks and the chart's probes: answers only while the database does (503 otherwise).
app.MapGet("/healthz", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "ok" });
});

// Unknown API paths are 404s; every other path is a client route of the Angular app.
app.MapFallback("/api/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
