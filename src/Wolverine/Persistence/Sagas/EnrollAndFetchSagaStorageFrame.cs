using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

/// <summary>
///     Opens saga storage for a saga chain, and saves it once the handler body has run.
/// </summary>
/// <remarks>
///     GH-4805. <b>Non-generic on purpose.</b> This used to be
///     <c>EnrollAndFetchSagaStorageFrame&lt;TId, TSaga&gt;</c>, closed over the user's saga type with
///     <c>CloseAndBuildAs</c> during policy application — which runs at startup under
///     <c>TypeLoadMode.Static</c> too. GH-4765 rooted the closed type so ILC would keep it, and that
///     turned out not to be enough: a <c>[DynamicDependency]</c> preserves <i>metadata</i> but does not
///     make ILC generate <i>code</i> for an instantiation, so a <c>Guid</c>-keyed saga still failed at
///     startup with "missing native code or metadata". Not closing a generic at all removes the problem
///     rather than asking the trimmer to work around it.
///
///     <para>The two identity types still have to produce closed <c>ISagaStorage</c> variable types, and
///     those come from <c>MakeGenericType</c> — safe here, and measured so: the sibling
///     <c>LightweightSagaPersistenceFrameProvider.CanPersist</c> has always closed
///     <c>ISagaStorage&lt;,&gt;</c> the same way during the same policy pass, and a native image runs it.
///     Generated code also names the closed return type of the storage call, so the instantiation is
///     compiled in regardless.</para>
/// </remarks>
public class EnrollAndFetchSagaStorageFrame : AsyncFrame, ISagaStorageFrame, IAotRootSource
{
    private readonly Type _idType;
    private readonly Type _sagaType;
    private readonly SagaSchemaCodegen? _codegen;
    private Variable _context = null!;
    private Variable _cancellation = null!;

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "GH-4805. Closes ISagaStorage<,>/ISagaStorage<> over the chain's saga and identity types to type the frame's variables. Both instantiations are named by the generated code this frame emits -- the storage call's return type -- so they are compiled into the native image, and the identical close in LightweightSagaPersistenceFrameProvider.CanPersist runs in the same policy pass of a working native image.")]
    public EnrollAndFetchSagaStorageFrame(Type idType, Type sagaType, SagaSchemaCodegen? codegen = null)
    {
        _idType = idType;
        _sagaType = sagaType;
        _codegen = codegen;

        Variable = new Variable(typeof(ISagaStorage<,>).MakeGenericType(idType, sagaType), this);
        SimpleVariable = new Variable(typeof(ISagaStorage<>).MakeGenericType(sagaType),
            Variable.Usage + "_Slim", this);
    }

    public Variable SimpleVariable { get; }

    public Variable Variable { get; }

    /// <summary>
    ///     GH-4805. Nothing closes this frame reflectively any more, so the frame itself needs no root.
    ///     The store's schema type does, but only for a Dynamic-mode host inside a trimmed app, where
    ///     <c>SagaSchemaFor</c> rather than the emitted factory is what builds it.
    /// </summary>
    public IEnumerable<Type> AotRoots()
    {
        if (_codegen != null) yield return _codegen.SchemaType;
    }

    /// <summary>
    ///     A type's name as generated code can use it, including when the type has no namespace.
    /// </summary>
    /// <remarks>
    ///     GH-4805. <c>FullNameInCode()</c> renders a type in the <b>global namespace</b> with a leading
    ///     <c>.</c> — so <c>new .DatabaseSagaSchema&lt;MySaga, string&gt;(...)</c>, which is not valid C#
    ///     and fails compilation with CS1526. That is not hypothetical: <c>Wolverine.Postgresql</c>'s
    ///     <c>Sagas/DatabaseSagaSchema.cs</c> declares no namespace at all, unlike its four sibling
    ///     stores, so every Postgresql saga chain emitted a file that would not compile until this guard
    ///     existed. Guarding here rather than relying on the declaration being fixed keeps a future store
    ///     with the same slip from silently breaking codegen.
    /// </remarks>
    private static string typeNameFor(Type type)
    {
        var rendered = type.FullNameInCode();
        return rendered.StartsWith('.') ? rendered.TrimStart('.') : rendered;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "GH-4805. Closes SagaSupport<,> only to RENDER ITS NAME as source text; nothing is instantiated from the Type. GenerateCode runs during code generation, which never happens in a native image -- StaticTypeLoader attaches the pre-generated type and never calls AssembleTypes. Reached only when the store supplied no SagaSchemaCodegen, which an AOT application does not do.")]
    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var idTypeName = _idType.FullNameInCode();
        var sagaTypeName = _sagaType.FullNameInCode();

        if (_codegen != null)
        {
            // GH-4805. The static lambda is the load-bearing part: it names the store's closed schema type
            // in compiled code, which is the only way ILC generates that constructor. `static` so the
            // delegate is cached by the compiler rather than allocated per message.
            writer.Write(
                $"await using var {Variable.Usage} = await {typeNameFor(_codegen.HelperType)}.{nameof(SagaSupport<int, Saga>.EnrollAndFetchSagaStorage)}<{idTypeName}, {sagaTypeName}>({_context.Usage}, static (definition, settings) => new {typeNameFor(_codegen.SchemaType)}(definition, settings));");
        }
        else
        {
            // The original path, for a store with no codegen opinion. Named through the closed type so the
            // rendering stays correct if SagaSupport is ever renamed or moved.
            var sagaSupport = typeof(SagaSupport<,>).MakeGenericType(_idType, _sagaType);
            writer.Write(
                $"await using var {Variable.Usage} = await {sagaSupport.FullNameInCode()}.{nameof(SagaSupport<int, Saga>.EnrollAndFetchSagaStorage)}({_context.Usage});");
        }

        writer.Write($"var {SimpleVariable.Usage} = {Variable.Usage};");

        Next?.GenerateCode(method, writer);

        writer.Write($"await {Variable.Usage}.{nameof(ISagaStorage<int, Saga>.SaveChangesAsync)}({_cancellation.Usage});");
    }
}

/// <summary>
///     Kept so that code naming this type by its old generic spelling still compiles. Nothing in Wolverine
///     closes it any more — see the remarks on <see cref="EnrollAndFetchSagaStorageFrame" /> for why that
///     mattered under Native AOT.
/// </summary>
[FSharpEmit(Skip = true,
    Reason = "GH-4805 source-compatibility shim. Nothing constructs it -- the frame provider and the variable source both build the non-generic base directly -- so F# code generation can never reach it. It inherits the base's emitter regardless.")]
public class EnrollAndFetchSagaStorageFrame<TId, TSaga> : EnrollAndFetchSagaStorageFrame
    where TSaga : Saga
{
    public EnrollAndFetchSagaStorageFrame() : base(typeof(TId), typeof(TSaga))
    {
    }
}
