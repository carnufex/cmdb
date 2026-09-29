using BenchmarkDotNet.Running;

// dotnet run -c Release --project tests/Cmdb.Graph.Benchmarks -- --filter '*' [--job short]
BenchmarkSwitcher.FromAssembly(typeof(Cmdb.Graph.Benchmarks.GraphBenchmarks).Assembly).Run(args);
