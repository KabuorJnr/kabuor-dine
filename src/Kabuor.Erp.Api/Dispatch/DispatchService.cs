// Fleet and dispatch: the business's vehicles (assets in the Vehicles category), delivery trips that carry issued
// invoices to customers, a waybill for every drop, and proof of delivery (who received it, or why it failed).
// A vehicle that is under repair, lost or already out on a trip can't be sent on another one.
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Dispatch;

public sealed record DeliveryItemInput(
    Guid? SalesDocId,
    string? CustomerName,
    string? DeliveryAddress,
    string? ContactPhone
);

public sealed record CreateTripInput(
    string? VehiclePlate,
    string DriverName,
    string? DriverPhone,
    string RouteName,
    string? Notes,
    List<DeliveryItemInput> Deliveries,
    Guid? VehicleId = null
);

public sealed record ConfirmPodInput(
    string RecipientName,
    string? PodNotes,
    DeliveryItemStatus? Status = null
);

public sealed class DispatchService(ErpDb db, Numbers numbers)
{
    static readonly DispatchStatus[] Open = [DispatchStatus.Draft, DispatchStatus.Dispatched, DispatchStatus.Delivered];

    public async Task<List<object>> ListTrips(string? status)
    {
        var trips = await db.DispatchTrips.Include(t => t.Deliveries).ToListAsync();
        var shown = string.IsNullOrWhiteSpace(status) || status == "all" ? trips
            : status == "open" ? trips.Where(t => Open.Contains(t.Status)).ToList()
            : Enum.TryParse<DispatchStatus>(status.Trim(), true, out var s) ? trips.Where(t => t.Status == s).ToList() : [];
        return shown.OrderByDescending(t => t.CreatedAt).Select(t => (object)new
        {
            t.Id, t.TripNumber, t.VehicleId, t.VehiclePlate, t.DriverName, t.DriverPhone, t.RouteName,
            Status = t.Status.ToString(), t.DispatchedAt, t.DeliveredAt, t.CreatedAt,
            DeliveriesCount = t.Deliveries.Count,
            DeliveredCount = t.Deliveries.Count(d => d.Status == DeliveryItemStatus.Delivered),
            FailedCount = t.Deliveries.Count(d => d.Status is DeliveryItemStatus.Failed or DeliveryItemStatus.PartialReturn),
            PendingCount = t.Deliveries.Count(d => d.Status is DeliveryItemStatus.Pending or DeliveryItemStatus.Dispatched),
        }).ToList();
    }

    /// <summary>How many trips are in each state, for the filter chips.</summary>
    public async Task<object> Counts()
    {
        var statuses = await db.DispatchTrips.Select(t => t.Status).ToListAsync();
        return new
        {
            all = statuses.Count,
            open = statuses.Count(Open.Contains),
            draft = statuses.Count(s => s == DispatchStatus.Draft),
            dispatched = statuses.Count(s => s == DispatchStatus.Dispatched),
            delivered = statuses.Count(s => s == DispatchStatus.Delivered),
            completed = statuses.Count(s => s == DispatchStatus.Completed),
            cancelled = statuses.Count(s => s == DispatchStatus.Cancelled),
        };
    }

    public async Task<DispatchTrip> GetTrip(Guid id)
    {
        var trip = await db.DispatchTrips
            .Include(t => t.Deliveries)
            .SingleOrDefaultAsync(t => t.Id == id)
            ?? throw new AppException("Dispatch trip not found.", 404);
        trip.Deliveries = trip.Deliveries.OrderBy(d => d.WaybillNumber, StringComparer.Ordinal).ToList();
        return trip;
    }

