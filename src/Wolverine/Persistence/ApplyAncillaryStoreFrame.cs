using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;

namespace Wolverine.Persistence;

public class ApplyAncillaryStoreFrame<T> : MethodCall, IAotRootSource
{
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MethodCall reflects AncillaryMessageStoreApplication<T>.GetMethod(\"Apply\") at codegen time. T flows in from a registered persistence pairing on WolverineOptions; AOT consumers pre-generate via TypeLoadMode.Static so the reflective close never fires.")]
    public ApplyAncillaryStoreFrame() : base(typeof(AncillaryMessageStoreApplication<T> ), "Apply")
    {
    }

    /// <summary>
    ///     GH-4765. <c>EFCorePersistenceFrameProvider.ApplyTransactionSupport</c> closes this frame over the
    ///     ancillary store's marker type — the user's <c>DbContext</c> — and nothing statically references
    ///     the closed type, so ILC trims it and the close throws when the chain model is built. That still
    ///     happens at startup under <c>TypeLoadMode.Static</c>, generated code or not.
    /// </summary>
    /// <remarks>
    ///     Both types, not just this frame. The base <see cref="MethodCall" /> constructor above resolves
    ///     <c>Apply</c> by name off <see cref="AncillaryMessageStoreApplication{T}" />, and that is a
    ///     reflective lookup: the generated code's direct call to the same method keeps the body, but not
    ///     the metadata <c>GetMethod</c> needs. Same reasoning as the handler types in the registry's own
    ///     rooting block.
    /// </remarks>
    public IEnumerable<Type> AotRoots()
    {
        yield return GetType();
        yield return typeof(AncillaryMessageStoreApplication<T>);
    }
}