using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Wolverine.Http.Newtonsoft.CodeGen;

namespace Wolverine.Http.Newtonsoft;

/// <summary>
///     Wolverine 6.0 extension methods that wire Newtonsoft.Json into the
///     Wolverine.Http JSON serialization pipeline. The Newtonsoft surface
///     lived inline on <see cref="WolverineHttpOptions"/> in Wolverine 5.x;
///     it moved to this separate package in 6.0 so the core
///     <c>WolverineFx.Http</c> NuGet package no longer carries a
///     Newtonsoft.Json dependency. Mirrors the symmetric extraction of
///     core Wolverine to <c>WolverineFx.Newtonsoft</c> (PR #2743).
///     See the Wolverine 6.0 migration guide.
/// </summary>
public static class WolverineHttpNewtonsoftExtensions
{
    /// <summary>
    ///     Register the Newtonsoft.Json HTTP-serialization singleton
    ///     (<c>NewtonsoftHttpSerialization</c>) used by the codegen frames
    ///     emitted when <see cref="WolverineHttpOptions"/> is opted into
    ///     <see cref="JsonUsage.NewtonsoftJson"/>. Call this alongside
    ///     <c>AddWolverineHttp()</c> on your <see cref="IServiceCollection"/>
    ///     when you intend to call
    ///     <see cref="UseNewtonsoftJsonForSerialization"/> later inside
    ///     <c>MapWolverineEndpoints</c>.
    /// </summary>
    /// <param name="services">The service collection to register against.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddWolverineHttpNewtonsoft(this IServiceCollection services)
    {
        services.AddSingleton<NewtonsoftHttpSerialization>(sp =>
        {
            var options = sp.GetRequiredService<WolverineHttpOptions>();
            var settings = new JsonSerializerSettings();
            if (options.NewtonsoftSettingsConfiguration is Action<JsonSerializerSettings> configure)
            {
                configure(settings);
            }
            return new NewtonsoftHttpSerialization(settings);
        });

        return services;
    }

    /// <summary>
    ///     Opt into using Newtonsoft.Json for all JSON serialization in the Wolverine
    ///     Http handlers. Mirrors the Wolverine 5.x
    ///     <c>WolverineHttpOptions.UseNewtonsoftJsonForSerialization</c> instance method.
    ///     Requires the <c>WolverineFx.Http.Newtonsoft</c> NuGet package and a prior
    ///     call to <see cref="AddWolverineHttpNewtonsoft"/> on the IServiceCollection.
    /// </summary>
    /// <param name="options">The Wolverine HTTP options.</param>
    /// <param name="configure">Optional callback to customize the Newtonsoft settings.</param>
    public static void UseNewtonsoftJsonForSerialization(this WolverineHttpOptions options,
        Action<JsonSerializerSettings>? configure = null)
    {
        if (options.Endpoints is null)
        {
            throw new InvalidOperationException(
                $"{nameof(UseNewtonsoftJsonForSerialization)} must be called inside the " +
                "MapWolverineEndpoints configuration callback so the HttpGraph is initialized.");
        }

        // Stash the user's settings callback on the (untyped) options slot.
        // AddWolverineHttpNewtonsoft()'s singleton factory reads it back when
        // the DI container first resolves NewtonsoftHttpSerialization at
        // request time.
        if (configure != null)
        {
            options.NewtonsoftSettingsConfiguration = configure;
        }

        options.Endpoints.UseNewtonsoftJson(new NewtonsoftHttpCodeGen());
    }
}
