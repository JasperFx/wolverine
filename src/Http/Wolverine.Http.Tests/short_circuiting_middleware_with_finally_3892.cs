using Alba;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

// GH-3892: a middleware class declaring BOTH a short-circuiting Before
// (return type includes IResult) and a Finally/FinallyAsync method used to
// throw NullReferenceException during HTTP chain code generation
public class short_circuiting_middleware_with_finally_3892 : IntegrationContext
{
    public short_circuiting_middleware_with_finally_3892(AppFixture fixture) : base(fixture)
    {
    }

    private Recorder recorder()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();
        return recorder;
    }

    [Fact]
    public async Task continue_path_runs_body_and_finally()
    {
        var recorder = this.recorder();

        var body = await Scenario(x =>
        {
            x.Get.Url("/middleware/shortcircuit-finally");
            x.StatusCodeShouldBeOk();
        });

        recorder.Actions.ShouldHaveTheSameElementsAs(
            "ShortCircuit.Before",
            "ShortCircuit.Action",
            "ShortCircuit.Finally");
    }

    [Fact]
    public async Task short_circuit_path_writes_result_skips_body_and_still_runs_finally()
    {
        var recorder = this.recorder();

        await Scenario(x =>
        {
            x.Get.Url("/middleware/shortcircuit-finally?stop=true");
            x.StatusCodeShouldBe(418);
        });

        recorder.Actions.ShouldHaveTheSameElementsAs(
            "ShortCircuit.Before",
            "ShortCircuit.Finally");
    }

    [Fact]
    public async Task exception_path_still_runs_finally()
    {
        var recorder = this.recorder();

        try
        {
            await Scenario(x =>
            {
                x.Get.Url("/middleware/shortcircuit-finally/throws");
                x.IgnoreStatusCode();
            });
        }
        catch (Exception)
        {
            // The endpoint throws on purpose; depending on hosting the exception
            // either surfaces here or as a 500. Either way Finally must have run.
        }

        recorder.Actions.ShouldHaveTheSameElementsAs(
            "ShortCircuit.Before",
            "ShortCircuit.Throws",
            "ShortCircuit.Finally");
    }

    [Fact]
    public async Task tuple_carrying_before_with_finally_async_continue_path()
    {
        var recorder = this.recorder();

        await Scenario(x =>
        {
            x.Get.Url("/middleware/shortcircuit-finally/tuple");
            x.StatusCodeShouldBeOk();
        });

        recorder.Actions.ShouldHaveTheSameElementsAs(
            "TupleShortCircuit.Before",
            "TupleShortCircuit.Action",
            "TupleShortCircuit.FinallyAsync:reserved");
    }

    [Fact]
    public async Task tuple_carrying_before_with_finally_async_short_circuit_path()
    {
        var recorder = this.recorder();

        await Scenario(x =>
        {
            x.Get.Url("/middleware/shortcircuit-finally/tuple?stop=true");
            x.StatusCodeShouldBe(419);
        });

        recorder.Actions.ShouldHaveTheSameElementsAs(
            "TupleShortCircuit.Before",
            "TupleShortCircuit.FinallyAsync:reserved");
    }
}
