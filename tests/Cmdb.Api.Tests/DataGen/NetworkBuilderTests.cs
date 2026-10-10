using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.DataGen.Geo;

namespace Cmdb.Api.Tests.DataGen;

public sealed class NetworkBuilderTests
{
    private static readonly Network Small = NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Current);

    [Fact]
    public void The_same_seed_always_gives_the_same_network()
    {
        var again = NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Current);
        var other = NetworkBuilder.Build(2, Scale.Small, TypeCatalog.Current);

        Fingerprint.Of(again).ShouldBe(Fingerprint.Of(Small));
        Fingerprint.Of(other).ShouldNotBe(Fingerprint.Of(Small));
    }

    [Fact]
    public void Has_the_requested_number_of_sites_of_every_kind()
    {
        // Manholes in the conduit (#235) come on top of the network's sites.
        Small.Sites.Count(s => s.Kind != SiteKind.Manhole).ShouldBe(Scale.Small.Sites);
        Small.Sites.Count(s => s.Kind == SiteKind.Manhole).ShouldBeGreaterThan(0);
        Small.Sites.Count(s => s.Kind == SiteKind.Hub).ShouldBe(Scale.Small.Hubs);
        Small.Sites.Count(s => s.Kind == SiteKind.Aggregation).ShouldBe(Scale.Small.Aggregations);
        Small.Sites.Select(s => s.Kind).Distinct().Count().ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void Every_site_lies_inside_the_territory()
    {
        var territory = new Territory();

        Small.Sites.ShouldAllBe(s => territory.Contains(s.X, s.Y));
    }

    [Fact]
    public void Names_are_codes_and_neutral_words_only()
    {
        Small.Sites.Select(s => s.Code).ShouldBeUnique();
        Small.Sites.ShouldAllBe(s => System.Text.RegularExpressions.Regex.IsMatch(s.Code, "^(HUB|AGG|RAD|SKP|SKV|BR)-[0-9]+$"));
    }

    [Fact]
    public void Generated_attributes_satisfy_each_type_schema()
    {
        foreach (var equipment in Small.Equipment)
        {
            TypeCatalog.Current.ValidateAttributes(equipment.Type.Key, NetworkBuilder.ParseAttributes(equipment.Attributes))
                .ShouldBeEmpty($"{equipment.Name} ({equipment.Type.Key}): {equipment.Attributes}");
        }
    }

    [Fact]
    public void Terminal_ids_are_dense_and_unique()
    {
        var ids = Small.Equipment.SelectMany(e => Enumerable.Range(0, e.Ports.Count).Select(i => e.TerminalAt(i)))
            .Concat(Small.Cables.SelectMany(c => Enumerable.Range(0, 2 * c.Count).Select(i => c.FirstEndTerminal + i)))
            .Order()
            .ToList();

        ids.ShouldBe(Enumerable.Range(1, (int)Small.Terminals).Select(i => (long)i));
    }

    [Fact]
    public void Connections_are_ordered_and_never_repeat_a_pair()
    {
        Small.Connections.ShouldAllBe(c => c.A < c.B);
        Small.Connections.Select(c => (c.A, c.B)).ShouldBeUnique();
    }

    [Fact]
    public void Channels_are_unique_per_terminal_and_hops_follow_connections_or_conductors()
    {
        Small.Channels.Select(c => (c.TerminalId, c.Kind, c.Number)).ShouldBeUnique();

        // Every consecutive pair of hops in a physical circuit is a connection or the two ends of one conductor.
        var connected = Small.Connections.Select(c => (c.A, c.B)).ToHashSet();
        var conductors = Small.Cables.SelectMany(c => Enumerable.Range(0, c.Count).Select(i => (c.FirstEndTerminal + (2 * i), c.FirstEndTerminal + (2 * i) + 1))).ToHashSet();
        var physical = Small.Circuits.Where(c => c.Layer == "physical").Select(c => c.Id).ToHashSet();
        foreach (var circuit in Small.Hops.Where(h => physical.Contains(h.CircuitId)).GroupBy(h => h.CircuitId))
        {
            var hops = circuit.OrderBy(h => h.Seq).Select(h => h.TerminalId).ToList();
            for (var i = 1; i < hops.Count; i++)
            {
                var pair = (Math.Min(hops[i - 1], hops[i]), Math.Max(hops[i - 1], hops[i]));
                (connected.Contains(pair) || conductors.Contains(pair)).ShouldBeTrue($"circuit {circuit.Key} hop {i}");
            }
        }
    }

    [Fact]
    public void Every_active_access_site_has_a_service()
    {
        var withService = Small.Services.Select(s => s.Name.Split(' ')[^1].TrimStart('#')).ToHashSet();
        var codes = Small.Services.Select(s => s.Name).ToList();

        Small.Sites.Where(s => s.Kind is SiteKind.Radio or SiteKind.Cabinet)
            .ShouldAllBe(s => codes.Any(n => n.Contains(s.Code, StringComparison.Ordinal)));
        withService.ShouldNotBeEmpty();
    }
}
