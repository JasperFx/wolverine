using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Xunit;

namespace CoreTests.Compilation;

/// <summary>
/// Closes #2722 — the test gap that opened up when JasperFx PR #239
/// closed #2718 (codegen CLI paths now invoke
/// <c>ICodeFile.AssertServiceLocationsAreAllowed(...)</c> on each file,
/// matching the runtime compile path in <c>DynamicTypeLoader.Initialize</c>).
///
/// Before that fix, the codegen-write/preview/test CLI silently emitted code
/// regardless of policy. After it, the CLI fails fast — but until these
/// tests landed, the new behaviour wasn't pinned, so a regression would have
/// gone unnoticed. Each test below builds a host under one of the three
/// <see cref="ServiceLocationPolicy"/> values, optionally pre-populates the
/// allow-list via <c>opts.CodeGeneration.AlwaysUseServiceLocationFor&lt;T&gt;()</c>,
/// and drives the same <see cref="DynamicCodeBuilder.GenerateAllCode"/> path
/// the CLI uses. The combinations cover the matrix the issue body called out.
/// </summary>
public class codegen_write_service_location_policy
{
    [Fact]
    public void not_allowed_with_no_allow_list_throws_when_handler_requires_service_location()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ServiceLocationPolicy = ServiceLocationPolicy.NotAllowed;

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(OpaqueWidgetHandler));

                    // Opaque lambda-factory — codegen can't inline-construct.
                    opts.Services.AddScoped<IOpaqueWidget>(_ => new OpaqueWidget());
                })
                .Build();

            var builder = BuildCodeBuilder(host);
            Should.Throw<InvalidServiceLocationException>(() => builder.GenerateAllCode());
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public void not_allowed_with_matching_allow_list_entry_succeeds()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ServiceLocationPolicy = ServiceLocationPolicy.NotAllowed;

                    // Explicitly opt this type into the allow-list. The codegen
                    // check exempts allow-listed types from producing
                    // ServiceLocationReports, so the per-file assertion passes.
                    opts.CodeGeneration.AlwaysUseServiceLocationFor<IOpaqueWidget>();

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(OpaqueWidgetHandler));

                    opts.Services.AddScoped<IOpaqueWidget>(_ => new OpaqueWidget());
                })
                .Build();

            var builder = BuildCodeBuilder(host);
            var code = builder.GenerateAllCode();
            code.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public void not_allowed_with_unrelated_allow_list_entry_still_throws()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ServiceLocationPolicy = ServiceLocationPolicy.NotAllowed;

                    // Allow-list entry is type-specific. The handler service-locates
                    // IOpaqueWidget, NOT IUnrelatedService, so the assertion still
                    // produces a ServiceLocationReport for IOpaqueWidget.
                    opts.CodeGeneration.AlwaysUseServiceLocationFor<IUnrelatedService>();

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(OpaqueWidgetHandler));

                    opts.Services.AddScoped<IOpaqueWidget>(_ => new OpaqueWidget());
                    opts.Services.AddScoped<IUnrelatedService>(_ => new UnrelatedService());
                })
                .Build();

            var builder = BuildCodeBuilder(host);
            Should.Throw<InvalidServiceLocationException>(() => builder.GenerateAllCode());
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Theory]
    [InlineData(ServiceLocationPolicy.AlwaysAllowed)]
    [InlineData(ServiceLocationPolicy.AllowedButWarn)]
    public void permissive_policies_succeed_even_when_handler_requires_service_location(
        ServiceLocationPolicy policy)
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ServiceLocationPolicy = policy;

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(OpaqueWidgetHandler));

                    opts.Services.AddScoped<IOpaqueWidget>(_ => new OpaqueWidget());
                })
                .Build();

            var builder = BuildCodeBuilder(host);
            var code = builder.GenerateAllCode();
            code.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Theory]
    [InlineData(ServiceLocationPolicy.NotAllowed)]
    [InlineData(ServiceLocationPolicy.AllowedButWarn)]
    [InlineData(ServiceLocationPolicy.AlwaysAllowed)]
    public void any_policy_succeeds_when_no_handler_requires_service_location(
        ServiceLocationPolicy policy)
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ServiceLocationPolicy = policy;

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(TransparentWidgetHandler));

                    // Concrete-type registration — codegen can inline-construct this
                    // via constructor injection with no service location.
                    opts.Services.AddScoped<TransparentWidget>();
                })
                .Build();

            var builder = BuildCodeBuilder(host);
            var code = builder.GenerateAllCode();
            code.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    /// <summary>
    /// Guards the Quickstart sample's service-location story (#2584 Q4 / β).
    /// The Quickstart program does fully transparent constructor and method-
    /// parameter injection with concrete-type registrations; codegen must NOT
    /// need service location for it under the new <see cref="ServiceLocationPolicy.NotAllowed"/>
    /// default. Mirrors the Quickstart's handler + repository setup so that any
    /// future regression that introduces an opaque registration there breaks CI.
    /// </summary>
    [Fact]
    public void quickstart_shape_passes_codegen_under_not_allowed_default()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // Don't set ServiceLocationPolicy explicitly — relies on the new
                    // 6.0 NotAllowed default. If any future change to the Quickstart
                    // sample introduces an opaque registration, codegen here would
                    // throw and this test would fail.
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(QuickstartCreateHandler))
                        .IncludeType(typeof(QuickstartCreatedHandler))
                        .IncludeType(typeof(QuickstartAssignHandler))
                        .IncludeType(typeof(QuickstartAssignedHandler));

                    // Concrete-type singletons matching the Quickstart's
                    // `AddSingleton<UserRepository>()` / `AddSingleton<IssueRepository>()`.
                    opts.Services.AddSingleton<QuickstartUserRepository>();
                    opts.Services.AddSingleton<QuickstartIssueRepository>();
                })
                .Build();

            var builder = BuildCodeBuilder(host);
            var code = builder.GenerateAllCode();
            code.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    private static DynamicCodeBuilder BuildCodeBuilder(IHost host)
    {
        var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();
        return new DynamicCodeBuilder(host.Services, collections)
        {
            ServiceVariableSource = host.Services.GetService<IServiceVariableSource>()
        };
    }
}

