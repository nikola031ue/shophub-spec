using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using ShopHub.Api.Tests.Integration.Postgres;

namespace ShopHub.Api.Tests.Integration;

/// <summary>Signed-in owners for the shop API tests: register, then sign in, as the panel does.</summary>
public static class Owners
{
    public const string Password = "correct-horse-battery";
    public const string Wallet = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";

    private static int _peer;

    /// <summary>A browser that has loaded the panel, at its own client address (keeps rate limits apart).</summary>
    public static async Task<BrowserClient> BrowserAsync(ShopHubApp app)
    {
        var http = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        var n = Interlocked.Increment(ref _peer);
        http.DefaultRequestHeaders.Add(ShopHubApp.PeerHeader, $"198.18.{n / 250}.{(n % 250) + 1}");
        var client = new BrowserClient(http);
        using var session = await client.SendAsync(HttpMethod.Get, "/api/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        return client;
    }

    public static async Task<BrowserClient> SignedInAsync(ShopHubApp app, string email)
    {
        var client = await BrowserAsync(app);
        using (var register = await client.SendAsync(HttpMethod.Post, "/api/register", new { email, password = Password }))
        {
            Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        }

        using var signIn = await client.SendAsync(HttpMethod.Post, "/api/signin", new { email, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        return client;
    }

    public static object NewShop(string name, string availability = "standard", string database = "standard", string wallet = Wallet) =>
        new { name, availability, database, walletAddress = wallet };

    public static async Task<JsonObject> CreateShopAsync(BrowserClient owner, string name, string availability = "standard")
    {
        using var response = await owner.SendAsync(HttpMethod.Post, "/api/shops", NewShop(name, availability));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response))!;
    }

    public static async Task<JsonObject?> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken);

    /// <summary>The response body without its per-request trace id, for "identical answer" checks.</summary>
    public static async Task<string> BodyWithoutTraceAsync(HttpResponseMessage response)
    {
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        body.Remove("traceId");
        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
