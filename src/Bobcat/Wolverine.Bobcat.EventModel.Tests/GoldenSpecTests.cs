using Bobcat.EventModel;
using Bobcat.EventModel.Dcb;
using Bobcat.EventModel.Emlang;

namespace Wolverine.Bobcat.EventModel.Tests;

/// <summary>
/// GH-6004: the whole output for each board fixture, so a change to the writer is read as a diff. Regenerate with
/// <c>WOLVERINE_UPDATE_SPEC_GOLDENS=1</c> and read what moved before committing.
/// </summary>
public class GoldenSpecTests
{
    private static (EmlangBoard Board, ImportedEventModel Model) shelter()
    {
        var board = EmlangReader.Read(Boards.Shelter);
        return (board, EmlangImport.ToCurated(board, "Shelter", "Shelter").Model);
    }

    private static (EmlangBoard Board, ImportedEventModel Model) orders()
    {
        var board = EventModelersJsonReader.Read(Boards.OrdersSpec);
        return (board, EmlangImport.ToCurated(board, "Orders", "Orders").Model);
    }

    [Fact]
    public void the_shelter_board_as_message_acts()
    {
        // The specification files are byte for byte what the writer wrote before it read the scenario model; only
        // TestSupport.cs (the HTTP base class) and GlobalUsings.cs (its namespaces) grew
        var (board, model) = shelter();
        Goldens.Check("shelter-message", EmlangSpecWriter.Write(board, model, "Shelter", "Shelter.Specs", "marten", null, ActStyle.Message));
    }

    [Fact]
    public void the_shelter_board_as_http_acts()
    {
        var (board, model) = shelter();
        Goldens.Check("shelter-http", EmlangSpecWriter.Write(board, model, "Shelter", "Shelter.Specs", "marten", null, ActStyle.Http));
    }

    [Fact]
    public void the_em_spec_board_as_the_board_says()
    {
        var (board, model) = orders();
        Goldens.Check("orders-board", EmlangSpecWriter.Write(board, model, "Orders", "Orders.Specs", "marten"));
    }

    [Fact]
    public void the_em_spec_board_as_message_acts()
    {
        var (board, model) = orders();
        Goldens.Check("orders-message", EmlangSpecWriter.Write(board, model, "Orders", "Orders.Specs", "marten", null, ActStyle.Message));
    }

    [Fact]
    public void the_dcb_playground_model()
    {
        var result = DcbPlaygroundReader.ToCurated(Boards.Dcb, "Duplication", "Duplication");
        Goldens.Check("dcb-prevent-record-duplication", EmlangSpecWriter.Write(null, result.Model, "Duplication", "Duplication.Specs", "marten", null, ActStyle.Board));
    }
}
