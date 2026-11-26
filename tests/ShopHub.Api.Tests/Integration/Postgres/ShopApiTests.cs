using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ShopHub.Api.Shops;
using static ShopHub.Api.Tests.Integration.Owners;

namespace ShopHub.Api.Tests.Integration.Postgres;

/// <summary>
/// T019 shop API on PostgreSQL with a fake cluster (contracts/panel-api.md "Shops"): list, create,
/// read, change (T021), delete (T022), the admin-password hand-off (FR-016) and 503 when a dependency doesn't answer. Isolation
/// between owners is in <c>Isolation/ShopIsolationTests</c>.
/// </summary>
public sealed class ShopApiTests(PostgresContainer db) : IClassFixture<PostgresContainer>
{
    private const string GoodWallet = "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ShopHubApp App, FakeKube Kube, LogSink Logs)> AppAsync(int? maxShops = null)
    {
        var kube = new FakeKube();
        var logs = new LogSink();
        var env = new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = await db.CreateDatabaseUrlAsync("shopapi_" + Guid.NewGuid().ToString("N")),
            ["REGISTRATION_RATE_LIMIT_PER_HOUR"] = "1000",
        };
        if (maxShops is { } max)
        {
            env["MAX_SHOPS_PER_OWNER"] = max.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return (new ShopHubApp(env, logs, services: kube.Register), kube, logs);
    }

