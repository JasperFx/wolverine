using System.Text.RegularExpressions;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Resources;
using Spectre.Console;
using Wolverine;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// GH-5670. A saga's flow drawn as a Mermaid diagram from its SagaDescriptor, the metadata CritterWatch draws from: each
// message in, the method that handles it, what that sends, schedules or appends, NotFound for a missing or completed
// saga, and the Compensate methods with their undo messages. Snapshot tests: the whole diagram, so any change to it
// is a deliberate one
public class drawing_saga_flows_5670 : IAsyncLifetime
{
    private IHost _host = null!;
    private ServiceCapabilities _capabilities = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddResourceSetupOnStartup();
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<ReportExport>()
                    .IncludeType<FreightBooking>()
                    .IncludeType<ParcelDelivery>()
                    .IncludeType<CourierRun>()
                    .IncludeType<WarehousePick>()
                    .IncludeType<GiftOrder>();
            }).StartAsync();

        _capabilities = await ServiceCapabilities.ReadFrom(_host.GetRuntime(), null, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private SagaDescriptor sagaOf<T>() => _capabilities.Sagas.Single(x => x.StateType.FullName == typeof(T).FullName);

    [Fact]
    public void describes_each_method_and_what_it_returns()
    {
        var saga = sagaOf<ReportExport>();
        saga.IsEventSourced.ShouldBeFalse();

        var cancel = saga.Messages.Single(x => x.MessageType.Name == nameof(CancelReportExport));
        cancel.Methods.Select(x => (x.Name, x.Role)).ShouldBe([("Handle", SagaRole.Orchestrate), ("NotFound", SagaRole.NotFound)]);
        cancel.Methods[1].Messages.Single().Name.ShouldBe(nameof(ReportExportAlreadyClosed));

        var start = saga.Messages.Single(x => x.MessageType.Name == nameof(StartReportExport)).Methods.Single();
        start.Scheduled.Select(x => x.Name).ShouldBe([nameof(PollReportExport), nameof(ReportExportDeadline)]);
        start.Messages.ShouldBeEmpty(); // the saga itself is its state, not a message

        saga.Messages.Single(x => x.MessageType.Name == nameof(PollReportExport)).IsTimeout.ShouldBeTrue();
        saga.Messages.Single(x => x.MessageType.Name == nameof(ReportExportReady)).Methods.Single().ReturnsMoreAtRunTime.ShouldBeTrue();
    }

    [Fact]
    public void a_document_saga()
    {
        SagaFlowDiagram.ToMermaid(sagaOf<ReportExport>()).ShouldBe(DocumentSagaFlow);
    }

    [Fact]
    public void a_compensating_saga()
    {
        var saga = sagaOf<FreightBooking>();
        saga.Compensations.Select(x => (x.FailedMessageType.Name, x.ExceptionType.Name))
            .ShouldBe([(nameof(ChargeFreightCard), nameof(Exception)), (nameof(ReserveFreightSpace), nameof(FreightSpaceUnavailable))]);

        SagaFlowDiagram.ToMermaid(saga).ShouldBe(CompensatingSagaFlow);
    }

    [Fact]
    public void an_event_sourced_saga_shows_the_events_each_method_appends()
    {
        var saga = sagaOf<ParcelDelivery>();
        saga.IsEventSourced.ShouldBeTrue();

        // Only messages are published: the events go to the saga's own stream
        var delivered = saga.Messages.Single(x => x.MessageType.Name == nameof(ParcelHandedOver));
        delivered.PublishedTypes.Select(x => x.Name).ShouldBe([nameof(NotifyParcelRecipient), nameof(LateParcelHandover)]);
        delivered.Methods[0].Events.Select(x => x.Name).ShouldBe([nameof(ParcelDelivered)]);

        SagaFlowDiagram.ToMermaid(saga).ShouldBe(EventSourcedSagaFlow);
    }

    [Fact]
    public void markdown_for_a_readme()
    {
        var markdown = SagaFlowDiagram.ToMarkdown(sagaOf<ReportExport>());
        markdown.ShouldBe($"## ReportExport\n\n```mermaid\n{DocumentSagaFlow}```\n");
    }

    [Fact]
    public void the_describe_section_prints_every_saga_as_markdown()
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(writer)
        });
        console.Profile.Width = 200;

        new WolverineSystemPart(_host.GetRuntime()).WriteSagaFlows(console).ShouldBeTrue();

        var rendered = writer.ToString();
        rendered.ShouldContain("Saga Flows");
        rendered.ShouldContain($"## ReportExport\n\n```mermaid\n{DocumentSagaFlow}```");
        rendered.ShouldContain("## FreightBooking");
        rendered.ShouldContain("## ParcelDelivery");
    }

    [Fact]
    public async Task the_saga_flow_command_writes_one_saga_to_a_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"saga-flow-{Guid.NewGuid():N}.mmd");
        try
        {
            var input = new SagaFlowInput
            {
                HostBuilder = Host.CreateDefaultBuilder().UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery().IncludeType<ReportExport>().IncludeType<FreightBooking>();
                }),
                SagaName = "reportexport",
                MermaidOnlyFlag = true,
                FileFlag = path
            };

            (await new SagaFlowCommand().Execute(input)).ShouldBeTrue();
            (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe(DocumentSagaFlow);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task the_saga_flow_command_refuses_a_saga_it_does_not_have()
    {
        var input = new SagaFlowInput
        {
            HostBuilder = Host.CreateDefaultBuilder().UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<ReportExport>();
            }),
            SagaName = "NoSuchSaga"
        };

        (await new SagaFlowCommand().Execute(input)).ShouldBeFalse();
    }

    [Fact]
    public void a_descriptor_from_an_older_service_is_drawn_from_its_roles_and_published_types()
    {
        // No Methods: what CritterWatch receives from a service older than GH-5670
        var saga = new SagaDescriptor(new TypeDescriptor("OldSaga", "Legacy.OldSaga", "Legacy"))
        {
            Messages =
            [
                new SagaMessageRole(new TypeDescriptor("BeginOld", "Legacy.BeginOld", "Legacy"), SagaRole.Start, "Id",
                    [new TypeDescriptor("OldSaga", "Legacy.OldSaga", "Legacy"), new TypeDescriptor("OldStarted", "Legacy.OldStarted", "Legacy")]),
                new SagaMessageRole(
                    new TypeDescriptor("Fault`1", "Wolverine.Fault`1[[Legacy.ShipOld, Legacy, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]", "Wolverine"),
                    SagaRole.Orchestrate, null, [])
            ]
        };

        SagaFlowDiagram.ToMermaid(saga).ShouldBe(OlderServiceFlow);
    }

    // GH-5701: with [SagaMissing(Discard)] on the saga, only a message for an existing saga is "logged and dropped". The
    // start message creates the saga, so it can't find it missing
    [Fact]
    public void a_saga_that_discards_messages_for_a_missing_saga()
    {
        var saga = sagaOf<CourierRun>();
        saga.Messages.Single(x => x.MessageType.Name == nameof(StartCourierRun)).DiscardedIfSagaMissing.ShouldBeFalse();
        saga.Messages.Single(x => x.MessageType.Name == nameof(CourierArrived)).DiscardedIfSagaMissing.ShouldBeTrue();

        SagaFlowDiagram.ToMermaid(saga).ShouldBe(DiscardingSagaFlow);
    }

    // GH-5701: a Compensate for an interface draws a failure edge from each concrete step it covers, and no node for the
    // interface itself
    [Fact]
    public void a_compensate_method_for_an_interface()
    {
        var saga = sagaOf<WarehousePick>();
        saga.Compensations.Single().Covers.Select(x => x.Name).ShouldBe([nameof(PackPickedItems), nameof(PickItems)]);

        SagaFlowDiagram.ToMermaid(saga).ShouldBe(InterfaceCompensationFlow);
    }

    // GH-5701: a catch-all Compensate(object, ...) covers the steps the saga sends, but not the message its
    // [CompletesSaga] method returns: GiftOrderShipped can't fail and be compensated
    [Fact]
    public void a_catch_all_compensate_method()
    {
        var saga = sagaOf<GiftOrder>();
        saga.Compensations.Single().Covers.Select(x => x.Name).ShouldBe([nameof(ShipGift), nameof(WrapGift)]);

        SagaFlowDiagram.ToMermaid(saga).ShouldBe(CatchAllCompensationFlow);
    }

    // GH-5701: the diagram and the runtime read what a Compensate method covers from the same place, so the failure
    // edges into each Compensate node are exactly the concrete Fault<T> chains filed under that method
    [Fact]
    public void each_compensate_methods_failure_edges_are_its_fault_chains()
    {
        var graph = _host.GetRuntime().Handlers;
        var sagas = _capabilities.Sagas.Where(x => x.Compensations.Count > 0).ToArray();
        sagas.Length.ShouldBeGreaterThanOrEqualTo(4);

        foreach (var saga in sagas)
        {
            var sagaType = graph.SagaChains().First(x => x.SagaType.FullName == saga.StateType.FullName).SagaType;
            var mermaid = SagaFlowDiagram.ToMermaid(saga);
            var names = Regex.Matches(mermaid, "^\\s*(msg_\\w+)\\(\\[\"(.+?)\"\\]\\)", RegexOptions.Multiline)
                .ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value);

            for (var i = 0; i < saga.Compensations.Count; i++)
            {
                var compensation = saga.Compensations[i];
                var method = SagaCompensationMethods.For(sagaType).Select(x => x)
                    .Single(x => x.FailedType.FullName == compensation.FailedMessageType.FullName &&
                                 x.ExceptionType.FullName == compensation.ExceptionType.FullName);

                var faultChains = graph.SagaChains()
                    .Where(x => x.SagaType == sagaType && x.SagaHandlerCalls().Any(call => call.Method == method.Method))
                    .Select(x => SagaCompensations.FaultMessageType(x.MessageType))
                    .OfType<Type>()
                    .Where(x => !x.IsInterface && !x.IsAbstract && x != typeof(object))
                    .Select(x => x.Name)
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                var node = $"Compensate_{compensation.FailedMessageType.Name}_{i + 1}";
                var drawn = Regex.Matches(mermaid, $"^\\s*(msg_\\w+) -\\.->\\|\"fails\"\\| {node}$", RegexOptions.Multiline)
                    .Select(x => names[x.Groups[1].Value])
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                drawn.ShouldBe(faultChains, $"{saga.StateType.Name}.{node}");
            }
        }
    }

    private const string DiscardingSagaFlow = """
        flowchart TD
            %% The flow of CourierRun, drawn by Wolverine from its saga metadata
            msg_StartCourierRun(["StartCourierRun"])
            msg_CourierArrived(["CourierArrived"])
            msg_CourierDispatched(["CourierDispatched"])
            subgraph saga_CourierRun["CourierRun"]
                Start_StartCourierRun["Start(StartCourierRun)"]
                Handle_CourierArrived["Handle(CourierArrived)"]
            end
            dropped["logged and dropped"]
            msg_StartCourierRun --> Start_StartCourierRun
            Start_StartCourierRun --> msg_CourierDispatched
            msg_CourierArrived -.->|"no saga, or completed"| dropped
            msg_CourierArrived --> Handle_CourierArrived
            subgraph legend["Legend"]
                legend_text["Rounded: a message<br/>Hexagon: a message scheduled for later, such as a timeout<br/>Rectangle: a method of the saga<br/>Dashed: scheduled, a failure, or a message that finds no saga or a completed one<br/>Messages a method sends through IMessageBus, rather than returns, are not shown"]
            end
            classDef message fill:#e8f1fb,stroke:#3b78c2,color:#10243e
            classDef timeout fill:#fff4dd,stroke:#c98a00,color:#3d2a00
            classDef event fill:#ffe2c6,stroke:#d9731a,color:#3d1f00
            classDef method fill:#f4f4f4,stroke:#6b6b6b,color:#1c1c1c
            classDef compensate fill:#fde8e8,stroke:#c53030,color:#3d0d0d
            classDef legend fill:#ffffff,stroke:#b0b0b0,color:#333333
            class msg_StartCourierRun,msg_CourierArrived,msg_CourierDispatched message
            class Start_StartCourierRun,Handle_CourierArrived method
            class legend_text legend
        """ + "\n";

    private const string InterfaceCompensationFlow = """
        flowchart TD
            %% The flow of WarehousePick, drawn by Wolverine from its saga metadata
            msg_StartWarehousePick(["StartWarehousePick"])
            msg_ItemsPicked(["ItemsPicked"])
            msg_PickItems(["PickItems"])
            msg_PackPickedItems(["PackPickedItems"])
            msg_ReturnItemsToShelf(["ReturnItemsToShelf"])
            subgraph saga_WarehousePick["WarehousePick"]
                Start_StartWarehousePick["Start(StartWarehousePick)"]
                Handle_ItemsPicked["Handle(ItemsPicked)"]
                Compensate_IWarehouseStep_1["Compensate(IWarehouseStep, Exception)"]
            end
            msg_StartWarehousePick --> Start_StartWarehousePick
            Start_StartWarehousePick --> msg_PickItems
            msg_ItemsPicked --> Handle_ItemsPicked
            Handle_ItemsPicked --> msg_PackPickedItems
            msg_PackPickedItems -.->|"fails"| Compensate_IWarehouseStep_1
            msg_PickItems -.->|"fails"| Compensate_IWarehouseStep_1
            Compensate_IWarehouseStep_1 --> msg_ReturnItemsToShelf
            subgraph legend["Legend"]
                legend_text["Rounded: a message<br/>Hexagon: a message scheduled for later, such as a timeout<br/>Rectangle: a method of the saga<br/>Dashed: scheduled, a failure, or a message that finds no saga or a completed one<br/>Messages a method sends through IMessageBus, rather than returns, are not shown"]
            end
            classDef message fill:#e8f1fb,stroke:#3b78c2,color:#10243e
            classDef timeout fill:#fff4dd,stroke:#c98a00,color:#3d2a00
            classDef event fill:#ffe2c6,stroke:#d9731a,color:#3d1f00
            classDef method fill:#f4f4f4,stroke:#6b6b6b,color:#1c1c1c
            classDef compensate fill:#fde8e8,stroke:#c53030,color:#3d0d0d
            classDef legend fill:#ffffff,stroke:#b0b0b0,color:#333333
            class msg_StartWarehousePick,msg_ItemsPicked,msg_PickItems,msg_PackPickedItems,msg_ReturnItemsToShelf message
            class Start_StartWarehousePick,Handle_ItemsPicked method
            class Compensate_IWarehouseStep_1 compensate
            class legend_text legend
        """ + "\n";

    private const string CatchAllCompensationFlow = """
        flowchart TD
            %% The flow of GiftOrder, drawn by Wolverine from its saga metadata
            msg_OrderGift(["OrderGift"])
            msg_GiftShipped(["GiftShipped"])
            msg_GiftWrapped(["GiftWrapped"])
            msg_WrapGift(["WrapGift"])
            msg_GiftOrderShipped(["GiftOrderShipped"])
            msg_ShipGift(["ShipGift"])
            msg_CancelGiftOrder(["CancelGiftOrder"])
            subgraph saga_GiftOrder["GiftOrder"]
                Start_OrderGift["Start(OrderGift)"]
                Handle_GiftShipped["Handle(GiftShipped)"]
                Handle_GiftWrapped["Handle(GiftWrapped)"]
                Compensate_Object_1["Compensate(Object, Exception)"]
            end
            msg_OrderGift --> Start_OrderGift
            Start_OrderGift --> msg_WrapGift
            msg_GiftShipped --> Handle_GiftShipped
            Handle_GiftShipped --> msg_GiftOrderShipped
            msg_GiftWrapped --> Handle_GiftWrapped
            Handle_GiftWrapped --> msg_ShipGift
            msg_ShipGift -.->|"fails"| Compensate_Object_1
            msg_WrapGift -.->|"fails"| Compensate_Object_1
            Compensate_Object_1 --> msg_CancelGiftOrder
            subgraph legend["Legend"]
                legend_text["Rounded: a message<br/>Hexagon: a message scheduled for later, such as a timeout<br/>Rectangle: a method of the saga<br/>Dashed: scheduled, a failure, or a message that finds no saga or a completed one<br/>Messages a method sends through IMessageBus, rather than returns, are not shown"]
            end
            classDef message fill:#e8f1fb,stroke:#3b78c2,color:#10243e
            classDef timeout fill:#fff4dd,stroke:#c98a00,color:#3d2a00
            classDef event fill:#ffe2c6,stroke:#d9731a,color:#3d1f00
            classDef method fill:#f4f4f4,stroke:#6b6b6b,color:#1c1c1c
            classDef compensate fill:#fde8e8,stroke:#c53030,color:#3d0d0d
            classDef legend fill:#ffffff,stroke:#b0b0b0,color:#333333
            class msg_OrderGift,msg_GiftShipped,msg_GiftWrapped,msg_WrapGift,msg_GiftOrderShipped,msg_ShipGift,msg_CancelGiftOrder message
            class Start_OrderGift,Handle_GiftShipped,Handle_GiftWrapped method
            class Compensate_Object_1 compensate
            class legend_text legend
        """ + "\n";

    private const string OlderServiceFlow = """
        flowchart TD
            %% The flow of OldSaga, drawn by Wolverine from its saga metadata
            msg_BeginOld(["BeginOld"])
            msg_Fault_1(["Fault#lt;ShipOld#gt;"])
            msg_OldStarted(["OldStarted"])
            subgraph saga_OldSaga["OldSaga"]
                Start_BeginOld["Start(BeginOld)"]
                Handle_Fault_1["Handle(Fault#lt;ShipOld#gt;)"]
            end
            msg_BeginOld --> Start_BeginOld
            Start_BeginOld --> msg_OldStarted
            msg_Fault_1 --> Handle_Fault_1
            subgraph legend["Legend"]
                legend_text["Rounded: a message<br/>Hexagon: a message scheduled for later, such as a timeout<br/>Rectangle: a method of the saga<br/>Dashed: scheduled, a failure, or a message that finds no saga or a completed one<br/>Messages a method sends through IMessageBus, rather than returns, are not shown"]
            end
            classDef message fill:#e8f1fb,stroke:#3b78c2,color:#10243e
            classDef timeout fill:#fff4dd,stroke:#c98a00,color:#3d2a00
            classDef event fill:#ffe2c6,stroke:#d9731a,color:#3d1f00
            classDef method fill:#f4f4f4,stroke:#6b6b6b,color:#1c1c1c
            classDef compensate fill:#fde8e8,stroke:#c53030,color:#3d0d0d
            classDef legend fill:#ffffff,stroke:#b0b0b0,color:#333333
            class msg_BeginOld,msg_Fault_1,msg_OldStarted message
            class Start_BeginOld,Handle_Fault_1 method
            class legend_text legend
        """ + "\n";

    private const string DocumentSagaFlow = """
        flowchart TD
            %% The flow of ReportExport, drawn by Wolverine from its saga metadata
            msg_StartReportExport(["StartReportExport"])
            msg_CancelReportExport(["CancelReportExport"])
            msg_PollReportExport{{"PollReportExport"}}
            msg_ReportExportDeadline{{"ReportExportDeadline"}}
            msg_ReportExportReady(["ReportExportReady"])
            msg_ReportExportAlreadyClosed(["ReportExportAlreadyClosed"])
            msg_ReportExportAbandoned(["ReportExportAbandoned"])
            subgraph saga_ReportExport["ReportExport"]
                Start_StartReportExport["Start(StartReportExport)"]
                Handle_CancelReportExport["Handle(CancelReportExport)"]
                NotFound_CancelReportExport["NotFound(CancelReportExport)"]
                Handle_PollReportExport["Handle(PollReportExport)"]
                Handle_ReportExportDeadline["Handle(ReportExportDeadline)"]
                Handle_ReportExportReady["Handle(ReportExportReady)"]
            end
            runtime["more messages, decided at run time"]
            msg_StartReportExport --> Start_StartReportExport
            Start_StartReportExport -.->|"schedules"| msg_PollReportExport
            Start_StartReportExport -.->|"schedules"| msg_ReportExportDeadline
            msg_CancelReportExport --> Handle_CancelReportExport
            msg_CancelReportExport -.->|"no saga, or completed"| NotFound_CancelReportExport
            NotFound_CancelReportExport --> msg_ReportExportAlreadyClosed
            msg_PollReportExport --> Handle_PollReportExport
            Handle_PollReportExport -.->|"schedules"| msg_PollReportExport
            msg_ReportExportDeadline --> Handle_ReportExportDeadline
            Handle_ReportExportDeadline --> msg_ReportExportAbandoned
            msg_ReportExportReady --> Handle_ReportExportReady
            Handle_ReportExportReady -.-> runtime
            subgraph legend["Legend"]
                legend_text["Rounded: a message<br/>Hexagon: a message scheduled for later, such as a timeout<br/>Rectangle: a method of the saga<br/>Dashed: scheduled, a failure, or a message that finds no saga or a completed one<br/>A timeout that finds no saga, or a completed one, is dropped<br/>Messages a method sends through IMessageBus, rather than returns, are not shown"]
            end
            classDef message fill:#e8f1fb,stroke:#3b78c2,color:#10243e
            classDef timeout fill:#fff4dd,stroke:#c98a00,color:#3d2a00
            classDef event fill:#ffe2c6,stroke:#d9731a,color:#3d1f00
            classDef method fill:#f4f4f4,stroke:#6b6b6b,color:#1c1c1c
            classDef compensate fill:#fde8e8,stroke:#c53030,color:#3d0d0d
            classDef legend fill:#ffffff,stroke:#b0b0b0,color:#333333
            class msg_StartReportExport,msg_CancelReportExport,msg_ReportExportReady,msg_ReportExportAlreadyClosed,msg_ReportExportAbandoned message
            class msg_PollReportExport,msg_ReportExportDeadline timeout
            class Start_StartReportExport,Handle_CancelReportExport,NotFound_CancelReportExport,Handle_PollReportExport,Handle_ReportExportDeadline,Handle_ReportExportReady method
            class legend_text legend
        """ + "\n";

    private const string CompensatingSagaFlow = """
        flowchart TD
            %% The flow of FreightBooking, drawn by Wolverine from its saga metadata
            msg_BookFreight(["BookFreight"])
            msg_FreightSpaceReserved(["FreightSpaceReserved"])
            msg_ReserveFreightSpace(["ReserveFreightSpace"])
            msg_ChargeFreightCard(["ChargeFreightCard"])
            msg_FreightBooked(["FreightBooked"])
            msg_RefundFreightCard(["RefundFreightCard"])
            msg_ReleaseFreightSpace(["ReleaseFreightSpace"])
            subgraph saga_FreightBooking["FreightBooking"]
                Start_BookFreight["Start(BookFreight)"]
                Handle_FreightSpaceReserved["Handle(FreightSpaceReserved)"]
                Compensate_ChargeFreightCard_1["Compensate(ChargeFreightCard, Exception)"]
                Compensate_ReserveFreightSpace_2["Compensate(ReserveFreightSpace, FreightSpaceUnavailable)"]
            end
            msg_BookFreight --> Start_BookFreight
            Start_BookFreight --> msg_ReserveFreightSpace
            msg_FreightSpaceReserved --> Handle_FreightSpaceReserved
            Handle_FreightSpaceReserved --> msg_ChargeFreightCard
            Handle_FreightSpaceReserved --> msg_FreightBooked
            msg_ChargeFreightCard -.->|"fails"| Compensate_ChargeFreightCard_1
            Compensate_ChargeFreightCard_1 --> msg_RefundFreightCard
            msg_ReserveFreightSpace -.->|"fails"| Compensate_ReserveFreightSpace_2
            Compensate_ReserveFreightSpace_2 --> msg_ReleaseFreightSpace
            subgraph legend["Legend"]
                legend_text["Rounded: a message<br/>Hexagon: a message scheduled for later, such as a timeout<br/>Rectangle: a method of the saga<br/>Dashed: scheduled, a failure, or a message that finds no saga or a completed one<br/>Messages a method sends through IMessageBus, rather than returns, are not shown"]
            end
            classDef message fill:#e8f1fb,stroke:#3b78c2,color:#10243e
            classDef timeout fill:#fff4dd,stroke:#c98a00,color:#3d2a00
            classDef event fill:#ffe2c6,stroke:#d9731a,color:#3d1f00
            classDef method fill:#f4f4f4,stroke:#6b6b6b,color:#1c1c1c
            classDef compensate fill:#fde8e8,stroke:#c53030,color:#3d0d0d
            classDef legend fill:#ffffff,stroke:#b0b0b0,color:#333333
            class msg_BookFreight,msg_FreightSpaceReserved,msg_ReserveFreightSpace,msg_ChargeFreightCard,msg_FreightBooked,msg_RefundFreightCard,msg_ReleaseFreightSpace message
            class Start_BookFreight,Handle_FreightSpaceReserved method
            class Compensate_ChargeFreightCard_1,Compensate_ReserveFreightSpace_2 compensate
            class legend_text legend
        """ + "\n";

    private const string EventSourcedSagaFlow = """
        flowchart TD
            %% The flow of ParcelDelivery, drawn by Wolverine from its saga metadata
            msg_RegisterParcel(["RegisterParcel"])
            msg_ParcelDeliveryDeadline{{"ParcelDeliveryDeadline"}}
            msg_ParcelHandedOver(["ParcelHandedOver"])
            msg_ParcelScanned(["ParcelScanned"])
            msg_NotifyParcelRecipient(["NotifyParcelRecipient"])
            msg_LateParcelHandover(["LateParcelHandover"])
            subgraph saga_ParcelDelivery["ParcelDelivery"]
                Start_RegisterParcel["Start(RegisterParcel)"]
                event_ParcelRegistered[/"ParcelRegistered"/]
                Handle_ParcelDeliveryDeadline["Handle(ParcelDeliveryDeadline)"]
                Handle_ParcelHandedOver["Handle(ParcelHandedOver)"]
                event_ParcelDelivered[/"ParcelDelivered"/]
                NotFound_ParcelHandedOver["NotFound(ParcelHandedOver)"]
                Handle_ParcelScanned["Handle(ParcelScanned)"]
                event_ParcelInTransit[/"ParcelInTransit"/]
                Compensate_ParcelScanned_1["Compensate(ParcelScanned, ScannerOffline)"]
                event_ScanVoided[/"ScanVoided"/]
            end
            runtime["more messages or events, decided at run time"]
            msg_RegisterParcel --> Start_RegisterParcel
            Start_RegisterParcel -.->|"schedules"| msg_ParcelDeliveryDeadline
            Start_RegisterParcel -->|"appends"| event_ParcelRegistered
            msg_ParcelDeliveryDeadline --> Handle_ParcelDeliveryDeadline
            Handle_ParcelDeliveryDeadline -.-> runtime
            msg_ParcelHandedOver --> Handle_ParcelHandedOver
            Handle_ParcelHandedOver --> msg_NotifyParcelRecipient
            Handle_ParcelHandedOver -->|"appends"| event_ParcelDelivered
            msg_ParcelHandedOver -.->|"no saga, or completed"| NotFound_ParcelHandedOver
            NotFound_ParcelHandedOver --> msg_LateParcelHandover
            msg_ParcelScanned --> Handle_ParcelScanned
            Handle_ParcelScanned -->|"appends"| event_ParcelInTransit
            msg_ParcelScanned -.->|"fails"| Compensate_ParcelScanned_1
            Compensate_ParcelScanned_1 -->|"appends"| event_ScanVoided
            Compensate_ParcelScanned_1 -.-> runtime
            subgraph legend["Legend"]
                legend_text["Rounded: a message<br/>Hexagon: a message scheduled for later, such as a timeout<br/>Rectangle: a method of the saga<br/>Slanted: an event appended to the saga's own stream<br/>Dashed: scheduled, a failure, or a message that finds no saga or a completed one<br/>A timeout that finds no saga, or a completed one, is dropped<br/>Messages a method sends through IMessageBus, rather than returns, are not shown"]
            end
            classDef message fill:#e8f1fb,stroke:#3b78c2,color:#10243e
            classDef timeout fill:#fff4dd,stroke:#c98a00,color:#3d2a00
            classDef event fill:#ffe2c6,stroke:#d9731a,color:#3d1f00
            classDef method fill:#f4f4f4,stroke:#6b6b6b,color:#1c1c1c
            classDef compensate fill:#fde8e8,stroke:#c53030,color:#3d0d0d
            classDef legend fill:#ffffff,stroke:#b0b0b0,color:#333333
            class msg_RegisterParcel,msg_ParcelHandedOver,msg_ParcelScanned,msg_NotifyParcelRecipient,msg_LateParcelHandover message
            class msg_ParcelDeliveryDeadline timeout
            class event_ParcelRegistered,event_ParcelDelivered,event_ParcelInTransit,event_ScanVoided event
            class Start_RegisterParcel,Handle_ParcelDeliveryDeadline,Handle_ParcelHandedOver,NotFound_ParcelHandedOver,Handle_ParcelScanned method
            class Compensate_ParcelScanned_1 compensate
            class legend_text legend
        """ + "\n";
}

