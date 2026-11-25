// ShopHub (spec 004). One image serves the API under /api and the Angular app from /.
using k8s;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using ShopHub.Api;
using ShopHub.Api.Accounts;
using ShopHub.Api.Auth;
using ShopHub.Api.Shops;
using ShopHub.Api.Storage.Postgres;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => Settings.Load(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => NpgsqlDataSource.Create(sp.GetRequiredService<Settings>().PostgresConnectionString));
// First hosted service: the schema is in place before anything else starts or the port opens.
builder.Services.AddHostedService<DatabaseInitializer>();

// Accounts (FR-001 to FR-004).
builder.Services.AddSingleton<ILockoutStore, PostgresLockoutStore>();
builder.Services.AddSingleton<SignInLockout>();
builder.Services.AddSingleton<IOwnerStore, PostgresOwnerStore>();
builder.Services.AddSingleton<PostgresSessionStore>();
builder.Services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<PostgresSessionStore>());
builder.Services.AddSingleton<IPasswordHasher<OwnerUser>, PasswordHasher<OwnerUser>>();
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSessions();
// Keys live in the database so every replica can read every cookie.
builder.Services.AddDataProtection().SetApplicationName("shophub");
builder.Services.AddOptions<KeyManagementOptions>()
    .Configure<NpgsqlDataSource>((o, db) => o.XmlRepository = new PostgresXmlRepository(db));

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

var app = builder.Build();

// First, so everything after it (rate limits, lockout, cookies) sees the real client (research R7).
app.UseTrustedForwardedHeaders();
app.UseSecurityHeaders();
app.UseCookiePolicy();
app.UseShopHubErrorHandling();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// For local checks and the chart's probes: answers only while the database does (503 otherwise).
app.MapGet("/healthz", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "ok" });
});

app.MapAccountEndpoints();

// Unknown API paths are 404s; every other path is a client route of the Angular app.
app.MapFallback("/api/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
