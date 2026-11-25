using Microsoft.AspNetCore.Identity;
using ShopHub.Api.Auth;

namespace ShopHub.Api.Accounts;

/// <summary>Marker type for <see cref="PasswordHasher{TUser}"/>: hashes don't depend on the user object.</summary>
public sealed class OwnerUser
{
    internal static readonly OwnerUser Instance = new();
}

public enum SignInOutcome
{
    Success,
    Failed,
    Locked,
}

public sealed record SignInResult(SignInOutcome Outcome, Guid OwnerId = default, TimeSpan RetryAfter = default);

public enum PasswordChangeOutcome
{
    Success,
    WrongCurrentPassword,
    Locked,
}

/// <summary>Validation rules of data-model.md <c>owners</c>.</summary>
public static class AccountRules
{
    public const int MaxEmailLength = 254;
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 200;

    /// <summary>Email: trimmed, at most 254 characters, exactly one '@' with text on both sides.</summary>
    public static string? EmailProblem(string? email)
    {
        var e = email?.Trim() ?? "";
        var at = e.IndexOf('@', StringComparison.Ordinal);
        return e.Length is 0 or > MaxEmailLength || at <= 0 || at == e.Length - 1 || e.IndexOf('@', at + 1) >= 0
            ? $"Enter an email address with one @ and at most {MaxEmailLength} characters."
            : null;
    }

    public static string? PasswordProblem(string? password) =>
        password is null || password.Length is < MinPasswordLength or > MaxPasswordLength
            ? $"The password must have {MinPasswordLength} to {MaxPasswordLength} characters."
            : null;
}

/// <summary>
/// Registration, sign-in and password change (FR-001 to FR-004). Every path that checks or sets a
/// password does exactly one PBKDF2 run, whether or not the account exists, so neither the response
/// nor its timing tells an unknown email from a known one (no account enumeration, FR-001).
/// </summary>
public sealed class AccountService(
    IOwnerStore owners,
    PostgresSessionStore sessions,
    SignInLockout lockout,
    IPasswordHasher<OwnerUser> hasher,
    TimeProvider time)
{
    // A hash of a random password, verified when the email is unknown: same cost, never a match.
    // Built once when the service is created, not on first use: a lazy hash would make the first
    // unknown-email sign-in do an extra PBKDF2 run, which is measurable.
    private readonly string _dummyHash = hasher.HashPassword(OwnerUser.Instance, Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Creates the account unless the email exists. The caller answers the same way either way; one
    /// hash is computed in both cases, and an existing account is never changed.
    /// </summary>
    public async Task RegisterAsync(string email, string password, CancellationToken ct)
    {
        var hash = hasher.HashPassword(OwnerUser.Instance, password);
        await owners.CreateIfAbsentAsync(Guid.CreateVersion7(), email.Trim(), LockoutKeys.Account(email), hash, time.GetUtcNow(), ct);
    }

    /// <summary>Checks the password unless (account, address) or the account is locked; a failure counts towards both.</summary>
    public async Task<SignInResult> SignInAsync(string email, string password, string address, CancellationToken ct)
    {
        var account = LockoutKeys.Account(email);
        var (check, retry) = await lockout.CheckAsync(account, address, ct);
        if (check == LockoutCheck.Locked)
        {
            return new SignInResult(SignInOutcome.Locked, RetryAfter: retry);
        }

        var owner = await owners.FindByEmailAsync(account, ct);
        var ok = Verify(owner?.PasswordHash ?? _dummyHash, password) && owner is not null;
        if (!ok)
        {
            await lockout.RecordFailureAsync(account, address, ct);
            return new SignInResult(SignInOutcome.Failed);
        }

        await lockout.RecordSuccessAsync(account, address, ct);
        return new SignInResult(SignInOutcome.Success, owner!.Id);
    }

    public Task<Guid> StartSessionAsync(Guid ownerId, CancellationToken ct) => sessions.CreateAsync(ownerId, time.GetUtcNow(), ct);

    /// <summary>
    /// Changes the password after checking the current one, under the same lockout as sign-in. The
    /// caller then ends the owner's other sessions (FR-002).
    /// </summary>
    public async Task<(PasswordChangeOutcome Outcome, TimeSpan RetryAfter)> ChangePasswordAsync(
        Guid ownerId, string current, string replacement, string address, CancellationToken ct)
    {
        var owner = await owners.GetAsync(ownerId, ct) ?? throw new InvalidOperationException("The signed-in owner is missing.");
        var account = LockoutKeys.Account(owner.Email);
        var (check, retry) = await lockout.CheckAsync(account, address, ct);
        if (check == LockoutCheck.Locked)
        {
            return (PasswordChangeOutcome.Locked, retry);
        }

        if (!Verify(owner.PasswordHash, current))
        {
            await lockout.RecordFailureAsync(account, address, ct);
            return (PasswordChangeOutcome.WrongCurrentPassword, TimeSpan.Zero);
        }

        await owners.SetPasswordHashAsync(ownerId, hasher.HashPassword(OwnerUser.Instance, replacement), ct);
        await lockout.RecordSuccessAsync(account, address, ct);
        return (PasswordChangeOutcome.Success, TimeSpan.Zero);
    }

    public async Task<string?> EmailAsync(Guid ownerId, CancellationToken ct) => (await owners.GetAsync(ownerId, ct))?.Email;

    private bool Verify(string hash, string password) =>
        hasher.VerifyHashedPassword(OwnerUser.Instance, hash, password) != PasswordVerificationResult.Failed;
}