// ---- a document saga: a recurring poll under a deadline, and a late cancellation --------------------------------------

public record StartReportExport(Guid ReportExportId);

public record PollReportExport(Guid ReportExportId) : TimeoutMessage(5.Minutes());

public record ReportExportDeadline(Guid ReportExportId) : TimeoutMessage(1.Hours());

public record ReportExportReady(Guid ReportExportId, Uri Download);

public record CancelReportExport(Guid ReportExportId);

public record ReportExportAbandoned(Guid ReportExportId);

public record ReportExportAlreadyClosed(Guid ReportExportId);

public record DownloadReportExport(Guid ReportExportId, Uri Download);

#region sample_saga_for_the_flow_diagram

public class ReportExport : Saga
{
    public Guid Id { get; set; }
    public int Polls { get; set; }

    public static (ReportExport, PollReportExport, ReportExportDeadline) Start(StartReportExport command)
        => (new ReportExport { Id = command.ReportExportId }, new PollReportExport(command.ReportExportId),
            new ReportExportDeadline(command.ReportExportId));

    public PollReportExport Handle(PollReportExport poll)
    {
        Polls++;
        return new PollReportExport(Id);
    }

    // What it sends is decided at run time, so the diagram can only say that there is more
    public OutgoingMessages Handle(ReportExportReady ready)
    {
        MarkCompleted();
        return [new DownloadReportExport(Id, ready.Download)];
    }

