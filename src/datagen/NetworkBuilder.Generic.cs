using System.Text.Json;

namespace Cmdb.DataGen;

/// <summary>
/// The generic network (#219), for a catalog without the synthetic models: the same topology, cables and terminations,
/// with the catalog's own active models. Every site but a splice point gets active equipment (access sites the model with
/// the fewest ports, hubs and aggregation nodes the one with the most, more units as ports run out) and power when the
/// catalog has a power model. Each link is a physical circuit patched through the ODFs, and each access site gets a
/// service on a logical circuit over its link and its aggregation node's ring.
/// </summary>
internal sealed partial class NetworkBuilder
{
    private void EquipGeneric(Site site)
    {
        var state = new SiteState();
        _state[site.Id] = state;
        if (site.Kind == SiteKind.Splice)
        {
            return;
        }
        Locate(site, state);
        AddDevice(site, state);
        if (site.Kind is SiteKind.Hub or SiteKind.Aggregation && _profile.Power.Count > 0)
        {
            Add(site, _profile.Power[0].Key);
        }
    }

    private Equipment AddDevice(Site site, SiteState state)
    {
        var type = site.Kind is SiteKind.Hub or SiteKind.Aggregation ? _profile.Active[^1] : _profile.Active[0];
        var device = Add(site, type.Key);
        state.Devices.Add(device);
        state.NextDevicePort = 0;
        return device;
    }

    /// <summary>The next free port on the site's active equipment, mounting another unit when they are full.</summary>
    private long DevicePort(Site site)
    {
        var state = _state[site.Id];
        if (state.Devices.Count == 0 || state.NextDevicePort >= state.Devices[^1].Ports.Count)
        {
            AddDevice(site, state);
        }
        return state.Devices[^1].TerminalAt(state.NextDevicePort++);
    }

    /// <summary>A link between two hubs over the backbone cable, one port on each.</summary>
    private void WireBackboneGeneric(Site a, Site b, Cable cable)
    {
        var hops = new List<long>();
        var arrive = Traverse([a, b], [cable], DevicePort(a), hops, a);
        var far = DevicePort(b);
        Connect(arrive, far, ConnectionKind.Patch, b);
        hops.Add(far);
        NewCircuit("physical", hops, Weakest(a.Lifecycle, b.Lifecycle));
    }

    /// <summary>
    /// The access site's link to its aggregation node, and a service from a customer port over that link, the node and its
    /// ring to the hub.
    /// </summary>
    private void WireAccessGeneric(Site site)
    {
        if (site.Kind == SiteKind.Splice)
        {
            return;
        }
        var aggregation = site.Aggregation!;
        var aggregationState = _state[aggregation.Id];
        var path = new List<Site>();
        var cables = new List<Cable>();
        for (var s = site; s != aggregation; s = s.Parent!)
        {
            path.Add(s);
            cables.Add(s.Uplink!);
        }
        path.Add(aggregation);

        var uplink = DevicePort(site);
        var hops = new List<long>();
        var arrive = Traverse(path, cables, uplink, hops, site);
        var port = DevicePort(aggregation);
        Connect(arrive, port, ConnectionKind.Patch, aggregation);
        hops.Add(port);
        var physical = NewCircuit("physical", hops, site.Lifecycle);

        var customer = DevicePort(site);
        var vlan = aggregationState.NextVlan++;
        var terminals = new List<long> { customer };
        var channels = new List<long?> { null };
        foreach (var t in new[] { uplink, port, aggregationState.RingStart, aggregationState.HubPort }.Where(t => t != 0))
        {
            terminals.Add(t);
            channels.Add(NewChannel(t, "vlan", vlan));
        }
        var logical = NewCircuit("logical", terminals, site.Lifecycle, channels);
        _net.Dependencies.Add((logical, physical));
        if (aggregationState.RingCircuit != 0)
        {
            _net.Dependencies.Add((logical, aggregationState.RingCircuit));
        }
        var serviceType = _profile.ServiceType;
        var name = _catalog.FindServiceType(serviceType)?.Name ?? "Tjänst";
        var attributes = SchemaAttributes.For(_catalog.AttributeSchema("service", serviceType), _nextService, site, _rng, null);
        var service = NewService($"{name} {site.Code}", serviceType, attributes, site.Lifecycle);
        _net.ServiceCircuits.Add((service, logical));
    }
}

/// <summary>
/// Instance attributes made up from a type's JSON Schema (#219), for catalogs whose models the generator does not know:
/// every required property, and the optional ones of plain types without a pattern, with values inside their bounds.
/// </summary>
internal static class SchemaAttributes
{
    public static string For(JsonElement? schema, long id, Site site, Random rng, string? label)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        if (schema is not { ValueKind: JsonValueKind.Object } s || !s.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return "{}";
        }
        var required = s.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var property in properties.EnumerateObject())
        {
            if (Value(property.Name, property.Value, id, site, rng, label, required.Contains(property.Name)) is { } value)
            {
                values[property.Name] = value;
            }
        }
        return JsonSerializer.Serialize(values);
    }

    private static object? Value(string name, JsonElement p, long id, Site site, Random rng, string? label, bool required)
    {
        if (p.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (p.TryGetProperty("const", out var constant))
        {
            return constant.Clone();
        }
        if (p.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            return choices[rng.Next(choices.GetArrayLength())].Clone();
        }
        if (p.TryGetProperty("default", out var fallback))
        {
            return fallback.Clone();
        }
        var type = p.TryGetProperty("type", out var t) ? t.ValueKind == JsonValueKind.Array ? t[0].GetString() : t.GetString() : null;
        if (!required && (p.TryGetProperty("pattern", out _) || type is null or "array" or "object"))
        {
            return null;
        }
        double Bound(string key, double otherwise) => p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : otherwise;
        switch (type)
        {
            case "integer":
                {
                    var min = (long)Math.Ceiling(Bound("minimum", Bound("exclusiveMinimum", -1) + 1));
                    var max = (long)Math.Floor(Bound("maximum", Bound("exclusiveMaximum", min + 101) - 1));
                    return max <= min ? min : min + rng.NextInt64(max - min + 1);
                }
            case "number":
                {
                    var min = Bound("minimum", 0);
                    var max = Math.Max(min, Bound("maximum", min + 100));
                    return Math.Round(min + (rng.NextDouble() * (max - min)), 1);
                }
            case "boolean":
                return rng.Next(2) == 0;
            case "array":
                return Array.Empty<object>();
            case "object":
                return new Dictionary<string, object>();
            case "string":
                {
                    var format = p.TryGetProperty("format", out var f) ? f.GetString() : null;
                    var text = format switch
                    {
                        "ipv4" => $"10.{(id >> 16) & 255}.{(id >> 8) & 255}.{id & 255}",
                        "date" => "2022-06-01",
                        "date-time" => "2022-06-01T08:00:00Z",
                        _ when name.Contains("serial", StringComparison.OrdinalIgnoreCase) => $"SN{rng.Next(0x1000000):X6}{rng.Next(0x10000):X4}",
                        _ when name.Contains("label", StringComparison.OrdinalIgnoreCase) => label ?? site.Code,
                        _ => $"{site.Code}-{id}",
                    };
                    var maxLength = (int)Bound("maxLength", int.MaxValue);
                    return text.Length > maxLength ? text[..maxLength] : text;
                }
            default:
                return null;
        }
    }
}
