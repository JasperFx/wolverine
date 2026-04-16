using Wolverine.Persistence.Sagas;
using Xunit;

namespace CoreTests.Persistence.Sagas;

public class saga_id_member_determination
{
    [Theory]
    [InlineData(typeof(SomeSagaMessage1), nameof(SomeSagaMessage1.RandomName))]
    [InlineData(typeof(SomeSagaMessage2), nameof(SomeSagaMessage2.SagaId))]
    [InlineData(typeof(SomeSagaMessage3), nameof(SomeSagaMessage3.SomeSagaId))]
    [InlineData(typeof(SomeSagaMessage4), nameof(SomeSagaMessage4.Id))]
    public void determine_the_member(Type messageType, string expectedMemberName)
    {
        SagaChain.DetermineSagaIdMember(messageType, typeof(SomeSaga))?.Name
            .ShouldBe(expectedMemberName);
    }

    [Fact]
    public void member_is_determined_by_attribute()
    {
        var method = typeof(SomeSaga).GetMethod(nameof(SomeSaga.Handle));

        SagaChain.DetermineSagaIdMember(typeof(SomeSagaMessage5), typeof(SomeSaga), method)
            !.Name.ShouldBe(nameof(SomeSagaMessage5.Hello));
    }

    [Fact]
    public void member_is_determined_by_attribute_scanning_all_methods()
    {
        // When multiple methods are passed, [SagaIdentityFrom] should be found
        // regardless of which method has it — fixes GH-2521
        var notFoundMethod = typeof(SagaWithNotFoundFirst).GetMethod(nameof(SagaWithNotFoundFirst.NotFound))!;
        var handleMethod = typeof(SagaWithNotFoundFirst).GetMethod(nameof(SagaWithNotFoundFirst.Handle))!;

        // Even when NotFound is listed first (no [SagaIdentityFrom]), the Handle method's attribute is found
        SagaChain.DetermineSagaIdMember(typeof(NonConventionalMessage), typeof(SagaWithNotFoundFirst),
            [notFoundMethod, handleMethod])
            !.Name.ShouldBe(nameof(NonConventionalMessage.TargetId));
    }

    [Fact]
    public void member_is_determined_by_attribute_even_with_single_wrong_method()
    {
        // When only the NotFound method is passed (old behavior), the attribute is NOT found
        var notFoundMethod = typeof(SagaWithNotFoundFirst).GetMethod(nameof(SagaWithNotFoundFirst.NotFound))!;

        // Without scanning all methods, falls back to convention — which won't find "TargetId"
        var result = SagaChain.DetermineSagaIdMember(typeof(NonConventionalMessage), typeof(SagaWithNotFoundFirst),
            [notFoundMethod]);

        // Should NOT find TargetId since NotFound doesn't have [SagaIdentityFrom]
        result.ShouldBeNull();
    }
}

public record SomeSagaMessage1(Guid Id, [property: SagaIdentity] Guid RandomName);
public record SomeSagaMessage2(Guid SagaId, Guid Id);
public record SomeSagaMessage3(Guid Id, Guid SomeSagaId, Guid SagaId);
public record SomeSagaMessage4(Guid Id);
public record SomeSagaMessage5(Guid Hello, Guid Id, Guid SagaId, Guid SomeSagaId);

#region sample_using_SagaIdentityFrom

public class SomeSaga
{
    public Guid Id { get; set; }

    public void Handle([SagaIdentityFrom(nameof(SomeSagaMessage5.Hello))] SomeSagaMessage5 message) { }
}

#endregion

// GH-2521: Message with non-conventional property name (no convention match for "SagaWithNotFoundFirst")
public record NonConventionalMessage(string TargetId);

// GH-2521: Saga where NotFound is declared BEFORE Handle
public class SagaWithNotFoundFirst : Saga
{
    public string Id { get; set; } = string.Empty;

    // NotFound is declared first — this caused the bug when only the first method was scanned
    public static void NotFound(NonConventionalMessage msg) { }

    public void Handle([SagaIdentityFrom(nameof(NonConventionalMessage.TargetId))] NonConventionalMessage msg) { }
}