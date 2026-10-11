using System.Diagnostics;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Trace;
using Cmdb.Catalog;
using Cmdb.Graph;
using FastEndpoints;

namespace Cmdb.Api.Features.Services;

/// <summary>A port on the service's path, placed on its model's picture when the catalog has one (#214).</summary>
public sealed record DrawingPort(long TerminalId, string Name, PortBox? Box);

/// <summary>Equipment on the path: its model and the picture of the side the used ports sit on.</summary>
public sealed record DrawingEquipment(long Id, string TypeKey, string Model, PanelImage? Image, IReadOnlyList<DrawingPort> Ports);

/// <summary>A cable on the path: its type, length and number of conductors.</summary>
public sealed record DrawingCable(long Id, string TypeName, string? TypeKey, double LengthM, int Conductors);

/// <summary>
/// The service drawn end to end (#257): its trace, with the circuits it rides on layer by layer, and what is needed to
/// draw each step: the equipment's picture with the used port, and each cable's type and length.
/// </summary>
public sealed record ServiceDrawing(TraceResult Trace, IReadOnlyList<DrawingEquipment> Equipment, IReadOnlyList<DrawingCable> Cables, double ElapsedMs);

public sealed class ServiceDrawingRequest
{
    public long Id { get; set; }

    /// <summary>In a plan's view (#24) instead of production; planned objects have no picture.</summary>
    [QueryParam]
    public long? Plan { get; set; }
}

/// <summary>
/// The service view (#257): the trace of a service (#9) and, for the equipment and cables on it, the model pictures,
/// the used ports' places on them, and the cable types and lengths. Only what the trace shows is described, so the
/// caller's scope (#22) applies as in the trace. Budget: the trace's, 50 ms.
/// </summary>
public sealed class ServiceDrawingEndpoint(GraphHolder holder, RequestDb db, ScopeMasks masks, Cmdb.Api.Features.Plans.PlanViews plans)
    : Endpoint<ServiceDrawingRequest, ServiceDrawing>
{
    public override void Configure() => Get("/services/{id}/drawing");

    public override async Task HandleAsync(ServiceDrawingRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var graph = holder.Require();
        IReadOnlyDictionary<long, TraceHop>? planned = null;
        if (req.Plan is { } planId)
        {
            if (await plans.GetAsync(graph, planId, HttpContext.Scope(), ct) is not { } view)
            {
                await Send.NotFoundAsync(ct);
                return;
            }
            graph = view.Graph;
            planned = (await Cmdb.Api.Features.Plans.PlannedNames.BuildAsync(db, view.Chain.Operations, ct)).Terminals;
        }
        var mask = await masks.GetAsync(graph, HttpContext.Scope(), ct);
        if (await TraceEndpoint.RunAsync(graph, mask, db, null, req.Id, null, ct, planned) is not { } trace)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var hops = trace.Circuits.SelectMany(c => c.Hops).Concat(trace.Physical?.Hops ?? []).ToList();
        var ports = hops.Where(h => h.Kind == "port" && h.Equipment is { Id: > 0 }).Select(h => h.TerminalId).Distinct().ToArray();
        var cables = hops.Where(h => h.Cable is { Id: > 0 }).Select(h => h.Cable!.Id).Distinct().ToArray();
        await Send.OkAsync(new ServiceDrawing(trace, await EquipmentAsync(ports, ct), await CablesAsync(cables, ct),
            Math.Round(sw.Elapsed.TotalMilliseconds, 2)), ct);
    }

    private async Task<List<DrawingEquipment>> EquipmentAsync(long[] ports, CancellationToken ct)
    {
        var rows = new List<(long Equipment, string Key, string Model, string? Slot, long Terminal, string Name, int Position)>();
        await using (var cmd = db.CreateCommand("""
            SELECT e.id, et.key, et.manufacturer || ' ' || et.model, e.slot, p.terminal_id, p.name, p.position
            FROM port p JOIN equipment e ON e.id = p.equipment_id JOIN equipment_type et ON et.id = e.equipment_type_id
            WHERE p.terminal_id = ANY($1)
            """))
        {
            cmd.Parameters.Add(new() { Value = ports });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4), reader.GetString(5), reader.GetInt32(6)));
            }
        }
        return [.. rows.GroupBy(r => r.Equipment).Select(g =>
        {
            var first = g.First();
            var type = TypeCatalog.Current.Find(first.Key);
            var cells = type is null ? [] : PortExpansion.Expand(type, first.Slot).ToDictionary(p => p.Position);
            var used = g.Select(r => new DrawingPort(r.Terminal, r.Name, cells.GetValueOrDefault(r.Position)?.Box)).ToList();
            // The side the used ports sit on, the front when they have no place on a picture.
            var side = used.Select(p => p.Box?.Side).FirstOrDefault(s => s is not null) ?? PortImage.FrontSide;
            var image = type?.Panel.Images?.Side(side) ?? type?.Panel.Images?.Front;
            return new DrawingEquipment(first.Equipment, first.Key, first.Model, image, used);
        })];
    }

    private async Task<List<DrawingCable>> CablesAsync(long[] ids, CancellationToken ct)
    {
        var cables = new List<DrawingCable>();
        await using var cmd = db.CreateCommand("""
            SELECT c.id, ct.name, ct.key, c.length_m, ct.conductor_count
            FROM cable c JOIN cable_type ct ON ct.id = c.cable_type_id
            WHERE c.id = ANY($1)
            """);
        cmd.Parameters.Add(new() { Value = ids });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            cables.Add(new DrawingCable(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                Math.Round(reader.GetDouble(3)), reader.GetInt32(4)));
        }
        return cables;
    }
}
