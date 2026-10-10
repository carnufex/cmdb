using System.Text.Json;
using System.Text.Json.Serialization;
using Cmdb.Database.Provenance;

namespace Cmdb.Catalog;

/// <summary>
/// One rule in <c>source-matching.json</c> (#216): an object a source reports, but does not yet know by its id, is the
/// object in cmdb whose <paramref name="Keys"/> all have the same values. Keys are recorded attributes with a plain value
/// (<c>name</c>, <c>code</c> …) or an object's own attributes (<c>attributes.serialNumber</c>).
/// </summary>
public sealed record MatchRule([property: JsonPropertyName("object")] string ObjectType, IReadOnlyList<string> Keys);

/// <summary>
/// How reconciliation links an object from a new source to one cmdb already has (#216, ADR-0020), as catalog data. The
/// rules for an object type are tried in order, and the first that finds exactly one object links it. Two or more is a
/// deviation to report, never a guess.
/// </summary>
public sealed class SourceMatching
{
    public const string File = "source-matching.json";

    /// <summary>Values that are links to other objects, positions or hashes: compared, never matched on.</summary>
    private static readonly HashSet<string> Structural = new(StringComparer.Ordinal) { "placement", "ends", "route", "position" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly Lazy<SourceMatching> CurrentMatching = new(() => Load(CatalogSource.Current));

    private SourceMatching(IReadOnlyList<MatchRule> rules) => Rules = rules;

    /// <summary>The rules this process uses, from <see cref="CatalogSource.Current"/>.</summary>
    public static SourceMatching Current => CurrentMatching.Value;

    /// <summary>No rules: objects are matched on source and id only.</summary>
    public static SourceMatching None { get; } = new([]);

    public IReadOnlyList<MatchRule> Rules { get; }

    /// <summary>The rules for an object type, in the order they are tried.</summary>
    public IEnumerable<MatchRule> For(string objectType) => Rules.Where(r => r.ObjectType == objectType);

    /// <summary>Loads and checks <c>source-matching.json</c>; a catalog without one has no rules.</summary>
    public static SourceMatching Load(CatalogSource source)
    {
        var json = source.Read(File);
        try
        {
            return json is null ? None : Parse(json);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}{Environment.NewLine}(catalog: {source.Name})", ex);
        }
    }

    public static SourceMatching Parse(string json)
    {
        List<MatchRule> rules;
        try
        {
            rules = JsonSerializer.Deserialize<List<MatchRule>>(json, Json) ?? [];
        }
        catch (JsonException ex)
        {
            throw Invalid([ex.Message]);
        }

        var errors = new List<string>();
        foreach (var rule in rules)
        {
            if (rule.ObjectType is null || rule.Keys is null)
            {
                errors.Add("object and keys are required");
                continue;
            }
            var name = $"'{rule.ObjectType}' [{string.Join(", ", rule.Keys)}]";
            if (!ReportedValues.ObjectTypes.Contains(rule.ObjectType))
            {
                errors.Add($"{name}: unknown object type, expected {string.Join(", ", ReportedValues.ObjectTypes)}");
                continue;
            }
            if (rule.Keys.Count == 0)
            {
                errors.Add($"{name}: needs at least one key");
            }
            foreach (var key in rule.Keys.Where(k => !KnownKey(rule.ObjectType, k)))
            {
                errors.Add($"{name}: '{key}' cannot be matched on, expected one of "
                    + $"{string.Join(", ", ReportedValues.Attributes[rule.ObjectType].Where(a => !Structural.Contains(a)))} or attributes.<key>");
            }
        }
        foreach (var duplicate in rules.Where(r => r.ObjectType is not null && r.Keys is not null)
                     .GroupBy(r => (r.ObjectType, string.Join(",", r.Keys))).Where(g => g.Count() > 1))
        {
            errors.Add($"'{duplicate.Key.ObjectType}' [{duplicate.Key.Item2}]: more than one rule");
        }
        return errors.Count > 0 ? throw Invalid(errors) : new SourceMatching(rules);
    }

    private static bool KnownKey(string objectType, string key) =>
        key is not null
        && ((key.StartsWith(ReportedValues.AttributePrefix, StringComparison.Ordinal) && key.Length > ReportedValues.AttributePrefix.Length
                && key[^1] != '*' && objectType != "circuit")
            || (ReportedValues.Attributes[objectType].Contains(key) && !Structural.Contains(key)));

    private static InvalidOperationException Invalid(IEnumerable<string> errors) =>
        new("Invalid source matching:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => $"{File}: {e}")));
}
