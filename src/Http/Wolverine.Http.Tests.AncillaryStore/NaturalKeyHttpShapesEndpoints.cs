using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Aggregation;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.AncillaryStore;

// Natural keys of other shapes over HTTP: a key wrapping an int, a class key with its own TryParse, a class
// key and a TKey? in the request body, optional and missing query string values, and [WriteAggregate("name")]
// without the key type. Registered on the same ancillary store as NkGadget, for the reason given there.

public readonly record struct BinNumber(int Value);

public class NkBin
{
    public Guid Id { get; set; }

    [NaturalKey]
    public BinNumber Number { get; set; }

    public string Label { get; set; } = string.Empty;

    [NaturalKeySource]
    public void Apply(NkBinCreated e)
    {
        Number = e.Number;
        Label = e.Label;
    }

    public void Apply(NkBinRelabelled e) => Label = e.Label;
}

public record NkBinCreated(BinNumber Number, string Label);
public record NkBinRelabelled(string Label);
public record RelabelNkBin(string Label);

// A class key binds from the route only through its own TryParse, in the IParsable form
public record PartCode(string Value)
{
    public static bool TryParse(string? value, IFormatProvider? provider, [NotNullWhen(true)] out PartCode? code)
    {
        code = string.IsNullOrWhiteSpace(value) ? null : new PartCode(value);
        return code != null;
    }
}

public class NkPart
{
    public Guid Id { get; set; }

    [NaturalKey]
    public PartCode Code { get; set; } = null!;

    public string Description { get; set; } = string.Empty;

    [NaturalKeySource]
    public void Apply(NkPartAdded e)
    {
        Code = e.Code;
        Description = e.Description;
    }

    public void Apply(NkPartDescribed e) => Description = e.Description;
}

public record NkPartAdded(PartCode Code, string Description);
public record NkPartDescribed(string Description);
public record DescribeNkPart(PartCode Code, string Description);

public record RenameNkGadgetByBody(GadgetCode? Code, string Name);

public record GadgetLookup(bool Found, string? Name);

[MartenStore(typeof(INkThingStore))]
public static class NkHttpShapeEndpoints
{
    [WolverinePost("/nkbins/{number}/relabel")]
    [EmptyResponse]
    public static NkBinRelabelled Relabel(RelabelNkBin command, [WriteAggregate<BinNumber>("number")] NkBin bin)
        => new(command.Label);

    [WolverineGet("/nkbins/{number}")]
    public static NkBin GetBin([ReadAggregate<BinNumber>("number")] NkBin bin) => bin;

    // No key type, but {code} is named like NkGadget's key member Code, so it's the natural key (GH-5954). Before that,
    // the route value was always the stream id, which any route value can be parsed into
    [WolverinePost("/nkgadgets-untyped/{code}/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameUntyped(RenameNkGadget command, [WriteAggregate("code")] NkGadget gadget)
        => new(command.Name);

    [WolverineGet("/nkparts/{code}")]
    public static NkPart GetPart([ReadAggregate<PartCode>("code")] NkPart part) => part;

    // The class key comes from the request body, found as the command's member of the key type
    [WolverinePost("/nkparts/describe")]
    [EmptyResponse]
    public static NkPartDescribed Describe(DescribeNkPart command, [WriteAggregate] NkPart part)
        => new(command.Description);

    [WolverinePost("/nkgadgets-by-body/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByBody(RenameNkGadgetByBody command, [WriteAggregate] NkGadget gadget)
        => new(command.Name);

    // A query string value without the key type: the parameter's type names the key, so the name finds it
    [WolverinePost("/nkgadgets-by-query-untyped/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByQueryUntyped(RenameNkGadget command, GadgetCode code,
        [WriteAggregate("code")] NkGadget gadget)
        => new(command.Name);

    [WolverineGet("/nkgadgets-by-optional-query")]
    public static GadgetLookup GetByOptionalQuery(GadgetCode? code,
        [ReadAggregate<GadgetCode>("code", Required = false)] NkGadget? gadget)
        => new(gadget != null, gadget?.Name);

    [WolverineGet("/nkgadgets-by-nullable-query")]
    public static NkGadget GetByNullableQuery(GadgetCode? code, [ReadAggregate<GadgetCode>("code")] NkGadget gadget)
        => gadget;

    [WolverinePost("/nkgadgets-by-nullable-query/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByNullableQuery(RenameNkGadget command, GadgetCode? code,
        [WriteAggregate<GadgetCode>("code")] NkGadget gadget)
        => new(command.Name);

    // A stream id from the query string, to compare a missing natural key with a missing stream id
    [WolverinePost("/nkgadgets-by-stream-id-query/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByStreamIdQuery(RenameNkGadget command, Guid id, [WriteAggregate("id")] NkGadget gadget)
        => new(command.Name);

}

[MartenStore(typeof(INkStringStore))]
public static class NkStringStreamKeyQueryEndpoints
{
    [WolverinePost("/nkstring-gadgets-by-stream-key-query/rename")]
    [EmptyResponse]
    public static NkStringGadgetRenamed RenameByStreamKeyQuery(RenameNkStringGadget command, string id, [WriteAggregate("id")] NkStringGadget gadget)
        => new(command.Name);
}
