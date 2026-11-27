using System.Globalization;

namespace ShopHub.Api.Shops;

/// <summary>Plain-language texts for the Shop's <c>Ready</c> reason (contracts/panel-api.md "Reason texts", R10).</summary>
public static class ReasonTexts
{
    public const string Creating = "The shop is being created.";
    public const string Other = "The platform is still working on it.";

    private static readonly Dictionary<string, string> Fixed = new(StringComparer.Ordinal)
    {
        ["WalletNotReady"] = "The wallet address is being checked.",
        ["DiscordChannelNotReady"] = "The notification channel is being created.",
        ["DatabaseProvisioning"] = "The database is being prepared.",
        ["DatabaseOperatorMissing"] = "The platform can't create this database type right now.",
        ["ReconcileError"] = "Something went wrong on the platform; it keeps retrying.",
        // Spec 005 D-6: the operator hasn't picked up the latest change yet (KubeGateway).
        [KubeGateway.ChangePendingReason] = "Applying your change.",
    };

    public const string SlowDeleteShop =
        "Deleting is taking longer than usual: the shop and its database are still being removed. The platform keeps retrying.";

    public const string SlowDeleteChannel =
        "Deleting is taking longer than usual: the notification channel can't be removed yet. The platform keeps retrying.";

    public const string SlowDeleteWallet =
        "Deleting is taking longer than usual: the wallet record is still being removed. The platform keeps retrying.";

    /// <summary>
    /// What still holds a slow delete, in the order things are removed (the Shop first; its Wallets
    /// and channel wait for it). Null when nothing is left: the record goes on the next pass.
    /// Object names are never shown.
    /// </summary>
    public static string? ForSlowDelete(IReadOnlyList<string> remaining)
    {
        bool Any(string plural) => remaining.Any(r => r.StartsWith(plural + "/", StringComparison.Ordinal));
        return Any(KubeGateway.Shops) ? SlowDeleteShop
            : Any(KubeGateway.Channels) ? SlowDeleteChannel
            : Any(KubeGateway.Wallets) ? SlowDeleteWallet
            : null;
    }

    /// <summary>Copies the shop runs at for an availability (FR-006: standard = 2, high = 3).</summary>
    public static int Replicas(string availability) => availability == Availability.High ? 3 : 2;

    /// <summary>
    /// The text for a not-ready Shop. The operator's own message is never shown: it is written for
    /// operators and may name internal objects.
    /// </summary>
    public static string For(ShopStatus? status, int replicas) => status?.Reason switch
    {
        null or "" => Creating,
        "Progressing" => string.Create(CultureInfo.InvariantCulture,
            $"The shop is starting ({Math.Min(status.ReadyReplicas, replicas)} of {replicas} copies running)."),
        var reason => Fixed.GetValueOrDefault(reason, Other),
    };
}
