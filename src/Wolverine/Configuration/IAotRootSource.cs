namespace Wolverine.Configuration;

/// <summary>
///     Implemented by anything that is itself built by closing an open generic reflectively, so that
///     <c>codegen write</c> can emit a <c>[DynamicDependency]</c> keeping that closed type alive under
///     Native AOT.
/// </summary>
/// <remarks>
///     GH-4765. Before this, the only AOT roots were the ones core could name for itself —
///     <c>AddAotRoots</c> is called from <c>HandlerRegistryCodeFile</c> and
///     <c>HttpEndpointRegistryCodeFile</c>, and neither can reference a <c>Wolverine.Marten</c> or
///     <c>Wolverine.EntityFrameworkCore</c> type. So a frame that a persistence package closes over the
///     user's saga, aggregate or <c>DbContext</c> type had no way to be rooted, and ILC trimmed it.
///
///     <para>Declaring the root here rather than in a registry keeps it next to the reflective close that
///     needs it: a new frame cannot be added and then forgotten by a list somewhere else. The chain walk
///     that collects these runs over frames that have already been built, which is why
///     <see cref="AotRoots" /> can simply answer <c>GetType()</c> — by then the close has happened and the
///     closed type is a real <c>Type</c>.</para>
///
///     <para>Two constraints the returned types have to meet. They must be <b>public</b>, because the
///     emitted root names them inside a <c>typeof()</c> in generated code that cannot see an internal
///     type — non-public types are filtered out rather than breaking the generated file. And they must be
///     closed: an open generic cannot be named in a <c>[DynamicDependency]</c> at all.</para>
/// </remarks>
public interface IAotRootSource
{
    /// <summary>
    ///     The closed types that have to survive trimming for this object to be built again at runtime.
    ///     Usually just <c>GetType()</c>.
    /// </summary>
    IEnumerable<Type> AotRoots();
}
