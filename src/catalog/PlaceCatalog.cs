using System.Collections.Frozen;
using System.Text.Json;

namespace Cmdb.Catalog;

/// <summary>A place people name in a question, with its area as WKT in SWEREF 99 TM (EPSG:3006).</summary>
public sealed record Place(string Code, string Name, string Wkt);

/// <summary>
/// Sweden's municipalities (#330), so a question can name a place: the network itself has codes and no place names. The
/// borders are Statistics Sweden's (CC0), simplified, which is enough for "the sites in Gävle" and not for settling a
/// border. Built by scripts/build-places.py.
/// </summary>
public static class PlaceCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<Names> Places = new(Load);

    /// <summary>By name, whatever its case, with or without "kommun" or "stad", and without its Swedish letters when that leaves one place (not Håbo and Habo).</summary>
    public static Place? Find(string? name)
    {
        var key = (name ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0)
        {
            return null;
        }
        if (Places.Value.Find(key) is { } place)
        {
            return place;
        }
        foreach (var suffix in new[] { " kommun", " stad" })
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal))
            {
                key = key[..^suffix.Length];
                // "Gävle kommun", "Stockholms stad".
                return Places.Value.Find(key) ?? (key.EndsWith('s') ? Places.Value.Find(key[..^1]) : null);
            }
        }
        return null;
    }

    private static Names Load()
    {
        using var stream = typeof(PlaceCatalog).Assembly.GetManifestResourceStream("places/municipalities.json")
            ?? throw new InvalidOperationException("The place catalog is not embedded.");
        var places = JsonSerializer.Deserialize<List<Place>>(stream, Json)
            ?? throw new InvalidOperationException("The place catalog is empty.");
        return new Names(
            places.ToFrozenDictionary(p => p.Name.ToLowerInvariant()),
            places.GroupBy(p => Plain(p.Name.ToLowerInvariant())).Where(g => g.Count() == 1).ToFrozenDictionary(g => g.Key, g => g.Single()));
    }

    /// <summary>The letters Swedish place names use, spelled out: Unicode normalisation does nothing with invariant globalization.</summary>
    private static string Plain(string lower) => lower.Replace('å', 'a').Replace('ä', 'a').Replace('ö', 'o').Replace('é', 'e');

    private sealed record Names(FrozenDictionary<string, Place> Exact, FrozenDictionary<string, Place> Plain)
    {
        public Place? Find(string lower) => Exact.GetValueOrDefault(lower) ?? Plain.GetValueOrDefault(PlaceCatalog.Plain(lower));
    }
}
