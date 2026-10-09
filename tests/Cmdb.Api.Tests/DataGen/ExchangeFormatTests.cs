using Cmdb.Catalog;
using Cmdb.DataGen.Exchange;

namespace Cmdb.Api.Tests.DataGen;

public sealed class ExchangeFormatTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("cmdb-exchange-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Csv_takes_the_separator_from_the_header_and_reads_quoted_fields()
    {
        var table = CsvTable.Parse("t.csv", "﻿id;name;note\r\n1;\"Nav; norr\";\"två\nrader\"\r\n\r\n2;Söder;\"sa \"\"hej\"\"\"\n");

        table.Rows.Count.ShouldBe(2);
        var (row, cells) = table.Rows[0];
        row.ShouldBe(2);
        table.Cell(cells, "NAME").ShouldBe("Nav; norr");
        table.Cell(cells, "note").ShouldBe("två\nrader");
        table.Rows[1].Row.ShouldBe(5);
        table.Cell(table.Rows[1].Cells, "note").ShouldBe("sa \"hej\"");
        table.Cell(table.Rows[1].Cells, "missing").ShouldBe("");
    }

    [Fact]
    public void Csv_written_reads_back()
    {
        using var text = new StringWriter();
        var writer = new CsvWriter(text, "a", "b", "c");
        writer.Row("x,y", 1.5, null);
        writer.Row("\"q\"", 2, "z");

        var table = CsvTable.Parse("t.csv", text.ToString());

        table.Rows.Select(r => table.Cell(r.Cells, "a")).ShouldBe(["x,y", "\"q\""]);
        table.Cell(table.Rows[0].Cells, "b").ShouldBe("1.5");
    }

    [Fact]
    public void Rows_are_checked_against_the_catalog_with_file_row_and_object()
    {
        File.WriteAllText(Path.Combine(_folder, ExchangeFormat.Sites), """
            id,code,name,siteType,x,y,lifecycle,backupHours
            s1,S-1,Ett,hub,600000,6900000,,8
            s2,S-2,Två,castle,600000,6900000,,
            s3,S-3,Tre,hub,99000000,6900000,,
            s4,S-1,Fyra,hub,600000,6900000,gone,
            s5,S-5,Fem,hub,,,,
            """);
        File.WriteAllText(Path.Combine(_folder, ExchangeFormat.Hops), """
            circuit,seq,equipment,port,cable,conductor,side,channel
            k1,0,e1,ge-0/0/1,,,,vlan:100
            k1,0,,,c1,1,A,
            k1,1,e1,,,,,
            k1,2,,,c1,1,C,lambda:3
            """);
        var errors = new List<ImportError>();

        var data = ExchangeFormat.Read(_folder, TypeCatalog.Current, errors);

        var messages = errors.Select(e => e.ToString()).ToList();
        messages.ShouldContain(m => m.StartsWith("sites.csv:3 s2: okänd sitetyp 'castle'", StringComparison.Ordinal));
        messages.ShouldContain("sites.csv:4 s3: positionen ligger utanför kartan");
        messages.ShouldContain(m => m.StartsWith("sites.csv:5 s4: lifecycle är en av", StringComparison.Ordinal));
        messages.ShouldContain("sites.csv:5 S-1: code finns redan på rad 2");
        messages.ShouldContain(m => m.StartsWith("sites.csv:6 s5: position anges", StringComparison.Ordinal));
        messages.ShouldContain("circuit-hops.csv:3 k1: seq 0 finns redan på rad 2");
        messages.ShouldContain(m => m.StartsWith("circuit-hops.csv:4 k1: terminalen anges med equipment och port", StringComparison.Ordinal));
        messages.ShouldContain(m => m.StartsWith("circuit-hops.csv:5 k1: channel skrivs som typ:nummer", StringComparison.Ordinal));
        data.Sites.Single(s => s.Id == "s1").Attributes.ShouldBe("""{"backupHours":8}""");
        data.Hops[0].ChannelKind.ShouldBe("vlan");
        data.Hops[0].ChannelNumber.ShouldBe(100);
        data.Files.ShouldBe([ExchangeFormat.Sites, ExchangeFormat.Hops], ignoreOrder: true);
    }

    [Fact]
    public void Lat_lon_become_sweref_and_a_route_is_read_as_wkt()
    {
        File.WriteAllText(Path.Combine(_folder, ExchangeFormat.Sites), "id,code,name,siteType,lat,lon\ns1,S-1,Ett,hub,63.8258,20.2630\n");
        File.WriteAllText(Path.Combine(_folder, ExchangeFormat.Cables),
            "id,code,cableType,a,b,route\nc1,C-1,fiber-12,s1,s2,\"LINESTRING(758800 7088280, 760490 7090010)\"\nc2,C-2,fiber-12,s1,s2,LINESTRING(1 2)\n");
        var errors = new List<ImportError>();

        var data = ExchangeFormat.Read(_folder, TypeCatalog.Current, errors);

        data.Sites.Single().X.ShouldBe(758_803, 1);
        data.Sites.Single().Y.ShouldBe(7_088_281, 1);
        data.Cables[0].Route.ShouldBe([758800, 7088280, 760490, 7090010]);
        errors.Select(e => e.ToString()).ShouldBe(["cables.csv:3 c2: route: '1 2' är ingen punkt i kartan"]);
    }
}
