using System.Net;
using System.Text.Json;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ShopHub.Api.Accounts;
using ShopHub.Api.Shops;
using ShopHub.Api.Storage.Postgres;
using ShopHub.Api.Tests.Integration.Postgres;

namespace ShopHub.Api.Tests.Integration.K3s;

/// <summary>
/// T018: the reconciler against a real API server, as ShopHub's ServiceAccount, with records in
/// PostgreSQL. Create, no-op repeat, drift repair, interrupted create at each step (SC-010), and an
/// object with ShopHub's labels but no record that every pass leaves alone (FR-015). Every request
/// is recorded: nothing lists or selects by label, and the only delete is an old Wallet generation
/// after a wallet change (T021, FR-009), by name, once the Shop points at the new one.
/// </summary>
[Collection(K3sGroup.Name)]
public sealed class ReconcilerTests(K3sCluster cluster, PostgresContainer db)
    : IClassFixture<PostgresContainer>, IAsyncLifetime
{
    private const string Shops = K3sCluster.ShopsNamespace;
    private const string Group = "shophub.io";
    private const string Version = "v1alpha1";
    private const string Wallet = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private readonly RequestRecorder _recorder = new();
    private readonly FailOnce _fail = new();
    private readonly List<NpgsqlDataSource> _sources = [];
    private Guid _owner;

    private PostgresShopStore Store { get; set; } = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var connectionString = await db.CreateDatabaseAsync("k3srec_" + Guid.NewGuid().ToString("N"));
        var source = NpgsqlDataSource.Create(connectionString);
        _sources.Add(source);
        await SchemaMigrator.MigrateAsync(source, Ct);
        Store = new PostgresShopStore(source);
        _owner = Guid.CreateVersion7();
        await new PostgresOwnerStore(source).CreateIfAbsentAsync(_owner, "o@example.test", "o@example.test", "hash", T0, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sources)
        {
            await s.DisposeAsync();
        }

        _recorder.Dispose();
        _fail.Dispose();
    }

    private KubeGateway? _gateway;

    /// <summary>A fresh reconciler (like a restarted replica) on one shared client: handlers belong to one client.</summary>
    private Reconciler NewReconciler()
    {
        _gateway ??= new KubeGateway(cluster.AsShopHub(_recorder, _fail), Shops);
        var gateway = _gateway;
        return new Reconciler(_sources[0], Store, () => gateway, new ReconcileTrigger(), NullLogger<Reconciler>.Instance);
    }

    private async Task<ShopRecord> CreateRecordAsync(string? name = null) =>
        (await Store.CreateAsync(_owner, new NewShop(name ?? "Shop " + Guid.NewGuid().ToString("N")[..8],
            Availability.Standard, DatabaseKind.Light, Wallet), 5, T0, Ct)).Shop!;

    private async Task<JsonElement?> GetAsync(string plural, string name)
    {
        try
        {
            var obj = await cluster.Admin.CustomObjects.GetNamespacedCustomObjectAsync(Group, Version, Shops, plural, name, Ct);
            return obj is JsonElement e ? e : JsonSerializer.SerializeToElement(obj);
        }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static (string Plural, string Name)[] Objects(string id) =>
        [("discordchannels", id), ("wallets", $"{id}-w1"), ("shops", id)];

    private async Task<string[]> ExistingAsync(string id)
    {
        var found = new List<string>();
        foreach (var (plural, name) in Objects(id))
        {
            if (await GetAsync(plural, name) is not null)
            {
                found.Add(plural);
            }
        }

        return [.. found];
    }

    /// <summary>Everything in <c>shops</c> labelled for this shop (admin view): exactly the record's three objects.</summary>
    private async Task AssertExactlyTheRecordsObjectsAsync(string id)
    {
        foreach (var (plural, name) in Objects(id))
        {
            var list = (JsonElement)JsonSerializer.SerializeToElement(await cluster.Admin.CustomObjects.ListNamespacedCustomObjectAsync(
                Group, Version, Shops, plural, labelSelector: $"shophub.io/shop={id}", cancellationToken: Ct));
            Assert.Equal([name], list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("metadata").GetProperty("name").GetString()));
        }
    }

    private async Task<string> ResourceVersionAsync(string plural, string name) =>
        (await GetAsync(plural, name))!.Value.GetProperty("metadata").GetProperty("resourceVersion").GetString()!;

    /// <summary>The reconciler only applies by name: no list, no selector, no delete, no Secret.</summary>
    private void AssertOnlyNamedApplies()
    {
        Assert.NotEmpty(_recorder.Requests);
        foreach (var (method, path) in _recorder.Requests)
        {
            Assert.Equal("PATCH", method);
            Assert.DoesNotContain("labelSelector", path, StringComparison.OrdinalIgnoreCase);
            var segments = path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(7, segments.Length); // apis/shophub.io/v1alpha1/namespaces/shops/<plural>/<name>
            Assert.Equal(Shops, segments[4]);
        }
    }

    [Fact]
    public async Task CreatesTheChannelWalletAndShopOfARecord()
    {
        var record = await CreateRecordAsync("Acme");

        Assert.Equal(new ReconcilePass(true, 1, 0), await NewReconciler().RunOnceAsync(Ct));

        var shop = (await GetAsync("shops", record.Id))!.Value;
        Assert.Equal("Acme", shop.GetProperty("spec").GetProperty("shopName").GetString());
        Assert.Equal($"{record.Id}-w1", shop.GetProperty("spec").GetProperty("walletRef").GetProperty("name").GetString());
        Assert.Equal(_owner.ToString("D"), shop.GetProperty("metadata").GetProperty("labels").GetProperty("shophub.io/owner").GetString());
        await AssertExactlyTheRecordsObjectsAsync(record.Id);
        AssertOnlyNamedApplies();
    }

    [Fact]
    public async Task ARepeatedPassChangesNothing()
    {
        var record = await CreateRecordAsync();
        await NewReconciler().RunOnceAsync(Ct);
        var before = await Task.WhenAll(Objects(record.Id).Select(o => ResourceVersionAsync(o.Plural, o.Name)));

        await NewReconciler().RunOnceAsync(Ct);
        await NewReconciler().RunOnceAsync(Ct);

        Assert.Equal(before, await Task.WhenAll(Objects(record.Id).Select(o => ResourceVersionAsync(o.Plural, o.Name))));
    }

    [Fact]
    public async Task DriftIsRepaired()
    {
        var record = await CreateRecordAsync();
        await NewReconciler().RunOnceAsync(Ct);

        // By hand: the Wallet deleted, the Shop's availability changed by another client.
        await cluster.Admin.CustomObjects.DeleteNamespacedCustomObjectAsync(Group, Version, Shops, "wallets", $"{record.Id}-w1", cancellationToken: Ct);
        await cluster.Admin.CustomObjects.PatchNamespacedCustomObjectAsync(
            new V1Patch("""{"spec":{"availability":"high"}}""", V1Patch.PatchType.MergePatch),
            Group, Version, Shops, "shops", record.Id, cancellationToken: Ct);

        await NewReconciler().RunOnceAsync(Ct);

        Assert.NotNull(await GetAsync("wallets", $"{record.Id}-w1"));
        Assert.Equal("standard", (await GetAsync("shops", record.Id))!.Value.GetProperty("spec").GetProperty("availability").GetString());
    }

    /// <summary>
    /// SC-010: a pass interrupted at each step (the request for that object fails) leaves only the
    /// objects before it, all with their record, and the next pass finishes the shop.
    /// </summary>
    [Theory]
    [InlineData("discordchannels", new string[0])]
    [InlineData("wallets", new[] { "discordchannels" })]
    [InlineData("shops", new[] { "discordchannels", "wallets" })]
    public async Task AnInterruptedCreateIsFinishedByTheNextPass(string failingStep, string[] leftAfterInterruption)
    {
        var record = await CreateRecordAsync();
        _fail.Arm(failingStep, record.Id);

        Assert.Equal(new ReconcilePass(true, 0, 1), await NewReconciler().RunOnceAsync(Ct));
        Assert.Equal(leftAfterInterruption, await ExistingAsync(record.Id));

        Assert.Equal(new ReconcilePass(true, 1, 0), await NewReconciler().RunOnceAsync(Ct));
        Assert.Equal(["discordchannels", "wallets", "shops"], await ExistingAsync(record.Id));
        await AssertExactlyTheRecordsObjectsAsync(record.Id);
    }

    [Fact]
    public async Task AnInterruptionAfterTheShopNeedsNothingMore()
    {
        var record = await CreateRecordAsync();
        await NewReconciler().RunOnceAsync(Ct); // all three applied; the process "dies" here
        var before = await Task.WhenAll(Objects(record.Id).Select(o => ResourceVersionAsync(o.Plural, o.Name)));

        Assert.Equal(new ReconcilePass(true, 1, 0), await NewReconciler().RunOnceAsync(Ct));
        Assert.Equal(before, await Task.WhenAll(Objects(record.Id).Select(o => ResourceVersionAsync(o.Plural, o.Name))));
    }

    /// <summary>FR-015: a Shop carrying ShopHub's labels but without a record is never touched.</summary>
    [Fact]
    public async Task AnObjectWithShopHubLabelsButNoRecordSurvivesEveryPass()
    {
        await CreateRecordAsync();
        const string stray = "s2stray222";
        var labels = new Dictionary<string, string>
        {
            ["shophub.io/owner"] = _owner.ToString("D"),
            ["shophub.io/shop"] = stray,
            ["app.kubernetes.io/managed-by"] = "shophub",
        };
        await cluster.Admin.CustomObjects.CreateNamespacedCustomObjectAsync(new Dictionary<string, object>
        {
            ["apiVersion"] = $"{Group}/{Version}",
            ["kind"] = "Shop",
            ["metadata"] = new Dictionary<string, object> { ["name"] = stray, ["labels"] = labels },
            ["spec"] = new Dictionary<string, object>
            {
                ["shopName"] = "Hand-made",
                ["availability"] = "standard",
                ["database"] = "standard",
                ["walletRef"] = new Dictionary<string, object> { ["name"] = "nowhere" },
                ["discordChannelRef"] = new Dictionary<string, object> { ["name"] = "nowhere" },
            },
        }, Group, Version, Shops, "shops", cancellationToken: Ct);
        var version = await ResourceVersionAsync("shops", stray);

        for (var i = 0; i < 3; i++)
        {
            await NewReconciler().RunOnceAsync(Ct);
        }

        Assert.Equal(version, await ResourceVersionAsync("shops", stray));
        Assert.DoesNotContain(_recorder.Requests, r => r.PathAndQuery.Contains(stray, StringComparison.Ordinal));
        AssertOnlyNamedApplies();
        await cluster.Admin.CustomObjects.DeleteNamespacedCustomObjectAsync(Group, Version, Shops, "shops", stray, cancellationToken: Ct);
    }

    /// <summary>
    /// FR-009 order: Wallet <c>-w2</c> is applied, then the Shop is pointed at it, and only then is
    /// <c>-w1</c> deleted, by name. Afterwards exactly one Wallet of the shop exists, and a later pass
    /// deletes nothing more.
    /// </summary>
    [Fact]
    public async Task AWalletChangeCreatesTheNewWalletRepointsTheShopThenDeletesTheOldOne()
    {
        const string newAddress = "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359";
        var record = await CreateRecordAsync();
        await NewReconciler().RunOnceAsync(Ct);
        await Store.UpdateAsync(_owner, record.Id, new ShopChange(Availability.High, newAddress), T0, Ct);
        _recorder.Clear();

        Assert.Equal(new ReconcilePass(true, 1, 0), await NewReconciler().RunOnceAsync(Ct));

        var prefix = $"/apis/{Group}/{Version}/namespaces/{Shops}/";
        Assert.Equal(
            [
                ("PATCH", $"discordchannels/{record.Id}"),
                ("PATCH", $"wallets/{record.Id}-w2"),
                ("PATCH", $"shops/{record.Id}"),
                ("DELETE", $"wallets/{record.Id}-w1"),
            ],
            _recorder.Requests.Select(r => (r.Method, r.PathAndQuery.Split('?')[0].Replace(prefix, "", StringComparison.Ordinal))));

        var shop = (await GetAsync("shops", record.Id))!.Value.GetProperty("spec");
        Assert.Equal($"{record.Id}-w2", shop.GetProperty("walletRef").GetProperty("name").GetString());
        Assert.Equal("high", shop.GetProperty("availability").GetString());
        Assert.Equal(newAddress, (await GetAsync("wallets", $"{record.Id}-w2"))!.Value.GetProperty("spec").GetProperty("address").GetString());
        var wallets = (JsonElement)JsonSerializer.SerializeToElement(await cluster.Admin.CustomObjects.ListNamespacedCustomObjectAsync(
            Group, Version, Shops, "wallets", labelSelector: $"shophub.io/shop={record.Id}", cancellationToken: Ct));
        Assert.Equal([$"{record.Id}-w2"], wallets.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("metadata").GetProperty("name").GetString()));

        _recorder.Clear();
        await NewReconciler().RunOnceAsync(Ct);
        Assert.DoesNotContain(_recorder.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task ARecordBeingDeletedIsNotApplied()
    {
        var record = await CreateRecordAsync();
        await using (var conn = await _sources[0].OpenConnectionAsync(Ct))
        {
            await Pg.ExecuteAsync(conn, "UPDATE shops SET state = 'Deleting' WHERE id = @id", Ct, ("id", record.Id));
        }

        await NewReconciler().RunOnceAsync(Ct);

        Assert.Empty(await ExistingAsync(record.Id));
    }

    /// <summary>Fails the first PATCH of one kind for one shop with 503, like a dropped connection mid-pass.</summary>
    private sealed class FailOnce : DelegatingHandler
    {
        private string? _segment;

        public void Arm(string plural, string id) => _segment = $"/{plural}/{(plural == "wallets" ? id + "-w1" : id)}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_segment is { } segment && request.Method == HttpMethod.Patch
                && request.RequestUri!.AbsolutePath.EndsWith(segment, StringComparison.Ordinal))
            {
                _segment = null;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request });
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
