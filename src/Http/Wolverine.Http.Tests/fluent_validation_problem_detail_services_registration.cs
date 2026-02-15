using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.FluentValidation;
using Wolverine.Http.FluentValidation;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.FluentValidation.Internals;
using Wolverine.Http.FluentValidation.Internals;
using Wolverine.Runtime;

namespace Wolverine.Http.Tests;

public class fluent_validation_problem_detail_services_registration
{
    [Fact]
    public void services_registered_once_automatic_extension_discovery_without_manual_call()
    {
        var services = new ServiceCollection();
        services.AddWolverine(ExtensionDiscovery.Automatic, opts => { opts.UseFluentValidation(); });

        int failureActionCount = services.Count(d => d.ServiceType == typeof(IFailureAction<>));
        int problemDetailCount = services.Count(d => d.ServiceType == typeof(IProblemDetailSource<>));

        failureActionCount.ShouldBe(1);
        problemDetailCount.ShouldBe(1);
    }

    [Fact]
    public void services_registered_once_automatic_extension_discovery_with_manual_call()
    {
        var services = new ServiceCollection();
        services.AddWolverine(ExtensionDiscovery.Automatic, opts =>
        {
            opts.UseFluentValidation();
            opts.UseFluentValidationProblemDetail();
        });

        int failureActionCount = services.Count(d => d.ServiceType == typeof(IFailureAction<>));
        int problemDetailCount = services.Count(d => d.ServiceType == typeof(IProblemDetailSource<>));

        failureActionCount.ShouldBe(1);
        problemDetailCount.ShouldBe(1);
    }

    [Fact]
    public void IProblemDetailSource_not_registered_manual_extension_discovery_without_manual_call()
    {
        var services = new ServiceCollection();
        services.AddWolverine(ExtensionDiscovery.ManualOnly, opts => { opts.UseFluentValidation(); });

        int problemDetailCount = services.Count(d => d.ServiceType == typeof(IProblemDetailSource<>));

        problemDetailCount.ShouldBe(0);
    }

    [Fact]
    public void services_registered_once_manual_extension_discovery_with_manual_call()
    {
        var services = new ServiceCollection();
        services.AddWolverine(ExtensionDiscovery.ManualOnly, opts =>
        {
            opts.UseFluentValidation();
            opts.UseFluentValidationProblemDetail();
        });

        int failureActionCount = services.Count(d => d.ServiceType == typeof(IFailureAction<>));
        int problemDetailCount = services.Count(d => d.ServiceType == typeof(IProblemDetailSource<>));

        problemDetailCount.ShouldBe(1);
        failureActionCount.ShouldBe(1);
    }

    [Fact]
    public void services_registered_once_with_multiple_service_registers_in_manual_mode()
    {
        var services = new ServiceCollection();
        services.AddWolverine(ExtensionDiscovery.ManualOnly, opts =>
        {
            opts.UseFluentValidation();

            opts.UseFluentValidationProblemDetail();
            opts.UseFluentValidationProblemDetail();
            opts.UseFluentValidationProblemDetail();
        });

        int failureActionCount = services.Count(d => d.ServiceType == typeof(IFailureAction<>));
        int problemDetailCount = services.Count(d => d.ServiceType == typeof(IProblemDetailSource<>));

        failureActionCount.ShouldBe(1);
        problemDetailCount.ShouldBe(1);
    }
}