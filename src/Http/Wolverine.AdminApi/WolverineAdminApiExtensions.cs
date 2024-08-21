using HtmlTags;
using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.AdminApi;

public static class WolverineAdminApiExtensions
{
    public static RouteGroupBuilder MapWolverineAdminApiEndpoints(this IEndpointRouteBuilder endpoints, string? groupPrefix = null)
    {
        var group = endpoints.MapGroup(groupPrefix ?? "_wolverine");
        group.WithTags("wolverine");
        group.WithDescription("Wolverine Admin API");

        var api = group.MapGroup("api");

        api.MapGet("/nodes", async (IWolverineRuntime runtime, HttpContext context) =>
        {
            var nodes = await runtime.Storage.Nodes.LoadAllNodesAsync(context.RequestAborted);
            return nodes;
        });

        var views = group.MapGroup("views");
        views.MapGet("/nodes", async (IWolverineRuntime runtime, HttpContext context) =>
        {
            var document = new HtmlDocument
            {
                Title = "Wolverine Nodes"
            };

            document.Head.Add("link").Attr("rel", "stylesheet").Attr("href", "https://unpkg.com/mvp.css");

            // <link rel="stylesheet" href="https://unpkg.com/mvp.css">

            var div = document.Body.Add("div").Style("margin", "50px");

            div.Add("h2").Text("Active Wolverine Nodes");

            var nodeTable = await buildNodesTable(runtime, context.RequestAborted);
            div.Append(nodeTable);

            div.Add("h2").Text("Message Storage");
            var countsTable = await buildStorageTable(runtime, context.RequestAborted);
            div.Append(countsTable);

            return Results.Content(document.ToString(), "text/html");
        });

        return group;
    }

    private static async Task<TableTag> buildStorageTable(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        var counts = await runtime.Storage.Admin.FetchCountsAsync();
        var hasMultiples = counts.Tenants.Count != 0;

        var table = new TableTag();
        table.AddHeaderRow(row =>
        {
            if (hasMultiples)
            {
                row.Header("Database Name");
            }
            row.Header("Incoming");
            row.Header("Scheduled");
            row.Header("Handled");
            row.Header("Outgoing");
        });

        if (hasMultiples)
        {
            foreach (var pair in counts.Tenants)
            {
                table.AddBodyRow(row =>
                {
                    row.Cell(pair.Key);
                    row.Cell(pair.Value.Incoming.ToString());
                    row.Cell(pair.Value.Scheduled.ToString());
                    row.Cell(pair.Value.Handled.ToString());
                    row.Cell(pair.Value.Outgoing.ToString());
                });
            }
        }
        else
        {
            table.AddBodyRow(row =>
            {
                row.Cell(counts.Incoming.ToString());
                row.Cell(counts.Scheduled.ToString());
                row.Cell(counts.Handled.ToString());
                row.Cell(counts.Outgoing.ToString());
            });
        }

        return table;
    }

    private static async Task<TableTag> buildNodesTable(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        var table = new TableTag();

        table.AddHeaderRow(row =>
        {
            row.Header("Unique Id");
            row.Header("Assigned Id");
            row.Header(nameof(WolverineNode.Started));
            row.Header("Last Health Check");
            row.Header(nameof(WolverineNode.ActiveAgents));
        });

        var nodes = await runtime.Storage.Nodes.LoadAllNodesAsync(cancellationToken);
        foreach (var node in nodes)
        {
            table.AddBodyRow(row =>
            {
                row.Cell(node.NodeId.ToString());
                var idText = node.AssignedNodeId.ToString();
                if (node.IsLeader())
                {
                    idText += " (leader)";
                }

                row.Cell(idText).Style("textAlign", "right");
                row.Cell(node.Started.ToString("u"));
                row.Cell(node.LastHealthCheck.ToString("u"));

                var agents = node.ActiveAgents.Count != 0
                    ? node.ActiveAgents.Select(x => x.ToString()).Join(", ")
                    : "None";

                row.Cell(agents);
            });
        }

        return table;
    }
}