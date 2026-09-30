namespace Cmdb.Cli;

internal static class Entry
{
    public static Task<int> Main(string[] args) => CliApp.RunAsync(args, Console.Out, Console.Error);
}