    public ReportExportAbandoned Handle(ReportExportDeadline deadline)
    {
        MarkCompleted();
        return new ReportExportAbandoned(Id);
    }

    public void Handle(CancelReportExport command) => MarkCompleted();

    // A cancellation that arrives after the export finished
    public static ReportExportAlreadyClosed NotFound(CancelReportExport command) => new(command.ReportExportId);
}

#endregion

// ---- a compensating saga --------------------------------------------------------------------------------------------

public record BookFreight(Guid FreightBookingId);

public record ReserveFreightSpace(Guid FreightBookingId);

public record ChargeFreightCard(Guid FreightBookingId);

public record FreightSpaceReserved(Guid FreightBookingId);

public record ReleaseFreightSpace(Guid FreightBookingId);

public record RefundFreightCard(Guid FreightBookingId);

public record FreightBooked(Guid FreightBookingId);

public class FreightSpaceUnavailable() : Exception("No freight space");

public class FreightBooking : CompensatingSaga
{
    public Guid Id { get; set; }

    public static (FreightBooking, ReserveFreightSpace) Start(BookFreight command)
        => (new FreightBooking { Id = command.FreightBookingId }, new ReserveFreightSpace(command.FreightBookingId));

    public (ChargeFreightCard, FreightBooked) Handle(FreightSpaceReserved reserved)
    {
        MarkCompleted();
        return (new ChargeFreightCard(Id), new FreightBooked(Id));
    }

