using System.Diagnostics;
using System.Globalization;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Npgsql;

// Synthetic network generator. See README ("Syntetisk data") and issue #6.
//   dotnet run --project src/datagen -- --scale full --seed 1 --reset
var seed = 1;
var scale = Scale.Small;
var reset = false;
var dryRun = false;
var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Cmdb")
    ?? "Host=127.0.0.1;Port=15432;Database=cmdb;Username=cmdb";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed":
            seed = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--scale":
            scale = Scale.Parse(args[++i]);
            break;
        case "--connection":
            connection = args[++i];
            break;
        case "--reset":
            reset = true;
            break;
        case "--dry-run":
            dryRun = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'. Usage: --scale small|medium|full --seed N [--reset] [--dry-run] [--connection CS]");
            return 1;
    }
}

var total = Stopwatch.StartNew();
Console.WriteLine($"Generating {scale.Name} network, seed {seed}");
var network = NetworkBuilder.Build(seed, scale, TypeCatalog.Embedded);
Console.WriteLine($"Generated in {total.Elapsed.TotalSeconds:0.0} s");
foreach (var (name, count) in network.Counts())
{
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {name,-14} {count,12:N0}"));
}

if (dryRun)
{
    Console.WriteLine("Sites per type:");
    foreach (var g in network.Sites.GroupBy(s => s.SiteType).OrderByDescending(g => g.Count()))
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {g.Key,-14} {g.Count(),12:N0}"));
    }
    Console.WriteLine("Equipment per type:");
    foreach (var g in network.Equipment.GroupBy(e => e.Type.Key).OrderByDescending(g => g.Count()))
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {g.Key,-14} {g.Count(),12:N0}"));
    }
    Console.WriteLine($"Fingerprint {Fingerprint.Of(network)}");
    return 0;
}

var load = Stopwatch.StartNew();
await using var db = NpgsqlDataSource.Create(connection);
await Loader.LoadAsync(db, network, reset, Console.Out);
Console.WriteLine($"Loaded in {load.Elapsed.TotalSeconds:0.0} s, total {total.Elapsed.TotalSeconds:0.0} s");
return 0;