    /// <summary>The trip with what a waybill needs: each drop's invoice number and total, and the goods on it so the
    /// customer can check them off as they sign.</summary>
    public async Task<object> TripDetail(Guid id)
    {
        var trip = await GetTrip(id);
        var docIds = trip.Deliveries.Where(d => d.SalesDocId != null).Select(d => d.SalesDocId!.Value).ToList();
        var docs = await db.SalesDocs.Where(d => docIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => new { d.Number, d.Total });
        var lines = (await db.SalesLines.Where(l => docIds.Contains(l.SalesDocId)).Select(l => new { l.SalesDocId, l.Description, l.Quantity, l.Unit }).ToListAsync())
            .ToLookup(l => l.SalesDocId, l => new { l.Description, l.Quantity, l.Unit });
        return new
        {
            trip.Id, trip.TripNumber, trip.VehicleId, trip.VehiclePlate, trip.DriverName, trip.DriverPhone, trip.RouteName, trip.Notes,
            Status = trip.Status.ToString(), trip.DispatchedAt, trip.DeliveredAt, trip.CreatedAt,
            Deliveries = trip.Deliveries.Select(d => new
            {
                d.Id, d.TripId, d.SalesDocId, d.WaybillNumber, d.CustomerName, d.DeliveryAddress, d.ContactPhone,
                Status = d.Status.ToString(), d.RecipientName, d.PodNotes, d.PodConfirmedAt,
                InvoiceNumber = d.SalesDocId is { } sid && docs.TryGetValue(sid, out var doc) ? doc.Number : null,
                InvoiceTotal = d.SalesDocId is { } sid2 && docs.TryGetValue(sid2, out var doc2) ? doc2.Total : (long?)null,
                Items = d.SalesDocId is { } sid3 ? lines[sid3].ToList() : [],
            }),
        };
    }

    /// <summary>The fleet: every vehicle on the asset register, whether it's free, out on a trip or off the road,
    /// and how much it has been used this month.</summary>
    public async Task<object> Fleet()
    {
        var vehicles = await db.Assets.Where(a => a.Category == "VEH" && a.Status != AssetStatus.Disposed).ToListAsync();
        var trips = await db.DispatchTrips.Include(t => t.Deliveries).Where(t => t.VehicleId != null).ToListAsync();
        var monthStart = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = vehicles.Select(v =>
        {
            var mine = trips.Where(t => t.VehicleId == v.Id).ToList();
            var current = mine.Where(t => t.Status is DispatchStatus.Dispatched or DispatchStatus.Delivered).OrderByDescending(t => t.DispatchedAt).FirstOrDefault();
            var state = v.Status is AssetStatus.UnderRepair ? "repair" : v.Status is AssetStatus.Lost ? "lost" : current is not null ? "out" : "available";
            return new
            {
                v.Id, v.Tag, v.Name, Plate = Plate(v), State = state, Condition = v.Condition.ToString(), v.AssignedTo,
                CurrentTrip = current is null ? null : new { current.Id, current.TripNumber, current.DriverName, current.RouteName, current.DispatchedAt, Drops = current.Deliveries.Count, Done = current.Deliveries.Count(d => d.Status is not (DeliveryItemStatus.Pending or DeliveryItemStatus.Dispatched)) },
                TripsThisMonth = mine.Count(t => t.Status != DispatchStatus.Cancelled && t.CreatedAt >= monthStart),
                DropsThisMonth = mine.Where(t => t.Status != DispatchStatus.Cancelled && t.CreatedAt >= monthStart).Sum(t => t.Deliveries.Count(d => d.Status == DeliveryItemStatus.Delivered)),
                LastTripAt = mine.Where(t => t.DispatchedAt != null).Select(t => t.DispatchedAt).OrderByDescending(x => x).FirstOrDefault(),
            };
        }).OrderBy(v => v.State switch { "out" => 0, "available" => 1, "repair" => 2, _ => 3 }).ThenBy(v => v.Plate).ToList();
        return new
        {
            counts = new { all = rows.Count, available = rows.Count(r => r.State == "available"), @out = rows.Count(r => r.State == "out"), repair = rows.Count(r => r.State is "repair" or "lost") },
            vehicles = rows,
        };
    }

