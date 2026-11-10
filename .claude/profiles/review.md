# Review profile — cmdb

<!-- Read by review-sweep and review-hotspots (plugin `review@carnufex`, enabled in .claude/settings.json).
     Facts about cmdb only; the method is in the plugin. -->

## Read first

- `CLAUDE.md`: the absolute rules (synthetic data only, every change against an issue, no silent architecture decisions,
  the performance budget is a requirement, authorisation is never bypassed — not in test, debug or export paths) and the
  conventions (vertical slices, EF Core code-first, EPSG:3006, OpenShift-compatible containers)
- `docs/plan.md`: principles and the performance budget per interaction — a breach is a bug
- `docs/arkitektur.md`, `docs/domanmodell.md`: layers, graph engine, data model
- `docs/adr/README.md` and the ADRs of the area under review; on authorisation always ADR-0007, ADR-0012 and ADR-0022
- `docs/agenter.md`, `docs/rostagenter.md`: the MCP surface and the voice agents
- `docs/drift.md`: environments, deploy, logs and alerts

## Areas

| Area | Where | Focus |
|---|---|---|
| `api` | `src/api/Features/*`, `src/api/{Auth,Agents,Diagnostics}`, `Program.cs` | every endpoint: scope checks, validation, contracts, MCP tools |
| `plans` | `src/api/Features/{Plans,Reservations,Operations,Reconciliation}` | change sets, apply, conflicts against production and other plans, the operation log |
| `graph` | `src/graph`, `tests/Cmdb.Graph.Benchmarks` | the in-memory graph: trace, impact, change feed, snapshots, memory and latency |
| `data` | `src/database`, `src/catalog`, `src/exchange`, `src/datagen` | EF model and migrations, RLS and direct access, catalog rules, import/export, synthetic data |
| `web` | `src/web/src`, `src/web/e2e` | UX, accessibility, design tokens, status as dot and text, signals, map (OpenLayers, SWEREF 99 TM) |
| `deploy` | `Dockerfile`s, `compose.yaml`, `scripts/`, `src/cli` | container rules, deploy script, CLI sync jobs |
| `quality` | `tests/`, `agent/`, `docs/` | test value and flakiness, agent tests, doc drift |

## Project rules

- architecture: features are vertical slices (FastEndpoints), one folder per feature with endpoint, request/response,
  validation, handler and test; no generic service layer.
- architecture: schema changes only through the EF model and `dotnet ef migrations add` (ADR-0009); bulk paths may use
  Npgsql/`COPY` against the EF schema.
- security: every read surface (API, graph engine, MCP, export, search, map tiles) applies the user's scopes (ADR-0007);
  Postgres RLS is the last barrier for direct access (ADR-0012); integration accounts have their own role and scope
  (ADR-0022). A surface without a negative test per scope is a finding.
- security: agents cannot apply plans — any path that lets MCP or a voice agent change production is a blocker.
- performance: a change on an interactive path checks the budget in `docs/plan.md`; graph changes need benchmark numbers.
- data: no real organisations, systems, people or data models anywhere, including fixtures and screenshots.
- modernity: .NET 10, Angular with standalone components and signals, PostGIS.
- reliability: logs carry the object reference (`type:id`), plan id or operation id that localises a fault.

## Not a finding

- Anything `scripts/verify.sh` already gates (`dotnet format`, prettier, lint, build, tests).
- Migration designer files and the model snapshot.
- Synthetic names that look plausible — they are made up by design.

## Tests policy

A missing test is a finding for scope/authorisation (allow and deny), plan apply and conflict logic, graph trace/impact,
data boundaries (import, export, RLS) and bug fixes that could regress. Integration tests run against real Postgres
(Testcontainers).

## Issues

- Labels: `type:<feature|bug|task>`, `area:<backend|data|frontend|docs|infra|security>`, `review-sweep`
- Issues, docs and PR descriptions in Swedish; code and commit messages in English.

## Hotspots

| Rank | Hotspot | What counts (paths, symbols) |
|---|---|---|
| 1 | Authorisation | `src/api/Auth/**`, `src/database/Scopes/**`, RLS policies in migrations, any `Scope`/visibility filter in `src/graph` or a feature's query |
| 1 | Plan apply | `src/api/Features/Plans/{ApplyPlan,PlanApply,PlanWrites,PlanSql}.cs`, `src/database/OperationLog.cs` — anything that writes production |
| 2 | Agent surface | `src/api/Agents/**`, MCP tools in features, `src/api/Features/Voice/**` — what an agent may read or propose |
| 2 | Graph engine | `src/graph/**` — correctness of trace and impact, memory and latency against the budget |
| 3 | Schema | `src/database/Model/**`, `src/database/Migrations/*.cs` (not designer files) |

The defaults (message contracts and jobs, public API, user content, CI, telemetry) follow below these.

## Person-reviewed paths

A PR touching these goes to Christopher with the hotspot map at the top of the PR description.

- `src/api/Auth/**`
- `src/database/Scopes/**`
- `src/api/Features/Plans/ApplyPlan.cs`
- `src/api/Features/Plans/PlanApply.cs`

## Noise

- `src/database/Migrations/*.Designer.cs`, `src/database/Migrations/*ModelSnapshot.cs`
- `**/package-lock.json`
- `changelog/**`
