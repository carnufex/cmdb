using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Cmdb.Cli.Sync;

/// <summary>
/// Writes the exchange format (#210, docs/import.md): one CSV per kind of object, referring to each other by the
/// source's ids. Only the files an adapter writes to exist, so reconciliation leaves the other kinds alone. The API
/// checks every row against the catalog; this writer only keeps the columns and quoting right.
/// </summary>
public sealed class ExchangeWriter(string folder) : IDisposable
{
    private readonly Dictionary<string, (StreamWriter Writer, int Columns)> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _rows = new(StringComparer.Ordinal);

    public string Folder => folder;

    /// <summary>Rows written per file.</summary>
    public IReadOnlyDictionary<string, int> Rows => _rows;

    /// <summary>A site, positioned in SWEREF 99 TM (<paramref name="x"/>, <paramref name="y"/>) or WGS 84 (lat, lon).</summary>
    public void Site(string id, string code, string name, string siteType, string? lifecycle = null, double? x = null, double? y = null,
        double? lat = null, double? lon = null, JsonObject? attributes = null) =>
        Row("sites.csv", ["id", "code", "name", "siteType", "x", "y", "lat", "lon", "lifecycle", "attributes"],
            id, code, name, siteType, x, y, lat, lon, lifecycle, attributes?.ToJsonString());

    /// <summary>A building, room, rack or position; <paramref name="parent"/> is another location's id.</summary>
    public void Location(string id, string site, string kind, string name, string? parent = null, short? rackUnits = null) =>
        Row("locations.csv", ["id", "site", "parent", "kind", "name", "rackUnits"], id, site, parent, kind, name, rackUnits);

    /// <summary>Equipment in a location, or a card in a slot of <paramref name="parent"/>.</summary>
    public void Equipment(string id, string site, string name, string type, string? lifecycle = null, string? location = null,
        string? parent = null, string? slot = null, short? rackPosition = null, JsonObject? attributes = null) =>
        Row("equipment.csv", ["id", "site", "location", "parent", "slot", "name", "type", "lifecycle", "rackPosition", "attributes"],
            id, site, location, parent, slot, name, type, lifecycle, rackPosition, attributes?.ToJsonString());

    /// <summary>A cable between two sites; <paramref name="route"/> is an optional WKT LINESTRING in SWEREF 99 TM.</summary>
    public void Cable(string id, string code, string cableType, string a, string b, string? lifecycle = null, string? route = null,
        JsonObject? attributes = null) =>
        Row("cables.csv", ["id", "code", "cableType", "a", "b", "lifecycle", "route", "attributes"],
            id, code, cableType, a, b, lifecycle, route, attributes?.ToJsonString());

    public void Service(string id, string code, string name, string serviceType, string? lifecycle = null, JsonObject? attributes = null) =>
        Row("services.csv", ["id", "code", "name", "serviceType", "lifecycle", "attributes"],
            id, code, name, serviceType, lifecycle, attributes?.ToJsonString());

    /// <summary>
    /// Any other file of the format (ports, connections, circuits …) with its columns as in docs/import.md. Every row
    /// of a file has the columns of its first.
    /// </summary>
    public void Row(string file, string[] header, params object?[] values)
    {
        if (values.Length != header.Length)
        {
            throw new ArgumentException($"{file}: {header.Length} kolumner men {values.Length} värden.", nameof(values));
        }
        if (!_files.TryGetValue(file, out var target))
        {
            var writer = new StreamWriter(Path.Combine(folder, file), false, new UTF8Encoding(false), 1 << 16);
            target = (writer, header.Length);
            _files[file] = target;
            Line(writer, header);
        }
        else if (target.Columns != header.Length)
        {
            throw new ArgumentException($"{file}: raderna har olika kolumner.", nameof(header));
        }
        Line(target.Writer, values);
        _rows[file] = _rows.GetValueOrDefault(file) + 1;
    }

    public void Dispose()
    {
        foreach (var (writer, _) in _files.Values)
        {
            writer.Dispose();
        }
        _files.Clear();
    }

    private static void Line(TextWriter writer, IEnumerable<object?> values)
    {
        writer.Write(string.Join(',', values.Select(Field)));
        writer.Write('\n');
    }

    private static string Field(object? value)
    {
        var text = value switch
        {
            null => "",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        return text.IndexOfAny([',', ';', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : text;
    }
}
