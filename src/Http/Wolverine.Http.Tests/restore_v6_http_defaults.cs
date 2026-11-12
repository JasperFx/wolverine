using System.Security.Claims;
using Alba;
using FluentValidation;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Mvc;
using JasperFx.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.FluentValidation;
using Wolverine.Http.FluentValidation;
using Xunit;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-5195 and GH-5520. The Wolverine.Http defaults that flipped in 7.0, each against its 6.x value after
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
        // (marten#5858), so the 7.0 default covers it with no Marten-specific call (GH-5459)
        await using var host = await startHostAsync();

        await host.Scenario(x =>
        {
            x.Post.Url("/v6defaults/marten-lock");
            x.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task the_default_conflict_mapping_covers_a_stream_id_collision()
    {
        // GH-5459. StartStream with a taken id throws the shared JasperFx.Events.ExistingStreamIdCollisionException
        // on Marten 10, Polecat and Fisher. It is not a ConcurrencyException, so the mapping catches it explicitly
        await using var host = await startHostAsync();

        var result = await host.Scenario(x =>
        {
            x.Post.Url("/v6defaults/stream-collision");
            x.StatusCodeShouldBe(409);
            x.ContentTypeShouldBe("application/problem+json");
        });

        (await result.ReadAsTextAsync()).ShouldContain("already exists");
    }

    [Fact]
    public async Task restoring_the_v6_defaults_leaves_a_stream_id_collision_unmapped()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true);

        (await statusOrEscapedException<global::JasperFx.Events.ExistingStreamIdCollisionException>(host,
                x => x.Post.Url("/v6defaults/stream-collision")))
            .ShouldNotBe(409);
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

    // GH-5520: RejectUnparseableHeaderValues, the pair of RejectUnparseableQueryValues

    [Fact]
    public async Task an_unparseable_header_value_is_a_400_by_default()
    {
        await using var host = await startHostAsync();

        await host.Scenario(x =>
        {
            x.Get.Url("/v6defaults/header");
            x.WithRequestHeader("x-count", "abc");
            x.StatusCodeShouldBe(400);
        });

        host.Services.GetRequiredService<WolverineHttpOptions>().RejectUnparseableHeaderValues.ShouldBeTrue();
    }

    [Fact]
    public async Task restoring_the_v6_defaults_binds_an_unparseable_header_value_leniently_again()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true);

        var result = await host.Scenario(x =>
        {
            x.Get.Url("/v6defaults/header");
            x.WithRequestHeader("x-count", "abc");
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("count 0");
    }

    [Fact]
    public async Task an_explicit_reject_unparseable_header_values_wins_over_restoring_the_v6_defaults()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true,
            configure: opts => opts.RejectUnparseableHeaderValues = true);

        await host.Scenario(x =>
        {
            x.Get.Url("/v6defaults/header");
            x.WithRequestHeader("x-count", "abc");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task an_explicit_false_turns_the_strict_header_default_off()
    {
        await using var host = await startHostAsync(configure: opts => opts.RejectUnparseableHeaderValues = false);

        var result = await host.Scenario(x =>
        {
            x.Get.Url("/v6defaults/header");
            x.WithRequestHeader("x-count", "abc");
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("count 0");
    }

    // GH-5520: FluentValidation ProblemDetails is automatic once UseFluentValidation() is called

    [Fact]
    public async Task fluent_validation_problem_details_are_automatic_with_use_fluent_validation()
    {
        await using var host = await startHostAsync(useFluentValidation: true);

        var result = await host.Scenario(x =>
        {
            x.Post.Json(new V6ValidatedRequest("")).ToUrl("/v6defaults/validated");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });

        (await result.ReadAsTextAsync()).ShouldContain("Name");

        await host.Scenario(x =>
        {
            x.Post.Json(new V6ValidatedRequest("Han")).ToUrl("/v6defaults/validated");
            x.StatusCodeShouldBe(200);
        });
    }

    [Fact]
    public async Task no_fluent_validation_problem_details_without_use_fluent_validation()
    {
        await using var host = await startHostAsync();

        await host.Scenario(x =>
        {
            x.Post.Json(new V6ValidatedRequest("")).ToUrl("/v6defaults/validated");
            x.StatusCodeShouldBe(200);
        });
    }

    [Fact]
    public async Task restoring_the_v6_defaults_leaves_fluent_validation_problem_details_off()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true, useFluentValidation: true);

        await host.Scenario(x =>
        {
            x.Post.Json(new V6ValidatedRequest("")).ToUrl("/v6defaults/validated");
            x.StatusCodeShouldBe(200);
        });
    }

    [Fact]
    public async Task opting_out_leaves_fluent_validation_problem_details_off()
    {
        await using var host = await startHostAsync(useFluentValidation: true,
            configure: opts => opts.DoNotUseFluentValidationProblemDetailMiddleware());

        await host.Scenario(x =>
        {
            x.Post.Json(new V6ValidatedRequest("")).ToUrl("/v6defaults/validated");
            x.StatusCodeShouldBe(200);
        });
    }

    [Fact]
    public async Task an_explicit_fluent_validation_middleware_wins_over_restoring_the_v6_defaults()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true, useFluentValidation: true,
            configure: opts => opts.UseFluentValidationProblemDetailMiddleware());

        await host.Scenario(x =>
        {
            x.Post.Json(new V6ValidatedRequest("")).ToUrl("/v6defaults/validated");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task the_explicit_fluent_validation_middleware_and_the_default_apply_it_once()
    {
        // Two copies would put two validation frames on the chain
        await using var host = await startHostAsync(useFluentValidation: true,
            configure: opts => opts.UseFluentValidationProblemDetailMiddleware());

        await host.Scenario(x =>
        {
            x.Post.Json(new V6ValidatedRequest("")).ToUrl("/v6defaults/validated");
            x.StatusCodeShouldBe(400);
        });

        host.Services.GetRequiredService<WolverineHttpOptions>().Policies
            .Count(x => x.GetType().Name == "HttpChainFluentValidationPolicy").ShouldBe(1);

        chainFor(host, nameof(V6DefaultsEndpoints.Validated)).SourceCode.ShouldNotBeNull()
            .Split("FluentValidationHttpExecutor.ExecuteOne").Length.ShouldBe(2);
    }

    // GH-5520: EnableRelayOfUserName, and the claim that names the user

    [Fact]
    public async Task the_user_name_is_relayed_by_default()
    {
        await using var host = await startHostAsync();

        await whoAmIShouldBe(host, namedUser("han@rebellion.org"), "han@rebellion.org");
    }

    [Fact]
    public async Task restoring_the_v6_defaults_does_not_relay_the_user_name()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true);

        await whoAmIShouldBe(host, namedUser("han@rebellion.org"), "nobody");
    }

    [Fact]
    public async Task an_explicit_relay_of_the_user_name_wins_over_restoring_the_v6_defaults()
    {
        await using var host = await startHostAsync(restoreV6Defaults: true,
            configureWolverine: opts => opts.EnableRelayOfUserName = true);

        await whoAmIShouldBe(host, namedUser("han@rebellion.org"), "han@rebellion.org");
    }

    [Fact]
    public async Task an_explicit_false_turns_the_user_name_relay_off()
    {
        await using var host = await startHostAsync(configureWolverine: opts => opts.EnableRelayOfUserName = false);

        await whoAmIShouldBe(host, namedUser("han@rebellion.org"), "nobody");
    }

    [Fact]
    public async Task a_principal_with_no_name_relays_nothing_by_default()
    {
        // An Auth0 access token: a subject, but nothing mapped to Identity.Name
        await using var host = await startHostAsync();

        await whoAmIShouldBe(host, subjectOnly("auth0|12345"), "nobody");
    }

    [Fact]
    public async Task the_user_name_claim_type_names_the_claim_that_is_relayed()
    {
        await using var host = await startHostAsync(configure: opts => opts.UserNameClaimType = "sub");

        await whoAmIShouldBe(host, subjectOnly("auth0|12345"), "auth0|12345");

        chainFor(host, nameof(V6DefaultsEndpoints.WhoAmI)).SourceCode.ShouldNotBeNull()
            .ShouldContain("UserNameMiddleware.Apply(httpContext, messageContext, \"sub\")");
    }

    [Fact]
    public async Task the_user_name_claim_type_relays_nothing_when_the_principal_lacks_the_claim()
    {
        await using var host = await startHostAsync(configure: opts => opts.UserNameClaimType = "sub");

        await whoAmIShouldBe(host, namedUser("han@rebellion.org"), "nobody");
    }

    private static ClaimsPrincipal namedUser(string name) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "TestAuth"));

    private static ClaimsPrincipal subjectOnly(string subject) =>
        new(new ClaimsIdentity([new Claim("sub", subject)], "TestAuth"));

    private static async Task whoAmIShouldBe(IAlbaHost host, ClaimsPrincipal user, string expected)
    {
        var result = await host.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = user);
            x.Get.Url("/v6defaults/whoami");
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe(expected);
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
        Action<WolverineHttpOptions>? configure = null, bool useFluentValidation = false,
        Action<WolverineOptions>? configureWolverine = null)
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

            if (useFluentValidation)
            {
                opts.UseFluentValidation(RegistrationBehavior.ExplicitRegistration);
                opts.Services.AddSingleton<IValidator<V6ValidatedRequest>, V6ValidatedRequestValidator>();
            }

            configureWolverine?.Invoke(opts);
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

    [PretendTransactional]
    [WolverinePost("/v6defaults/stream-collision")]
    public static string StreamCollision() =>
        throw new global::JasperFx.Events.ExistingStreamIdCollisionException(Guid.NewGuid());

    [RequiresTenant]
    [WolverineGet("/v6defaults/tenanted")]
    public static string Tenanted(IMessageBus bus) => throw new UnknownTenantIdException(bus.TenantId!);

    [WolverineGet("/v6defaults/header")]
    public static string Header([FromHeader(Name = "x-count")] int count) => $"count {count}";

    [WolverinePost("/v6defaults/validated")]
    public static string Validated(V6ValidatedRequest request) => "ok";

    [WolverineGet("/v6defaults/whoami")]
    public static string WhoAmI(IMessageContext context) => context.UserName ?? "nobody";
}

public record V6ValidatedRequest(string Name);

public class V6ValidatedRequestValidator : AbstractValidator<V6ValidatedRequest>
{
    public V6ValidatedRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
    }
}
