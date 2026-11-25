using System.Text.Json;
using k8s;
using k8s.Models;
using ShopHub.Api.Shops;

namespace ShopHub.Api.Tests.Integration.K3s;

[CollectionDefinition(Name)]
public sealed class K3sGroup : ICollectionFixture<K3sCluster>
{
    public const string Name = "k3s";
}

/// <summary>
/// T014: the gateway against a real API server, acting as ShopHub's ServiceAccount (so it works with
/// exactly the delivery.md Role). Every request it makes is recorded: deletes name single objects,
/// nothing uses a label selector, and the only Secret read is <c>&lt;id&gt;-admin</c>.
/// </summary>
[Collection(K3sGroup.Name)]
public sealed class GatewayTests(K3sCluster cluster) : IDisposable
{
    private const string Shops = K3sCluster.ShopsNamespace;
    private const string Group = "shophub.io";
    private const string Version = "v1alpha1";
    private const string Wallet = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";
    private const string OtherWallet = "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359";
    private static readonly Guid Owner = Guid.Parse("0193a5e0-0000-7000-8000-000000000001");

    private readonly RequestRecorder _recorder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _recorder.Dispose();

    private KubeGateway Gateway() => new(cluster.AsShopHub(_recorder), Shops);

    /// <summary>A fresh, valid shop id per test, so tests sharing the cluster don't meet.</summary>
    private static string NewId()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        return "s" + string.Concat(Enumerable.Range(0, 9).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]));
    }

    private static ShopResources Resources(string id, string name = "My shop", string availability = "standard",
        string wallet = Wallet, int generation = 1) =>
        new(id, Owner, name, availability, "standard", wallet, generation);

    private async Task<JsonElement?> GetAsync(string plural, string name)
    {
        try
        {
            var obj = await cluster.Admin.CustomObjects.GetNamespacedCustomObjectAsync(Group, Version, Shops, plural, name, Ct);
            return obj is JsonElement e ? e : JsonSerializer.SerializeToElement(obj);
        }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static string Str(JsonElement e, params string[] path) =>
        path.Aggregate(e, (cur, p) => cur.GetProperty(p)).GetString()!;

    /// <summary>No request may select by label or delete a collection; deletes name one object.</summary>
    private void AssertOnlyExactNames()
    {
        Assert.NotEmpty(_recorder.Requests);
        foreach (var (method, path) in _recorder.Requests)
        {
            Assert.DoesNotContain("labelSelector", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fieldSelector", path, StringComparison.OrdinalIgnoreCase);
            // Only the shops namespace: its three kinds, or a single Secret by name.
            Assert.True(path.StartsWith($"/apis/{Group}/{Version}/namespaces/{Shops}/", StringComparison.Ordinal)
                || (method == "GET" && path.StartsWith($"/api/v1/namespaces/{Shops}/secrets/", StringComparison.Ordinal)),
                $"{method} {path}");
            if (method == "DELETE")
            {
                // .../namespaces/shops/<plural>/<name>: never the collection URL.
                var segments = path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(7, segments.Length);
            }
        }
    }

    [Fact]
    public async Task ApplyCreatesTheThreeObjectsWithLabelsAndFieldManager()
    {
        var id = NewId();

        await Gateway().ApplyAsync(Resources(id), Ct);

        var shop = (await GetAsync("shops", id))!.Value;
        Assert.Equal("My shop", Str(shop, "spec", "shopName"));
        Assert.Equal("standard", Str(shop, "spec", "availability"));
        Assert.Equal($"{id}-w1", Str(shop, "spec", "walletRef", "name"));
        Assert.Equal(id, Str(shop, "spec", "discordChannelRef", "name"));
        Assert.Equal(Wallet, Str((await GetAsync("wallets", $"{id}-w1"))!.Value, "spec", "address"));
        Assert.Equal(id, Str((await GetAsync("discordchannels", id))!.Value, "spec", "channelName"));
        foreach (var (plural, name) in new[] { ("shops", id), ("wallets", $"{id}-w1"), ("discordchannels", id) })
        {
            var meta = (await GetAsync(plural, name))!.Value.GetProperty("metadata");
            Assert.Equal(Owner.ToString(), Str(meta, "labels", "shophub.io/owner"));
            Assert.Equal(id, Str(meta, "labels", "shophub.io/shop"));
            Assert.Equal("shophub", Str(meta, "labels", "app.kubernetes.io/managed-by"));
            Assert.Contains(meta.GetProperty("managedFields").EnumerateArray(),
                f => f.GetProperty("manager").GetString() == "shophub" && f.GetProperty("operation").GetString() == "Apply");
        }

        AssertOnlyExactNames();
    }

    [Fact]
    public async Task ApplyAgainChangesAvailabilityAndWallet()
    {
        var id = NewId();
        var gateway = Gateway();
        await gateway.ApplyAsync(Resources(id), Ct);

        await gateway.ApplyAsync(Resources(id, availability: "high", wallet: OtherWallet, generation: 2), Ct);
        await gateway.DeleteWalletAsync(id, 1, Ct);

        var shop = (await GetAsync("shops", id))!.Value;
        Assert.Equal("high", Str(shop, "spec", "availability"));
        Assert.Equal($"{id}-w2", Str(shop, "spec", "walletRef", "name"));
        Assert.Equal(OtherWallet, Str((await GetAsync("wallets", $"{id}-w2"))!.Value, "spec", "address"));
        Assert.Null(await GetAsync("wallets", $"{id}-w1"));
        AssertOnlyExactNames();
    }

    [Fact]
    public async Task CrdValidationErrorsSurfaceWithTheServerMessage()
    {
        var id = NewId();

        var e = await Assert.ThrowsAsync<KubeValidationException>(() =>
            Gateway().ApplyAsync(Resources(id, name: new string('x', 101)), Ct));

        Assert.Contains("shopName", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("SABCDEFGH2")]
    [InlineData("sabcdefgh2-admin")]
    [InlineData("")]
    public async Task InvalidIdsAreRefusedBeforeAnyRequest(string id)
    {
        var gateway = Gateway();

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.ApplyAsync(Resources(id), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.DeleteShopAsync(id, 1, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.ReadAdminPasswordAsync(id, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetShopStatusAsync(id, Ct));

        Assert.Empty(_recorder.Requests);
    }

    [Fact]
    public async Task StatusIsReadFromTheShop()
    {
        var id = NewId();
        var gateway = Gateway();
        Assert.Null(await gateway.GetShopStatusAsync(id, Ct));
        await gateway.ApplyAsync(Resources(id), Ct);
        Assert.Equal(new ShopStatus(false, null, null, null, 0), await gateway.GetShopStatusAsync(id, Ct));

        // The operator's part, played by the admin.
        var status = new
        {
            status = new
            {
                url = $"https://{id}.shops.localhost",
                readyReplicas = 2,
                conditions = new[]
                {
                    new { type = "Ready", status = "True", reason = "Ready", message = "All good", lastTransitionTime = "2026-11-25T10:00:00Z" },
                },
            },
        };
        await cluster.Admin.CustomObjects.PatchNamespacedCustomObjectStatusAsync(
            new V1Patch(status, V1Patch.PatchType.MergePatch), Group, Version, Shops, "shops", id, cancellationToken: Ct);

        Assert.Equal(new ShopStatus(true, "Ready", "All good", $"https://{id}.shops.localhost", 2),
            await gateway.GetShopStatusAsync(id, Ct));
    }

    [Fact]
    public async Task TheOnlySecretTheGatewayReadsIsTheAdminSecret()
    {
        var id = NewId();
        var gateway = Gateway();
        Assert.Null(await gateway.ReadAdminPasswordAsync(id, Ct));
        foreach (var (name, value) in new[] { ($"{id}-admin", "generated-pw"), ($"{id}-db-app", "db-pw") })
        {
            await cluster.Admin.CoreV1.CreateNamespacedSecretAsync(new V1Secret
            {
                Metadata = new V1ObjectMeta { Name = name },
                StringData = new Dictionary<string, string> { ["password"] = value },
            }, Shops, cancellationToken: Ct);
        }

        Assert.Equal("generated-pw", await gateway.ReadAdminPasswordAsync(id, Ct));
        await gateway.ApplyAsync(Resources(id), Ct);
        await gateway.GetShopStatusAsync(id, Ct);
        await gateway.RemainingAsync(id, 1, Ct);
        await gateway.DeleteShopAsync(id, 1, Ct);

        var secretRequests = _recorder.Requests.Where(r => r.PathAndQuery.Contains("/secrets", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, secretRequests.Count);
        Assert.All(secretRequests, r => Assert.Equal(("GET", $"/api/v1/namespaces/{Shops}/secrets/{id}-admin"), r));
    }

    /// <summary>
    /// Delete removes exactly the names derived from the record: the Shop, its DiscordChannel and every
    /// Wallet generation. Another shop of the same owner, and an object that carries this shop's labels
    /// under a name ShopHub didn't derive, are untouched: nothing deletes by label selector.
    /// </summary>
    [Fact]
    public async Task DeleteRemovesExactlyTheRecordsNames()
    {
        var id = NewId();
        var neighbour = NewId();
        var gateway = Gateway();
        await gateway.ApplyAsync(Resources(id), Ct);
        await gateway.ApplyAsync(Resources(id, wallet: OtherWallet, generation: 2), Ct);
        await gateway.ApplyAsync(Resources(neighbour, name: "Neighbour"), Ct);
        var decoy = new
        {
            apiVersion = $"{Group}/{Version}",
            kind = "Wallet",
            metadata = new
            {
                name = $"{id}-decoy",
                labels = new Dictionary<string, string> { ["shophub.io/shop"] = id, ["app.kubernetes.io/managed-by"] = "shophub" },
            },
            spec = new { address = Wallet },
        };
        await cluster.Admin.CustomObjects.CreateNamespacedCustomObjectAsync(decoy, Group, Version, Shops, "wallets", cancellationToken: Ct);
        Assert.Equal(["shops/" + id, "discordchannels/" + id, $"wallets/{id}-w1", $"wallets/{id}-w2"],
            await gateway.RemainingAsync(id, 2, Ct));
        _recorder.Clear();

        await gateway.DeleteShopAsync(id, 2, Ct);

        Assert.Empty(await gateway.RemainingAsync(id, 2, Ct));
        Assert.NotNull(await GetAsync("shops", neighbour));
        Assert.NotNull(await GetAsync("wallets", $"{neighbour}-w1"));
        Assert.NotNull(await GetAsync("discordchannels", neighbour));
        Assert.NotNull(await GetAsync("wallets", $"{id}-decoy"));
        var deletes = _recorder.Requests.Where(r => r.Method == "DELETE").Select(r => r.PathAndQuery.Split('?')[0]).ToList();
        var prefix = $"/apis/{Group}/{Version}/namespaces/{Shops}";
        Assert.Equal([$"{prefix}/shops/{id}", $"{prefix}/discordchannels/{id}", $"{prefix}/wallets/{id}-w1", $"{prefix}/wallets/{id}-w2"], deletes);
        AssertOnlyExactNames();

        // Deleting again is fine: everything is already gone.
        await gateway.DeleteShopAsync(id, 2, Ct);
    }
}