public interface IOpaqueWidget;
public class OpaqueWidget : IOpaqueWidget;

public interface IUnrelatedService;
public class UnrelatedService : IUnrelatedService;

public class TransparentWidget;

public record OpaqueWidgetMessage;
public record TransparentWidgetMessage;

public static class OpaqueWidgetHandler
{
    public static void Handle(OpaqueWidgetMessage _, IOpaqueWidget widget)
    {
        widget.ShouldNotBeNull();
    }
}

public static class TransparentWidgetHandler
{
    public static void Handle(TransparentWidgetMessage _, TransparentWidget widget)
    {
        widget.ShouldNotBeNull();
    }
}

// --- Quickstart-shaped fixtures (mirror src/Samples/Quickstart) ---

public class QuickstartUserRepository;
public class QuickstartIssueRepository;

public record QuickstartCreateCommand(string Title);
public record QuickstartCreatedEvent(Guid Id);
public record QuickstartAssignCommand(Guid IssueId, Guid AssigneeId);
public record QuickstartAssignedEvent(Guid Id);

public class QuickstartCreateHandler
{
    private readonly QuickstartIssueRepository _repository;
    public QuickstartCreateHandler(QuickstartIssueRepository repository) => _repository = repository;
    public QuickstartCreatedEvent Handle(QuickstartCreateCommand _) => new(Guid.NewGuid());
}

public static class QuickstartCreatedHandler
{
    public static void Handle(QuickstartCreatedEvent _, QuickstartIssueRepository repository)
    {
        repository.ShouldNotBeNull();
    }
}

public class QuickstartAssignHandler
{
    public QuickstartAssignedEvent Handle(QuickstartAssignCommand command, QuickstartIssueRepository issues)
    {
        issues.ShouldNotBeNull();
        return new QuickstartAssignedEvent(command.IssueId);
    }
}

public static class QuickstartAssignedHandler
{
    public static void Handle(QuickstartAssignedEvent _, QuickstartUserRepository users, QuickstartIssueRepository issues)
    {
        users.ShouldNotBeNull();
        issues.ShouldNotBeNull();
    }
}
