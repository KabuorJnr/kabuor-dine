// Fleet and dispatch. The dispatch office (and anyone who can see the fleet) sees vehicles, trips, waybills and the
// live map; planning trips, sending them out, driver links, proof of delivery and closing trips need the fleet role
// (dispatchers, storekeepers, procurement, accountants, owners). Drivers use their trip's private link, no login.
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Dispatch;

namespace Kabuor.Erp;

public static class DispatchEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var dispatch = api.MapGroup("/dispatch").RequireAuthorization(Policies.FleetView);

        dispatch.MapGet("/trips", async (DispatchService svc, string? status) =>
            await svc.ListTrips(status));

        dispatch.MapGet("/counts", (DispatchService svc) => svc.Counts());
        dispatch.MapGet("/fleet", (DispatchService svc) => svc.Fleet());
        dispatch.MapGet("/to-deliver", (DispatchService svc) => svc.ToDeliver());
        dispatch.MapGet("/live", (DriverLinks s) => s.Live());

        dispatch.MapGet("/trips/{id:guid}", async (Guid id, DispatchService svc) =>
            await svc.TripDetail(id));

        dispatch.MapPost("/trips", async (CreateTripInput input, DispatchService svc) =>
            Results.Created("", await svc.CreateTrip(input)))
            .RequireAuthorization(Policies.Fleet);

        dispatch.MapPost("/trips/{id:guid}/dispatch", async (Guid id, DispatchService svc) =>
            await svc.Dispatch(id))
            .RequireAuthorization(Policies.Fleet);

        dispatch.MapPost("/trips/{id:guid}/driver-link", (Guid id, DriverLinks s, HttpRequest req) => s.Link(id, PublicUrl.Of(req)))
            .RequireAuthorization(Policies.Fleet);

        dispatch.MapPost("/deliveries/{deliveryId:guid}/pod", async (Guid deliveryId, ConfirmPodInput input, DispatchService svc) =>
            await svc.ConfirmPod(deliveryId, input))
            .RequireAuthorization(Policies.Fleet);

        dispatch.MapPost("/trips/{id:guid}/complete", async (Guid id, DispatchService svc) =>
            await svc.CompleteTrip(id))
            .RequireAuthorization(Policies.Fleet);

        dispatch.MapPost("/trips/{id:guid}/cancel", async (Guid id, DispatchService svc) =>
            await svc.CancelTrip(id))
            .RequireAuthorization(Policies.Fleet);
    }

    /// <summary>The driver's link. No sign-in: the secret in the address opens one trip, and only while it lasts.</summary>
    public static void MapDriverLinks(RouteGroupBuilder api)
    {
        var drive = api.MapGroup("/drive/{token}").RequireRateLimiting("drive");
        drive.MapGet("", (string token, DriverLinks s) => s.DriverView(token));
        drive.MapPost("/ping", (string token, PingInput input, DriverLinks s) => s.Ping(token, input));
        drive.MapPost("/drops/{dropId:guid}", (string token, Guid dropId, DriverPodInput input, DriverLinks s) => s.Confirm(token, dropId, input));
    }
}
