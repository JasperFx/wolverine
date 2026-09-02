// TriageResults is a static collection that the IncidentTriage and LlmTextResponse handlers record
// into, because a Wolverine handler is discovered assembly wide and has no per-test seam to write to.
// end_to_end clears it in InitializeAsync, which is enough within one class and useless across
// several: xUnit runs test classes in parallel by default, and callout_specs, failure_handling,
// durable_round_trip, response_binding and Samples all publish IncidentTriage messages of their own.
// Their answers landed in the same static list while end_to_end was asserting on it, so
// the_answer_comes_back_as_an_ordinary_typed_message saw three triages where it expects one.
//
// Same fix, and the same reason, as the NoParallelization.cs files in PersistenceTests, PolecatTests
// and Wolverine.AmazonS3.Tests. The suite is 42 tests that run in about a second, so serializing the
// classes costs nothing worth measuring.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
