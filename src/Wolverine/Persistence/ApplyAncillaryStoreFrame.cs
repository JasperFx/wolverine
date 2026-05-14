using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Persistence.Durability;

namespace Wolverine.Persistence;

public class ApplyAncillaryStoreFrame<T> : MethodCall
{
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MethodCall reflects AncillaryMessageStoreApplication<T>.GetMethod(\"Apply\") at codegen time. T flows in from a registered persistence pairing on WolverineOptions; AOT consumers pre-generate via TypeLoadMode.Static so the reflective close never fires.")]
    public ApplyAncillaryStoreFrame() : base(typeof(AncillaryMessageStoreApplication<T> ), "Apply")
    {
    }

}