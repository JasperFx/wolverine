using Alba;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Mvc;
using JasperFx.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-5195. The Wolverine.Http defaults that flipped in 7.0, each against its 6.x value after
/// <see cref="WolverineOptions.RestoreV6Defaults"/> and against an explicit setting, which always wins.
/// The push receiver's inbox deduplication default is covered in <c>push_receiver_5016</c>, beside the
/// rest of the push receiver's tests.
/// </summary>
public class restore_v6_http_defaults
{
    [Fact]
    public async Task an_unparseable_query_value_is_a_400_by_default()
    {
        await using var host = await startHostAsync();

        await host.Scenario(x =>
        {
            x.Get.Url("/v6defaults/paged?page=abc");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task restoring_the_v6_defaults_binds_an_unparseable_query_value_leniently_again()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true);

        var result = await host.Scenario(x =>
        {
            x.Get.Url("/v6defaults/paged?page=abc");
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("page 0");
    }

    [Fact]
    public async Task an_explicit_reject_unparseable_query_values_wins_over_restoring_the_v6_defaults()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true,
            configure: opts => opts.RejectUnparseableQueryValues = true);

        await host.Scenario(x =>
        {
            x.Get.Url("/v6defaults/paged?page=abc");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task a_concurrency_failure_is_a_409_by_default()
    {
        await using var host = await startHostAsync();

        await host.Scenario(x =>
        {
            x.Post.Url("/v6defaults/conflict");
            x.StatusCodeShouldBe(409);
        });

        producesStatusCodes(host, nameof(V6DefaultsEndpoints.Conflict)).ShouldContain(409);
    }

    [Fact]
    public async Task the_default_conflict_mapping_covers_martens_exclusive_lock_failure()
    {
        // Under Marten 9.x, Marten's StreamLockedException derived from MartenException, so only an explicit
        // MapMartenConcurrencyFailuresToConflict() mapped it. Marten 10 throws the shared JasperFx.Events type
        // (marten#5858), so the 7.0 default covers it with no Marten-specific call
        await using var host = await startHostAsync();

        await host.Scenario(x =>
        {
            x.Post.Url("/v6defaults/marten-lock");
            x.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task restoring_the_v6_defaults_leaves_a_concurrency_failure_unmapped()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true);

        (await statusOrEscapedException<ConcurrencyException>(host, x => x.Post.Url("/v6defaults/conflict")))
            .ShouldNotBe(409);

        producesStatusCodes(host, nameof(V6DefaultsEndpoints.Conflict)).ShouldNotContain(409);
    }

    [Fact]
    public async Task opting_out_leaves_a_concurrency_failure_unmapped()
    {
        await using var host = await startHostAsync(configure: opts => opts.DoNotMapConcurrencyFailuresToConflict());

        (await statusOrEscapedException<ConcurrencyException>(host, x => x.Post.Url("/v6defaults/conflict")))
            .ShouldNotBe(409);
    }

    [Fact]
    public async Task an_explicit_conflict_mapping_replaces_the_default_rather_than_adding_a_second_one()
    {
        // Two copies of the middleware would mean two identical catch clauses in the generated code, which
        // does not compile, so this endpoint answering at all proves there is only one
        await using var host = await startHostAsync(configure: opts => opts.MapConcurrencyFailuresToConflict(
            toProblemDetails: ex => new ProblemDetails { Title = "Custom conflict" }));

        var result = await host.Scenario(x =>
        {
            x.Post.Url("/v6defaults/conflict");
            x.StatusCodeShouldBe(409);
        });

        (await result.ReadAsTextAsync()).ShouldContain("Custom conflict");
    }

    [Fact]
    public async Task an_explicit_conflict_mapping_wins_over_restoring_the_v6_defaults()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true,
            configure: opts => opts.MapConcurrencyFailuresToConflict());

        await host.Scenario(x =>
        {
            x.Post.Url("/v6defaults/conflict");
            x.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task an_unknown_tenant_is_a_404_by_default()
    {
        await using var host = await startHostAsync();

        await host.Scenario(x =>
        {
            x.Get.Url("/v6defaults/tenanted?tenantId=ghost");
            x.StatusCodeShouldBe(404);
        });

        producesStatusCodes(host, nameof(V6DefaultsEndpoints.Tenanted)).ShouldContain(404);
    }

    [Fact]
    public async Task restoring_the_v6_defaults_leaves_an_unknown_tenant_unmapped()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true);

        (await statusOrEscapedException<UnknownTenantIdException>(host,
                x => x.Get.Url("/v6defaults/tenanted?tenantId=ghost")))
            .ShouldNotBe(404);

        producesStatusCodes(host, nameof(V6DefaultsEndpoints.Tenanted)).ShouldNotContain(404);
    }

    [Fact]
    public async Task an_explicit_unknown_tenant_mapping_wins_over_restoring_the_v6_defaults()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true,
            configure: opts => opts.MapUnknownTenantToNotFound());

        await host.Scenario(x =>
        {
            x.Get.Url("/v6defaults/tenanted?tenantId=ghost");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task the_correlation_id_header_applies_to_every_endpoint_by_default()
    {
        await using var host = await startHostAsync();

        var options = host.Services.GetRequiredService<WolverineHttpOptions>();
        options.ApplyCorrelationIdHeaderToAllEndpoints.ShouldBeTrue();
        options.Endpoints!.ApplyCorrelationIdHeaderToAllEndpoints.ShouldBeTrue();
    }

    [Fact]
    public async Task restoring_the_v6_defaults_applies_the_correlation_id_header_only_through_the_middleware()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true);

        var options = host.Services.GetRequiredService<WolverineHttpOptions>();
        options.ApplyCorrelationIdHeaderToAllEndpoints.ShouldBeFalse();
        options.Endpoints!.ApplyCorrelationIdHeaderToAllEndpoints.ShouldBeFalse();
    }

    [Fact]
    public async Task an_explicit_correlation_id_setting_wins_over_restoring_the_v6_defaults()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true,
            configure: opts => opts.ApplyCorrelationIdHeaderToAllEndpoints = true);

        host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!
            .ApplyCorrelationIdHeaderToAllEndpoints.ShouldBeTrue();
    }

    [Fact]
    public async Task an_explicit_false_turns_the_correlation_id_default_off()
    {
        await using var host = await startHostAsync(configure: opts => opts.ApplyCorrelationIdHeaderToAllEndpoints = false);

        host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!
            .ApplyCorrelationIdHeaderToAllEndpoints.ShouldBeFalse();
    }

    private static HttpChain chainFor(IAlbaHost host, string methodName)
    {
        return host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.Method.Method.Name == methodName);
    }

