using System.Reflection;
using Shouldly;
using Wolverine.Diagnostics;
using Xunit;

namespace Wolverine.Http.FSharpTests;

/// <summary>
///     GH-4681. The <c>fsharp-coverage</c> command has always given an exact answer in seconds, and
///     nothing ran it — so the remaining count drifted upward on its own. GH-2969, the audit whose stated
///     goal was that every <see cref="JasperFx.CodeGeneration.Frames.Frame" /> either emits F# or is
///     explicitly marked not-applicable, was closed with most of the bucket still full, and two frames
///     (<c>FromFileStrategy</c>'s, then <c>ReadMultipartBody</c> from #4674) joined it afterwards. Neither
///     was anyone's mistake: there was no gate, no test, and no review checklist item.
///     <para>
///     This is that gate. It does not close the gap — 97 emitters is a much larger piece of work — it
///     stops the number growing while that work is or isn't done. Lower the baseline as frames land.
///     </para>
/// </summary>
public class FSharpCoverageRatchet
{
    /// <summary>
    ///     The frames in <c>Wolverine</c> and <c>Wolverine.Http</c> that still inherit the default-throwing
    ///     <c>Frame.GenerateFSharpCode</c> seam. Every name here is an open audit item, not an approved
    ///     exclusion — a frame that genuinely cannot be emitted belongs in the OTHER bucket, marked
    ///     <c>[FSharpEmit(Skip = true, Reason = "...")]</c> with a reason, which also removes it from here.
    ///     <para>
    ///     Deliberately a SET rather than a count. A count can be satisfied by deleting one frame while
    ///     adding another, says nothing about which frame changed, and makes a contributor guess. This
    ///     tells them the name.
    ///     </para>
    /// </summary>
    private static readonly string[] _remainingBaseline =
    [
        // These three really are in the GLOBAL namespace, not Wolverine.Http.CodeGen -- which is what
        // the_baseline_only_lists_frames_that_still_exist caught when this list was first written from
        // the issue's summary table rather than from the tally.
        "AsParametersBindingFrame",
        "AssignPropertyFrame",
        "FormBindingFrame",
        "Wolverine.Attributes.AlwaysPublishResponseFrame",
        "Wolverine.Http.ApplyHttpAware",
        "Wolverine.Http.CodeGen.FormFileCollectionPropertyFrame",
        "Wolverine.Http.CodeGen.FormFilePropertyFrame",
        "Wolverine.Http.CodeGen.FromFileValue",
        "Wolverine.Http.CodeGen.FromFileValues",
        "Wolverine.Http.CodeGen.MaybeEndHandlerWithProblemDetailsFrame",
        "Wolverine.Http.CodeGen.MaybeEndWithProblemDetailsFrame",
        "Wolverine.Http.CodeGen.ParsedArrayFormValue",
        "Wolverine.Http.CodeGen.ParsedArrayQueryStringValue",
        "Wolverine.Http.CodeGen.ParsedCollectionFormValue",
        "Wolverine.Http.CodeGen.ParsedCollectionQueryStringValue",
        "Wolverine.Http.CodeGen.ReadClaimFrame",
        "Wolverine.Http.CodeGen.RequestServicesFrame",
        "Wolverine.Http.CodeGen.RequirementResultHttpFrame",
        "Wolverine.Http.CodeGen.ResponseCacheFrame",
        "Wolverine.Http.CodeGen.SimpleValidationHttpFrame",
        "Wolverine.Http.CodeGen.WriteContentLength",
        "Wolverine.Http.CodeGen.WriteContentType",
        "Wolverine.Http.ContentNegotiation.ContentNegotiationWriteFrame",
        "Wolverine.Http.Policies.ClaimDeduplicatedResponseFrame",
        "Wolverine.Http.Policies.DeduplicationFingerprintFrame",
        "Wolverine.Http.Policies.DeduplicationProblemDetailsFrame",
        "Wolverine.Http.Policies.DeduplicationStatusCodeFrame",
        "Wolverine.Http.Policies.EnableRequestBufferingFrame",
        "Wolverine.Http.Policies.RecordDeduplicatedResponseFrame",
        "Wolverine.Http.Policies.ReleaseDeduplicationIdOnHttpFailureFrame",
        "Wolverine.Http.Policies.ReleaseUnansweredDeduplicatedResponseFrame",
        "Wolverine.Http.Policies.ScopeDeduplicationIdFrame",
        "Wolverine.Http.Policies.SetStatusCodeAndReturnIfEntityIsNullFrame",
        "Wolverine.Http.Policies.WriteProblemDetailsIfNull",
        "Wolverine.Http.Resources.WriteStatusCodeFrame",
        "Wolverine.Logging.LogStartingActivity",
        "Wolverine.Middleware.ResultTypeHandlerFrame",
        "Wolverine.Persistence.Codegen.ClaimDeduplicationIdFrame",
        "Wolverine.Persistence.Codegen.DeduplicationIdMissingFrame",
        "Wolverine.Persistence.Codegen.HandlerDeduplicationStopFrame",
        "Wolverine.Persistence.Codegen.MissingDeduplicationIdFrame",
        "Wolverine.Persistence.Codegen.RefuseDuplicateClaimAtCommitFrame",
        "Wolverine.Persistence.Codegen.ReleaseDeduplicationIdOnFailureFrame",
        "Wolverine.Persistence.ConstructSpecificationFrame",
        "Wolverine.Persistence.EventSourcing.ApplyBoundaryEventsFromAsyncEnumerableFrame`1",
        "Wolverine.Persistence.EventSourcing.ApplyEventsFromAsyncEnumerableFrame`1",
        "Wolverine.Persistence.ForEachStorageActionFrame",
        "Wolverine.Persistence.LoadEntityFrameBlock",
        "Wolverine.Persistence.MultiTenancy.TenantIdResolutionFrame",
        "Wolverine.Persistence.Sagas.CreateMissingSagaFrame",
        "Wolverine.Persistence.Sagas.EnrollAndFetchSagaStorageFrame`2",
        "Wolverine.Persistence.Sagas.LoadSagaOperation",
        "Wolverine.Persistence.Sagas.SagaOperation",
        "Wolverine.Persistence.Sagas.ShouldProceedGuardFrame",
        "Wolverine.Persistence.ThrowRequiredDataMissingExceptionFrame",
        "Wolverine.Runtime.Batching.BatchContextResolutionFrame",
        "Wolverine.Runtime.Handlers.CaptureCascadingMessagesInCatch",
        "Wolverine.Runtime.Handlers.ResultUnwrapAndCascadeFrame",
        "Wolverine.Shims.MassTransit.ConsumeContextFrame"
    ];

