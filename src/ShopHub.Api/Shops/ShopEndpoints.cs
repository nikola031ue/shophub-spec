using System.Text.Json;
using System.Text.Json.Nodes;
using ShopHub.Api.Auth;

namespace ShopHub.Api.Shops;

/// <summary>
/// Shop API (contracts/panel-api.md "Shops", FR-005 to FR-011, FR-016). Every handler takes the
/// owner from the session and reads the record through that owner: another owner's shop and a
/// missing one get the identical 404.
/// </summary>
public static class ShopEndpoints
{
    public const string NotFoundTitle = "Shop not found.";

    public sealed record CreateShopRequest(string? Name, string? Availability, string? Database, string? WalletAddress);

    public sealed record DeleteShopRequest(string? ConfirmName);

    /// <summary>contracts/panel-api.md <c>Shop</c>.</summary>
    public sealed record ShopDto(
        string Id,
        string Name,
        string Availability,
        string Database,
        string WalletAddress,
        string State,
        string? Reason,
        string? Url,
        int ReadyReplicas,
        int Replicas,
        DateTimeOffset CreatedAt);

    public sealed record ShopListDto(IReadOnlyList<ShopDto> Shops);

    public sealed record AdminPasswordDto(string Password);

    public static IEndpointRouteBuilder MapShopEndpoints(this IEndpointRouteBuilder app)
    {
        var shops = app.MapGroup("/api/shops").RequireAuthorization(Sessions.Policy);
        shops.MapGet("", ListAsync);
        shops.MapPost("", CreateAsync).RequireXsrf();
        shops.MapGet("/{id}", GetAsync);
        shops.MapPatch("/{id}", UpdateAsync).RequireXsrf();
        shops.MapDelete("/{id}", DeleteAsync).RequireXsrf();
        shops.MapPost("/{id}/admin-password", AdminPasswordAsync).RequireXsrf();
        return app;
    }

    public static ShopDto ToDto(ShopRecord record, ShopStatus? status, DateTimeOffset now, IReadOnlyList<string>? remaining = null)
    {
        var panel = StateMapping.Map(record, status, now, remaining);
        return new ShopDto(
            record.Id,
            record.DisplayName,
            record.Availability,
            record.Database,
            record.WalletAddress,
            panel.State,
            panel.Reason,
            status?.Url,
            status?.ReadyReplicas ?? 0,
            ReasonTexts.Replicas(record.Availability),
            record.CreatedAt);
    }

    private static Guid Owner(HttpContext context) => Sessions.OwnerId(context.User)!.Value;

