using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

public class EnrollAndFetchSagaStorageFrame<TId, TSaga> : AsyncFrame, ISagaStorageFrame, IAotRootSource
    where TSaga : Saga
{
    private Variable _context = null!;
    private Variable _cancellation = null!;

    public EnrollAndFetchSagaStorageFrame()
    {
        Variable = new Variable(typeof(ISagaStorage<TId, TSaga>), this);
        SimpleVariable = new Variable(typeof(ISagaStorage<TSaga>), Variable.Usage + "_Slim", this);
    }

    public Variable SimpleVariable { get; }

    public Variable Variable { get; }

    /// <summary>
    ///     GH-4765. This frame is built by closing its own open generic over the user's saga type and that
    ///     type's identity — <c>SagaStorageVariableSource</c> and
    ///     <c>LightweightSagaPersistenceFrameProvider</c> both do it, and all five relational packages
    ///     register one of those. Nothing statically references the closed type, so ILC trims it, and the
    ///     close then throws when the chain model is built: which still happens at startup under
    ///     <c>TypeLoadMode.Static</c>, generated code or not.
    /// </summary>
    /// <remarks>
    ///     The generic parameters are a type identity here, not a dispatch mechanism — the closed
    ///     <c>SagaSupport&lt;TId, TSaga&gt;</c> in <see cref="GenerateCode" /> is only ever rendered as
    ///     source text. So this could in principle be de-genericized instead, but the two
    ///     <see cref="Variable" /> types are load-bearing to downstream frames and rebuilding them would
    ///     need <c>MakeGenericType</c> — the one operation measured as unsafe in a native image unless
    ///     something already references the closed type. Rooting avoids that trade entirely.
    /// </remarks>
    public IEnumerable<Type> AotRoots()
    {
        yield return GetType();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"await using var {Variable.Usage} = await {typeof(SagaSupport<TId, TSaga>).FullNameInCode()}.{nameof(SagaSupport<TId, TSaga>.EnrollAndFetchSagaStorage)}({_context.Usage});");
        writer.Write($"var {SimpleVariable.Usage} = {Variable.Usage};");
        
        Next?.GenerateCode(method, writer);
        
        writer.Write($"await {Variable.Usage}.{nameof(ISagaStorage<TId, TSaga>.SaveChangesAsync)}({_cancellation.Usage});");
    }
}