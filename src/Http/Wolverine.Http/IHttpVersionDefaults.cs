namespace Wolverine.Http;

/// <summary>
/// GH-5520. A Wolverine.Http extension package's share of the defaults that flipped in 7.0, registered in the
/// container by the package. Applied by <c>MapWolverineEndpoints()</c> after the user's configuration and
/// <see cref="WolverineHttpOptions"/>' own defaults, and before endpoint discovery, so an implementation sees
/// both <see cref="WolverineOptions.V6DefaultsRestored"/> and anything the user configured explicitly, which
/// must win.
/// </summary>
internal interface IHttpVersionDefaults
{
    void Apply(WolverineHttpOptions options, WolverineOptions wolverineOptions, IServiceProvider services);
}
