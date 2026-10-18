using System.Reflection;
using Bobcat;
using Bobcat.CritterStack;
using JasperFx.Events.EventModeling;

// bobcat#471, moved here from Bobcat.Generators.Tests with the vocabulary (GH-5082, JasperFx/bobcat#744).
// Bobcat compiled the specs below in a generator harness; here they are this assembly's own projected specs,
// so the descriptor is the real one Bobcat's generator emitted into this build, read through GeneratedEventModel.
// They are [Fact(Explicit = true)]: they exist to be compiled, and a null service provider never runs.

namespace Wolverine.Bobcat.Tests.CritterStackRoles.Bank
{
    public record AccountOpened(Guid Id, string Owner);
    public record FundsDeposited(Guid Id, decimal Amount);
    public record FundsWithdrawn(Guid Id, decimal Amount);
    public record DepositReceipt(Guid Id);
    public class Account { public Guid Id { get; set; } }
    public class AccountSummary { public Guid Id { get; set; } }
    public class Statement { public Guid Id { get; set; } }
    public record ClientEnrolled(Guid Id, string Name);
    public class Client { public Guid Id { get; set; } }
}

namespace Wolverine.Bobcat.Tests.CritterStackRoles.Specs
{
    using Bank;

    [BobcatFeature("Accounts")]
    public class account_specs : CritterStackSpec
    {
        public account_specs() : base((IServiceProvider)null!) { }

        [Fact(Explicit = true), BobcatSlice(SliceName = "Deposit")]
        public async Task a_deposit_is_credited()
        {
            var id = Guid.NewGuid();
            await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));
            await WhenActing("a deposit is made", _ => Task.CompletedTask);
            ThenEvents(typeof(FundsDeposited));
            await ThenAggregate<Account>(id);
            await ThenDocument<Statement>(id);
            ThenMessagesSent<DepositReceipt>();

            // Negatives stamp nothing
            ThenNotEmitted<FundsWithdrawn>();
        }

        [Fact(Explicit = true), BobcatSlice(SliceName = "Withdraw")]
        public async Task a_withdrawal_acting_on_the_stream()
        {
            var id = Guid.NewGuid();
            await GivenEvents(typeof(Account), id, new AccountOpened(id, "Ann"));
            await WhenActingOn<Account>(id, "a withdrawal", _ => Task.CompletedTask, new FundsWithdrawn(id, 1m));
        }

        [Fact(Explicit = true), BobcatSlice(SliceName = "OpenAccount")]
        public async Task an_account_is_opened_for_a_known_client()
        {
            var client = Guid.NewGuid();
            await GivenContext<Client>(client, new ClientEnrolled(client, "Ann"));
            await WhenActing("an account is opened", _ => Task.CompletedTask);
            await ThenAggregate<Account>(Guid.NewGuid());
        }

        [Fact(Explicit = true), BobcatSlice(SliceName = "CloseAccount", Pattern = "Command")]
        public async Task an_account_is_closed_for_a_stored_client()
        {
            await GivenReadModel<Client>(new Client());
            await WhenActing("an account is closed", _ => Task.CompletedTask);
        }
    }

    [BobcatFeature("Summaries")]
    [BobcatSlice(SliceType = typeof(AccountSummary))] // BOBCAT024 prefers the type; the slice is still "AccountSummary"
    public class account_summary : CritterStackSpec
    {
        public account_summary() : base((IServiceProvider)null!) { }

        [Fact(Explicit = true)]
        public async Task the_summary_shows_the_balance()
        {
            var id = Guid.NewGuid();
            await GivenEventsOn<Account>(id, new AccountOpened(id, "Ann"), new FundsDeposited(id, 10m));
            await ThenReadModel<AccountSummary>(id);
            await ThenSingleDocument<Statement>();
        }
    }
}

