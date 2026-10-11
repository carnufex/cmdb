namespace Cmdb.Api.IntegrationTests;

/// <summary>
/// The performance budget (docs/plan.md) is for warm calls on a machine doing nothing else. Here the whole suite runs in
/// parallel on one machine, and other tests load graphs and networks while a timed call runs (#273, #280, #282), so a
/// budget is checked on the fastest of a few warm calls: still a real call through the API, never a cached answer.
/// </summary>
internal static class Budget
{
    /// <summary>Calls once to warm up, then <paramref name="calls"/> times; returns the last answer and the fastest time.</summary>
    public static async Task<(T Answer, double FastestMs)> WarmAsync<T>(Func<Task<T>> call, Func<T, double> elapsedMs, int calls = 3)
    {
        var answer = await call();
        var fastest = double.MaxValue;
        for (var i = 0; i < calls; i++)
        {
            answer = await call();
            fastest = Math.Min(fastest, elapsedMs(answer));
        }
        return (answer, fastest);
    }
}
