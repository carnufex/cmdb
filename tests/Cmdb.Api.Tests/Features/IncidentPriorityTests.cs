using Cmdb.Api.Features.Voice;

namespace Cmdb.Api.Tests.Features;

/// <summary>Incident priority by rules (#135): what the fault does to services, never how the caller sounds.</summary>
public sealed class IncidentPriorityTests
{
    private static FaultService Service(bool critical, Redundancy redundancy) =>
        new(1, "TJ-1", "Tjänst", "mobile-backhaul", critical, redundancy, redundancy == Redundancy.None ? 1 : 2);

    [Fact]
    public void A_critical_service_without_a_working_path_is_P1_whether_it_has_one_path_or_false_redundancy()
    {
        IncidentPriority.For([Service(true, Redundancy.None)]).ShouldBe("P1");
        IncidentPriority.For([Service(true, Redundancy.False)]).ShouldBe("P1");
        IncidentPriority.For([Service(false, Redundancy.Working), Service(true, Redundancy.False)]).ShouldBe("P1");
    }

    [Fact]
    public void A_critical_service_with_a_working_path_or_another_service_down_is_P2()
    {
        IncidentPriority.For([Service(true, Redundancy.Working)]).ShouldBe("P2");
        IncidentPriority.For([Service(false, Redundancy.None)]).ShouldBe("P2");
        IncidentPriority.For([Service(false, Redundancy.False)]).ShouldBe("P2");
    }

    [Fact]
    public void Everything_still_reachable_or_nothing_affected_is_P3()
    {
        IncidentPriority.For([Service(false, Redundancy.Working)]).ShouldBe("P3");
        IncidentPriority.For([]).ShouldBe("P3");
    }
}