    private static async Task<JsonObject[]> ListAsync(BrowserClient owner)
    {
        using var response = await owner.SendAsync(HttpMethod.Get, "/api/shops");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await ReadAsync(response))!["shops"]!.AsArray().Select(s => s!.AsObject())];
    }

    [Fact]
    public async Task EveryShopEndpointNeedsASignedInOwner()
    {
        var (app, _, _) = await AppAsync();
        using var _app = app;
        using var anonymous = await BrowserAsync(app);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/api/shops"), (HttpMethod.Post, "/api/shops"),
                     (HttpMethod.Get, "/api/shops/s7k2m4qxab"), (HttpMethod.Post, "/api/shops/s7k2m4qxab/admin-password"),
                 })
        {
            using var response = await anonymous.SendAsync(method, path, method == HttpMethod.Post ? NewShop("Acme") : null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task WritesNeedTheXsrfToken()
    {
        var (app, _, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var shop = await CreateShopAsync(owner, "Acme");

        foreach (var path in new[] { "/api/shops", $"/api/shops/{shop["id"]}/admin-password" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = System.Net.Http.Json.JsonContent.Create(NewShop("Other")),
            };
            request.Headers.Add("Cookie", owner.Cookie("shophub.session"));
            using var response = await owner.Http.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Single(await ListAsync(owner));
    }

    [Fact]
    public async Task CreateAnswers201WithAPreparingShopAndWakesTheReconciler()
    {
        var (app, kube, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");

        using var response = await owner.SendAsync(HttpMethod.Post, "/api/shops",
            NewShop("  Acme Clothing ", "high", "light", GoodWallet.ToLowerInvariant()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var shop = (await ReadAsync(response))!;
        var id = (string)shop["id"]!;
        Assert.True(ShopNames.IsValidId(id));
        Assert.Equal($"/api/shops/{id}", response.Headers.Location!.OriginalString);
        Assert.Equal("Acme Clothing", (string?)shop["name"]);
        Assert.Equal("high", (string?)shop["availability"]);
        Assert.Equal("light", (string?)shop["database"]);
        Assert.Equal(GoodWallet, (string?)shop["walletAddress"]);
        Assert.Equal("preparing", (string?)shop["state"]);
        Assert.Equal(ReasonTexts.Creating, (string?)shop["reason"]);
        Assert.Null(shop["url"]);
        Assert.Equal(0, (int)shop["readyReplicas"]!);
        Assert.Equal(3, (int)shop["replicas"]!);

        // The write woke the reconciler, which applies the new record (well before its 30 s interval).
        for (var i = 0; i < 100 && !kube.Applied.Contains(id); i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Contains(id, kube.Applied);
    }

    [Fact]
    public async Task InvalidInputGivesFieldErrorsAndCreatesNothing()
    {
        var (app, _, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");

        foreach (var (body, field) in new (object Body, string Field)[]
                 {
                     (NewShop(""), "name"),
                     (NewShop(new string('a', 101)), "name"),
                     (NewShop("Acme", availability: "medium"), "availability"),
                     (NewShop("Acme", database: "mysql"), "database"),
                     (NewShop("Acme", wallet: "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAEd"), "walletAddress"),
                     (NewShop("Acme", wallet: "0x12"), "walletAddress"),
                 })
        {
            using var response = await owner.SendAsync(HttpMethod.Post, "/api/shops", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotNull((await ReadAsync(response))!["errors"]![field]);
        }

        Assert.Empty(await ListAsync(owner));
    }

    [Fact]
    public async Task ASecondShopWithTheSameNameInOtherCaseIs409DuplicateName()
    {
        var (app, _, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        await CreateShopAsync(owner, "Acme");

        using var response = await owner.SendAsync(HttpMethod.Post, "/api/shops", NewShop(" ACME ", "high"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = (await ReadAsync(response))!;
        Assert.Equal("duplicateName", (string?)problem["reason"]);
        Assert.Equal(ShopMessages.DuplicateName, (string?)problem["errors"]!["name"]![0]);
        Assert.Single(await ListAsync(owner));
    }

    [Fact]
    public async Task ARepeatedCreateAnswers200WithTheSameShop()
    {
        var (app, _, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var first = await CreateShopAsync(owner, "Acme");

        using var again = await owner.SendAsync(HttpMethod.Post, "/api/shops", NewShop("Acme"));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((string?)first["id"], (string?)(await ReadAsync(again))!["id"]);
        Assert.Single(await ListAsync(owner));
    }

    [Fact]
    public async Task TheShopAfterTheLimitIs409LimitWithTheNumber()
    {
        var (app, _, _) = await AppAsync(maxShops: 2);
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        await CreateShopAsync(owner, "One");
        await CreateShopAsync(owner, "Two");

        using var response = await owner.SendAsync(HttpMethod.Post, "/api/shops", NewShop("Three"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = (await ReadAsync(response))!;
        Assert.Equal("limit", (string?)problem["reason"]);
        Assert.Equal(ShopMessages.Limit(2), (string?)problem["title"]);
    }

    [Fact]
    public async Task ListAndGetShowStateReasonAndUrlFromTheCluster()
    {
        var (app, kube, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var older = (string)(await CreateShopAsync(owner, "Older"))["id"]!;
        await Task.Delay(5, Ct);
        var newer = (string)(await CreateShopAsync(owner, "Newer", "high"))["id"]!;
        kube.SetReady(older, $"https://{older}.shops.localhost");
        kube.SetStatus(newer, new ShopStatus(false, "Progressing", "operator detail", null, 1));

        var list = await ListAsync(owner);

        Assert.Equal([newer, older], list.Select(s => (string)s["id"]!));
        Assert.Equal("preparing", (string?)list[0]["state"]);
        Assert.Equal("The shop is starting (1 of 3 copies running).", (string?)list[0]["reason"]);
        Assert.Equal("ready", (string?)list[1]["state"]);
        Assert.Null(list[1]["reason"]);
        Assert.Equal($"https://{older}.shops.localhost", (string?)list[1]["url"]);
        Assert.All(list, s => Assert.Null(s["password"]));

        using var one = await owner.SendAsync(HttpMethod.Get, $"/api/shops/{older}");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(list[1].ToJsonString(), (await ReadAsync(one))!.ToJsonString());
    }

    [Fact]
    public async Task AnUnknownOrMalformedIdIsTheSame404()
    {
        var (app, kube, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");

        using var unknown = await owner.SendAsync(HttpMethod.Get, "/api/shops/s7k2m4qxab");
        using var malformed = await owner.SendAsync(HttpMethod.Get, "/api/shops/..%2F..%2Fsecrets");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Equal(await BodyWithoutTraceAsync(unknown), await BodyWithoutTraceAsync(malformed));
        Assert.Empty(kube.StatusReads);
    }

    [Fact]
    public async Task TheAdminPasswordIsGivenOnlyWhileReadyAndUnchanged()
    {
        var (app, kube, logs) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var id = (string)(await CreateShopAsync(owner, "Acme"))["id"]!;
        var password = "gen-" + Guid.NewGuid().ToString("N");
        kube.SetAdminPassword(id, password);
        var path = $"/api/shops/{id}/admin-password";

        // Not ready yet: no shop check, no Secret read.
        using (var notReady = await owner.SendAsync(HttpMethod.Post, path))
        {
            Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
            Assert.Equal("notReady", (string?)(await ReadAsync(notReady))!["reason"]);
        }

        Assert.Empty(kube.Probes);
        Assert.Empty(kube.SecretReads);

        // Ready and unchanged: the password, never cached.
        kube.SetReady(id);
        using (var given = await owner.SendAsync(HttpMethod.Post, path))
        {
            Assert.Equal(HttpStatusCode.OK, given.StatusCode);
            Assert.Equal(password, (string?)(await ReadAsync(given))!["password"]);
            Assert.Contains("no-store", given.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        }

        Assert.Equal([id], kube.SecretReads);

        // The shop admin changed it: 409 changed, and the Secret isn't read again.
        kube.SetPasswordChanged(id, true);
        using (var changed = await owner.SendAsync(HttpMethod.Post, path))
        {
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Equal("changed", (string?)(await ReadAsync(changed))!["reason"]);
        }

        Assert.Equal([id], kube.SecretReads);

        // The password appears in no other response and in no log line.
        using var list = await owner.SendAsync(HttpMethod.Get, "/api/shops");
        Assert.DoesNotContain(password, await list.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Lines, l => l.Contains(password, StringComparison.Ordinal));
    }

    private static void AssertNotCacheable(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore, $"no-store missing on {(int)response.StatusCode}");
        Assert.Contains("no-cache", response.Headers.Pragma.Select(p => p.Name));
    }

    /// <summary>FR-016: no answer of the admin-password endpoint may be cached, whatever its status.</summary>
    [Fact]
    public async Task EveryAdminPasswordAnswerIsNoStoreAndNoCache()
    {
        var (app, kube, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var id = (string)(await CreateShopAsync(owner, "Acme"))["id"]!;
        var path = $"/api/shops/{id}/admin-password";
        kube.SetAdminPassword(id, "gen-password");

        using (var notReady = await owner.SendAsync(HttpMethod.Post, path))
        {
            Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
            AssertNotCacheable(notReady);
        }

        kube.SetReady(id);
        using (var given = await owner.SendAsync(HttpMethod.Post, path))
        {
            Assert.Equal(HttpStatusCode.OK, given.StatusCode);
            AssertNotCacheable(given);
        }

        kube.SetPasswordChanged(id, null);
        using (var silent = await owner.SendAsync(HttpMethod.Post, path))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, silent.StatusCode);
            AssertNotCacheable(silent);
        }

        kube.SetPasswordChanged(id, true);
        using (var changed = await owner.SendAsync(HttpMethod.Post, path))
        {
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            AssertNotCacheable(changed);
        }

        using var unknown = await owner.SendAsync(HttpMethod.Post, "/api/shops/s7k2m4qxab/admin-password");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        AssertNotCacheable(unknown);
    }

    [Fact]
    public async Task AShopThatDoesNotAnswerGives503AndAMissingSecret409()
    {
        var (app, kube, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var id = (string)(await CreateShopAsync(owner, "Acme"))["id"]!;
        kube.SetReady(id);

        kube.SetPasswordChanged(id, null);
        using (var silent = await owner.SendAsync(HttpMethod.Post, $"/api/shops/{id}/admin-password"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, silent.StatusCode);
        }

        Assert.Empty(kube.SecretReads);

        kube.SetPasswordChanged(id, false);
        using var noSecret = await owner.SendAsync(HttpMethod.Post, $"/api/shops/{id}/admin-password");
        Assert.Equal(HttpStatusCode.Conflict, noSecret.StatusCode);
        Assert.Equal("notReady", (string?)(await ReadAsync(noSecret))!["reason"]);
    }

    [Fact]
    public async Task AnUnreachableKubernetesApiGives503ButCreateStillRecords()
    {
        var (app, kube, logs) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var id = (string)(await CreateShopAsync(owner, "Acme"))["id"]!;

        kube.Down = true;
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/api/shops"), (HttpMethod.Get, $"/api/shops/{id}"),
                     (HttpMethod.Post, $"/api/shops/{id}/admin-password"),
                 })
        {
            using var response = await owner.SendAsync(method, path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("ShopHub is temporarily unavailable.", (string?)(await ReadAsync(response))!["title"]);
        }

        // Create writes only the record; the reconciler applies it once the cluster answers.
        using var create = await owner.SendAsync(HttpMethod.Post, "/api/shops", NewShop("Second"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.DoesNotContain(logs.Lines, l => l.Contains("Unhandled error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreachableDatabaseGives503()
    {
        var (app, _, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        await CreateShopAsync(owner, "Acme");

        // Take the app's database away: refuse new connections and drop the open ones.
        var database = new Npgsql.NpgsqlConnectionStringBuilder(db.ConnectionString);
        var name = app.Services.GetRequiredService<Settings>().PostgresConnectionString;
        var dbName = new Npgsql.NpgsqlConnectionStringBuilder(name).Database!;
        await using (var admin = new Npgsql.NpgsqlConnection(database.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var cmd = new Npgsql.NpgsqlCommand(
                $"ALTER DATABASE \"{dbName}\" ALLOW_CONNECTIONS false; " +
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{dbName}'", admin);
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        try
        {
            using var list = await owner.SendAsync(HttpMethod.Get, "/api/shops");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, list.StatusCode);
        }
        finally
        {
            await using var admin = new Npgsql.NpgsqlConnection(database.ConnectionString);
            await admin.OpenAsync(Ct);
            await using var cmd = new Npgsql.NpgsqlCommand($"ALTER DATABASE \"{dbName}\" ALLOW_CONNECTIONS true", admin);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public async Task ChangeNeedsASignedInOwnerAndTheXsrfToken()
    {
        var (app, _, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var id = (string)(await CreateShopAsync(owner, "Acme"))["id"]!;
        using var anonymous = await BrowserAsync(app);

        using (var response = await anonymous.SendAsync(HttpMethod.Patch, $"/api/shops/{id}", new { availability = "high" }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/shops/{id}")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { availability = "high" }),
        };
        request.Headers.Add("Cookie", owner.Cookie("shophub.session"));
        using (var response = await owner.Http.SendAsync(request, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal("standard", (string?)(await ListAsync(owner))[0]["availability"]);
    }

    [Fact]
    public async Task ChangeAnswers200WithTheChangedShopAndWakesTheReconciler()
    {
        var (app, kube, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var id = (string)(await CreateShopAsync(owner, "Acme"))["id"]!;
        kube.SetReady(id);
        for (var i = 0; i < 100 && !kube.Applied.Contains(id); i++)
        {
            await Task.Delay(50, Ct);
        }

        var appliedBefore = kube.Applied.Count;

        using var response = await owner.SendAsync(HttpMethod.Patch, $"/api/shops/{id}",
            new { availability = "high", walletAddress = GoodWallet.ToLowerInvariant() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var shop = (await ReadAsync(response))!;
        Assert.Equal((id, "Acme", "high", "standard", GoodWallet, 3),
            ((string)shop["id"]!, (string)shop["name"]!, (string)shop["availability"]!, (string)shop["database"]!,
             (string)shop["walletAddress"]!, (int)shop["replicas"]!));
        Assert.Equal("ready", (string?)shop["state"]);
        Assert.Equal(shop.ToJsonString(), (await ListAsync(owner))[0].ToJsonString());

        for (var i = 0; i < 100 && kube.Applied.Count == appliedBefore; i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(kube.Applied.Count > appliedBefore);
    }

    [Fact]
    public async Task RepeatingTheCurrentValuesAnswers200AndChangesNothing()
    {
        var (app, _, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var created = await CreateShopAsync(owner, "Acme");
        var id = (string)created["id"]!;

        using var response = await owner.SendAsync(HttpMethod.Patch, $"/api/shops/{id}",
            new { availability = "standard", walletAddress = Wallet.ToUpperInvariant().Replace("0X", "0x", StringComparison.Ordinal) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var shop = (await ReadAsync(response))!;
        Assert.Equal(("standard", Wallet), ((string)shop["availability"]!, (string)shop["walletAddress"]!));
        var store = app.Services.GetRequiredService<IShopStore>();
        var record = (await store.ListAllForReconcilerAsync(Ct)).Single();
        Assert.Equal((1, record.CreatedAt), (record.WalletGeneration, record.UpdatedAt));
    }

    [Fact]
    public async Task NameDatabaseBadValuesAndEmptyChangesAre400AndChangeNothing()
    {
        var (app, _, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var id = (string)(await CreateShopAsync(owner, "Acme"))["id"]!;

        foreach (var (body, field) in new (object Body, string Field)[]
                 {
                     (new { availability = "high", name = "Renamed" }, "name"),
                     (new { availability = "high", database = "standard" }, "database"),
                     (new { availability = "medium" }, "availability"),
                     (new { walletAddress = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAEd" }, "walletAddress"),
                     (new { }, ""),
                     (null!, ""),
                 })
        {
            using var response = await owner.SendAsync(HttpMethod.Patch, $"/api/shops/{id}", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotNull((await ReadAsync(response))!["errors"]![field]);
        }

        var shop = (await ListAsync(owner))[0];
        Assert.Equal(("Acme", "standard", "standard", Wallet),
            ((string)shop["name"]!, (string)shop["availability"]!, (string)shop["database"]!, (string)shop["walletAddress"]!));
    }

    [Fact]
    public async Task AShopBeingDeletedCannotBeChanged()
    {
        var (app, _, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var id = (string)(await CreateShopAsync(owner, "Acme"))["id"]!;
        await using (var conn = await app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>().OpenConnectionAsync(Ct))
        {
            await ShopHub.Api.Storage.Postgres.Pg.ExecuteAsync(conn, "UPDATE shops SET state = 'Deleting' WHERE id = @id", Ct, ("id", id));
        }

        using var response = await owner.SendAsync(HttpMethod.Patch, $"/api/shops/{id}", new { availability = "high" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("deleting", (string?)(await ReadAsync(response))!["reason"]);
        Assert.Equal("standard", (string?)(await ListAsync(owner))[0]["availability"]);
    }

    [Fact]
    public async Task DeleteNeedsASignedInOwnerAndTheXsrfToken()
    {
        var (app, _, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var id = (string)(await CreateShopAsync(owner, "Acme"))["id"]!;
        using var anonymous = await BrowserAsync(app);

        using (var response = await anonymous.SendAsync(HttpMethod.Delete, $"/api/shops/{id}", new { confirmName = "Acme" }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/shops/{id}")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { confirmName = "Acme" }),
        };
        request.Headers.Add("Cookie", owner.Cookie("shophub.session"));
        using (var response = await owner.Http.SendAsync(request, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal("preparing", (string?)(await ListAsync(owner))[0]["state"]);
    }

    [Fact]
    public async Task AMistypedOrMissingNameIs400AndDeletesNothing()
    {
        var (app, kube, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var id = (string)(await CreateShopAsync(owner, "Acme Clothing"))["id"]!;

        foreach (var body in new object?[] { new { confirmName = "acme clothing" }, new { confirmName = "Acme" }, new { }, null })
        {
            using var response = await owner.SendAsync(HttpMethod.Delete, $"/api/shops/{id}", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ShopMessages.ConfirmNameProblem, (string?)(await ReadAsync(response))!["errors"]!["confirmName"]![0]);
        }

        Assert.Equal("preparing", (string?)(await ListAsync(owner))[0]["state"]);
        Assert.Empty(kube.Deleted);
    }

    [Fact]
    public async Task DeleteAnswers202BeingDeletedThenTheShopLeavesTheList()
    {
        var (app, kube, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var id = (string)(await CreateShopAsync(owner, "Acme Clothing"))["id"]!;
        kube.SetReady(id);
        // Finalizers hold the channel for a moment: the shop shows as being deleted meanwhile.
        kube.SetRemaining(id, $"discordchannels/{id}");

        using var response = await owner.SendAsync(HttpMethod.Delete, $"/api/shops/{id}", new { confirmName = " Acme Clothing " });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var shop = (await ReadAsync(response))!;
        Assert.Equal((id, "deleting"), ((string)shop["id"]!, (string)shop["state"]!));
        Assert.Null(shop["url"]);
        for (var i = 0; i < 100 && !kube.Deleted.Contains(id); i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Contains(id, kube.Deleted);
        Assert.Equal("deleting", (string?)(await ListAsync(owner)).Single()["state"]);

        using (var repeat = await owner.SendAsync(HttpMethod.Delete, $"/api/shops/{id}", new { confirmName = "Acme Clothing" }))
        {
            Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
        }

        using (var change = await owner.SendAsync(HttpMethod.Patch, $"/api/shops/{id}", new { availability = "high" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);
        }

        using (var password = await owner.SendAsync(HttpMethod.Post, $"/api/shops/{id}/admin-password"))
        {
            Assert.Equal("notReady", (string?)(await ReadAsync(password))!["reason"]);
        }

        // The objects are gone: the woken reconciler (5 s while waiting) removes the record.
        kube.SetRemaining(id);
        var shops = await ListAsync(owner);
        for (var i = 0; i < 200 && shops.Length > 0; i++)
        {
            await Task.Delay(50, Ct);
            shops = await ListAsync(owner);
        }

        Assert.Empty(shops);
        using var gone = await owner.SendAsync(HttpMethod.Get, $"/api/shops/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    /// <summary>
    /// A delete held past SC-003 (here a channel whose finalizer can't finish) says so in the list
    /// and on the shop, without naming the object; a fresh one has no reason.
    /// </summary>
    [Fact]
    public async Task ASlowDeleteShowsWhatStillHoldsIt()
    {
        var (app, kube, _) = await AppAsync();
        using var _app = app;
        using var owner = await SignedInAsync(app, "x@example.test");
        var id = (string)(await CreateShopAsync(owner, "Acme Clothing"))["id"]!;
        kube.SetRemaining(id, $"discordchannels/{id}");
        using (var response = await owner.SendAsync(HttpMethod.Delete, $"/api/shops/{id}", new { confirmName = "Acme Clothing" }))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Null((await ReadAsync(response))!["reason"]);
        }

        Assert.Null((await ListAsync(owner)).Single()["reason"]);

        await using (var conn = await app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>().OpenConnectionAsync(Ct))
        {
            await ShopHub.Api.Storage.Postgres.Pg.ExecuteAsync(
                conn, "UPDATE shops SET updated_at = updated_at - interval '3 minutes' WHERE id = @id", Ct, ("id", id));
        }

        var listed = (await ListAsync(owner)).Single();
        Assert.Equal(("deleting", ReasonTexts.SlowDeleteChannel), ((string)listed["state"]!, (string?)listed["reason"]));
        using var one = await owner.SendAsync(HttpMethod.Get, $"/api/shops/{id}");
        Assert.Equal(ReasonTexts.SlowDeleteChannel, (string?)(await ReadAsync(one))!["reason"]);
    }
}
