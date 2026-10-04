namespace Wolverine.Persistence.Sagas;

/// <summary>
///     GH-4805. What a message store tells codegen so that generated saga code can build the store's saga
///     schema itself.
/// </summary>
/// <param name="SchemaType">
///     The <b>closed</b> schema type to construct. Rendered as source text; never instantiated from this
///     <see cref="Type" />.
/// </param>
/// <param name="HelperType">
///     The type declaring the static <c>EnrollAndFetchSagaStorage&lt;TId, TSaga&gt;(MessageContext, factory)</c>
///     the generated code should call.
/// </param>
/// <remarks>
///     A record rather than two <see cref="Type" /> arguments on the frame's constructor, because
///     <c>CloseAndBuildAs</c> has both a <c>params Type[]</c> overload and constructor-argument overloads:
///     passing <see cref="Type" /> values as constructor arguments silently binds to the
///     <c>params Type[]</c> one and fails at runtime with "The number of generic arguments provided
///     doesn't equal...". One argument that is not a <see cref="Type" /> cannot be mistaken that way.
/// </remarks>
public sealed record SagaSchemaCodegen(Type SchemaType, Type HelperType);

/// <summary>
///     GH-4805. Implemented by a message store that wants generated code to build its saga schema
///     directly, instead of the store reaching it at runtime through a generic virtual method that Native
///     AOT cannot dispatch.
/// </summary>
/// <remarks>
///     <para>Consulted once per saga chain while the chain MODEL is built, so everything here runs under
///     the JIT during <c>codegen write</c> or startup — never in a native image. That is what makes it
///     safe for an implementation to use <c>MakeGenericType</c> to name its closed schema type: the
///     result is only ever rendered as <b>source text</b>, never instantiated reflectively.</para>
///
///     <para>Returning <c>null</c> opts a store out for that saga and leaves it on the original
///     <c>ISagaSupport</c> path — which is what every store that does not implement this interface
///     already gets. Nothing is required of a store with no opinion.</para>
/// </remarks>
public interface ISagaStorageCodeSource
{
    /// <summary>
    ///     How generated code should build saga storage for this saga and identity pair, or <c>null</c> to
    ///     use the default runtime path.
    /// </summary>
    SagaSchemaCodegen? SagaSchemaCodegenFor(Type sagaType, Type idType);
}
