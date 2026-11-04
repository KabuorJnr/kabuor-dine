using Kabuor.Erp.Common;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Data;

/// <summary>
/// Runs a screen's independent reads at the same time. The database is a few hundred milliseconds away, so a page
/// that reads ten things one after another waits for all ten; side by side it waits about as long as the slowest.
/// On PostgreSQL each read gets its own pooled connection (same company, same filters). SQLite (local files and the
/// tests) shares the request's context, one read at a time.
/// </summary>
public sealed class SideBySide(ErpDb db, RequestContext ctx, DbContextOptions<ErpDb> options)
{
    readonly SemaphoreSlim _shared = new(1, 1);

    /// <summary>True when filters can run in the database. SQLite can't compare DateTimeOffsets, so there the
    /// rows come back and are filtered in memory.</summary>
    public bool Postgres => db.Database.IsNpgsql();

    public async Task<T> Read<T>(Func<ErpDb, Task<T>> query)
    {
        if (Postgres)
        {
            await using var own = new ErpDb(options, ctx);
            return await query(own);
        }
        await _shared.WaitAsync();
        try { return await query(db); }
        finally { _shared.Release(); }
    }
}
