using Cmdb.Api.Features.Plans;

namespace Cmdb.Api.Tests.Features;

/// <summary>Planned ids (#107): negative, derived from the operation, and never shared between kinds.</summary>
public sealed class PlannedIdsTests
{
    [Fact]
    public void Terminals_conductors_and_objects_never_share_an_id()
    {
        long[] ops = [1, 2, 3, 99, 12_345];
        var terminals = ops.SelectMany(op => Enumerable.Range(1, 2 * 288).Select(n => Planned.Terminal(op, n))).ToList();
        var conductors = ops.SelectMany(op => Enumerable.Range(1, 288).Select(k => Planned.Conductor(op, k))).ToList();
        var objects = ops.Select(Planned.ObjectId).ToList();

        terminals.Concat(conductors).Concat(objects).ShouldBeUnique();
        terminals.Concat(conductors).Concat(objects).ShouldAllBe(id => id < 0);
    }
}
