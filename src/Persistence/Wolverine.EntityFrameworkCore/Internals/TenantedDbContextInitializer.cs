using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Wolverine.EntityFrameworkCore.Internals;

internal class TenantedDbContextInitializer<T> : IResourceCreator where T : DbContext
{
    private readonly IDbContextBuilder<T> _builder;
    private readonly WolverineOptions _options;

    public TenantedDbContextInitializer(IDbContextBuilder<T> builder, WolverineOptions options)
    {
        _builder = builder;
        _options = options;
        
        SubjectUri = new Uri(options.SubjectUri, $"tenanted-dbcontext/{typeof(T).ShortNameInCode()}");
    }

    public Task Check(CancellationToken token)
    {
        // TODO -- more here?
        return Task.CompletedTask;
    }

    public Task ClearState(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task Teardown(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public async Task Setup(CancellationToken token)
    {
        await _builder.ApplyAllChangesToDatabasesAsync();
    }

    public Task<IRenderable> DetermineStatus(CancellationToken token)
    {
        return Task.FromResult<IRenderable>(new Markup("No checks."));
    }

    public Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        return _builder.EnsureAllDatabasesAreCreatedAsync();
    }

    public string Type => "Multi-Tenanted DbContext";
    public string Name => typeof(T).FullNameInCode();
    public Uri SubjectUri { get; }
    public Uri ResourceUri => new Uri("wolverine://" + typeof(T).FullNameInCode());
}