// FisherTests bootstraps Wolverine hosts from most of its classes, and
// after_commit_runs_after_the_commit flips DynamicCodeBuilder.WithinCodegenCommand -- a
// process-wide static -- to force codegen without starting a host. It resets the flag in a
// finally, so the window is small, but any host bootstrapping in a *concurrent* class inside
// that window silently comes up in DurabilityMode.MediatorOnly and then throws
// "This operation is not allowed with Wolverine is bootstrapped in MediatorOnly mode" on the
// first PublishAsync. That reads as an unrelated failure several classes away.
//
// Same reasoning, and the same one-liner, as PolecatTests.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
