using System.Net;
using System.Text.Json.Nodes;
using ShopHub.Api.Tests.Integration.Postgres;
using static ShopHub.Api.Tests.Integration.Owners;

namespace ShopHub.Api.Tests.Integration.Isolation;

/// <summary>
/// FR-005, FR-018, SC-004 (quickstart Scenario 7): owner B, naming owner A's shop on every shop
/// endpoint, gets exactly the 404 of an unknown id, and nothing of A's is read from the cluster on
/// B's behalf, and nothing of A's changes. A's shops are not in B's list.
/// </summary>
public sealed class ShopIsolationTests(PostgresContainer db) : IClassFixture<PostgresContainer>
{
    /// <summary>Every endpoint that names a shop, as (method, path template, JSON body or null).</summary>
    public static TheoryData<string, string, string?> Endpoints => new()
    {
        { "GET", "/api/shops/{id}", null },
        { "POST", "/api/shops/{id}/admin-password", null },
        { "PATCH", "/api/shops/{id}", """{"availability":"high","walletAddress":"0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359"}""" },
        // The right name of A's shop: B still gets the plain 404, not a hint that the name matched.
        { "DELETE", "/api/shops/{id}", """{"confirmName":"Acme"}""" },
    };

    private async Task<(ShopHubApp App, FakeKube Kube, BrowserClient A, BrowserClient B, string ShopOfA)> SetUpAsync()
    {
        var kube = new FakeKube();
        var app = new ShopHubApp(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = await db.CreateDatabaseUrlAsync("iso_" + Guid.NewGuid().ToString("N")),
            ["REGISTRATION_RATE_LIMIT_PER_HOUR"] = "1000",
        }, services: kube.Register);
        var a = await SignedInAsync(app, "a@example.test");
        var b = await SignedInAsync(app, "b@example.test");
        var shop = (string)(await CreateShopAsync(a, "Acme"))["id"]!;
        // A's shop is ready and has a password: anything leaking would have something to leak.
        kube.SetReady(shop);
        kube.SetAdminPassword(shop, "secret-of-a");
        return (app, kube, a, b, shop);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task AnotherOwnersShopGetsTheSame404AsAnUnknownId(string method, string template, string? body)
    {
        var (app, kube, a, b, shopOfA) = await SetUpAsync();
        using var _app = app;
        using var _a = a;
        using var _b = b;
        var verb = new HttpMethod(method);
        JsonNode? Body() => body is null ? null : JsonNode.Parse(body);
        using var before = await a.SendAsync(HttpMethod.Get, $"/api/shops/{shopOfA}");
        var shopBefore = (await ReadAsync(before))!.ToJsonString();
        var readsBefore = (kube.StatusReads.Count, kube.SecretReads.Count, kube.Probes.Count, kube.Deleted.Count);

        using var foreign = await b.SendAsync(verb, template.Replace("{id}", shopOfA, StringComparison.Ordinal), Body());
        using var unknown = await b.SendAsync(verb, template.Replace("{id}", "s7k2m4qxab", StringComparison.Ordinal), Body());

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await BodyWithoutTraceAsync(unknown), await BodyWithoutTraceAsync(foreign));
        Assert.Equal(
            unknown.Content.Headers.ContentType?.ToString(), foreign.Content.Headers.ContentType?.ToString());
        Assert.Equal(readsBefore, (kube.StatusReads.Count, kube.SecretReads.Count, kube.Probes.Count, kube.Deleted.Count));
        using var after = await a.SendAsync(HttpMethod.Get, $"/api/shops/{shopOfA}");
        Assert.Equal(shopBefore, (await ReadAsync(after))!.ToJsonString());

        // A still reaches the same endpoint (so the 404 above is about ownership, not the route).
        using var own = await a.SendAsync(verb, template.Replace("{id}", shopOfA, StringComparison.Ordinal), Body());
        Assert.Equal(method == "DELETE" ? HttpStatusCode.Accepted : HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task AnotherOwnersShopsAreNotInTheList()
    {
        var (app, _, a, b, shopOfA) = await SetUpAsync();
        using var _app = app;
        using var _a = a;
        using var _b = b;
        var shopOfB = (string)(await CreateShopAsync(b, "Acme"))["id"]!;

        using var listOfB = await b.SendAsync(HttpMethod.Get, "/api/shops");
        using var listOfA = await a.SendAsync(HttpMethod.Get, "/api/shops");

        Assert.Equal([shopOfB], (await ReadAsync(listOfB))!["shops"]!.AsArray().Select(s => (string)s!["id"]!));
        Assert.Equal([shopOfA], (await ReadAsync(listOfA))!["shops"]!.AsArray().Select(s => (string)s!["id"]!));
    }
}
