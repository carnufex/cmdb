using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cmdb.Catalog;

public sealed record ClassificationLevel(int Level, string Name, string Description);

/// <param name="AppliesTo">Object types it can be set on: site, equipment, cable or service.</param>
/// <param name="CriticalFrom">The lowest level that counts as critical (priority rules, fault analysis).</param>
/// <param name="Inheritance">How a derived level is worked out (#177): through containment (equipment, rack, room, building,
/// site) and through dependency (what carries a classified service). Only <c>max</c> for now.</param>
/// <param name="Rules">Requirements that come with a level (#178); none by default.</param>
public sealed record ClassificationSchema(string Key, string Name, string Description, IReadOnlyList<string> AppliesTo,
    IReadOnlyList<ClassificationLevel> Levels, int CriticalFrom, ClassificationInheritance Inheritance,
    IReadOnlyList<ClassificationRule>? Rules = null)
{
    public IReadOnlyList<ClassificationRule> RuleList => Rules ?? [];

    public ClassificationLevel? Level(int level) => Levels.FirstOrDefault(l => l.Level == level);
}

public sealed record ClassificationInheritance(string Containment, string Dependency);

/// <summary>
/// A requirement an object must meet once its level (derived, #177) reaches <paramref name="FromLevel"/> (#178). There are two kinds:
/// <c>cables</c> counts the in-service cables at a site against <paramref name="Min"/> (<paramref name="Supporting"/>: only cables that
/// themselves carry at least the site's level; <paramref name="Independent"/>: count distinct sites at their other ends, so two
/// cables to one neighbour count once), and <c>attribute</c> needs a numeric attribute of the object itself to be at least
/// <paramref name="Min"/>. Nothing else: rules are data, not code.
/// </summary>
public sealed record ClassificationRule(string Id, int FromLevel, IReadOnlyList<string> AppliesTo, string Type, string Requirement, int Min,
    bool Supporting = false, bool Independent = false, string? Attribute = null);

/// <summary>
/// The classification schemas (#176, ADR-0017), loaded and checked from the embedded catalog files. A schema says which
/// levels exist, which kinds of object take them and how they are inherited; the levels objects have are rows
/// in <c>classification</c>. A new classification is a catalog file, not a migration.
/// </summary>
public sealed class ClassificationCatalog
{
    public static readonly IReadOnlySet<string> ObjectTypes = new HashSet<string>(StringComparer.Ordinal) { "site", "equipment", "cable", "service" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly Lazy<ClassificationCatalog> EmbeddedCatalog = new(() => Load(typeof(ClassificationCatalog).Assembly));

    private readonly FrozenDictionary<string, ClassificationSchema> _schemas;

    private ClassificationCatalog(IEnumerable<ClassificationSchema> schemas) =>
        _schemas = schemas.ToFrozenDictionary(s => s.Key, StringComparer.Ordinal);

    public static ClassificationCatalog Embedded => EmbeddedCatalog.Value;

    public IReadOnlyCollection<ClassificationSchema> Schemas => _schemas.Values;

    public ClassificationSchema? Find(string key) => _schemas.GetValueOrDefault(key);

    public static ClassificationCatalog Load(Assembly assembly)
    {
        var schemas = new List<ClassificationSchema>();
        var errors = new List<string>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("classifications/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            ClassificationSchema schema;
            try
            {
                schema = JsonSerializer.Deserialize<ClassificationSchema>(stream, Json) ?? throw new JsonException("empty");
            }
            catch (JsonException e)
            {
                errors.Add($"{name}: {e.Message}");
                continue;
            }
            Check(name, schema, errors);
            schemas.Add(schema);
        }
        errors.AddRange(schemas.GroupBy(s => s.Key).Where(g => g.Count() > 1).Select(g => $"classifications: key {g.Key} is used twice."));
        return errors.Count > 0
            ? throw new InvalidOperationException("The classification catalog is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors))
            : new ClassificationCatalog(schemas);
    }

    private static void Check(string file, ClassificationSchema schema, List<string> errors)
    {
        void Error(string message) => errors.Add($"{file}: {message}");
        if (schema.Key.Length is 0 or > 50 || schema.Key.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_')))
        {
            Error("key is 1–50 characters: lower-case letters, digits, - and _.");
        }
        if (schema.AppliesTo.Count == 0 || schema.AppliesTo.Any(t => !ObjectTypes.Contains(t)))
        {
            Error($"appliesTo is one or more of {string.Join(", ", ObjectTypes)}.");
        }
        if (schema.Levels.Count == 0 || schema.Levels.Select(l => l.Level).Distinct().Count() != schema.Levels.Count
            || schema.Levels.Any(l => l.Level < 1 || l.Level > 100 || l.Name.Length == 0))
        {
            Error("levels are named and numbered 1–100, each number once.");
        }
        else if (schema.Levels.All(l => l.Level != schema.CriticalFrom))
        {
            Error("criticalFrom must be one of the levels.");
        }
        if (schema.Inheritance.Containment != "max" || schema.Inheritance.Dependency != "max")
        {
            Error("inheritance is max for containment and dependency (the only rule so far).");
        }
        foreach (var rule in schema.RuleList)
        {
            if (schema.Levels.All(l => l.Level != rule.FromLevel))
            {
                Error($"rule {rule.Id}: fromLevel must be one of the levels.");
            }
            if (rule.AppliesTo.Count == 0 || rule.AppliesTo.Any(t => !schema.AppliesTo.Contains(t) || t != "site"))
            {
                Error($"rule {rule.Id}: appliesTo is site, and a type the schema applies to (sites are the only kind rules check so far).");
            }
            if (rule.Type is not ("cables" or "attribute") || rule.Min < 1 || rule.Requirement.Length == 0
                || (rule.Type == "attribute" && string.IsNullOrEmpty(rule.Attribute)))
            {
                Error($"rule {rule.Id}: type is cables or attribute (with attribute), min at least 1, and a requirement in words.");
            }
        }
        if (schema.RuleList.GroupBy(r => r.Id).Any(g => g.Count() > 1))
        {
            Error("rule ids are unique.");
        }
    }
}
