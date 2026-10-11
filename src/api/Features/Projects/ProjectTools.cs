using System.ComponentModel;
using Cmdb.Api.Agents;
using Cmdb.Api.Auth;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cmdb.Api.Features.Projects;

public sealed record AgentStage(int Seq, string Plan, string Name, string Status, string? Flag, int Changes, int Problems, decimal CableMetres,
    decimal Cost, string Url);

public sealed record AgentProject(string Ref, string Name, string Owner, string Description, IReadOnlyList<AgentStage> Stages, int HiddenStages,
    decimal CableMetres, decimal Cost, string Currency, string NextStep);

[McpServerToolType]
public sealed class ProjectTools(ProjectService projects, AgentLinks links, IHttpContextAccessor http)
{
    [McpServerTool(Name = "list_projects", Title = "Projekt", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Projects (#293): areas built out in stages, where each stage is a plan building on the one before. Lists those whose " +
        "area your scopes reach, with how many stages are applied.")]
    public async Task<IReadOnlyList<ProjectSummary>> ListProjects(CancellationToken ct = default) =>
        await projects.ListAsync(http.HttpContext!.Scope(), ct);

    [McpServerTool(Name = "get_project", Title = "Projekt i detalj", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A project's stages in order: each plan with its status, changes, changes that no longer fit, metres of new cable and " +
        "cost from its materials, the totals, and what to do next. Give the stage's url to the person who asked.")]
    public async Task<AgentProject> GetProject(
        [Description("\"project:<id>\" or the id.")] string reference,
        CancellationToken ct = default)
    {
        var text = reference.StartsWith("project:", StringComparison.Ordinal) ? reference["project:".Length..] : reference;
        if (!long.TryParse(text, out var id) || await projects.GetAsync(id, http.HttpContext!.Scope(), ct) is not { } p)
        {
            throw new McpException($"No project {reference}.");
        }
        return new AgentProject(AgentLinks.Ref("project", id), p.Name, p.Owner, p.Description,
            [.. p.Stages.Select(s => new AgentStage(s.Seq, AgentLinks.Ref("plan", s.Plan.Id), s.Plan.Name, s.Plan.Status, s.Plan.Flag, s.Changes, s.Problems,
                s.CableMetres, s.Cost, links.ForPlan(s.Plan.Id)))],
            p.HiddenStages, p.CableMetres, p.Cost, p.Currency, p.NextStep);
    }
}
