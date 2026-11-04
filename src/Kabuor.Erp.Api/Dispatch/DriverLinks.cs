// Drivers and the live map. A driver needs no login: when a trip goes out the office sends them a private link
// (/d/{token}) on WhatsApp. On it they see their drops in order, call customers, get directions, confirm each delivery,
// and, while the trip is on the road, their phone shares its location. The dispatch office watches every trip on the
// road on one map: where each vehicle is, whether it's moving, stopped or out of signal, and the trail it took.
// Location is only taken while the trip is out (sent out, not yet completed or cancelled), and only while the driver
// has the link open and sharing switched on: the office sees work trips, never a driver's evening.
using System.Security.Cryptography;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Dispatch;

public sealed record PingInput(double Lat, double Lng, double? Accuracy, double? Speed, double? Heading);
public sealed record DriverPodInput(string? RecipientName, string? PodNotes, DeliveryItemStatus? Status, double? Lat, double? Lng);

public sealed class DriverLinks(ErpDb db, DispatchService dispatch, RequestContext ctx)
{
    /// <summary>Closer together than this, a ping only moves the dot: the trail keeps one point per this many seconds.</summary>
    const int TrailEverySeconds = 20;
    /// <summary>No ping for this long and the map says the phone has gone quiet (no signal, battery, link closed).</summary>
    public const int QuietAfterMinutes = 5;
    /// <summary>Slower than this (m/s, about 4 km/h) counts as stopped: GPS wobbles even when parked.</summary>
    const double MovingSpeed = 1.2;

    static bool OnTheRoad(DispatchTrip t) => t.Status is DispatchStatus.Dispatched or DispatchStatus.Delivered;