    /// <summary>
    ///     The assemblies this gate covers, passed EXPLICITLY rather than discovered.
    ///     <para>
    ///     This is the whole reason the tally takes an assembly set. <c>fsharp-coverage</c> scans
    ///     <c>AppDomain.CurrentDomain.GetAssemblies()</c>, which is correct for a CLI ("whatever this host
    ///     references") and useless for a gate: the answer depends on what the JIT has loaded at the moment
    ///     of the call. The first draft of this test did <c>_ = typeof(HttpChain)</c> to force the load, the
    ///     JIT elided it as having no effect, and the baseline came back 25 instead of 54 — a green gate
    ///     over less than half the surface. Naming the assemblies is the fix.
    ///     </para>
    /// </summary>
    private static Assembly[] CoveredAssemblies() =>
    [
        typeof(WolverineOptions).Assembly,
        typeof(HttpChain).Assembly
    ];

    [Fact]
    public void no_new_frame_may_join_the_remaining_bucket()
    {
        var remaining = WolverineDiagnosticsCommand
            .TallyFSharpCoverage(CoveredAssemblies())
            .Remaining
            .Select(x => x.FullName!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var added = remaining.Except(_remainingBaseline).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var removed = _remainingBaseline.Except(remaining).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        // Report BOTH directions in one message. A contributor who added a frame needs its name, and
        // somebody who implemented an emitter needs to be told to lower the baseline rather than left to
        // decipher a count mismatch.
        var message = string.Empty;

        if (added.Length > 0)
        {
            message +=
                $"""
                 {added.Length} frame(s) have no F# emitter and are not in the committed baseline:

                     {string.Join("\n    ", added)}

                 Either override GenerateFSharpCode on them, or -- if F# genuinely cannot reach them --
                 mark them [FSharpEmit(Skip = true, Reason = "...")] with a reason saying why. Adding them
                 to the baseline below is the last resort, and grows a number GH-4681 exists to shrink.

                 """;
        }

        if (removed.Length > 0)
        {
            message +=
                $"""
                 {removed.Length} frame(s) in the baseline now emit F# (or no longer exist). Nice --
                 delete them from _remainingBaseline so the ratchet holds the new ground:

                     {string.Join("\n    ", removed)}

                 """;
        }

        message.ShouldBeEmpty(message);
    }

    [Fact]
    public void the_baseline_only_lists_frames_that_still_exist()
    {
        // Guards the ratchet itself. A baseline entry for a renamed or deleted frame can never match, so
        // it would silently make room for a genuinely new frame to take its place in the count while
        // set-equality still looked fine on that one name.
        var allFrames = WolverineDiagnosticsCommand
            .TallyFSharpCoverage(CoveredAssemblies())
            .AllFrames
            .Select(x => x.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        _remainingBaseline.Where(x => !allFrames.Contains(x))
            .ShouldBeEmpty("Baseline names no longer present as a loaded Frame type");
    }

    [Fact]
    public void an_intentionally_skipped_frame_carries_a_reason()
    {
        // The three existing [FSharpEmit(Skip = true)] frames all say WHY, and those reasons are the
        // reference for anyone adding a fourth. A skip with no reason is indistinguishable from giving up.
        var skipped = WolverineDiagnosticsCommand.TallyFSharpCoverage(CoveredAssemblies()).Skipped;

        skipped.ShouldNotBeEmpty();
        skipped.Where(x => string.IsNullOrWhiteSpace(x.Reason))
            .Select(x => x.Type.FullName)
            .ShouldBeEmpty("Frames marked FSharpEmit(Skip = true) with no Reason");
    }
}
