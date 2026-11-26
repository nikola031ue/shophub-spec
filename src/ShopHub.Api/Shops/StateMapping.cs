namespace ShopHub.Api.Shops;

/// <summary>The panel's shop states (contracts/panel-api.md <c>Shop.state</c>).</summary>
public static class PanelStates
{
    public const string Preparing = "preparing";
    public const string Ready = "ready";
    public const string Attention = "attention";
    public const string Deleting = "deleting";
}

public sealed record PanelState(string State, string? Reason);

/// <summary>data-model.md "Panel state mapping" (FR-008, R10).</summary>
public static class StateMapping
{
    /// <summary>A shop not ready this long after its last change needs attention.</summary>
    public static readonly TimeSpan AttentionAfter = TimeSpan.FromMinutes(10);

    /// <summary>A delete still running this long after it was asked for is past SC-003 (2 minutes).</summary>
    public static readonly TimeSpan SlowDeleteAfter = TimeSpan.FromMinutes(2);

    /// <summary>Whether the panel should say what still holds a shop being deleted.</summary>
    public static bool IsSlowDelete(ShopRecord record, DateTimeOffset now) =>
        record.State == RecordState.Deleting && now - record.UpdatedAt >= SlowDeleteAfter;

    /// <param name="status">The Shop's status, or null if the Shop doesn't exist (yet).</param>
    /// <param name="remaining">
    /// For a slow delete, the objects still in the cluster (<c>&lt;plural&gt;/&lt;name&gt;</c>);
    /// otherwise not asked for.
    /// </param>
    public static PanelState Map(ShopRecord record, ShopStatus? status, DateTimeOffset now, IReadOnlyList<string>? remaining = null)
    {
        if (record.State == RecordState.Deleting)
        {
            return new PanelState(PanelStates.Deleting, IsSlowDelete(record, now) ? ReasonTexts.ForSlowDelete(remaining ?? []) : null);
        }

        if (status is { Ready: true })
        {
            return new PanelState(PanelStates.Ready, null);
        }

        var state = now - record.UpdatedAt >= AttentionAfter ? PanelStates.Attention : PanelStates.Preparing;
        return new PanelState(state, ReasonTexts.For(status, ReasonTexts.Replicas(record.Availability)));
    }
}
