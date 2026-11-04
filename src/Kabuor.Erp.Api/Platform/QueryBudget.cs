// How many database round trips each request makes. Against a database in another region (Neon in Frankfurt, people
// in Kenya) every round trip costs tens of milliseconds, so a page that asks fifty small questions is slow however
// fast each one is. Every request is counted; one over the budget is logged with its route so it can be fixed, and in
// development (or with Diagnostics:ServerTiming) the count and time go back in a Server-Timing header, where the
// browser's network panel shows them.
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Kabuor.Erp.Platform;

public sealed class QueryStats
{
    public int Count;
    public long Ticks;
}

public sealed class QueryCounter : DbCommandInterceptor
{
    static readonly AsyncLocal<QueryStats?> Current = new();

    public static QueryStats Begin() => Current.Value = new QueryStats();
    public static void End() => Current.Value = null;

    static void Count(CommandExecutedEventData e)
    {
        if (Current.Value is not { } s) return;
        Interlocked.Increment(ref s.Count);
        Interlocked.Add(ref s.Ticks, e.Duration.Ticks);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result) { Count(eventData); return result; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default) { Count(eventData); return ValueTask.FromResult(result); }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result) { Count(eventData); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default) { Count(eventData); return ValueTask.FromResult(result); }
    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result) { Count(eventData); return result; }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default) { Count(eventData); return ValueTask.FromResult(result); }

    /// <summary>Counts the request's queries; logs the ones over Diagnostics:QueryBudget (default 40).</summary>
    public static Func<HttpContext, Func<Task>, Task> Middleware(IConfiguration config, IHostEnvironment env, ILogger log)
    {
        var budget = config.GetValue("Diagnostics:QueryBudget", 40);
        var header = env.IsDevelopment() || config.GetValue("Diagnostics:ServerTiming", false);
        return async (http, next) =>
        {
            if (!(http.Request.Path.StartsWithSegments("/api")) || http.Request.Path.StartsWithSegments("/api/live"))
            {
                await next();
                return;
            }
            var stats = Begin();
            var clock = Stopwatch.StartNew();
            if (header)
                http.Response.OnStarting(() =>
                {
                    http.Response.Headers["Server-Timing"] = $"db;desc=\"{stats.Count} queries\";dur={TimeSpan.FromTicks(stats.Ticks).TotalMilliseconds:0.0}, app;dur={clock.Elapsed.TotalMilliseconds:0.0}";
                    return Task.CompletedTask;
                });
            try { await next(); }
            finally
            {
                if (stats.Count > budget)
                    log.LogWarning("{Method} {Route} made {Queries} database queries ({DbMs:0} ms in the database, {Ms:0} ms in all); the budget is {Budget}",
                        http.Request.Method, (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value, stats.Count,
                        TimeSpan.FromTicks(stats.Ticks).TotalMilliseconds, clock.Elapsed.TotalMilliseconds, budget);
                End();
            }
        };
    }
}
