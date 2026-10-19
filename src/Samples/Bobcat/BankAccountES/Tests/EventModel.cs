using Bobcat.Xunit;
using JasperFx.Events.EventModeling;

namespace BankAccountES.Tests;

/// <summary>
/// bobcat#172, the Bobcat-side half of the multi-source event-model vehicle. Four producers feed one
/// descriptor here -- this assembly's projected specs (Declared), the C# overlay in Program.cs (Declared),
/// the host's Wolverine + HTTP chains (Derived), and since bobcat#300 the host's STORE (Derived): one View
/// slice per registered projection, read from the projection registry by jasperfx#825 and registered by
/// AddMarten / AddFisher with no wiring here. These specs assert the fold: every role names its source,
/// declarations survive where nothing outranks them, and the deliberately planted disagreement
/// (FreezeAccount.cs emits AccountFlagged, which no spec mentions) surfaces as a hotspot instead of
/// vanishing. The fifth source -- runtime observation -- is CritterWatch's.
/// </summary>
/// <remarks>
/// These specs bind no slice and call nothing that carries an Event Model role, so they contribute nothing
/// to the model they assert on.
/// </remarks>
[BobcatFeature("Event Model")]
public class EventModel(BankAccountHost app)
{
    private readonly EventModelSteps _model = new(app);

    [BobcatSpec]
    public async Task four_sources_fold_into_one_provenance_stamped_model()
    {
        await _model.AssembleEventModel();

        _model.ThereIsExactlyOneModelNamed("BankAccount");
        _model.EveryClaimedRoleNamesItsSource();
        _model.TheRoleIsClaimedBy("FreezeAccount", EventModelRole.EmittedEvents, EventModelProvenance.Derived);
        _model.TheRoleIsClaimedBy("FreezeAccount", EventModelRole.HandlerType, EventModelProvenance.Derived);
        _model.TheRoleIsClaimedBy("FreezeAccount", EventModelRole.Domain, EventModelProvenance.Declared);
        _model.TheRoleIsClaimedBy("FreezeAccount", EventModelRole.TriggerLabel, EventModelProvenance.Declared);
        _model.TheRoleIsClaimedBy("FreezeAccount", EventModelRole.Specifications, EventModelProvenance.Declared);
    }

    [BobcatSpec]
    public async Task the_planted_disagreement_surfaces_as_a_hotspot_instead_of_vanishing()
    {
        await _model.AssembleEventModel();

        _model.TheSliceReportsADisagreementOn("FreezeAccount", EventModelRole.EmittedEvents);
        _model.ThatDisagreementKept(EventModelProvenance.Derived, "AccountFlagged");
        _model.ThatDisagreementDropped(EventModelProvenance.Declared, "AccountFrozen");
        _model.TheSliceReportsNoDisagreement("DepositFunds");
        _model.TheSliceReportsNoDisagreement("WithdrawFunds");

        // bobcat#500: update_a_client addresses its endpoint by route name; the route the generator resolved
        // from it is the specs' own claim, and agrees with the one Wolverine derives from the chain
        _model.TheSpecsAloneSayTheSliceIsTriggeredAt("UpdateClient", "PUT /api/clients/{clientId}");
        _model.TheSliceReportsNoDisagreement("UpdateClient");
    }

    [BobcatSpec]
    public async Task declared_names_domains_and_spec_bindings_survive_the_merge()
    {
        await _model.AssembleEventModel();

        _model.TheSliceIsInDomain("FreezeAccount", "Banking");
        _model.TheSliceIsTriggeredBy("FreezeAccount", "The fraud desk");
        _model.TheSliceBindsTheSpecification("FreezeAccount", "Freeze Account/freezing an account records the freeze");
        _model.TheSliceBindsTheSpecification("DepositFunds", "Bank Account Event Sourcing/deposit funds");
        _model.TheSliceBindsTheSpecification("WithdrawFunds",
            "Bank Account Event Sourcing/withdrawing more than the balance leaves the account untouched");
    }

