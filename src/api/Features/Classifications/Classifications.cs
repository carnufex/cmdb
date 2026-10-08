using System.ComponentModel;
using System.Security.Claims;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using FastEndpoints;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace Cmdb.Api.Features.Classifications;

public sealed record ClassificationLevelView(int Level, string Name, string Description);

public sealed record ClassificationSchemaView(string Key, string Name, string Description, IReadOnlyList<string> AppliesTo,
    IReadOnlyList<ClassificationLevelView> Levels, int CriticalFrom);

/// <param name="Name">The level's name in the schema, e.g. "Kritisk".</param>
public sealed record ObjectClassification(string Schema, string SchemaName, int Level, string Name, bool Critical, string Source,
    string SetBy, DateTimeOffset SetAt);

/// <summary>Reading and writing the classifications objects have (#176, ADR-0017), shared by REST, MCP and applied plans.</summary>
public static class ClassificationStore
{
    public static ClassificationSchemaView View(ClassificationSchema s) =>
        new(s.Key, s.Name, s.Description, s.AppliesTo, [.. s.Levels.Select(l => new ClassificationLevelView(l.Level, l.Name, l.Description))], s.CriticalFrom);

    /// <summary>The problem with setting this level on this kind of object, or null when it is fine.</summary>
    public static string? Problem(string type, string schemaKey, int? level)
    {
        if (ClassificationCatalog.Current.Find(schemaKey) is not { } schema)
        {
            return $"Klassningen {schemaKey} finns inte: {string.Join(", ", ClassificationCatalog.Current.Schemas.Select(s => s.Key))}.";
        }
        if (!schema.AppliesTo.Contains(type))
        {
            return $"{schema.Name} gäller {string.Join(", ", schema.AppliesTo)}, inte {type}.";
        }
        return level is { } l && schema.Level(l) is null
            ? $"{schema.Name} har nivåerna {string.Join(", ", schema.Levels.Select(x => x.Level))}, inte {l}."
            : null;
    }

