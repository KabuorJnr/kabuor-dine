using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
// The dispatch office and drivers' links: a Dispatcher works the fleet and nothing else; a driver's link opens one
// trip, shares location only while the trip is on the road, confirms its own drops, and stops working when it ends.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class DriverLinkTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    async Task<HttpClient> Owner()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Lake Haulage", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    /// <summary>An issued invoice on a trip with two drops; returns the trip.</summary>
    static async Task<JsonElement> Trip(HttpClient c)
    {
        var cust = (await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Rift Valley Supermarket", phone = "0733112233" }))).GetProperty("id").GetGuid();
        var item = (await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Rice 25kg Pack", price = 3_200_00 }))).GetProperty("id").GetGuid();
        var inv = (await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust, lines = new[] { new { itemId = item, quantity = 4 } } }))).GetProperty("id").GetGuid();
        await Ok(await c.PostAsync($"/api/sales/{inv}/issue", null));
        return await Ok(await c.PostAsJsonAsync("/api/dispatch/trips", new
        {
            vehiclePlate = "KCX 412M", driverName = "Peter Owino", driverPhone = "0722123456", routeName = "Kondele – Nyalenda",
            deliveries = new object[]
            {
                new { salesDocId = inv, customerName = "Rift Valley Supermarket", deliveryAddress = "Nakuru Town", contactPhone = "0733112233" },
                new { customerName = "Obunga Roofing", deliveryAddress = "Obunga", contactPhone = "0756330210" },
            },
        }));
    }

    static string Token(JsonElement link) => link.GetProperty("url").GetString()!.Split("/d/")[1];

    static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public async Task The_site_lets_the_drivers_page_ask_for_location_and_the_map_load_its_tiles()
    {
        // The browser refuses geolocation outright when Permissions-Policy says geolocation=(), and map tiles when
        // the CSP leaves out their host; production adds the same headers from vercel.json, so both are checked.
        var res = await factory.CreateClient().GetAsync("/api/health");
        Assert.Contains("geolocation=(self)", res.Headers.GetValues("Permissions-Policy").Single());
        Assert.Contains("https://tile.openstreetmap.org", res.Headers.GetValues("Content-Security-Policy").Single());
        // Found from this file's own folder, so it works wherever the build output goes.
        var dir = new DirectoryInfo(Path.GetDirectoryName(SourceFile())!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vercel.json"))) dir = dir.Parent;
        var vercel = File.ReadAllText(Path.Combine(dir!.FullName, "vercel.json"));
        Assert.Contains("geolocation=(self)", vercel);
        Assert.Contains("https://tile.openstreetmap.org", vercel);
    }

    [Fact]
    public async Task A_dispatcher_runs_the_fleet_and_sees_nothing_else()
    {
        var owner = await Owner();
        var email = $"{Guid.NewGuid():N}@example.com";
        await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = "Dispatch Office", email, phone = "0700 444 555", role = "Dispatcher", password = "dispatch-pass-123" }));
        var d = factory.CreateClient();
        var login = await TestSignIn.First(d, email, "dispatch-pass-123");
        d.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("token").GetString());
        var permissions = login.GetProperty("user").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("fleet", permissions);
        Assert.DoesNotContain("sales.view", permissions);

        var trip = await Trip(owner);
        var id = trip.GetProperty("id").GetGuid();
        await Ok(await d.GetAsync("/api/dispatch/live"));
        await Ok(await d.PostAsync($"/api/dispatch/trips/{id}/driver-link", null));
        await Ok(await d.PostAsync($"/api/dispatch/trips/{id}/dispatch", null));
        foreach (var url in new[] { "/api/sales?kind=Invoice", "/api/customers", "/api/employees", "/api/reports/vat", "/api/settings/company" })
            Assert.Equal(HttpStatusCode.Forbidden, (await d.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_drivers_link_shares_location_only_while_the_trip_is_on_the_road()
    {
        var c = await Owner();
        var trip = await Trip(c);
        var id = trip.GetProperty("id").GetGuid();
        var token = Token(await Ok(await c.PostAsync($"/api/dispatch/trips/{id}/driver-link", null)));
        var driver = factory.CreateClient(); // no sign-in: the link is the key

        var view = await Ok(await driver.GetAsync($"/api/drive/{token}"));
        Assert.Equal("Lake Haulage", view.GetProperty("business").GetString());
        Assert.False(view.GetProperty("sharing").GetBoolean());
        Assert.Equal(2, view.GetProperty("drops").GetArrayLength());
        Assert.Equal("Rice 25kg Pack", view.GetProperty("drops")[0].GetProperty("items")[0].GetProperty("description").GetString());
        // Not sent out yet: nothing is taken.
        Assert.Equal(HttpStatusCode.Conflict, (await driver.PostAsJsonAsync($"/api/drive/{token}/ping", new { lat = -0.0917, lng = 34.768 })).StatusCode);

        await Ok(await c.PostAsync($"/api/dispatch/trips/{id}/dispatch", null));
        await Ok(await driver.PostAsJsonAsync($"/api/drive/{token}/ping", new { lat = -0.0917, lng = 34.768, accuracy = 8, speed = 11.5, heading = 90 }));
        await Ok(await driver.PostAsJsonAsync($"/api/drive/{token}/ping", new { lat = -0.0920, lng = 34.770, accuracy = 8, speed = 12.0, heading = 95 }));
        Assert.Equal(HttpStatusCode.BadRequest, (await driver.PostAsJsonAsync($"/api/drive/{token}/ping", new { lat = 123.0, lng = 34.768 })).StatusCode);

        var live = await Ok(await c.GetAsync("/api/dispatch/live"));
        var row = Assert.Single(live.GetProperty("trips").EnumerateArray());
        Assert.Equal("moving", row.GetProperty("state").GetString());
        Assert.Equal(-0.0920, row.GetProperty("lastLat").GetDouble(), 4);
        // Two pings seconds apart move the dot but keep one point on the trail.
        Assert.Equal(1, row.GetProperty("trail").GetArrayLength());
        Assert.Equal(1, live.GetProperty("counts").GetProperty("moving").GetInt32());

        // The driver confirms a drop from the link, with where the phone was.
        var drop = view.GetProperty("drops")[0].GetProperty("id").GetGuid();
        await Ok(await driver.PostAsJsonAsync($"/api/drive/{token}/drops/{drop}", new { recipientName = "Mary Wanjiku", status = "Delivered", lat = -0.0921, lng = 34.771 }));
        live = await Ok(await c.GetAsync("/api/dispatch/live"));
        var confirmed = Assert.Single(live.GetProperty("trips")[0].GetProperty("confirmed").EnumerateArray());
        Assert.Equal(-0.0921, confirmed.GetProperty("lat").GetDouble(), 4);

        // Pings never fill the audit trail people read.
        var audit = await Ok(await c.GetAsync("/api/audit?entity=DriverPing"));
        Assert.Equal(0, audit.GetArrayLength());

        // Back at the yard: the link stops sharing.
        await Ok(await c.PostAsync($"/api/dispatch/trips/{id}/complete", null));
        Assert.Equal(HttpStatusCode.Conflict, (await driver.PostAsJsonAsync($"/api/drive/{token}/ping", new { lat = -0.09, lng = 34.77 })).StatusCode);
        Assert.Empty((await Ok(await c.GetAsync("/api/dispatch/live"))).GetProperty("trips").EnumerateArray());

        // 90 days on, the trail and the trip's last position are gone (the privacy notice's promise).
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Kabuor.Erp.Data.ErpDb>();
        var (pings, trips) = await Kabuor.Erp.Dispatch.DriverTrailRetention.Prune(db, DateTimeOffset.UtcNow.AddDays(Kabuor.Erp.Dispatch.DriverTrailRetention.KeepDays + 1));
        Assert.True(pings >= 1 && trips >= 1);
        Assert.False(await db.DriverPings.IgnoreQueryFilters().AnyAsync(p => p.TripId == id));
        var kept = await db.DispatchTrips.IgnoreQueryFilters().SingleAsync(t => t.Id == id);
        Assert.Null(kept.LastLat);
        Assert.Null(kept.LastPingAt);
        // The proof of delivery keeps its place: it's the business's record.
        Assert.Equal(-0.0921, (await db.DeliveryItems.IgnoreQueryFilters().SingleAsync(d => d.Id == drop)).PodLat!.Value, 4);
    }

    [Fact]
    public async Task A_link_opens_only_its_own_trip()
    {
        var a = await Owner();
        var b = await Owner();
        var tripA = await Trip(a);
        var tripB = await Trip(b);
        var tokenA = Token(await Ok(await a.PostAsync($"/api/dispatch/trips/{tripA.GetProperty("id").GetGuid()}/driver-link", null)));
        await Ok(await b.PostAsync($"/api/dispatch/trips/{tripB.GetProperty("id").GetGuid()}/dispatch", null));
        var dropB = tripB.GetProperty("deliveries")[0].GetProperty("id").GetGuid();
        var driver = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await driver.PostAsJsonAsync($"/api/drive/{tokenA}/drops/{dropB}", new { recipientName = "Someone Else", status = "Delivered" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await driver.GetAsync($"/api/drive/{new string('0', 36)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await driver.GetAsync("/api/drive/short")).StatusCode);
        // B's office doesn't see A's trip on its map, and A's link didn't touch B's drop.
        var detailB = await Ok(await b.GetAsync($"/api/dispatch/trips/{tripB.GetProperty("id").GetGuid()}"));
        Assert.Equal("Dispatched", detailB.GetProperty("deliveries")[0].GetProperty("status").GetString());
    }
}
