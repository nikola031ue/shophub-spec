namespace ShopHub.Api.Auth;

// Sign-in lockout, ported from shop 0.2.0 (Domain/Admin.cs) and adapted to many accounts
// (spec 004 FR-003, research R7): the account is the normalised email, and the shop-wide ceiling
// becomes a per-account ceiling across all addresses, so one owner's failures never lock another.

/// <summary>
/// Failed sign-ins counted for one scope: an (account, client address) pair, or one account across
/// all addresses (address <see cref="LockoutKeys.AnyAddress"/>).
/// </summary>
public sealed record LockoutScope(int FailedCount, DateTimeOffset? FailedWindowStart, DateTimeOffset? LockedUntil)
{
    public static readonly LockoutScope Empty = new(0, null, null);

    public bool IsLocked(DateTimeOffset now, out TimeSpan retryAfter)
    {
        retryAfter = LockedUntil is { } until && until > now ? until - now : TimeSpan.Zero;
        return retryAfter > TimeSpan.Zero;
    }
}

/// <summary>
/// One fixed-window rule: <see cref="MaxFailures"/> failures within <see cref="Window"/> (counted
/// from the first failure) lock the scope for <see cref="LockFor"/>. Locking resets the counter.
/// </summary>
public sealed record LockoutPolicy(int MaxFailures, TimeSpan Window, TimeSpan LockFor)
{
    /// <summary>FR-003: 5 failures for one account from one address in 15 minutes.</summary>
    public static readonly LockoutPolicy PerAccountAddress = new(5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

    /// <summary>FR-003 ceiling against distributed guessing: 50 failures for one account across all addresses.</summary>
    public static readonly LockoutPolicy PerAccount = new(50, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

    /// <summary>How long a scope's state matters after its last change.</summary>
    public TimeSpan Retention => Window > LockFor ? Window : LockFor;

    public LockoutScope ApplyFailure(LockoutScope scope, DateTimeOffset now)
    {
        if (scope.IsLocked(now, out _))
        {
            return scope;
        }

        var newWindow = scope.FailedWindowStart is not { } start || now - start >= Window;
        var count = newWindow ? 1 : scope.FailedCount + 1;
        var windowStart = newWindow ? now : scope.FailedWindowStart;
        return count >= MaxFailures
            ? new LockoutScope(0, null, now + LockFor)
            : new LockoutScope(count, windowStart, null);
    }
}

/// <summary>The two rules applied to every failed sign-in.</summary>
public sealed record LockoutRules(LockoutPolicy PerAccountAddress, LockoutPolicy PerAccount)
{
    public static readonly LockoutRules Default = new(LockoutPolicy.PerAccountAddress, LockoutPolicy.PerAccount);

    public TimeSpan Retention => PerAccountAddress.Retention > PerAccount.Retention
        ? PerAccountAddress.Retention
        : PerAccount.Retention;
}

/// <summary>The (account, address) scope and the account-wide scope a sign-in is checked against.</summary>
public sealed record LockoutState(LockoutScope Scope, LockoutScope Account)
{
    public static readonly LockoutState Empty = new(LockoutScope.Empty, LockoutScope.Empty);

    /// <summary>Locked while either scope is locked; the retry time is the later of the two.</summary>
    public bool IsLocked(DateTimeOffset now, out TimeSpan retryAfter)
    {
        Scope.IsLocked(now, out var scope);
        Account.IsLocked(now, out var account);
        retryAfter = scope > account ? scope : account;
        return retryAfter > TimeSpan.Zero;
    }

    /// <summary>
    /// Counts one failure in both scopes, unless either is already locked: then nothing changes.
    /// Attempts that slipped past the lock check (concurrent requests) must not count towards the
    /// account ceiling, or one address alone could lock the account everywhere (shop 0.2.0 fix).
    /// </summary>
    public LockoutState ApplyFailure(LockoutRules rules, DateTimeOffset now) => IsLocked(now, out _)
        ? this
        : new(rules.PerAccountAddress.ApplyFailure(Scope, now), rules.PerAccount.ApplyFailure(Account, now));
}

public static class LockoutKeys
{
    /// <summary>Address of an account's all-addresses scope; never a real address.</summary>
    public const string AnyAddress = "*";

    /// <summary>The account key: the normalised email (data-model.md <c>owners.email_normalized</c>).</summary>
    public static string Account(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Rejects empty keys and addresses that would collide with the account-wide scope.</summary>
    public static void Validate(string account, string address)
    {
        if (string.IsNullOrEmpty(account))
        {
            throw new ArgumentException("Invalid lockout account.", nameof(account));
        }

        if (string.IsNullOrEmpty(address) || address == AnyAddress)
        {
            throw new ArgumentException("Invalid lockout address.", nameof(address));
        }
    }
}

/// <summary>Lockout state shared by every replica (PostgreSQL <c>signin_lockouts</c>).</summary>
public interface ILockoutStore
{
    /// <summary>The (account, address) scope and the account-wide scope; empty when unused or expired.</summary>
    Task<LockoutState> GetAsync(string account, string address, CancellationToken ct);

    /// <summary>Atomically applies <see cref="LockoutState.ApplyFailure"/> to both scopes and returns the new state.</summary>
    Task<LockoutState> RecordFailureAsync(
        LockoutRules rules, string account, string address, DateTimeOffset now, CancellationToken ct);

    /// <summary>Clears only the (account, address) scope; the account-wide scope is kept.</summary>
    Task ResetAsync(string account, string address, CancellationToken ct);
}

public enum LockoutCheck
{
    Allowed,
    Locked,
}

/// <summary>
/// The sign-in flow around a password check, as in shop's <c>AdminAuth.SignInAsync</c>: refuse while
/// locked, count a failure, clear the pair on success. The password check itself is the caller's.
/// </summary>
public sealed class SignInLockout(ILockoutStore store, TimeProvider time)
{
    public static LockoutRules Rules => LockoutRules.Default;

    public async Task<(LockoutCheck Check, TimeSpan RetryAfter)> CheckAsync(string account, string address, CancellationToken ct)
    {
        var state = await store.GetAsync(account, address, ct);
        return state.IsLocked(time.GetUtcNow(), out var retry) ? (LockoutCheck.Locked, retry) : (LockoutCheck.Allowed, TimeSpan.Zero);
    }

    public Task RecordFailureAsync(string account, string address, CancellationToken ct) =>
        store.RecordFailureAsync(Rules, account, address, time.GetUtcNow(), ct);

    public async Task RecordSuccessAsync(string account, string address, CancellationToken ct)
    {
        if ((await store.GetAsync(account, address, ct)).Scope != LockoutScope.Empty)
        {
            await store.ResetAsync(account, address, ct);
        }
    }
}