    /// <summary>
    /// wolverine#4181, FIXED at 6.31.0 -- the flipped tripwire: the HTTP-derived source no longer claims
    /// TriggerLabel, so the overlay's human label wins the role by being its only claimant.
    /// </summary>
    [BobcatSpec]
    public async Task an_http_slices_trigger_label_belongs_to_the_overlay_again()
    {
        await _model.AssembleEventModel();

        _model.TheRoleIsClaimedBy("WithdrawFunds", EventModelRole.TriggerLabel, EventModelProvenance.Declared);
        _model.TheSliceIsTriggeredBy("WithdrawFunds", "Customer at the ATM");
    }

    /// <summary>
    /// bobcat#175: read models asserted as an identity, not only as a provenance claim -- which is how
    /// wolverine#4182 came to be caught by eye on the canvas rather than by a spec.
    /// </summary>
    /// <remarks>
    /// These are Wolverine's claims. Since wolverine#4975 it derives a GET chain as a Query that READS what it returns,
    /// so the type is in ReadsFrom, as the specs already said it was
    /// (<see cref="a_get_spec_is_a_query_slice_that_reads_what_it_returns"/>). Before it, a GET chain was a View that
    /// produced its response type as a read model, and these steps checked ReadModelTypes.
    /// </remarks>
    [BobcatSpec]
    public async Task a_query_slice_reads_the_document_type_it_returns()
    {
        await _model.AssembleEventModel();

        _model.TheSliceReadsFrom("GET /api/accounts/{id}", "Account");
        _model.TheSliceReadsFrom("GET /api/clients/{id}", "Client");
        _model.TheSliceReadsFrom("GET /api/accounts/{accountId}/transactions", "AccountTransactions");
    }

    /// <summary>
    /// bobcat#501: a GET spec is a Query slice. Its read type goes to ReadsFrom, so the store's AccountTransactions
    /// View is joined to the transactions query by a ReadModelRead link nobody drew. The Account stream the spec
    /// arranges with GivenEvents is setup feeding that projection, so it stamps nothing: no ReadsFrom (which used to
    /// link every slice producing Account to the query), no ConsumedEvents.
    /// </summary>
    /// <remarks>
    /// Until wolverine#4975 Wolverine derived every GET chain as a View, and Derived outranks Declared, so the merged
    /// pattern was View with one Query-vs-View Pattern disagreement per GET slice, pinned here so that the fix would
    /// turn it red. It did when these samples moved into the Wolverine repository (GH-5082) and ran against the
    /// Wolverine that has the fix, so the steps now say what was always meant: a Query, and no disagreement.
    /// </remarks>
    [BobcatSpec]
    public async Task a_get_spec_is_a_query_slice_that_reads_what_it_returns()
    {
        _model.TheSpecsAloneSayTheSliceIsAQueryReading("GET /api/accounts/{accountId}/transactions", "AccountTransactions");
        _model.TheSpecsAloneSayTheSliceIsAQueryReading("GET /api/clients/{clientId}/accounts", "Account");

        await _model.AssembleEventModel();
        _model.TheSliceReadsFromExactly("GET /api/accounts/{accountId}/transactions", "AccountTransactions");
        _model.TheSliceReadsFromExactly("GET /api/clients/{clientId}/accounts", "Account");
        _model.ThereIsALink(EventModelLinkKind.ReadModelRead, "AccountTransactions",
            "GET /api/accounts/{accountId}/transactions", "AccountTransactions");
        _model.ThereIsNoLinkTo(EventModelLinkKind.ReadModelRead, "GET /api/accounts/{accountId}/transactions", "Account");

        // wolverine#4975: Wolverine derives the GET chain as a Query too, so the sources agree
        _model.TheGetSliceIsAQueryTheSourcesAgreeOn("GET /api/accounts/{accountId}/transactions");
        _model.TheGetSliceIsAQueryTheSourcesAgreeOn("GET /api/clients/{clientId}/accounts");
    }