    public ReleaseFreightSpace Compensate(ReserveFreightSpace failed, FreightSpaceUnavailable e) => new(Id);

    public RefundFreightCard Compensate(ChargeFreightCard failed, ExceptionInfo e) => new(Id);
}

// ---- GH-5701: a saga that discards a message for a missing saga --------------------------------------------------------

public record StartCourierRun(Guid CourierRunId);

public record CourierDispatched(Guid CourierRunId);

public record CourierArrived(Guid CourierRunId);

[SagaMissing(SagaMissingBehavior.Discard)]
public class CourierRun : Saga
{
    public Guid Id { get; set; }

    public static (CourierRun, CourierDispatched) Start(StartCourierRun command)
        => (new CourierRun { Id = command.CourierRunId }, new CourierDispatched(command.CourierRunId));

    public void Handle(CourierArrived arrived) => MarkCompleted();
}

// ---- GH-5701: a Compensate method for an interface ---------------------------------------------------------------------

public interface IWarehouseStep
{
    Guid WarehousePickId { get; }
}

public record StartWarehousePick(Guid WarehousePickId);

public record PickItems(Guid WarehousePickId) : IWarehouseStep;

public record ItemsPicked(Guid WarehousePickId);

public record PackPickedItems(Guid WarehousePickId) : IWarehouseStep;

