using ShopHub.Api.Shops;

namespace ShopHub.Api.Tests.Unit;

/// <summary>data-model.md "Panel state mapping" and contracts/panel-api.md "Reason texts" (FR-008, R10).</summary>
public sealed class StateMappingTests
{
    private static readonly DateTimeOffset Changed = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private static ShopRecord Record(string state = RecordState.Active, string availability = Availability.Standard) =>
        new("s7k2m4qxab", Guid.Empty, "Acme", availability, DatabaseKind.Standard,
            "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed", 2, state, Changed, Changed);

    private static ShopStatus Status(bool ready, string? reason = null, int readyReplicas = 0) =>
        new(ready, reason, "operator detail: deployment/s7k2m4qxab 1/2", null, readyReplicas);

    [Fact]
    public void DeletingWinsOverEverything() =>
        Assert.Equal(new PanelState(PanelStates.Deleting, null),
            StateMapping.Map(Record(RecordState.Deleting), Status(true), Changed.AddHours(1)));

    [Fact]
    public void ADeleteWithinTwoMinutesHasNoReason()
    {
        var almost = Changed + StateMapping.SlowDeleteAfter - TimeSpan.FromMilliseconds(1);
        Assert.False(StateMapping.IsSlowDelete(Record(RecordState.Deleting), almost));
        Assert.Equal(new PanelState(PanelStates.Deleting, null),
            StateMapping.Map(Record(RecordState.Deleting), null, almost, ["discordchannels/s7k2m4qxab"]));
    }

    [Fact]
    public void OnlyARecordBeingDeletedCanBeASlowDelete() =>
        Assert.False(StateMapping.IsSlowDelete(Record(), Changed.AddHours(1)));

    /// <summary>
    /// Past SC-003 the panel says what still holds the delete, in removal order, and never names
    /// an object; nothing left means the record goes on the next pass.
    /// </summary>
    [Theory]
    [InlineData(new[] { "shops/s7k2m4qxab", "discordchannels/s7k2m4qxab", "wallets/s7k2m4qxab-w2" }, ReasonTexts.SlowDeleteShop)]
    [InlineData(new[] { "discordchannels/s7k2m4qxab", "wallets/s7k2m4qxab-w1" }, ReasonTexts.SlowDeleteChannel)]
    [InlineData(new[] { "wallets/s7k2m4qxab-w1" }, ReasonTexts.SlowDeleteWallet)]
    [InlineData(new string[0], null)]
    public void ASlowDeleteSaysWhatStillHoldsIt(string[] remaining, string? text)
    {
        var at = Changed + StateMapping.SlowDeleteAfter;
        Assert.True(StateMapping.IsSlowDelete(Record(RecordState.Deleting), at));
        var panel = StateMapping.Map(Record(RecordState.Deleting), null, at, remaining);
        Assert.Equal(new PanelState(PanelStates.Deleting, text), panel);
        Assert.DoesNotContain("s7k2m4qxab", panel.Reason ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyAtAnyAgeIsReadyWithoutReason()
    {
        Assert.Equal(new PanelState(PanelStates.Ready, null), StateMapping.Map(Record(), Status(true), Changed));
        Assert.Equal(new PanelState(PanelStates.Ready, null), StateMapping.Map(Record(), Status(true), Changed.AddDays(3)));
    }

    [Fact]
    public void NotReadyIsPreparingUntilTenMinutesThenNeedsAttention()
    {
        var almost = Changed + StateMapping.AttentionAfter - TimeSpan.FromMilliseconds(1);
        Assert.Equal(PanelStates.Preparing, StateMapping.Map(Record(), Status(false, "DatabaseProvisioning"), almost).State);
        Assert.Equal(PanelStates.Attention,
            StateMapping.Map(Record(), Status(false, "DatabaseProvisioning"), Changed + StateMapping.AttentionAfter).State);
    }

    [Fact]
    public void AMissingShopIsBeingCreated() =>
        Assert.Equal(new PanelState(PanelStates.Preparing, ReasonTexts.Creating), StateMapping.Map(Record(), null, Changed));

    [Theory]
    [InlineData("WalletNotReady", "The wallet address is being checked.")]
    [InlineData("DiscordChannelNotReady", "The notification channel is being created.")]
    [InlineData("DatabaseProvisioning", "The database is being prepared.")]
    [InlineData("DatabaseOperatorMissing", "The platform can't create this database type right now.")]
    [InlineData("ReconcileError", "Something went wrong on the platform; it keeps retrying.")]
    [InlineData(null, "The shop is being created.")]
    [InlineData("", "The shop is being created.")]
    [InlineData("SomethingNew", "The platform is still working on it.")]
    [InlineData("walletnotready", "The platform is still working on it.")]
    public void ReasonCodesMapToTheContractTexts(string? reason, string text) =>
        Assert.Equal(text, StateMapping.Map(Record(), Status(false, reason), Changed).Reason);

    [Theory]
    [InlineData(Availability.Standard, 1, "The shop is starting (1 of 2 copies running).")]
    [InlineData(Availability.High, 2, "The shop is starting (2 of 3 copies running).")]
    [InlineData(Availability.High, 0, "The shop is starting (0 of 3 copies running).")]
    [InlineData(Availability.Standard, 3, "The shop is starting (2 of 2 copies running).")]
    public void ProgressingCountsCopiesAgainstTheAvailability(string availability, int readyReplicas, string text) =>
        Assert.Equal(text, StateMapping.Map(Record(availability: availability), Status(false, "Progressing", readyReplicas), Changed).Reason);

    [Fact]
    public void TheOperatorsMessageIsNeverShown()
    {
        foreach (var reason in new[] { null, "Progressing", "ReconcileError", "Unknown" })
        {
            Assert.DoesNotContain("deployment/", StateMapping.Map(Record(), Status(false, reason), Changed).Reason);
        }
    }
}
