namespace Cmdb.Database.Model;

// Mapped to Postgres enums; labels are the snake_case member names (e.g. under_construction).

public enum LifecycleState
{
    Planned,
    UnderConstruction,
    InService,
    Decommissioning,
    Removed,
}

public enum TerminalKind
{
    Port,
    ConductorEnd,
}

public enum ConnectionKind
{
    Patch,
    Splice,
    Termination,
    Internal,
}

public enum CableMedium
{
    Fiber,
    Copper,
    Coax,
    Power,
}
