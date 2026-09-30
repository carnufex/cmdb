using BenchmarkDotNet.Running;

// dotnet run -c Release --project tests/Cmdb.Graph.Benchmarks -- --filter '*' [--job short]
// dotnet run -c Release --project tests/Cmdb.Graph.Benchmarks -- scale full 2x 4x
if (args is ["scale", .. var scales])
{
    Cmdb.Graph.Benchmarks.ScaleReport.Run(scales);
    return;
}
BenchmarkSwitcher.FromAssembly(typeof(Cmdb.Graph.Benchmarks.GraphBenchmarks).Assembly).Run(args);
