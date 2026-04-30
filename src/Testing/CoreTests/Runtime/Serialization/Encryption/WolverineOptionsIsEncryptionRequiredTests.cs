using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Runtime.Serialization.Encryption;

public class WolverineOptionsIsEncryptionRequiredTests
{
    public interface IBase { }
    public abstract class AbstractBase { }
    public sealed record ConcreteImpl(string X) : IBase;
    public sealed class ConcreteAbstract : AbstractBase { }
    public sealed record Unrelated(string X);
    public sealed record ExactType(string X);

    [Fact]
    public void exact_type_match_returns_true()
    {
        var opts = new WolverineOptions();
        opts.RequiredEncryptedTypes.Add(typeof(ExactType));

        opts.IsEncryptionRequired(typeof(ExactType)).ShouldBeTrue();
    }

    [Fact]
    public void subtype_of_registered_interface_returns_true()
    {
        var opts = new WolverineOptions();
        opts.RequiredEncryptedTypes.Add(typeof(IBase));

        opts.IsEncryptionRequired(typeof(ConcreteImpl)).ShouldBeTrue();
    }

    [Fact]
    public void subtype_of_registered_abstract_class_returns_true()
    {
        var opts = new WolverineOptions();
        opts.RequiredEncryptedTypes.Add(typeof(AbstractBase));

        opts.IsEncryptionRequired(typeof(ConcreteAbstract)).ShouldBeTrue();
    }

    [Fact]
    public void unrelated_type_returns_false()
    {
        var opts = new WolverineOptions();
        opts.RequiredEncryptedTypes.Add(typeof(IBase));

        opts.IsEncryptionRequired(typeof(Unrelated)).ShouldBeFalse();
    }

    [Fact]
    public void empty_set_returns_false()
    {
        var opts = new WolverineOptions();

        opts.IsEncryptionRequired(typeof(ExactType)).ShouldBeFalse();
    }

    [Fact]
    public void null_type_returns_false()
    {
        var opts = new WolverineOptions();
        opts.RequiredEncryptedTypes.Add(typeof(IBase));

        opts.IsEncryptionRequired(null!).ShouldBeFalse();
    }

    [Fact]
    public void result_is_cached_per_type()
    {
        var opts = new WolverineOptions();
        opts.RequiredEncryptedTypes.Add(typeof(IBase));

        // First call: scans set, caches true for ConcreteImpl.
        opts.IsEncryptionRequired(typeof(ConcreteImpl)).ShouldBeTrue();

        // Mutate the set: remove the marker after the answer is cached.
        opts.RequiredEncryptedTypes.Remove(typeof(IBase));

        // Cache wins — answer for ConcreteImpl is still true.
        opts.IsEncryptionRequired(typeof(ConcreteImpl)).ShouldBeTrue();

        // A type whose answer was never cached uses the (now-empty) live set.
        opts.IsEncryptionRequired(typeof(ExactType)).ShouldBeFalse();
    }
}