namespace Wolverine.Bobcat.Tests.CritterStackRoles
{
    /// <summary>
    /// bobcat#471: the store-only <see cref="CritterStackScenario" /> and its <see cref="CritterStackSpec" />
    /// forwarding twins carry <c>[EventModelRole]</c>, so a projected spec written against the real vocabulary —
    /// read from WolverineFx.Bobcat's metadata, as a consumer's build reads it — reaches the Event Model with
    /// its aggregates, events, arranged events, read models, documents and messages.
    /// </summary>
    public class CritterStackSpecRoleTests
    {
        [Fact]
        public void a_projected_critter_stack_spec_reaches_the_event_model_with_its_roles()
        {
            var slices = GeneratedEventModel.For(typeof(CritterStackSpecRoleTests).Assembly)!.Slices;

            var deposit = slices.Single(x => x.Name == "Deposit");
            deposit.CommandType.ShouldBeNull();
            names(deposit.AggregateTypes).ShouldBe(["Account"]);
            names(deposit.EmittedEvents).ShouldBe(["FundsDeposited"]);
            names(deposit.PublishedMessages).ShouldBe(["DepositReceipt"]);
            names(deposit.ReadModelTypes).ShouldBeEmpty();
            names(deposit.Specifications.ShouldHaveSingleItem().ResolvedTypes)
                .ShouldBe(["Account", "AccountOpened", "FundsDeposited", "Statement", "DepositReceipt"]);

            var withdraw = slices.Single(x => x.Name == "Withdraw");
            names(withdraw.AggregateTypes).ShouldBe(["Account"]);
            names(withdraw.EmittedEvents).ShouldBe(["FundsWithdrawn"]);
            names(withdraw.Specifications.ShouldHaveSingleItem().ResolvedTypes)
                .ShouldBe(["Account", "AccountOpened", "FundsWithdrawn"]);

            // bobcat#502: the arranged lookup is read from, and only the stream the act decides against is an aggregate
            var open = slices.Single(x => x.Name == "OpenAccount");
            names(open.ReadsFrom).ShouldBe(["Client"]);
            names(open.AggregateTypes).ShouldBe(["Account"]);
            names(open.Specifications.ShouldHaveSingleItem().ResolvedTypes).ShouldBe(["Client", "ClientEnrolled", "Account"]);
            names(deposit.ReadsFrom).ShouldBeEmpty();

            // bobcat#614: a document stored before a command is a lookup the command reads, not a view it produces
            var close = slices.Single(x => x.Name == "CloseAccount");
            names(close.ReadsFrom).ShouldBe(["Client"]);
            close.ReadModelTypes.ShouldBeEmpty();

            var summary = slices.Single(x => x.Name == "AccountSummary");
            summary.Pattern.ShouldBe(SlicePattern.View);
            names(summary.AggregateTypes).ShouldBe(["Account"]);
            names(summary.ReadModelTypes).ShouldBe(["AccountSummary"]);
            names(summary.ConsumedEvents).ShouldBe(["AccountOpened", "FundsDeposited"]);

            // A document is resolved, never a slot on the slice
            names(summary.Specifications.ShouldHaveSingleItem().ResolvedTypes).ShouldContain("Statement");
        }

        [Fact]
        public void every_spec_twin_declares_the_same_roles_as_the_scenario_method_it_forwards_to()
        {
            var scenario = rolesByMethod(typeof(CritterStackScenario));
            var spec = rolesByMethod(typeof(CritterStackSpec));

            spec.ShouldNotBeEmpty();
            foreach (var (signature, roles) in spec)
            {
                scenario.Keys.ShouldContain(signature);
                scenario[signature].ShouldBe(roles, $"the roles on CritterStackSpec.{signature}");
            }

            // The only role-bearing scenario overloads without a Spec twin
            scenario.Keys.Except(spec.Keys).Order().ShouldBe([
                "GivenEventsOn(Type, Object, Object[])",
                "ThenStreamIsStarted(Type, Object)"
            ]);
        }

        [Fact]
        public void every_act_that_names_no_command_is_declared_an_act_on_both_twins()
        {
            // bobcat#615: WhenActing runs a lambda, so without [EventModelAct] its slice would read as a View
            string[] acts(Type type) => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<EventModelActAttribute>() is not null)
                .Select(signatureOf).Order().ToArray();

            acts(typeof(CritterStackScenario)).ShouldBe(acts(typeof(CritterStackSpec)));
            acts(typeof(CritterStackSpec)).Select(x => x.Split('(')[0]).Distinct().ShouldBe(["WhenActing", "WhenActingOn<T>"]);
        }

        [Fact]
        public void every_role_names_a_parameter_of_its_method()
        {
            // BOBCAT034 runs on a consumer's source, not on Bobcat's own; nameof already keeps these honest
            foreach (var type in new[] { typeof(CritterStackScenario), typeof(CritterStackSpec) })
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            foreach (var role in method.GetCustomAttributes<EventModelRoleAttribute>())
            {
                var names = method.GetParameters().Select(x => x.Name)
                    .Concat(method.IsGenericMethod ? method.GetGenericArguments().Select(x => x.Name) : []);
                names.ShouldContain(role.From, $"{type.Name}.{method.Name}");
            }
        }

        private static Dictionary<string, string[]> rolesByMethod(Type type)
            => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => (Method: m, Roles: m.GetCustomAttributes<EventModelRoleAttribute>()
                    .Select(r => $"{r.Role}:{r.From}").Order().ToArray()))
                .Where(x => x.Roles.Length > 0)
                .ToDictionary(x => signatureOf(x.Method), x => x.Roles);

        private static string signatureOf(MethodInfo method)
        {
            var generic = method.IsGenericMethod ? $"<{string.Join(", ", method.GetGenericArguments().Select(x => x.Name))}>" : "";
            return $"{method.Name}{generic}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";
        }

        private static string[] names(IEnumerable<JasperFx.Descriptors.TypeDescriptor> types)
            => types.Select(x => x.Name).ToArray();
    }
}
