using OracleFormsDemo;

namespace OracleFormsDemo.Tests;

public sealed class DemoSessionStoreTests
{
    [Fact]
    public void Create_IssuesDistinctCryptographicallySizedOpaqueTokens()
    {
        var store = new DemoSessionStore();

        var tokens = Enumerable.Range(0, 64)
            .Select(index => store.Create(SessionRole.Customer, 1000 + index, $"Customer {index}").Token)
            .ToArray();

        Assert.All(tokens, token =>
        {
            Assert.NotEmpty(token);
            Assert.Equal(43, token.Length);
            Assert.DoesNotContain("Customer", token, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Create_StoresCustomerRoleAndAccount()
    {
        var store = new DemoSessionStore();

        var (token, created) = store.Create(SessionRole.Customer, 123456, "Test Customer");
        var resolved = store.Resolve(token);

        Assert.Equal(created, resolved);
        Assert.Equal(SessionRole.Customer, resolved!.Role);
        Assert.Equal(123456, resolved.AccountId);
    }

    [Fact]
    public void Create_StoresManagerWithoutAccount()
    {
        var store = new DemoSessionStore();

        var (token, _) = store.Create(SessionRole.Manager, null, "local-manager");
        var resolved = store.Resolve(token);

        Assert.NotNull(resolved);
        Assert.Equal(SessionRole.Manager, resolved.Role);
        Assert.Null(resolved.AccountId);
    }

    [Fact]
    public void Revoke_InvalidatesIssuedToken()
    {
        var store = new DemoSessionStore();
        var (token, _) = store.Create(SessionRole.Customer, 123456, "Test Customer");

        store.Revoke(token);

        Assert.Null(store.Resolve(token));
    }

    [Fact]
    public void Resolve_RemovesExpiredSessionAtInjectedTime()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
        var store = new DemoSessionStore(clock, TimeSpan.FromMinutes(15));
        var (token, session) = store.Create(SessionRole.Customer, 123456, "Test Customer");

        Assert.Equal(clock.GetUtcNow().AddMinutes(15), session.ExpiresAt);
        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.Null(store.Resolve(token));
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
    }
}