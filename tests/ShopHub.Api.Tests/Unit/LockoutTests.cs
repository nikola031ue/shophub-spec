using Microsoft.Extensions.Time.Testing;
using ShopHub.Api.Auth;

namespace ShopHub.Api.Tests.Unit;

/// <summary>FR-003 lockout rules and sign-in flow, ported from shop 0.2.0 and adapted to many accounts.</summary>
public sealed class LockoutTests
{
    private const string X = "owner-x@example.com";
    private const string Y = "owner-y@example.com";
    private const string AddrA = "203.0.113.7";
    private const string AddrB = "198.51.100.9";
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch.AddDays(1);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
    private readonly InMemoryLockoutStore _store = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SignInLockout Lockout() => new(_store, _time);

    [Fact]
    public void RulesMatchTheSpec()
    {
        Assert.Equal(5, LockoutRules.Default.PerAccountAddress.MaxFailures);
        Assert.Equal(50, LockoutRules.Default.PerAccount.MaxFailures);
        Assert.All([LockoutRules.Default.PerAccountAddress, LockoutRules.Default.PerAccount], p =>
        {
            Assert.Equal(TimeSpan.FromMinutes(15), p.Window);
            Assert.Equal(TimeSpan.FromMinutes(15), p.LockFor);
        });
    }

    [Fact]
    public void LocksOnFifthFailureWithinWindow()
    {
        var policy = LockoutPolicy.PerAccountAddress;
        var scope = LockoutScope.Empty;
        for (var i = 1; i <= 4; i++)
        {
            scope = policy.ApplyFailure(scope, T0.AddMinutes(i));
            Assert.Equal(i, scope.FailedCount);
            Assert.False(scope.IsLocked(T0.AddMinutes(i), out _));
        }

        scope = policy.ApplyFailure(scope, T0.AddMinutes(5));

        Assert.True(scope.IsLocked(T0.AddMinutes(5), out var retry));
        Assert.Equal(TimeSpan.FromMinutes(15), retry);
        Assert.False(scope.IsLocked(T0.AddMinutes(20), out _));
    }

    [Fact]
    public void WindowStartsAtFirstFailure()
    {
        var policy = LockoutPolicy.PerAccountAddress;
        var scope = policy.ApplyFailure(LockoutScope.Empty, T0);
        scope = policy.ApplyFailure(scope, T0.AddMinutes(14));
        Assert.Equal(2, scope.FailedCount);

        scope = policy.ApplyFailure(scope, T0.AddMinutes(15));

        Assert.Equal(new LockoutScope(1, T0.AddMinutes(15), null), scope);
    }

    [Fact]
    public void StateIsLockedWhileEitherScopeIs()
    {
        var locked = new LockoutScope(0, null, T0.AddMinutes(10));
        var longer = new LockoutScope(0, null, T0.AddMinutes(12));

        Assert.False(LockoutState.Empty.IsLocked(T0, out _));
        Assert.True(new LockoutState(locked, LockoutScope.Empty).IsLocked(T0, out var a));
        Assert.Equal(TimeSpan.FromMinutes(10), a);
        Assert.True(new LockoutState(LockoutScope.Empty, locked).IsLocked(T0, out _));
        Assert.True(new LockoutState(locked, longer).IsLocked(T0, out var c));
        Assert.Equal(TimeSpan.FromMinutes(12), c);
    }

    /// <summary>The shop 0.2.0 burst fix: attempts while either scope is locked count nowhere.</summary>
    [Fact]
    public void FailuresWhileLockedCountNowhere()
    {
        var state = LockoutState.Empty;
        for (var i = 0; i < 100; i++)
        {
            state = state.ApplyFailure(LockoutRules.Default, T0);
        }

        Assert.True(state.Scope.IsLocked(T0, out _));
        Assert.Equal(new LockoutScope(5, T0, null), state.Account);

        var accountLocked = new LockoutState(new LockoutScope(1, T0, null), new LockoutScope(0, null, T0.AddMinutes(5)));
        Assert.Equal(accountLocked, accountLocked.ApplyFailure(LockoutRules.Default, T0));
    }

    [Theory]
    [InlineData("", AddrA)]
    [InlineData(X, "*")]
    [InlineData(X, "")]
    public void KeysCannotHitTheAccountWideScope(string account, string address) =>
        Assert.Throws<ArgumentException>(() => LockoutKeys.Validate(account, address));