    /// <summary>A vehicle's number plate: its serial number on the register (where plates are kept), else its name.</summary>
    static string Plate(Asset v) => (v.SerialNumber.Length > 0 ? v.SerialNumber : v.Name).ToUpperInvariant();

    /// <summary>Issued invoices from the last 60 days that aren't on a trip yet (or came back undelivered): what still has to go out.</summary>
    public async Task<object> ToDeliver()
    {
        var since = Clock.Today.AddDays(-60);
        // On a trip that wasn't cancelled, unless it came back undelivered: then it has to go out again.
        var onTrips = await db.DeliveryItems.Where(d => d.SalesDocId != null && d.Status != DeliveryItemStatus.Failed)
            .Join(db.DispatchTrips.Where(t => t.Status != DispatchStatus.Cancelled), d => d.TripId, t => t.Id, (d, _) => d.SalesDocId!.Value).ToListAsync();
        var taken = onTrips.ToHashSet();
        var docs = await db.SalesDocs.Include(d => d.Customer)
            .Where(d => d.Kind == SalesDocKind.Invoice && (d.Status == SalesDocStatus.Sent || d.Status == SalesDocStatus.PartPaid || d.Status == SalesDocStatus.Paid) && d.IssueDate >= since)
            .ToListAsync();
        return docs.Where(d => !taken.Contains(d.Id)).OrderBy(d => d.IssueDate).ThenBy(d => d.Number)
            .Select(d => new { d.Id, d.Number, d.IssueDate, d.Total, Customer = d.Customer?.Name ?? "", Phone = d.Customer?.Phone ?? "", Address = d.Customer?.Address ?? "" });
    }