public record ReturnItemsToShelf(Guid WarehousePickId);

public class WarehousePick : CompensatingSaga
{
    public Guid Id { get; set; }

    public static (WarehousePick, PickItems) Start(StartWarehousePick command)
        => (new WarehousePick { Id = command.WarehousePickId }, new PickItems(command.WarehousePickId));

    public PackPickedItems Handle(ItemsPicked picked) => new(Id);

    public ReturnItemsToShelf Compensate(IWarehouseStep failed, ExceptionInfo e) => new(Id);
}

// ---- GH-5701: a catch-all Compensate method ----------------------------------------------------------------------------

public record OrderGift(Guid GiftOrderId);

public record WrapGift(Guid GiftOrderId);

public record GiftWrapped(Guid GiftOrderId);

public record ShipGift(Guid GiftOrderId);

public record GiftShipped(Guid GiftOrderId);

public record GiftOrderShipped(Guid GiftOrderId);

public record CancelGiftOrder(Guid GiftOrderId);

public class GiftOrder : CompensatingSaga
{
    public Guid Id { get; set; }

    public static (GiftOrder, WrapGift) Start(OrderGift command)
        => (new GiftOrder { Id = command.GiftOrderId }, new WrapGift(command.GiftOrderId));

    public ShipGift Handle(GiftWrapped wrapped) => new(Id);

