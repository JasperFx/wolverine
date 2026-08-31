using Shouldly;
using Xunit;

namespace Wolverine.Redis.Tests.Persistence;

/// <summary>
/// The ways this could look like it was working while it was not, each closed deliberately.
/// </summary>
public class redis_refuses_what_it_cannot_actually_do
{
    private record ADocument(string Id);

    private class ARedisSaga : Saga
    {
        public string Id { get; set; } = null!;
    }

    private class NotReallyASaga
    {
        public string Id { get; set; } = null!;
    }

    /// <summary>
    /// <c>Store&lt;T&gt;()</c> writes are last-write-wins. A saga registered there would keep working
    /// under every sequential test and lose a concurrent write in production with no error anywhere,
    /// which is the exact failure the saga registration exists to prevent.
    /// </summary>
    [Fact]
    public void store_refuses_a_saga_type()
    {
        var ex = Should.Throw<InvalidRedisMappingException>(() =>
            new RedisPersistenceConfiguration().Store<ARedisSaga>(x => x.KeyFor = ctx => $"{ctx.Id}"));

        ex.Message.ShouldContain("last-write-wins");
        ex.Message.ShouldContain("Register it with Saga<");
    }

    /// <summary>
    /// And the other way: the saga registration is what makes <c>CanApply</c> claim a chain, so a type
    /// with no completion state registered there would have the saga frames generated against it and
    /// fail at codegen instead of here.
    /// </summary>
    [Fact]
    public void saga_refuses_a_type_that_is_not_a_saga()
    {
        var ex = Should.Throw<InvalidRedisMappingException>(() =>
            new RedisPersistenceConfiguration().Saga(typeof(NotReallyASaga), x => x.KeyFor = ctx => $"{ctx.Id}"));

        ex.Message.ShouldContain("does not inherit from Wolverine's Saga");
    }

    /// <summary>
    /// There is no default key layout, because a key Wolverine invented would collide with whatever
    /// else the application keeps in the same Redis. Failing at the registration is much easier to
    /// place than failing at codegen.
    /// </summary>
    [Fact]
    public void a_registration_without_a_key_function_is_refused()
    {
        var ex = Should.Throw<InvalidRedisMappingException>(() =>
            new RedisPersistenceConfiguration().Store<ADocument>(_ => { }));

        ex.Message.ShouldContain("No key function was set");
    }

    [Fact]
    public void a_non_positive_expiry_is_refused()
    {
        var ex = Should.Throw<InvalidRedisMappingException>(() =>
            new RedisPersistenceConfiguration().Store<ADocument>(x =>
            {
                x.KeyFor = ctx => $"{ctx.Id}";
                x.ExpiresAfter = TimeSpan.Zero;
            }));

        ex.Message.ShouldContain("ExpiresAfter must be positive");
    }

    [Fact]
    public void an_unregistered_type_says_so_rather_than_inventing_a_key()
    {
        var configuration = new RedisPersistenceConfiguration();

        var ex = Should.Throw<InvalidRedisMappingException>(() => configuration.MappingFor(typeof(ADocument)));

        ex.Message.ShouldContain("It was never registered");
    }
}