    public async Task<DispatchTrip> CreateTrip(CreateTripInput input)
    {
        var driver = (input.DriverName ?? "").Trim();
        var route = (input.RouteName ?? "").Trim();
        Asset? vehicle = null;
        if (input.VehicleId is { } vid)
        {
            vehicle = await db.Assets.SingleOrDefaultAsync(a => a.Id == vid && a.Category == "VEH") ?? throw new AppException("That vehicle isn't on the asset register.");
            if (vehicle.Status is AssetStatus.UnderRepair or AssetStatus.Lost or AssetStatus.Disposed)
                throw new AppException($"{Plate(vehicle)} is {(vehicle.Status == AssetStatus.UnderRepair ? "under repair" : vehicle.Status == AssetStatus.Lost ? "marked lost" : "disposed of")}. Choose another vehicle.");
        }
        var plate = vehicle is not null ? Plate(vehicle) : (input.VehiclePlate ?? "").Trim().ToUpperInvariant();

        if (plate.Length < 3) throw new AppException("Choose a vehicle, or enter its number plate.");
        if (driver.Length < 2) throw new AppException("Enter the driver's name.");
        if (route.Length < 2) throw new AppException("Enter the delivery route / destination.");
        if (input.Deliveries is not { Count: > 0 }) throw new AppException("Add at least one delivery drop to the trip.");
        if (input.Deliveries.Count > 100) throw new AppException("A dispatch trip can have at most 100 delivery drops.");

        var trip = new DispatchTrip
        {
            TripNumber = await numbers.Next("TRIP"),
            VehicleId = vehicle?.Id,
            VehiclePlate = plate,
            DriverName = driver,
            DriverPhone = Phones.ToMsisdn(input.DriverPhone) ?? (input.DriverPhone ?? "").Trim(),
            RouteName = route,
            Notes = (input.Notes ?? "").Trim(),
            Status = DispatchStatus.Draft
        };

        var salesDocIds = input.Deliveries.Where(d => d.SalesDocId != null).Select(d => d.SalesDocId!.Value).ToList();
        if (salesDocIds.Count != salesDocIds.Distinct().Count()) throw new AppException("The same invoice is on the trip twice.");
        var salesDocs = await db.SalesDocs.Include(s => s.Customer).Where(s => salesDocIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        // Goods go out against an issued invoice: not a quote, a draft or a voided one, and not one already on its way.
        foreach (var id in salesDocIds)
        {
            var doc = salesDocs.GetValueOrDefault(id) ?? throw new AppException("One of the invoices on this trip wasn't found.", 404);
            if (doc.Kind != SalesDocKind.Invoice || doc.Status is SalesDocStatus.Draft or SalesDocStatus.Void)
                throw new AppException($"{doc.Number} isn't an issued invoice, so it can't go on a trip.");
        }
        var already = await db.DeliveryItems.Where(d => d.SalesDocId != null && salesDocIds.Contains(d.SalesDocId.Value) && d.Status != DeliveryItemStatus.Failed)
            .Join(db.DispatchTrips.Where(t => t.Status != DispatchStatus.Cancelled), d => d.TripId, t => t.Id, (d, t) => new { d.SalesDocId, t.TripNumber }).FirstOrDefaultAsync();
        if (already is not null) throw new AppException($"{salesDocs[already.SalesDocId!.Value].Number} is already on trip {already.TripNumber}.");

        foreach (var drop in input.Deliveries)
        {
            SalesDoc? doc = drop.SalesDocId is { } sid ? salesDocs[sid] : null;
            var custName = !string.IsNullOrWhiteSpace(drop.CustomerName)
                ? drop.CustomerName.Trim()
                : doc?.Customer?.Name ?? "Walk-in";
            var phone = !string.IsNullOrWhiteSpace(drop.ContactPhone)
                ? (Phones.ToMsisdn(drop.ContactPhone) ?? drop.ContactPhone.Trim())
                : doc?.Customer?.Phone ?? "";
            var address = !string.IsNullOrWhiteSpace(drop.DeliveryAddress)
                ? drop.DeliveryAddress.Trim()
                : doc?.Customer?.Address is { Length: > 0 } a ? a : route;

            var waybill = await numbers.Next("WB");
            trip.Deliveries.Add(new DeliveryItem
            {
                TripId = trip.Id,
                SalesDocId = doc?.Id,
                WaybillNumber = waybill,
                CustomerName = custName,
                DeliveryAddress = address,
                ContactPhone = phone,
                Status = DeliveryItemStatus.Pending
            });
        }

        db.DispatchTrips.Add(trip);
        await db.SaveChangesAsync();
        return trip;
    }

    public async Task<DispatchTrip> Dispatch(Guid tripId)
    {
        var trip = await GetTrip(tripId);
        if (trip.Status != DispatchStatus.Draft) throw new AppException("Only draft trips can be dispatched.");
        if (trip.Deliveries.Count == 0) throw new AppException("Trip has no deliveries assigned.");
        if (trip.VehicleId is { } vid)
        {
            var vehicle = await db.Assets.SingleOrDefaultAsync(a => a.Id == vid);
            if (vehicle?.Status is AssetStatus.UnderRepair or AssetStatus.Lost or AssetStatus.Disposed)
                throw new AppException($"{trip.VehiclePlate} can't go out: it's {(vehicle.Status == AssetStatus.UnderRepair ? "under repair" : "off the road")}.");
            var busy = await db.DispatchTrips.Where(t => t.VehicleId == vid && t.Id != trip.Id && (t.Status == DispatchStatus.Dispatched || t.Status == DispatchStatus.Delivered))
                .Select(t => t.TripNumber).FirstOrDefaultAsync();
            if (busy is not null) throw new AppException($"{trip.VehiclePlate} is still out on {busy}. Complete that trip first.");
        }

        trip.Status = DispatchStatus.Dispatched;
        trip.DispatchedAt = DateTimeOffset.UtcNow;
        foreach (var delivery in trip.Deliveries)
        {
            if (delivery.Status == DeliveryItemStatus.Pending)
                delivery.Status = DeliveryItemStatus.Dispatched;
        }

        await db.SaveChangesAsync();
        return trip;
    }

    public async Task<DeliveryItem> ConfirmPod(Guid deliveryId, ConfirmPodInput input)
    {
        var recipient = (input.RecipientName ?? "").Trim();
        var status = input.Status ?? DeliveryItemStatus.Delivered;
        if (status is DeliveryItemStatus.Pending or DeliveryItemStatus.Dispatched)
            throw new AppException("Proof of delivery status must be Delivered, PartialReturn, or Failed.");
        // Who signed for it; or, when nobody could, why not.
        if (status != DeliveryItemStatus.Failed && recipient.Length < 2) throw new AppException("Enter recipient name for Proof of Delivery (e-POD).");
        if (status == DeliveryItemStatus.Failed && recipient.Length < 2 && (input.PodNotes ?? "").Trim().Length < 3) throw new AppException("Say why it couldn't be delivered.");

        var delivery = await db.DeliveryItems.SingleOrDefaultAsync(d => d.Id == deliveryId)
            ?? throw new AppException("Delivery item not found.", 404);

        var trip = await db.DispatchTrips.Include(t => t.Deliveries).SingleAsync(t => t.Id == delivery.TripId);
        if (trip.Status == DispatchStatus.Draft) throw new AppException("Trip has not been dispatched yet.");
        if (trip.Status == DispatchStatus.Completed) throw new AppException("Trip is already completed.");
        if (trip.Status == DispatchStatus.Cancelled) throw new AppException("That trip was cancelled.");

        delivery.RecipientName = recipient;
        delivery.PodNotes = (input.PodNotes ?? "").Trim();
        delivery.Status = status;
        delivery.PodConfirmedAt = DateTimeOffset.UtcNow;

        // If all deliveries are resolved, mark trip as Delivered
        if (trip.Deliveries.All(d => d.Status is DeliveryItemStatus.Delivered or DeliveryItemStatus.PartialReturn or DeliveryItemStatus.Failed))
        {
            trip.Status = DispatchStatus.Delivered;
            trip.DeliveredAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return delivery;
    }

    public async Task<DispatchTrip> CompleteTrip(Guid tripId)
    {
        var trip = await GetTrip(tripId);
        if (trip.Status == DispatchStatus.Draft) throw new AppException("Trip must be dispatched before completing.");
        if (trip.Status == DispatchStatus.Completed) throw new AppException("Trip already completed.");
        if (trip.Status == DispatchStatus.Cancelled) throw new AppException("That trip was cancelled.");
        // Back at the yard: drops nobody signed for count as failed (brought back), so nothing stays "on the road".
        foreach (var d in trip.Deliveries.Where(d => d.Status is DeliveryItemStatus.Pending or DeliveryItemStatus.Dispatched))
            (d.Status, d.PodNotes, d.PodConfirmedAt) = (DeliveryItemStatus.Failed, "Brought back: not delivered on this trip.", DateTimeOffset.UtcNow);
        trip.Status = DispatchStatus.Completed;
        trip.DeliveredAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return trip;
    }

    /// <summary>Called off before anything was delivered: the invoices can go on another trip.</summary>
    public async Task<DispatchTrip> CancelTrip(Guid tripId)
    {
        var trip = await GetTrip(tripId);
        if (trip.Status is DispatchStatus.Completed or DispatchStatus.Cancelled) throw new AppException($"That trip is already {trip.Status.ToString().ToLowerInvariant()}.");
        if (trip.Deliveries.Any(d => d.Status is DeliveryItemStatus.Delivered or DeliveryItemStatus.PartialReturn))
            throw new AppException("Some drops on this trip were delivered. Complete it instead.");
        trip.Status = DispatchStatus.Cancelled;
        await db.SaveChangesAsync();
        return trip;
    }
}
