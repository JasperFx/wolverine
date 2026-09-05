using System.Reflection;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Module1;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// GH-4156, from the report against GH-4151's <c>AssertPreBuiltTypesExist</c>: a class library holds both the
/// handlers and the composition root, so it is what Wolverine resolves as
/// <see cref="WolverineOptions.ApplicationAssembly" />, while `codegen write` emits into the ENTRY project --
/// so the pre-built types land in a different assembly. On 6.30.1 that became a hard startup failure where
/// 6.29.2 had started cleanly, and it was read as a regression.
///
/// <para>It is not. In <see cref="TypeLoadMode.Static" /> there is no fallback: with the two assemblies
/// disagreeing, not one handler type can be attached, so before the startup assertion the host booted
/// healthy and then failed per message from inside executor construction -- where no failure policy can
/// reach, and where the pipeline's last-resort recovery ACKED THE ENVELOPE AWAY. See
/// <see cref="Bug_4151_executor_build_failure_loses_message" />, which pins both halves of that. The 6.29.2
/// host in this shape was not working; it was silently discarding every durable message it received.</para>
///
/// <para>So these tests pin the diagnosis rather than a fix: the library really is what registers Wolverine,
/// TypeLoadMode.Auto really is the working configuration for this layout, and Static really must fail loudly.
/// The fix for an application in this shape is the one the exception message already gives.</para>
/// </summary>
public class Bug_4156_static_mode_with_a_class_library_composition_root
{
    private static readonly Assembly TheClassLibrary = typeof(CompositionRootInAClassLibrary).Assembly;

    [Fact]
    public async Task the_class_library_is_what_registers_wolverine()
    {
        // The premise of the whole report, and the only half of it that can be asserted deterministically
        // here. RegistrationCallingAssembly is captured per-options at registration; the ApplicationAssembly
        // that follows from it is pinned PROCESS-WIDE by whichever host started first
        // (RememberedApplicationAssembly), so reading that back inside a parallel test suite asserts against
        // whatever else happened to build a host first -- it resolved to Module1 run alone and to CoreTests
        // run in the full suite. That is a property of the test process, not of the layout.
        using var host = CompositionRootInAClassLibrary.BuildHost();
        await host.StartAsync(TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.RegistrationCallingAssembly!.GetName().Name.ShouldBe(TheClassLibrary.GetName().Name);
        options.RegistrationCallingAssembly.GetName().Name
            .ShouldNotBe(typeof(Bug_4156_static_mode_with_a_class_library_composition_root).Assembly.GetName().Name);
    }

    [Fact]
    public void registration_capture_survives_wolverine_frames_deeper_in_the_stack()
    {
        // Deterministic reconstruction of how the_class_library_is_what_registers_wolverine failed in
        // full-suite runs while passing in isolation. determineCallingAssembly used to anchor its walk at
        // the LAST frame in assembly "Wolverine" anywhere in the stack. The stack does not always end at
        // the registering caller: captured live in a full run, a prior test's
        // TrackedSession.ExecuteAndTrackAsync completed and ran its continuation chain inline on the
        // thread-pool thread, straight through xunit and into the next test's synchronous UseWolverine.
        // That stale Wolverine frame 200+ frames down pulled the anchor past Module1.BuildHost, nothing
        // but thread-pool frames remained beyond it, and the walk fell through to
        // Assembly.GetEntryAssembly(): CoreTests.
        //
        // Registering the Module1 host from inside another host's UseWolverine callback puts Wolverine
        // frames below the registering caller on every run, no suite timing required. The anchor must stay
        // on the innermost contiguous Wolverine call chain and resolve Module1 regardless.
        Assembly? captured = null;
        using var outer = Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            using var host = CompositionRootInAClassLibrary.BuildHost();
            captured = host.Services.GetRequiredService<WolverineOptions>().RegistrationCallingAssembly;
        }).Build();

        captured!.GetName().Name.ShouldBe(TheClassLibrary.GetName().Name);
    }

    [Fact]
    public async Task auto_mode_starts_cleanly_in_this_layout()
    {
        // The reporter's passing test, and the working configuration for this shape: Auto generates what it
        // cannot load, so the assembly split costs a cold start rather than correctness.
        using var host = CompositionRootInAClassLibrary.BuildHost(opts =>
        {
            // Set explicitly rather than left to the process-wide inference, for the reason above. This is
            // the value that inference produces for this layout; pinning it here is what makes the test
            // deterministic rather than a race against every other host in the suite.
            opts.ApplicationAssembly = TheClassLibrary;
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        });

        await host.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task static_mode_fails_the_start_and_names_the_assembly_it_searched()
    {
        var ex = await Should.ThrowAsync<MissingPreBuiltTypesException>(() =>
            CompositionRootInAClassLibrary.BuildHost(opts =>
            {
                opts.ApplicationAssembly = TheClassLibrary;
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
            }).StartAsync(TestContext.Current.CancellationToken));

        // Naming the assembly is the whole diagnostic value: "30 types could not be loaded" without saying
        // WHERE it looked leaves the reader no way to see that the library, not the entry project, is what
        // was searched.
        ex.Message.ShouldContain(TheClassLibrary.GetName().Name!);
        ex.Message.ShouldContain(nameof(Bug4156Ping));
    }
}