    /// <summary>
    /// The JSON body, or null when there is none, it isn't JSON, or it doesn't parse. Read here
    /// rather than bound as a parameter: a bound body makes the route require a JSON content type,
    /// and a request without one would get the framework's 404 instead of the 400 the contract gives.
    /// </summary>
    private static async Task<T?> BodyAsync<T>(HttpRequest request, CancellationToken ct)
        where T : class
    {
        if (!request.HasJsonContentType())
        {
            return null;
        }

        try
        {
            return await request.ReadFromJsonAsync<T>(ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The shop as the panel shows it. A shop being deleted has no Shop status to read; the cluster
    /// is asked about it only once the delete is slow (past SC-003), to say what still holds it,
    /// for example a channel whose finalizer can't finish.
    /// </summary>
    private static async Task<ShopDto> DtoAsync(IKubeGateway kube, ShopRecord record, DateTimeOffset now, CancellationToken ct)
    {
        if (record.State == RecordState.Deleting)
        {
            var remaining = StateMapping.IsSlowDelete(record, now)
                ? await kube.RemainingAsync(record.Id, record.WalletGeneration, ct)
                : null;
            return ToDto(record, null, now, remaining);
        }

        return ToDto(record, await kube.GetShopStatusAsync(record.Id, ct), now);
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: NotFoundTitle);

    private static IResult Conflict(string title, string reason, IDictionary<string, string[]>? errors = null)
    {
        var extensions = new Dictionary<string, object?> { ["reason"] = reason };
        if (errors is not null)
        {
            extensions["errors"] = errors;
        }

        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: title, extensions: extensions);
    }

    private static async Task<IResult> ListAsync(
        HttpContext context, IShopStore store, IKubeGateway kube, TimeProvider time, CancellationToken ct)
    {
        var records = await store.ListAsync(Owner(context), ct);
        var now = time.GetUtcNow();
        return Results.Ok(new ShopListDto(await Task.WhenAll(records.Select(r => DtoAsync(kube, r, now, ct)))));
    }

    /// <summary>
    /// 201 for a new shop; 200 with the existing shop for a repeat of the same create (SC-010).
    /// Only the record is written here; the reconciler, woken now, creates the cluster objects.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        CreateShopRequest request, HttpContext context, IShopStore store, IKubeGateway kube, Settings settings,
        ReconcileTrigger trigger, TimeProvider time, CancellationToken ct)
    {
        var shop = ShopRules.Validate(request.Name, request.Availability, request.Database, request.WalletAddress, out var problems);
        if (shop is null)
        {
            return Results.ValidationProblem(problems);
        }

        var now = time.GetUtcNow();
        var result = await store.CreateAsync(Owner(context), shop, settings.MaxShopsPerOwner, now, ct);
        switch (result.Outcome)
        {
            case CreateOutcome.Created:
                trigger.Wake();
                return Results.Created($"/api/shops/{result.Shop!.Id}", ToDto(result.Shop, null, now));
            case CreateOutcome.Existing:
                trigger.Wake();
                return Results.Ok(await DtoAsync(kube, result.Shop!, now, ct));
            case CreateOutcome.DuplicateName:
                return Conflict(ShopMessages.DuplicateName, "duplicateName",
                    new Dictionary<string, string[]> { ["name"] = [ShopMessages.DuplicateName] });
            default:
                return Conflict(ShopMessages.Limit(settings.MaxShopsPerOwner), "limit");
        }
    }

    private static async Task<IResult> GetAsync(
        string id, HttpContext context, IShopStore store, IKubeGateway kube, TimeProvider time, CancellationToken ct)
    {
        if (await store.GetAsync(Owner(context), id, ct) is not { } record)
        {
            return NotFound();
        }

        return Results.Ok(await DtoAsync(kube, record, time.GetUtcNow(), ct));
    }

    /// <summary>
    /// FR-012: availability and/or wallet address. The body is read as a plain JSON object so that
    /// <c>name</c> and <c>database</c> are refused whenever they appear, not only when they differ.
    /// Ownership is checked before anything about the request is answered beyond its shape, and a
    /// repeat of the current values answers 200 without writing. The reconciler, woken on a change,
    /// applies it (and for a new wallet: new Wallet, Shop repointed, old Wallet deleted, FR-009).
    /// </summary>
    private static async Task<IResult> UpdateAsync(
        string id, HttpContext context, IShopStore store, IKubeGateway kube, ReconcileTrigger trigger,
        TimeProvider time, CancellationToken ct)
    {
        var change = ShopRules.ValidateChange(await BodyAsync<JsonObject>(context.Request, ct), out var problems);
        if (change is null)
        {
            return Results.ValidationProblem(problems);
        }

        var now = time.GetUtcNow();
        var result = await store.UpdateAsync(Owner(context), id, change, now, ct);
        switch (result.Outcome)
        {
            case UpdateOutcome.NotFound:
                return NotFound();
            case UpdateOutcome.Deleting:
                return Conflict(ShopMessages.BeingDeleted, "deleting");
            case UpdateOutcome.Updated:
                trigger.Wake();
                break;
        }

        return Results.Ok(await DtoAsync(kube, result.Shop!, now, ct));
    }

    /// <summary>
    /// FR-013: 202 with the shop as "being deleted" once the typed name matches; the reconciler,
    /// woken now, deletes the cluster objects by name and then the record. A repeat while it is
    /// being deleted answers 202 again. Ownership comes first, so a stranger gets the plain 404
    /// whatever name they type, and a missing body is a mismatch like any other.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        string id, HttpContext context, IShopStore store, ReconcileTrigger trigger, TimeProvider time, CancellationToken ct)
    {
        var request = await BodyAsync<DeleteShopRequest>(context.Request, ct);
        var now = time.GetUtcNow();
        var result = await store.MarkDeletingAsync(Owner(context), id, request?.ConfirmName, now, ct);
        switch (result.Outcome)
        {
            case DeleteOutcome.NotFound:
                return NotFound();
            case DeleteOutcome.NameMismatch:
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirmName"] = [ShopMessages.ConfirmNameProblem] });
        }

        trigger.Wake();
        return Results.Json(ToDto(result.Shop!, null, now), statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// FR-016: the generated shop admin password, to the shop's owner only, on an explicit request.
    /// Ownership first (404), then readiness, then the shop itself is asked whether the password was
    /// already changed; only if not is Secret <c>&lt;id&gt;-admin</c> read. Never cached or logged.
    /// </summary>
    private static async Task<IResult> AdminPasswordAsync(
        string id, HttpContext context, IShopStore store, IKubeGateway kube, IShopAdminProbe probe, CancellationToken ct)
    {
        // Every answer of this endpoint, not only the one with the password, is kept out of caches.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        if (await store.GetAsync(Owner(context), id, ct) is not { } record)
        {
            return NotFound();
        }

        const string notReady = "The shop isn't ready yet. Try again when it is.";
        if (record.State == RecordState.Deleting || await kube.GetShopStatusAsync(record.Id, ct) is not { Ready: true })
        {
            return Conflict(notReady, "notReady");
        }

        switch (await probe.AdminPasswordChangedAsync(record.Id, ct))
        {
            case null:
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The shop didn't answer. Try again shortly.");
            case true:
                return Conflict("The shop admin has already changed this password. Use the new one.", "changed");
        }

        return await kube.ReadAdminPasswordAsync(record.Id, ct) is { } password
            ? Results.Ok(new AdminPasswordDto(password))
            : Conflict(notReady, "notReady");
    }
}