    private static int[] producesStatusCodes(IAlbaHost host, string methodName)
    {
        return chainFor(host, methodName).BuildEndpoint(RouteWarmup.Lazy)
            .Metadata.OfType<IProducesResponseTypeMetadata>()
            .Select(x => x.StatusCode)
            .ToArray();
    }

    /// <summary>
    /// What an unmapped failure looks like depends on the host: the exception either escapes the scenario or
    /// is turned into a 500 first. Either way it is not the mapped status.
    /// </summary>
    private static async Task<int> statusOrEscapedException<TException>(IAlbaHost host, Action<Scenario> request)
        where TException : Exception
    {
        try
        {
            var response = await host.Scenario(x =>
            {
                request(x);
                x.IgnoreStatusCode();
            });

            return response.Context.Response.StatusCode;
        }
        catch (TException)
        {
            return 500;
        }
    }

    private static async Task<IAlbaHost> startHostAsync(bool restoreV6Defaults = false,
        Action<WolverineHttpOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            if (restoreV6Defaults)
            {
                opts.RestoreV6Defaults();
            }

            opts.Durability.Mode = DurabilityMode.MediatorOnly;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(restore_v6_http_defaults).Assembly);
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.TenantId.IsQueryStringValue("tenantId");

            configure?.Invoke(opts);

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the GH-5195 endpoints", type => type != typeof(V6DefaultsEndpoints)));
        }));
    }
}

/// <summary>
/// Marks the chain transactional, which is what the default conflict mapping is scoped to, without bringing a
/// message store into the test
/// </summary>
public class PretendTransactionalAttribute : ModifyHttpChainAttribute
{
    public override void Modify(HttpChain chain, GenerationRules rules)
    {
        chain.IsTransactional = true;
    }
}

public static class V6DefaultsEndpoints
{
    [WolverineGet("/v6defaults/paged")]
    public static string Paged(int page) => $"page {page}";

    [PretendTransactional]
    [WolverinePost("/v6defaults/conflict")]
    public static string Conflict() => throw new ConcurrencyException("Somebody else got there first");

    [PretendTransactional]
    [WolverinePost("/v6defaults/marten-lock")]
    public static string MartenLock() =>
        throw new global::JasperFx.Events.StreamLockedException(Guid.NewGuid(), new Exception("Lock not available"));

    [RequiresTenant]
    [WolverineGet("/v6defaults/tenanted")]
    public static string Tenanted(IMessageBus bus) => throw new UnknownTenantIdException(bus.TenantId!);
}