    // Sent as the saga completes: what it says when it is finished, not a step
    [CompletesSaga]
    public GiftOrderShipped Handle(GiftShipped shipped)
    {
        MarkCompleted();
        return new GiftOrderShipped(Id);
    }

    public CancelGiftOrder Compensate(object failed, ExceptionInfo e) => new(Id);
}

// ---- an event-sourced saga -------------------------------------------------------------------------------------------

public record RegisterParcel(Guid ParcelDeliveryId);

public record ParcelScanned(Guid ParcelDeliveryId, string Depot);

public record ParcelHandedOver(Guid ParcelDeliveryId);

public record ParcelDeliveryDeadline(Guid ParcelDeliveryId) : TimeoutMessage(2.Days());

public record NotifyParcelRecipient(Guid ParcelDeliveryId);

public record LateParcelHandover(Guid ParcelDeliveryId);

public record ReturnParcelToSender(Guid ParcelDeliveryId);

// The saga's events
public record ParcelRegistered(Guid ParcelDeliveryId);

public record ParcelInTransit(string Depot);

public record ParcelDelivered;

public record ParcelLost;

public record ScanVoided(string Depot);

public class ScannerOffline() : Exception("The scanner is offline");

public class ParcelDelivery : EventSourcedSaga
{
    public Guid Id { get; set; }

