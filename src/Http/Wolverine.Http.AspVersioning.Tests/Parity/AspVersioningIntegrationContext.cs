using Alba;
using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Wolverine.Http.AspVersioning.Tests.Parity;

/// <summary>
/// One shared Alba host for every integration tier, mirroring the shared-fixture convention in
/// <c>Wolverine.Http.Tests</c> (<c>AppFixture</c> + <c>IntegrationContext</c> +
/// <c>[Collection("integration")]</c>).
/// </summary>
public class AspVersioningAppFixture : ParityFixture
{
    public override Task InitializeAsync() =>
        BuildHost(
            services =>
            {
                services
                    .AddApiVersioning(options =>
                    {
                        options.ReportApiVersions = true;

                        options.ApiVersionReader = ApiVersionReader.Combine(
                            new QueryStringApiVersionReader(),
                            new HeaderApiVersionReader("api-version"),
                            new MediaTypeApiVersionReader()
                        );

                        // Version-keyed sunset policy (null name = unnamed set, which is what the bridge
                        // builds and what the native twin uses) so the sunset twins can assert Sunset/Link
                        // header parity. 5.0 is unique to the sunset twins, so no other endpoint is affected.
                        var sunset = options.Policies.Sunset(null, new ApiVersion(5, 0));
                        sunset.SetEffectiveDate(
                            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)
                        );
                        sunset
                            .Link(new Uri("https://example.com/sunset"))
                            .Title("Sunset")
                            .Type("text/html");
                    })
                    .AddApiExplorer(options =>
                    {
                        options.GroupNameFormat = "'v'VVV"; // "v1", "v2", "v4", ...
                        options.SubstituteApiVersionInUrl = false;
                    });

                services.AddEndpointsApiExplorer();

                services.AddSwaggerGen(options =>
                {
                    options.DocInclusionPredicate((docName, api) => api.GroupName == docName);
                    // The supported-wins twins register two endpoints at the SAME version (8.0) on
                    // GET /wolverine/conflict, so that version's document has a duplicate path+method.
                    // This is Swashbuckle's prescribed resolver for it.
                    options.ResolveConflictingActions(descriptions => descriptions.First());
                    options.OperationFilter<DeprecatedVersionOperationFilter>();
                });
                services.ConfigureOptions<ConfigureVersionedSwaggerDocs>();
            },
            app =>
            {
                app.UseSwagger();

                // Native minimal-API twins for the parity tier — registered with their own ApiVersionSets,
                // exactly as a vanilla Asp.Versioning consumer would (see ParityEndpoints).
                ParityEndpoints.MapNative(app);
            }
        );
}

[CollectionDefinition("integration")]
public class AspVersioningIntegrationCollection : ICollectionFixture<AspVersioningAppFixture>;

/// <summary>
/// Base class for the integration tiers. Hands tests the shared host and the <c>Scenario</c> /
/// service-resolution helpers, matching <c>Wolverine.Http.Tests.IntegrationContext</c>.
/// </summary>
[Collection("integration")]
public abstract class AspVersioningIntegrationContext
{
    protected AspVersioningIntegrationContext(AspVersioningAppFixture fixture) =>
        Host = fixture.Host;

    protected IAlbaHost Host { get; }

    protected Task<IScenarioResult> Scenario(Action<Scenario> configure) =>
        Host.Scenario(configure);

    protected T Service<T>()
        where T : notnull => Host.Services.GetRequiredService<T>();

    /// <summary>The API-explorer group name Asp.Versioning assigns to <paramref name="version"/>.</summary>
    protected string GroupNameFor(ApiVersion version) =>
        Service<IApiVersionDescriptionProvider>()
            .ApiVersionDescriptions.Single(d => d.ApiVersion == version)
            .GroupName;
}

/// <summary>
/// Canonical Asp.Versioning + Swashbuckle recipe: register one Swagger document per discovered API
/// version, named by the API explorer's group name.
/// </summary>
internal sealed class ConfigureVersionedSwaggerDocs : IConfigureOptions<SwaggerGenOptions>
{
    private readonly IApiVersionDescriptionProvider _provider;

    public ConfigureVersionedSwaggerDocs(IApiVersionDescriptionProvider provider) =>
        _provider = provider;

    public void Configure(SwaggerGenOptions options)
    {
        foreach (var description in _provider.ApiVersionDescriptions)
        {
            options.SwaggerDoc(
                description.GroupName,
                new OpenApiInfo
                {
                    Title = "AspVersioning Integration Tests",
                    Version = description.ApiVersion.ToString(),
                }
            );
        }
    }
}

/// <summary>Marks operations belonging to a deprecated API version as deprecated in the document.</summary>
internal sealed class DeprecatedVersionOperationFilter : IOperationFilter
{
    private readonly IApiVersionDescriptionProvider _provider;

    public DeprecatedVersionOperationFilter(IApiVersionDescriptionProvider provider) =>
        _provider = provider;

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var groupName = context.ApiDescription.GroupName;
        if (groupName is null)
        {
            return;
        }

        var description = _provider.ApiVersionDescriptions.FirstOrDefault(d =>
            d.GroupName == groupName
        );
        if (description is { IsDeprecated: true })
        {
            operation.Deprecated = true;
        }
    }
}
