using Cmdb.Graph;

namespace Cmdb.Api.Tests.Graph;

/// <summary>Plan views as base + delta (#24): the same answers as a graph built with the changes, sharing the base.</summary>
public sealed class GraphOverlayTests
{
    // 1 —patch— 2 —splice— 10 ═ 11 —splice— 3 [—patch— 4] —splice— 20 ═ 21 —splice— 5
    private static GraphData Network(bool crossConnect)
    {
        var d = new GraphData();
        d.EquipmentIds.AddRange([100, 101, 102]);
        d.EquipmentSites.AddRange([1, 2, 3]);
        d.PortTerminals.AddRange([1, 2, 3, 4, 5]);
        d.PortEquipment.AddRange([100, 100, 101, 101, 102]);
        d.CableIds.AddRange([50, 51]);
        d.CableLifecycles.AddRange([(byte)Lifecycle.InService, (byte)Lifecycle.InService]);
        d.ConductorIds.AddRange([60, 61]);
        d.ConductorCables.AddRange([50, 51]);
        d.EndTerminals.AddRange([10, 11, 20, 21]);
        d.EndConductors.AddRange([60, 60, 61, 61]);
        Connect(d, 1, 2, EdgeKind.Patch);
        Connect(d, 2, 10, EdgeKind.Splice);
        Connect(d, 3, 11, EdgeKind.Splice);
        if (crossConnect)
        {
            Connect(d, 3, 4, EdgeKind.Patch);
        }
        Connect(d, 4, 20, EdgeKind.Splice);
        Connect(d, 5, 21, EdgeKind.Splice);
        return d;
    }

    private static void Connect(GraphData d, long a, long b, EdgeKind kind)
    {
        d.ConnectionA.Add(a);
        d.ConnectionB.Add(b);
        d.ConnectionKinds.Add((byte)kind);
        d.ConnectionLifecycles.Add((byte)Lifecycle.InService);
    }

    private static long[] Trace(Cmdb.Graph.Graph g, long start)
    {
        g.TryGetNode(start, out var node);
        return [.. GraphTrace.Physical(g, node).Nodes.Select(g.TerminalId)];
    }

    [Fact]
    public void An_added_connection_traces_like_a_graph_built_with_it()
    {
        var production = GraphBuilder.Build(Network(crossConnect: false), "v1");
        var built = GraphBuilder.Build(Network(crossConnect: true), "v1");

        var (plan, issues) = production.WithChanges([new(3, 4, EdgeKind.Patch, Add: true)]);

        issues.ShouldBeEmpty();
        Trace(plan, 1).ShouldBe(Trace(built, 1));
        Trace(plan, 5).ShouldBe(Trace(built, 5));
        plan.IsOverlay.ShouldBeTrue();
        plan.Base.ShouldBeSameAs(production);
        plan.OverlayNodes.ShouldBe(2);
        // Production is untouched.
        Trace(production, 1).ShouldNotContain(5);
    }

    [Fact]
    public void A_removed_connection_splits_the_path()
    {
        var production = GraphBuilder.Build(Network(crossConnect: true), "v1");
        var built = GraphBuilder.Build(Network(crossConnect: false), "v1");

        var (plan, issues) = production.WithChanges([new(4, 3, EdgeKind.Patch, Add: false)]);

        issues.ShouldBeEmpty();
        Trace(plan, 1).ShouldBe(Trace(built, 1));
        Trace(production, 1).ShouldContain(5);
    }

    [Fact]
    public void Changes_apply_in_order_and_ones_that_do_not_fit_are_reported()
    {
        var production = GraphBuilder.Build(Network(crossConnect: true), "v1");

        var (plan, issues) = production.WithChanges([
            new(3, 4, EdgeKind.Patch, Add: true),     // already connected in production
            new(3, 4, EdgeKind.Patch, Add: false),
            new(3, 4, EdgeKind.Patch, Add: false),    // removed by the change before
            new(3, 999, EdgeKind.Patch, Add: true),
            new(10, 11, EdgeKind.Patch, Add: false),  // a conductor is not a connection
            new(2, 2, EdgeKind.Patch, Add: true),
        ]);

        issues.Select(i => (i.Index, i.Problem)).ShouldBe([
            (0, GraphChangeProblem.AlreadyConnected),
            (2, GraphChangeProblem.NotConnected),
            (3, GraphChangeProblem.UnknownTerminal),
            (4, GraphChangeProblem.NotConnected),
            (5, GraphChangeProblem.SameTerminal),
        ]);
        Trace(plan, 1).ShouldNotContain(5);
    }

    [Fact]
    public void A_terminal_takes_one_connection_of_each_kind()
    {
        var production = GraphBuilder.Build(Network(crossConnect: false), "v1");

        // 1 has a patch to 2 and 4 a splice to 20: a second patch at 1 or a second splice at 4 is a conflict.
        var (_, issues) = production.WithChanges([
            new(1, 3, EdgeKind.Patch, Add: true),
            new(4, 5, EdgeKind.Splice, Add: true),
            new(4, 3, EdgeKind.Patch, Add: true),
        ]);

        issues.Select(i => (i.Index, i.Problem)).ShouldBe([
            (0, GraphChangeProblem.Occupied),
            (1, GraphChangeProblem.Occupied),
        ]);
    }

    [Fact]
    public void A_view_of_a_view_stacks_the_changes_on_the_same_base()
    {
        var production = GraphBuilder.Build(Network(crossConnect: false), "v1");

        var (dependency, _) = production.WithChanges([new(3, 4, EdgeKind.Patch, Add: true)]);
        var (plan, issues) = dependency.WithChanges([new(1, 5, EdgeKind.Termination, Add: true)]);

        issues.ShouldBeEmpty();
        plan.Base.ShouldBeSameAs(production);
        Trace(plan, 1).ShouldContain(20);
        Trace(dependency, 1).ShouldContain(20);
        Trace(production, 1).ShouldNotContain(20);
    }
}