    public static async Task<List<ObjectClassification>> OfAsync(NpgsqlDataSource db, string type, long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT schema_key, level, source, set_by, set_at FROM classification WHERE object_type = $1 AND object_id = $2 ORDER BY schema_key
            """);
        cmd.Parameters.Add(new() { Value = type });
        cmd.Parameters.Add(new() { Value = id });
        var list = new List<ObjectClassification>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (ClassificationCatalog.Current.Find(reader.GetString(0)) is not { } schema)
            {
                continue;
            }
            var level = reader.GetInt32(1);
            list.Add(new ObjectClassification(schema.Key, schema.Name, level, schema.Level(level)?.Name ?? $"Nivå {level}", level >= schema.CriticalFrom,
                reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return list;
    }

    /// <summary>Sets the level, or clears it with a null level. Idempotent.</summary>
    public static async Task SetAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string type, long id, string schemaKey, int? level, string actor,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(level is null
            ? "DELETE FROM classification WHERE object_type = $1 AND object_id = $2 AND schema_key = $3"
            : """
              INSERT INTO classification (object_type, object_id, schema_key, level, source, set_by)
              VALUES ($1, $2, $3, $4, 'set', $5)
              ON CONFLICT (schema_key, object_type, object_id) DO UPDATE SET level = $4, source = 'set', set_by = $5, set_at = now()
              """, conn, tx);
        cmd.Parameters.Add(new() { Value = type });
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = schemaKey });
        if (level is { } l)
        {
            cmd.Parameters.Add(new() { Value = l });
            cmd.Parameters.Add(new() { Value = actor });
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <param name="Derived">The criticality once contents and carried services are counted (#177); null when it cannot be worked out.</param>
public sealed record ObjectClassificationAnswer(IReadOnlyList<ObjectClassification> Direct, DerivedClassification? Derived, RuleReport? Rules);

/// <summary>The classification schemas of the catalog.</summary>
public sealed class ListClassificationSchemasEndpoint : EndpointWithoutRequest<IReadOnlyList<ClassificationSchemaView>>
{
    public override void Configure() => Get("/classifications/schemas");

    public override async Task HandleAsync(CancellationToken ct) =>
        await Send.OkAsync([.. ClassificationCatalog.Current.Schemas.OrderBy(s => s.Key, StringComparer.Ordinal).Select(ClassificationStore.View)], ct);
}

public sealed class ObjectClassificationRequest
{
    public string Type { get; set; } = "";
    public long Id { get; set; }
}

/// <summary>An object's classifications. Outside the caller's scopes the object does not exist.</summary>
public sealed class GetObjectClassificationEndpoint(RequestDb db) : Endpoint<ObjectClassificationRequest, IReadOnlyList<ObjectClassification>>
{
    public override void Configure() => Get("/classifications");

    public override async Task HandleAsync(ObjectClassificationRequest req, CancellationToken ct)
    {
        if (!ClassificationCatalog.ObjectTypes.Contains(req.Type) || !await PlanSql.ObjectVisibleAsync(db.Source, req.Type, req.Id, HttpContext.Scope(), ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(await ClassificationStore.OfAsync(db.Source, req.Type, req.Id, ct), ct);
    }
}

public sealed class SetClassificationRequest
{
    public string Type { get; set; } = "";
    public long Id { get; set; }
    public string Schema { get; set; } = "";

    /// <summary>The level, or null to clear it.</summary>
    public int? Level { get; set; }
}

/// <summary>
/// Sets or clears a classification directly in production, from the panel. People with write access only, and only on what
/// their scopes show; agents propose it as a plan operation instead (<c>set_classification</c>, ADR-0011).
/// </summary>
public sealed class SetClassificationEndpoint(RequestDb db, TrustedApplications trust) : Endpoint<SetClassificationRequest>
{
    public override void Configure()
    {
        Put("/classifications");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(SetClassificationRequest req, CancellationToken ct)
    {
        if (User.FindFirstValue(CmdbClaims.Client) is { } client && trust.AgentClients.Contains(client))
        {
            await Send.ResultAsync(TypedResults.Problem("Agenter föreslår klassningar i en plan, de sätter dem inte direkt.", statusCode: StatusCodes.Status403Forbidden));
            return;
        }
        if (!ClassificationCatalog.ObjectTypes.Contains(req.Type) || !await PlanSql.ObjectVisibleAsync(db.Source, req.Type, req.Id, HttpContext.Scope(), ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (ClassificationStore.Problem(req.Type, req.Schema, req.Level) is { } problem)
        {
            AddError(problem);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        await ClassificationStore.SetAsync(conn, null, req.Type, req.Id, req.Schema, req.Level, PlanSql.Actor(User), ct);
        await Send.NoContentAsync(ct);
    }
}

/// <summary>Classification for agents (#176): what schemas exist and what an object has. Setting one goes through a plan.</summary>
[McpServerToolType]
public sealed class ClassificationTools(RequestDb db, IHttpContextAccessor http, ClassificationDerivation derivation, ClassificationRules rules)
{
    [McpServerTool(Name = "describe_classifications", Title = "Klassningar", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The classification schemas (for example criticality 1–5): their levels, which object types they apply to, and the level " +
        "that counts as critical. Set one with add_to_plan (kind set_classification).")]
    public IReadOnlyList<ClassificationSchemaView> Describe() =>
        [.. ClassificationCatalog.Current.Schemas.OrderBy(s => s.Key, StringComparer.Ordinal).Select(ClassificationStore.View)];

    [McpServerTool(Name = "get_classification", Title = "Hämta klassning", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The classifications an object has, with level names and who set them (direct), and the criticality it gets from " +
        "what it contains and carries (derived: the highest of its own level, its equipment's and the services that run through it, " +
        "with the objects that cause it), and the requirements that level brings (rules): which are met and which are not, with a hint. " +
        "Within your access scopes.")]
    public async Task<ObjectClassificationAnswer> Get(
        [Description("A reference \"type:id\" (site, equipment, cable or service), e.g. \"site:1268\".")] string reference,
        [Description("A plan, \"plan:12\": the derived level in that plan's view, with its classification changes counted.")] string? plan = null,
        CancellationToken ct = default)
    {
        var colon = reference.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !ClassificationCatalog.ObjectTypes.Contains(reference[..colon])
            || !long.TryParse(reference.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            || !await PlanSql.ObjectVisibleAsync(db.Source, reference[..colon], id, http.HttpContext!.Scope(), ct))
        {
            throw new McpException($"{reference} finns inte, eller ligger utanför ditt omfång.");
        }
        long? planId = null;
        if (plan is not null)
        {
            planId = long.TryParse(plan.StartsWith("plan:", StringComparison.Ordinal) ? plan[5..] : plan, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : throw new McpException($"\"{plan}\" is not a plan reference like \"plan:12\".");
        }
        var type = reference[..colon];
        return new ObjectClassificationAnswer(await ClassificationStore.OfAsync(db.Source, type, id, ct),
            await derivation.DeriveAsync(http.HttpContext!.Scope(), type, id, "criticality", planId, ct),
            await rules.EvaluateAsync(http.HttpContext!.Scope(), type, id, "criticality", planId, ct));
    }
}
