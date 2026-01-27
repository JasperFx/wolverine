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