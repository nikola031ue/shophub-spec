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
    };

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
