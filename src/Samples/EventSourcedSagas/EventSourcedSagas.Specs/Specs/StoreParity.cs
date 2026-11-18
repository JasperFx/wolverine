using Bobcat;

namespace EventSourcedSagas.Specs;

// GH-5415 (Q10 of the spec review): the store-sensitive specifications run on all three event stores. Marten's feature
// names are the plain ones; Polecat's and Fisher's carry the store's name, so every specification has an identity of
// its own.

[BobcatFeature("An event-sourced saga starts from events alone")]
[Collection(nameof(FulfillmentCollection))]
public class starting_an_event_sourced_saga_on_marten(InlineFulfillmentServices services) : starting_an_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga starts from events alone (Polecat)")]
[Collection(nameof(PolecatFulfillmentCollection))]
public class starting_an_event_sourced_saga_on_polecat(PolecatFulfillmentServices services) : starting_an_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga starts from events alone (Fisher)")]
[Collection(nameof(FisherFulfillmentCollection))]
public class starting_an_event_sourced_saga_on_fisher(FisherFulfillmentServices services) : starting_an_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga continues from its own stream")]
[Collection(nameof(FulfillmentCollection))]
public class continuing_an_event_sourced_saga_on_marten(InlineFulfillmentServices services) : continuing_an_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga continues from its own stream (Polecat)")]
[Collection(nameof(PolecatFulfillmentCollection))]
public class continuing_an_event_sourced_saga_on_polecat(PolecatFulfillmentServices services) : continuing_an_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga continues from its own stream (Fisher)")]
[Collection(nameof(FisherFulfillmentCollection))]
public class continuing_an_event_sourced_saga_on_fisher(FisherFulfillmentServices services) : continuing_an_event_sourced_saga(services);

[BobcatFeature("Messages for a completed event-sourced saga")]
[Collection(nameof(FulfillmentCollection))]
public class messages_for_a_completed_event_sourced_saga_on_marten(InlineFulfillmentServices services) : messages_for_a_completed_event_sourced_saga(services);

[BobcatFeature("Messages for a completed event-sourced saga (Polecat)")]
[Collection(nameof(PolecatFulfillmentCollection))]
public class messages_for_a_completed_event_sourced_saga_on_polecat(PolecatFulfillmentServices services) : messages_for_a_completed_event_sourced_saga(services);

[BobcatFeature("Messages for a completed event-sourced saga (Fisher)")]
[Collection(nameof(FisherFulfillmentCollection))]
public class messages_for_a_completed_event_sourced_saga_on_fisher(FisherFulfillmentServices services) : messages_for_a_completed_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga completes with a SagaCompleted event")]
[Collection(nameof(FulfillmentCollection))]
public class completing_an_event_sourced_saga_on_marten(InlineFulfillmentServices services) : completing_an_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga completes with a SagaCompleted event (Polecat)")]
[Collection(nameof(PolecatFulfillmentCollection))]
public class completing_an_event_sourced_saga_on_polecat(PolecatFulfillmentServices services) : completing_an_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga completes with a SagaCompleted event (Fisher)")]
[Collection(nameof(FisherFulfillmentCollection))]
public class completing_an_event_sourced_saga_on_fisher(FisherFulfillmentServices services) : completing_an_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga is kept as an Inline snapshot")]
[Collection(nameof(FulfillmentCollection))]
public class an_inline_snapshot_on_marten(InlineFulfillmentServices services) : an_inline_snapshot(services);

[BobcatFeature("An event-sourced saga is kept as an Inline snapshot (Polecat)")]
[Collection(nameof(PolecatFulfillmentCollection))]
public class an_inline_snapshot_on_polecat(PolecatFulfillmentServices services) : an_inline_snapshot(services);

[BobcatFeature("An event-sourced saga is kept as an Inline snapshot (Fisher)")]
[Collection(nameof(FisherFulfillmentCollection))]
public class an_inline_snapshot_on_fisher(FisherFulfillmentServices services) : an_inline_snapshot(services);