    public static (ParcelRegistered, ParcelDeliveryDeadline) Start(RegisterParcel command)
        => (new ParcelRegistered(command.ParcelDeliveryId), new ParcelDeliveryDeadline(command.ParcelDeliveryId));

    public ParcelInTransit Handle(ParcelScanned scanned) => new(scanned.Depot);

    // An event, then a message: a message an event-sourced saga sends goes in a DeliveryMessage or OutgoingMessages
    public (ParcelDelivered, DeliveryMessage<NotifyParcelRecipient>) Handle(ParcelHandedOver handedOver)
    {
        MarkCompleted();
        return (new ParcelDelivered(), new NotifyParcelRecipient(Id).WithDeliveryOptions(new DeliveryOptions()));
    }

    public IEnumerable<object> Handle(ParcelDeliveryDeadline deadline)
    {
        yield return new ParcelLost();
        yield return new SagaCompleted();
    }

    public static LateParcelHandover NotFound(ParcelHandedOver handedOver) => new(handedOver.ParcelDeliveryId);

    public (ScanVoided, OutgoingMessages) Compensate(ParcelScanned failed, ScannerOffline e)
        => (new ScanVoided(failed.Depot), [new ReturnParcelToSender(Id)]);

    public void Apply(ParcelRegistered e) => Id = e.ParcelDeliveryId;
}
