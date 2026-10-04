using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Sagas;

public class SagaStorageVariableSource : IVariableSource
{
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "type is supplied by Wolverine's variable-source matching pipeline (from chain.ServiceDependencies) without DAM annotation. The TypeExtensions.Closes call inspects the type's generic-interface graph; for ISagaStorage<,>/ISagaStorage<>, the application-rooted saga types are preserved in any practical setup. AOT consumers register saga types explicitly per the AOT publishing guide.")]
    public bool Matches(Type type)
    {
        return type.Closes(typeof(ISagaStorage<,>)) || type.Closes(typeof(ISagaStorage<>));
    }

    // GH-4805: builds the NON-generic EnrollAndFetchSagaStorageFrame directly; nothing is closed
    // here any more. Same Dynamic-mode rationale as the saga frame providers
    // and chunk M (LoggerVariableSource) — AOT-clean apps run pre-generated
    // frames in TypeLoadMode.Static where these closures are baked in.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "EnrollAndFetchSagaStorageFrame<,> closed over runtime saga types during Dynamic codegen; AOT consumers run pre-generated frames. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "EnrollAndFetchSagaStorageFrame<,> closed over runtime saga types during Dynamic codegen; AOT consumers run pre-generated frames. See AOT guide.")]
    public Variable Create(Type type)
    {
        var sagaType = type.GetGenericArguments().Last();
        var idType = SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType();

        // GH-4805. Direct construction rather than CloseAndBuildAs, for the same reason as the frame
        // provider: the frame is not generic any more. This path only runs during code generation, so it
        // was never the one that failed under AOT -- it is changed to keep one way of building the frame.
        return new EnrollAndFetchSagaStorageFrame(idType!, sagaType).SimpleVariable;
    }
}