using StronglyTypedIds;
using Wolverine.Http;
using Wolverine.Http.Marten;

namespace WolverineWebApi.Marten;

[StronglyTypedId(Template.Guid)]
public readonly partial struct ToyId;

public class Toy
{
    public ToyId Id { get; set; }
    public string Name { get; set; }
}

public static class ToyEndpoints
{
    [WolverineGet("/toys/{id}")]
    public static Toy Get([Document] Toy thing) => thing;
}