[BobcatFeature("An event-sourced saga's timeout")]
[Collection(nameof(FulfillmentCollection))]
public class the_payment_timeout_on_marten(InlineFulfillmentServices services) : the_payment_timeout(services);

[BobcatFeature("An event-sourced saga's timeout (Polecat)")]
[Collection(nameof(PolecatFulfillmentCollection))]
public class the_payment_timeout_on_polecat(PolecatFulfillmentServices services) : the_payment_timeout(services);

[BobcatFeature("An event-sourced saga's timeout (Fisher)")]
[Collection(nameof(FisherFulfillmentCollection))]
public class the_payment_timeout_on_fisher(FisherFulfillmentServices services) : the_payment_timeout(services);

[BobcatFeature("An event-sourced saga's compensation is recorded as events")]
[Collection(nameof(FulfillmentCollection))]
public class compensating_an_event_sourced_saga_on_marten(InlineFulfillmentServices services) : compensating_an_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga's compensation is recorded as events (Polecat)")]
[Collection(nameof(PolecatFulfillmentCollection))]
public class compensating_an_event_sourced_saga_on_polecat(PolecatFulfillmentServices services) : compensating_an_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga's compensation is recorded as events (Fisher)")]
[Collection(nameof(FisherFulfillmentCollection))]
public class compensating_an_event_sourced_saga_on_fisher(FisherFulfillmentServices services) : compensating_an_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga's compensation meets a concurrent append")]
[Collection(nameof(FulfillmentCollection))]
public class compensating_while_another_message_appends_on_marten(InlineFulfillmentServices services) : compensating_while_another_message_appends(services);

[BobcatFeature("An event-sourced saga's compensation meets a concurrent append (Polecat)")]
[Collection(nameof(PolecatFulfillmentCollection))]
public class compensating_while_another_message_appends_on_polecat(PolecatFulfillmentServices services) : compensating_while_another_message_appends(services);

[BobcatFeature("An event-sourced saga's compensation meets a concurrent append (Fisher)")]
[Collection(nameof(FisherFulfillmentCollection))]
public class compensating_while_another_message_appends_on_fisher(FisherFulfillmentServices services) : compensating_while_another_message_appends(services);

[BobcatFeature("A completed event-sourced saga's stream is archived after its retention period")]
[Collection(nameof(FulfillmentCollection))]
public class retaining_a_completed_event_sourced_saga_on_marten(InlineFulfillmentServices services) : retaining_a_completed_event_sourced_saga(services);

[BobcatFeature("A completed event-sourced saga's stream is archived after its retention period (Polecat)")]
[Collection(nameof(PolecatFulfillmentCollection))]
public class retaining_a_completed_event_sourced_saga_on_polecat(PolecatFulfillmentServices services) : retaining_a_completed_event_sourced_saga(services);

[BobcatFeature("A completed event-sourced saga's stream is archived after its retention period (Fisher)")]
[Collection(nameof(FisherFulfillmentCollection))]
public class retaining_a_completed_event_sourced_saga_on_fisher(FisherFulfillmentServices services) : retaining_a_completed_event_sourced_saga(services);

[BobcatFeature("An event-sourced saga's history")]
[Collection(nameof(FulfillmentCollection))]
public class reading_an_event_sourced_sagas_history_on_marten(InlineFulfillmentServices services) : reading_an_event_sourced_sagas_history(services);

[BobcatFeature("An event-sourced saga's history (Polecat)")]
[Collection(nameof(PolecatFulfillmentCollection))]
public class reading_an_event_sourced_sagas_history_on_polecat(PolecatFulfillmentServices services) : reading_an_event_sourced_sagas_history(services);

[BobcatFeature("An event-sourced saga's history (Fisher)")]
[Collection(nameof(FisherFulfillmentCollection))]
public class reading_an_event_sourced_sagas_history_on_fisher(FisherFulfillmentServices services) : reading_an_event_sourced_sagas_history(services);