    [Theory]
    [InlineData(" Owner@Example.COM ", "owner@example.com")]
    [InlineData("a@b.c", "a@b.c")]
    public void AccountKeyIsTheNormalisedEmail(string email, string key) =>
        Assert.Equal(key, LockoutKeys.Account(email));

    [Fact]
    public async Task SixthAttemptIsLockedUntil15MinutesPass()
    {
        var lockout = Lockout();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(LockoutCheck.Allowed, (await lockout.CheckAsync(X, AddrA, Ct)).Check);
            await lockout.RecordFailureAsync(X, AddrA, Ct);
            _time.Advance(TimeSpan.FromSeconds(30));
        }

        var (check, retry) = await lockout.CheckAsync(X, AddrA, Ct);
        Assert.Equal(LockoutCheck.Locked, check);
        Assert.Equal(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(30), retry);

        _time.Advance(retry);
        Assert.Equal(LockoutCheck.Allowed, (await lockout.CheckAsync(X, AddrA, Ct)).Check);
    }

    [Fact]
    public async Task LockIsPerAccountAndAddress()
    {
        var lockout = Lockout();
        for (var i = 0; i < 5; i++)
        {
            await lockout.RecordFailureAsync(X, AddrA, Ct);
        }

        Assert.Equal(LockoutCheck.Locked, (await lockout.CheckAsync(X, AddrA, Ct)).Check);
        Assert.Equal(LockoutCheck.Allowed, (await lockout.CheckAsync(X, AddrB, Ct)).Check);
        Assert.Equal(LockoutCheck.Allowed, (await lockout.CheckAsync(Y, AddrA, Ct)).Check);
    }

    [Fact]
    public async Task FiftyFailuresAcrossAddressesLockTheAccountEverywhereButNotOthers()
    {
        var lockout = Lockout();
        for (var i = 0; i < 50; i++)
        {
            await lockout.RecordFailureAsync(X, $"192.0.2.{i / 2}", Ct);
        }

        var (check, retry) = await lockout.CheckAsync(X, AddrB, Ct);
        Assert.Equal(LockoutCheck.Locked, check);
        Assert.Equal(TimeSpan.FromMinutes(15), retry);
        Assert.Equal(LockoutCheck.Allowed, (await lockout.CheckAsync(Y, AddrB, Ct)).Check);
    }

    [Fact]
    public async Task SuccessResetsOnlyItsOwnPair()
    {
        var lockout = Lockout();
        for (var i = 0; i < 4; i++)
        {
            await lockout.RecordFailureAsync(X, AddrA, Ct);
        }

        await lockout.RecordFailureAsync(X, AddrB, Ct);

        await lockout.RecordSuccessAsync(X, AddrA, Ct);

        Assert.Equal(LockoutScope.Empty, _store.Scope(X, AddrA));
        Assert.Equal(1, _store.Scope(X, AddrB).FailedCount);
        Assert.Equal(5, _store.Scope(X, LockoutKeys.AnyAddress).FailedCount);
    }

    /// <summary>In-memory <see cref="ILockoutStore"/> with the same semantics as the PostgreSQL store.</summary>
    internal sealed class InMemoryLockoutStore : ILockoutStore
    {
        private readonly Dictionary<(string, string), LockoutScope> _scopes = [];

        public LockoutScope Scope(string account, string address) =>
            _scopes.GetValueOrDefault((account, address), LockoutScope.Empty);

        public Task<LockoutState> GetAsync(string account, string address, CancellationToken ct)
        {
            LockoutKeys.Validate(account, address);
            return Task.FromResult(new LockoutState(Scope(account, address), Scope(account, LockoutKeys.AnyAddress)));
        }

        public async Task<LockoutState> RecordFailureAsync(
            LockoutRules rules, string account, string address, DateTimeOffset now, CancellationToken ct)
        {
            var next = (await GetAsync(account, address, ct)).ApplyFailure(rules, now);
            _scopes[(account, address)] = next.Scope;
            _scopes[(account, LockoutKeys.AnyAddress)] = next.Account;
            return next;
        }

        public Task ResetAsync(string account, string address, CancellationToken ct)
        {
            _scopes.Remove((account, address));
            return Task.CompletedTask;
        }
    }
}
