using System.Text.Json;
using System.Text.Json.Serialization;
using Cmdb.Database.Provenance;

namespace Cmdb.Catalog;

/// <summary>
/// One rule in <c>source-priority.json</c> (#215): which sources may write <paramref name="Attribute"/> on
/// <paramref name="ObjectType"/>, highest priority first. The attribute is one of the recorded ones (<c>name</c>,
/// <c>position</c> …), an object's own attribute (<c>attributes.serialNumber</c>), all of those (<c>attributes.*</c>) or
/// everything (<c>*</c>); the object is an object type or <c>*</c>. <paramref name="AutoApply"/> lists the sources whose
/// changes to the attribute reconciliation brings into production without review (#216, ADR-0020); each is one of
/// <paramref name="Sources"/>.
/// </summary>
public sealed record SourceRule([property: JsonPropertyName("object")] string ObjectType, string Attribute, IReadOnlyList<string> Sources,
    IReadOnlyList<string>? AutoApply = null);

/// <summary>
/// Which source system owns which attribute (#215, ADR-0019), as catalog data. The most specific rule decides: the
/// exact attribute before <c>attributes.*</c> before <c>*</c>, and an object type before <c>*</c>. Without a rule any
/// source may write, and the one that confirmed last owns the value. Enforced by reconciliation (#216).
/// </summary>
public sealed class SourcePriority
{
    public const string File = "source-priority.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly Lazy<SourcePriority> CurrentPriority = new(() => Load(CatalogSource.Current));

    private readonly Dictionary<(string ObjectType, string Attribute), SourceRule> _rules;

    private SourcePriority(IReadOnlyList<SourceRule> rules)
    {
        Rules = rules;
        _rules = rules.ToDictionary(r => (r.ObjectType, r.Attribute));
    }

    /// <summary>The priority this process uses, from <see cref="CatalogSource.Current"/>.</summary>
    public static SourcePriority Current => CurrentPriority.Value;

    /// <summary>No rules: every source may write everything.</summary>
    public static SourcePriority None { get; } = new([]);

    public IReadOnlyList<SourceRule> Rules { get; }

    /// <summary>The rule that decides <paramref name="attribute"/> on <paramref name="objectType"/>, or null when none does.</summary>
    public SourceRule? RuleFor(string objectType, string attribute)
    {
        foreach (var o in new[] { objectType, "*" })
        {
            if (_rules.TryGetValue((o, attribute), out var rule)
                || (attribute.StartsWith(ReportedValues.AttributePrefix, StringComparison.Ordinal)
                    && _rules.TryGetValue((o, ReportedValues.AttributePrefix + "*"), out rule))
                || _rules.TryGetValue((o, "*"), out rule))
            {
                return rule;
            }
        }
        return null;
    }

    /// <summary>
    /// The source's place in line for the attribute, 0 being the highest; <see cref="int.MaxValue"/> when no rule
    /// decides (every source alike), and null when a rule leaves the source out and it may not write.
    /// </summary>
    public int? Rank(string objectType, string attribute, string source)
    {
        if (RuleFor(objectType, attribute) is not { } rule)
        {
            return int.MaxValue;
        }
        for (var i = 0; i < rule.Sources.Count; i++)
        {
            if (rule.Sources[i] == source)
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>Whether the source's changes to the attribute go into production without review (#216).</summary>
    public bool AutoApplies(string objectType, string attribute, string source) =>
        RuleFor(objectType, attribute)?.AutoApply?.Contains(source) == true;

    /// <summary>
    /// Which of the sources reporting an attribute owns its value: the highest-ranked one that may write it, and
    /// among equals the one that confirmed last. Null when none may.
    /// </summary>
    public string? Owner(string objectType, string attribute, IEnumerable<(string Source, DateTimeOffset ConfirmedAt)> reporting) =>
        reporting
            .Select(r => (r.Source, r.ConfirmedAt, Rank: Rank(objectType, attribute, r.Source)))
            .Where(r => r.Rank is not null)
            .OrderBy(r => r.Rank)
            .ThenByDescending(r => r.ConfirmedAt)
            .Select(r => r.Source)
            .FirstOrDefault();

    /// <summary>Loads and checks <c>source-priority.json</c>; a catalog without one has no rules.</summary>
    public static SourcePriority Load(CatalogSource source)
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

    public static SourcePriority Parse(string json)
    {
        List<SourceRule> rules;
        try
        {
            rules = JsonSerializer.Deserialize<List<SourceRule>>(json, Json) ?? [];
        }
        catch (JsonException ex)
        {
            throw Invalid([ex.Message]);
        }

        var errors = new List<string>();
        foreach (var rule in rules)
        {
            if (rule.ObjectType is null || rule.Attribute is null || rule.Sources is null)
            {
                errors.Add("object, attribute and sources are required");
                continue;
            }
            var name = $"'{rule.ObjectType}' '{rule.Attribute}'";
            if (rule.ObjectType != "*" && !ReportedValues.ObjectTypes.Contains(rule.ObjectType))
            {
                errors.Add($"{name}: unknown object type, expected {string.Join(", ", ReportedValues.ObjectTypes)} or *");
            }
            else if (!KnownAttribute(rule.ObjectType, rule.Attribute))
            {
                errors.Add($"{name}: unknown attribute, expected one of {string.Join(", ", Attributes(rule.ObjectType))}, attributes.<key>, attributes.* or *");
            }
            if (rule.Sources.Count == 0 || rule.Sources.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"{name}: needs at least one source, none empty");
            }
            foreach (var duplicate in rule.Sources.Where(s => s is not null).GroupBy(s => s).Where(g => g.Count() > 1))
            {
                errors.Add($"{name}: source '{duplicate.Key}' is listed more than once");
            }
            foreach (var auto in (rule.AutoApply ?? []).Where(a => !rule.Sources.Contains(a)))
            {
                errors.Add($"{name}: autoApply source '{auto}' is not one of its sources");
            }
        }
        foreach (var duplicate in rules.Where(r => r.ObjectType is not null && r.Attribute is not null)
                     .GroupBy(r => (r.ObjectType, r.Attribute)).Where(g => g.Count() > 1))
        {
            errors.Add($"'{duplicate.Key.ObjectType}' '{duplicate.Key.Attribute}': more than one rule");
        }
        return errors.Count > 0 ? throw Invalid(errors) : new SourcePriority(rules);
    }

    private static IEnumerable<string> Attributes(string objectType) =>
        objectType == "*"
            ? ReportedValues.Attributes.Values.SelectMany(a => a).Distinct()
            : ReportedValues.Attributes[objectType];

    private static bool KnownAttribute(string objectType, string attribute) =>
        attribute == "*"
        || (attribute.StartsWith(ReportedValues.AttributePrefix, StringComparison.Ordinal) && attribute.Length > ReportedValues.AttributePrefix.Length
            && objectType != "circuit")
        || Attributes(objectType).Contains(attribute);

    private static InvalidOperationException Invalid(IEnumerable<string> errors) =>
        new("Invalid source priority:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => $"{File}: {e}")));
}