    /// <summary>The trip a driver's link opens. Looked up across businesses (the link is the only key the driver has),
    /// then the request works inside that trip's business like any other.</summary>
    public async Task<DispatchTrip> ByToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 24) throw new AppException("This driver link isn't valid. Ask the office to send it again.", 404);
        var trip = await db.DispatchTrips.IgnoreQueryFilters().Include(t => t.Deliveries).SingleOrDefaultAsync(t => t.DriverToken == token)
            ?? throw new AppException("This driver link isn't valid. Ask the office to send it again.", 404);
        (ctx.CompanyId, ctx.Actor) = (trip.CompanyId, $"Driver {trip.DriverName}".Trim());
        return trip;
    }

    /// <summary>The trip's driver link, made the first time it's asked for.</summary>
    public async Task<object> Link(Guid tripId, string origin)
    {
        var trip = await dispatch.GetTrip(tripId);
        if (trip.Status is DispatchStatus.Completed or DispatchStatus.Cancelled) throw new AppException("That trip has ended: its driver link no longer opens.");
        if (trip.DriverToken.Length == 0)
        {
            trip.DriverToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
            await db.SaveChangesAsync();
        }
        return new { url = $"{origin}/d/{trip.DriverToken}" };
    }

    /// <summary>What the driver's phone shows.</summary>
    public async Task<object> DriverView(string token)
    {
        var trip = await ByToken(token);
        var company = await db.Companies.IgnoreQueryFilters().Where(c => c.Id == trip.CompanyId).Select(c => new { c.Name, c.Phone }).SingleAsync();
        var docIds = trip.Deliveries.Where(d => d.SalesDocId != null).Select(d => d.SalesDocId!.Value).ToList();
        var lines = (await db.SalesLines.Where(l => docIds.Contains(l.SalesDocId)).Select(l => new { l.SalesDocId, l.Description, l.Quantity, l.Unit }).ToListAsync())
            .ToLookup(l => l.SalesDocId, l => new { l.Description, l.Quantity, l.Unit });
        return new
        {
            business = company.Name, officePhone = company.Phone,
            trip.TripNumber, trip.RouteName, trip.VehiclePlate, trip.DriverName, Status = trip.Status.ToString(), trip.DispatchedAt,
            sharing = OnTheRoad(trip), trip.LastPingAt,
            drops = trip.Deliveries.OrderBy(d => d.WaybillNumber, StringComparer.Ordinal).Select(d => new
            {
                d.Id, d.WaybillNumber, d.CustomerName, d.DeliveryAddress, d.ContactPhone, Status = d.Status.ToString(), d.RecipientName, d.PodConfirmedAt,
                Items = d.SalesDocId is { } sid ? lines[sid].ToList() : [],
            }),
        };
    }

    /// <summary>A reading from the driver's phone. Taken only while the trip is on the road.</summary>
    public async Task<object> Ping(string token, PingInput p)
    {
        var trip = await ByToken(token);
        if (!OnTheRoad(trip)) throw new AppException(trip.Status == DispatchStatus.Draft ? "The office hasn't sent this trip out yet." : "This trip has ended. Your location is no longer shared.", 409);
        if (double.IsNaN(p.Lat) || double.IsNaN(p.Lng) || p.Lat is < -90 or > 90 || p.Lng is < -180 or > 180) throw new AppException("That location doesn't look right.");
        var now = DateTimeOffset.UtcNow;
        var keepInTrail = trip.LastPingAt is not { } last || (now - last).TotalSeconds >= TrailEverySeconds;
        (trip.LastLat, trip.LastLng, trip.LastAccuracy, trip.LastPingAt) = (p.Lat, p.Lng, Clean(p.Accuracy, 0, 100_000), now);
        trip.LastSpeed = Clean(p.Speed, 0, 90);
        trip.LastHeading = Clean(p.Heading, 0, 360);
        if (keepInTrail) db.DriverPings.Add(new DriverPing { TripId = trip.Id, Lat = p.Lat, Lng = p.Lng, Accuracy = trip.LastAccuracy, Speed = trip.LastSpeed, Heading = trip.LastHeading, At = now });
        await db.SaveChangesAsync();
        return new { sharing = true, at = now };
    }

    static double? Clean(double? v, double min, double max) => v is { } x && !double.IsNaN(x) && x >= min && x <= max ? x : null;

    /// <summary>The driver confirms a drop from their link: same rules as the office's proof of delivery, plus where the
    /// phone was at the time.</summary>
    public async Task<object> Confirm(string token, Guid dropId, DriverPodInput input)
    {
        var trip = await ByToken(token);
        if (trip.Deliveries.All(d => d.Id != dropId)) throw new AppException("That drop isn't on this trip.", 404);
        var drop = await dispatch.ConfirmPod(dropId, new ConfirmPodInput(input.RecipientName ?? "", input.PodNotes, input.Status));
        if (input.Lat is { } lat && input.Lng is { } lng && lat is >= -90 and <= 90 && lng is >= -180 and <= 180)
        {
            (drop.PodLat, drop.PodLng) = (lat, lng);
            await db.SaveChangesAsync();
        }
        return new { drop.Id, Status = drop.Status.ToString(), drop.PodConfirmedAt };
    }

    /// <summary>The dispatch office's map: every trip on the road, where it is and how it's doing, with its trail.</summary>
    public async Task<object> Live()
    {
        var now = DateTimeOffset.UtcNow;
        var trips = await db.DispatchTrips.Include(t => t.Deliveries)
            .Where(t => t.Status == DispatchStatus.Dispatched || t.Status == DispatchStatus.Delivered)
            .ToListAsync();
        var ids = trips.Select(t => t.Id).ToList();
        var since = now.AddHours(-12);
        // The last 12 hours of each trail (filtered here: SQLite can't compare dates with offsets).
        var pings = (await db.DriverPings.Where(p => ids.Contains(p.TripId)).Select(p => new { p.TripId, p.Lat, p.Lng, p.At }).ToListAsync())
            .Where(p => p.At >= since).ToLookup(p => p.TripId);
        var rows = trips.Select(t =>
        {
            var quietFor = t.LastPingAt is { } at ? (now - at).TotalMinutes : (double?)null;
            var state = t.LastPingAt is null ? "waiting" : quietFor > QuietAfterMinutes ? "quiet" : (t.LastSpeed ?? 0) >= MovingSpeed ? "moving" : "stopped";
            // Stopped since: just after the last trail point more than ~40 m from where it is now (or, if it hasn't
            // moved since it started sharing, the first point).
            DateTimeOffset? stoppedSince = null;
            if (state == "stopped" && t.LastLat is { } la && t.LastLng is { } lo)
            {
                var away = pings[t.Id].OrderByDescending(p => p.At).FirstOrDefault(p => Metres(p.Lat, p.Lng, la, lo) > 40);
                stoppedSince = pings[t.Id].Where(p => away is null || p.At > away.At).Select(p => (DateTimeOffset?)p.At).Min() ?? t.LastPingAt;
            }
            var drops = t.Deliveries.OrderBy(d => d.WaybillNumber, StringComparer.Ordinal).ToList();
            var next = drops.FirstOrDefault(d => d.Status is DeliveryItemStatus.Pending or DeliveryItemStatus.Dispatched);
            return new
            {
                t.Id, t.TripNumber, t.VehiclePlate, t.DriverName, t.DriverPhone, t.RouteName, Status = t.Status.ToString(), t.DispatchedAt,
                State = state, t.LastLat, t.LastLng, t.LastSpeed, t.LastHeading, t.LastAccuracy, t.LastPingAt, StoppedSince = stoppedSince,
                LinkSent = t.DriverToken.Length > 0,
                Done = drops.Count(d => d.Status is not (DeliveryItemStatus.Pending or DeliveryItemStatus.Dispatched)), Drops = drops.Count,
                Next = next is null ? null : new { next.CustomerName, next.DeliveryAddress, next.ContactPhone, next.WaybillNumber },
                Confirmed = drops.Where(d => d.PodLat != null).Select(d => new { d.WaybillNumber, d.CustomerName, Status = d.Status.ToString(), Lat = d.PodLat, Lng = d.PodLng, d.PodConfirmedAt }),
                Trail = Thin(pings[t.Id].OrderBy(p => p.At).Select(p => new[] { Math.Round(p.Lat, 6), Math.Round(p.Lng, 6) }).ToList(), 400),
            };
        })
        // Who needs a look first: quiet phones, then stopped, then moving, then not started sharing.
        .OrderBy(r => r.State switch { "quiet" => 0, "stopped" => 1, "moving" => 2, _ => 3 }).ThenBy(r => r.VehiclePlate).ToList();
        return new
        {
            counts = new { onRoad = rows.Count, moving = rows.Count(r => r.State == "moving"), stopped = rows.Count(r => r.State == "stopped"), quiet = rows.Count(r => r.State == "quiet"), waiting = rows.Count(r => r.State == "waiting") },
            quietAfterMinutes = QuietAfterMinutes,
            trips = rows,
            at = now,
        };
    }

    static List<double[]> Thin(List<double[]> points, int max)
    {
        if (points.Count <= max) return points;
        var step = (double)points.Count / max;
        var kept = Enumerable.Range(0, max).Select(i => points[(int)(i * step)]).ToList();
        kept[^1] = points[^1];
        return kept;
    }

    /// <summary>Distance on the ground between two points, in metres (haversine).</summary>
    public static double Metres(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6_371_000;
        static double Rad(double d) => d * Math.PI / 180;
        var a = Math.Pow(Math.Sin(Rad(lat2 - lat1) / 2), 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(Rad(lng2 - lng1) / 2), 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }
}
