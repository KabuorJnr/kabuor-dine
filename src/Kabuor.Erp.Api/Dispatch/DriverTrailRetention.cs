// Drivers' location points are personal data, and the privacy notice says how long they're kept: the trail of a trip
// (DriverPing) for 90 days, and a trip's last-known position until 90 days after its last ping once it's off the road.
// Runs a few minutes after start and then every 6 hours, across every business. Proof-of-delivery locations stay with
// the delivery record, which is the business's own.
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Dispatch;

public sealed class DriverTrailRetention(IServiceScopeFactory scopes, IConfiguration config, ILogger<DriverTrailRetention> log) : BackgroundService
{
    public const int KeepDays = 90;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (config.GetValue("Retention:Disabled", false)) return; // tests and one-off tools
        await Task.Delay(TimeSpan.FromMinutes(2), stop);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var (pings, trips) = await Prune(scope.ServiceProvider.GetRequiredService<ErpDb>(), DateTimeOffset.UtcNow);
                if (pings + trips > 0) log.LogInformation("Driver locations: removed {Pings} old trail points and cleared {Trips} finished trips' last position", pings, trips);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Driver location clean-up failed");
            }
            await Task.Delay(TimeSpan.FromHours(6), stop);
        }
    }

    /// <summary>Deletes trail points older than <see cref="KeepDays"/> and forgets the last position of trips that left
    /// the road that long ago. Returns how many of each.</summary>
    public static async Task<(int Pings, int Trips)> Prune(ErpDb db, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-KeepDays);
        int pings;
        if (db.Database.IsNpgsql())
            pings = await db.DriverPings.IgnoreQueryFilters().Where(p => p.At < cutoff).ExecuteDeleteAsync();
        else
        {
            // SQLite can't compare DateTimeOffset in a query: pick them out here (only tests and local runs use it).
            var old = (await db.DriverPings.IgnoreQueryFilters().Select(p => new { p.Id, p.At }).ToListAsync()).Where(p => p.At < cutoff).Select(p => p.Id).ToList();
            pings = 0;
            foreach (var chunk in old.Chunk(500)) pings += await db.DriverPings.IgnoreQueryFilters().Where(p => chunk.Contains(p.Id)).ExecuteDeleteAsync();
        }

        var offRoad = await db.DispatchTrips.IgnoreQueryFilters()
            .Where(t => t.LastPingAt != null && t.Status != DispatchStatus.Dispatched && t.Status != DispatchStatus.Delivered)
            .Select(t => new { t.Id, t.LastPingAt }).ToListAsync();
        var stale = offRoad.Where(t => t.LastPingAt < cutoff).Select(t => t.Id).ToList();
        var trips = 0;
        foreach (var chunk in stale.Chunk(500))
            trips += await db.DispatchTrips.IgnoreQueryFilters().Where(t => chunk.Contains(t.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.LastLat, (double?)null).SetProperty(t => t.LastLng, (double?)null)
                .SetProperty(t => t.LastSpeed, (double?)null).SetProperty(t => t.LastHeading, (double?)null)
                .SetProperty(t => t.LastAccuracy, (double?)null).SetProperty(t => t.LastPingAt, (DateTimeOffset?)null));
        return (pings, trips);
    }
}
