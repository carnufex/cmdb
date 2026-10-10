using System.Globalization;
using System.Text;

namespace Cmdb.Exchange;

/// <summary>
/// CSV as spreadsheets write it: a header row, comma or semicolon (whichever the header uses), fields in double quotes
/// when they hold the separator, a quote or a line break. Rows are numbered from 2, the header being row 1.
/// </summary>
public sealed class CsvTable
{
    private readonly Dictionary<string, int> _columns;

    private CsvTable(string file, IReadOnlyList<string> header, List<(int Row, string[] Cells)> rows)
    {
        File = file;
        Header = header;
        Rows = rows;
        _columns = header.Select((h, i) => (h, i)).GroupBy(x => x.h, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
    }

    public string File { get; }

    public IReadOnlyList<string> Header { get; }

    public IReadOnlyList<(int Row, string[] Cells)> Rows { get; }

    public bool Has(string column) => _columns.ContainsKey(column);

    /// <summary>The trimmed cell, or "" when the column or the cell is missing.</summary>
    public string Cell(string[] cells, string column) =>
        _columns.TryGetValue(column, out var i) && i < cells.Length ? cells[i].Trim() : "";

    public static CsvTable Parse(string file, string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }
        var firstLine = text.AsSpan(0, Math.Max(0, text.IndexOfAny(['\r', '\n']) is var n and >= 0 ? n : text.Length));
        var separator = firstLine.Count(';') > firstLine.Count(',') ? ';' : ',';
        var records = Records(text, separator).ToList();
        if (records.Count == 0)
        {
            return new CsvTable(file, [], []);
        }
        var header = records[0].Cells.Select(h => h.Trim()).ToArray();
        return new CsvTable(file, header, [.. records.Skip(1).Where(r => r.Cells.Any(c => c.Length > 0))]);
    }

    private static IEnumerable<(int Row, string[] Cells)> Records(string text, char separator)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var row = 1;
        var line = 1;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                cells.Add(cell.ToString());
                cell.Clear();
                yield return (row, [.. cells]);
                cells.Clear();
                line++;
                row = line;
            }
            else
            {
                cell.Append(c);
            }
        }
        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            yield return (row, [.. cells]);
        }
    }
}

/// <summary>Writes CSV with comma separators, quoting only where needed.</summary>
public sealed class CsvWriter(TextWriter writer, params string[] header)
{
    private bool _headerWritten;

    public void Row(params object?[] values)
    {
        if (!_headerWritten)
        {
            Line(header);
            _headerWritten = true;
        }
        if (values.Length != header.Length)
        {
            throw new ArgumentException($"Expected {header.Length} values, got {values.Length}.", nameof(values));
        }
        Line(values);
    }

    /// <summary>Writes the header even when there are no rows.</summary>
    public void Flush()
    {
        if (!_headerWritten)
        {
            Line(header);
            _headerWritten = true;
        }
    }

    private void Line(IEnumerable<object?> values)
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
