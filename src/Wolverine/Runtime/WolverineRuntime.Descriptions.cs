using Oakton.Descriptions;

namespace Wolverine.Runtime;

public sealed partial class WolverineRuntime : IDescribedSystemPartFactory
{
    IDescribedSystemPart[] IDescribedSystemPartFactory.Parts()
    {
        Handlers.Compile(Options, _container);

        return new IDescribedSystemPart[] { Options, Handlers };
    }
}