    /// <summary>
    /// wolverine#4182, FIXED at 6.31.0 (via #4185): the collection query unwraps to its element type and
    /// shares the Account node with its single-document sibling instead of minting a raw-generic one.
    /// </summary>
    [BobcatSpec]
    public async Task a_collection_query_reads_the_element_type_it_returns()
    {
        await _model.AssembleEventModel();

        _model.TheSliceReadsFrom("GET /api/clients/{clientId}/accounts", "Account");
    }

    /// <summary>
    /// bobcat#300 -- the store rung. Slices merge BY NAME, and the store names its View slice after the
    /// document type, which is how <see cref="AccountView"/> already binds it. That agreement is entirely
    /// conventional; this spec is what enforces it.
    /// </summary>
    [BobcatSpec]
    public async Task the_store_rung_and_the_spec_rung_describe_one_account_slice_not_two()
    {
        await _model.AssembleEventModel();

        _model.ThereIsExactlyOneSliceNamed("Account");
        _model.TheSliceHasPattern("Account", SlicePattern.View);
        _model.TheSliceReadsTheReadModel("Account", "Account");
        _model.TheRoleIsClaimedBy("Account", EventModelRole.ReadModelTypes, EventModelProvenance.Derived);
        _model.TheRoleIsClaimedBy("Account", EventModelRole.ConsumedEvents, EventModelProvenance.Derived);
        _model.TheSliceConsumes("Account", "AccountOpened");
        _model.TheSliceConsumes("Account", "FundsDeposited");
        _model.TheSliceConsumes("Account", "FundsWithdrawn");
        _model.TheSliceConsumes("Account", "AccountFrozen");
        _model.TheSpecsAloneSayTheSliceConsumes("Account", "AccountOpened");
        _model.TheSpecsAloneSayTheSliceConsumes("Account", "FundsDeposited");
        _model.TheSpecsAloneSayTheSliceConsumes("Account", "FundsWithdrawn");
        _model.TheSpecsAloneSayTheSliceConsumes("Account", "AccountFrozen");
        _model.TheRoleIsClaimedBy("Account", EventModelRole.Specifications, EventModelProvenance.Declared);
        _model.TheRoleIsClaimedBy("Account", EventModelRole.Domain, EventModelProvenance.Declared);
        _model.TheSliceBindsTheSpecification("Account", "Account View/the account read model folds the streams history");
        _model.TheSliceReportsNoDisagreement("Account");
    }

    /// <summary>
    /// bobcat#502: OpenAccount's spec arranges a Client stream only so the endpoint's <c>[Entity]</c> lookup finds
    /// the client. <c>GivenContext&lt;Client&gt;</c> says so, and the slice reads Client instead of claiming it as
    /// an aggregate -- the stream the command decides against is the Account it starts.
    /// </summary>
    [BobcatSpec]
    public async Task an_arranged_lookup_is_read_from_and_not_an_aggregate_of_the_slice()
    {
        _model.TheSpecsAloneSayTheSliceReadsFrom("OpenAccount", "Client");

        // ...and the merged model agrees: the chain's [Entity] Client is a read too (wolverine GH-4419)
        await _model.AssembleEventModel();
        _model.TheSliceReadsFrom("OpenAccount", "Client");
        _model.IsNotAnAggregateOf("Client", "OpenAccount");
    }

    /// <summary>
    /// The payoff of jasperfx#823 + #824 + #825 landing together: the store supplies ConsumedEvents, the roles
    /// give them an element, and Links turns the pair into a cross-slice edge nobody drew.
    /// </summary>
    [BobcatSpec]
    public async Task the_events_the_command_slices_emit_reach_the_account_view_as_links_nobody_drew()
    {
        await _model.AssembleEventModel();

        _model.ThereIsALink(EventModelLinkKind.EventConsumed, "DepositFunds", "Account", "FundsDeposited");
        _model.ThereIsALink(EventModelLinkKind.EventConsumed, "WithdrawFunds", "Account", "FundsWithdrawn");
        _model.ThereIsALink(EventModelLinkKind.EventConsumed, "FreezeAccount", "Account", "AccountFrozen");
        _model.ThereIsALink(EventModelLinkKind.EventConsumed, "OpenAccount", "Account", "AccountOpened");
    }
}
