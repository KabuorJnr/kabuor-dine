using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class TransportationAndGovernanceTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    async Task<(HttpClient client, JsonElement session)> Owner(string industry = "general")
    {
        var c = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.com";
        var res = await c.PostAsJsonAsync("/api/auth/register", new
        {
            companyName = "Logistics & Trade East Africa",
            ownerName = "Managing Director",
            email,
            phone = "0722334455",
            password = "a-secure-password-123",
            industry
        });
        var s = await Ok(res);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return (c, s);
    }

    static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    [Fact]
    public async Task Industry_selection_during_onboarding_configures_company_profile_and_policies()
    {
        var (c, session) = await Owner("distribution");

        // Verify company payload returned in registration session
        var comp = session.GetProperty("company");
        Assert.Equal("distribution", comp.GetProperty("industry").GetString());

        // Verify settings/company endpoint
        var settingsRes = await Ok(await c.GetAsync("/api/settings/company"));
        Assert.Equal("distribution", settingsRes.GetProperty("industry").GetString());
        Assert.True(settingsRes.GetProperty("preventNegativeStock").GetBoolean());

        // Update company industry to transportation and set accounting period lock date
        var updateRes = await c.PutAsJsonAsync("/api/settings/company", new
        {
            name = "Logistics & Trade East Africa",
            phone = "0722334455",
            email = "info@logisticstrade.ke",
            county = "Nairobi",
            vatRegistered = false,
            industry = "transportation",
            accountingLockDate = "2026-09-30"
        });
        Assert.Equal(HttpStatusCode.NoContent, updateRes.StatusCode);

        // Verify updated settings
        var updated = await Ok(await c.GetAsync("/api/settings/company"));
        Assert.Equal("transportation", updated.GetProperty("industry").GetString());
        Assert.Equal("2026-09-30", updated.GetProperty("accountingLockDate").GetString());
    }

    [Fact]
    public async Task Accounting_period_hard_lock_date_blocks_backdated_invoices_and_bills()
    {
        var (c, _) = await Owner();

        // Lock accounting up to 2026-09-30
        await c.PutAsJsonAsync("/api/settings/company", new
        {
            name = "Locked Books Ltd",
            phone = "0711223344",
            email = "books@locked.ke",
            vatRegistered = false,
            accountingLockDate = "2026-09-30"
        });

        var cust = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Customer Locked Test" })));
        var item = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Audit Controlled Item", price = 1_500_00 })));

        // Attempt to create invoice with issue date in locked period (2026-09-15 <= 2026-09-30)
        var backdatedInvoiceRes = await c.PostAsJsonAsync("/api/sales", new
        {
            kind = "Invoice",
            customerId = cust,
            issueDate = "2026-09-15",
            lines = new[] { new { itemId = item, quantity = 2 } }
        });
        Assert.Equal(HttpStatusCode.Conflict, backdatedInvoiceRes.StatusCode);
        var errText = await backdatedInvoiceRes.Content.ReadAsStringAsync();
        Assert.Contains("locked", errText, StringComparison.OrdinalIgnoreCase);

        // Create invoice in unlocked period (2026-10-01) -> succeeds
        var validInvoice = await Ok(await c.PostAsJsonAsync("/api/sales", new
        {
            kind = "Invoice",
            customerId = cust,
            issueDate = "2026-10-01",
            lines = new[] { new { itemId = item, quantity = 2 } }
        }));
        Assert.NotNull(validInvoice.GetProperty("number").GetString());

        // Supplier bills: Attempt to create bill with issue date in locked period
        var supp = Id(await Ok(await c.PostAsJsonAsync("/api/suppliers", new { name = "Audit Supplier Ltd" })));
        var backdatedBillRes = await c.PostAsJsonAsync("/api/purchases", new
        {
            kind = "Bill",
            supplierId = supp,
            issueDate = "2026-09-20",
            lines = new[] { new { itemId = item, quantity = 5, unitCost = 1_000_00 } }
        });
        Assert.Equal(HttpStatusCode.Conflict, backdatedBillRes.StatusCode);

        // Create bill in unlocked period (2026-10-05) -> succeeds
        var validBill = await Ok(await c.PostAsJsonAsync("/api/purchases", new
        {
            kind = "Bill",
            supplierId = supp,
            issueDate = "2026-10-05",
            lines = new[] { new { itemId = item, quantity = 5, unitCost = 1_000_00 } }
        }));
        Assert.NotNull(validBill.GetProperty("number").GetString());
    }

    [Fact]
    public async Task Three_way_matching_detects_po_vs_bill_price_and_quantity_variance()
    {
        var (c, _) = await Owner();
        var supp = Id(await Ok(await c.PostAsJsonAsync("/api/suppliers", new { name = "Agro Feeds Supplies Ltd" })));
        var item = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Animal Feed 50kg", price = 3_000_00, trackStock = true })));

        // 1. Create Purchase Order for 10 bags @ Ksh 2,200 each (220,000 cents)
        var po = await Ok(await c.PostAsJsonAsync("/api/purchases", new
        {
            kind = "Order",
            supplierId = supp,
            lines = new[] { new { itemId = item, quantity = 10, unitCost = 2_200_00 } }
        }));
        var poId = Id(po);

        // 2. Receive goods to draft bill
        var bill = await Ok(await c.PostAsync($"/api/purchases/{poId}/receive", null));
        var billId = Id(bill);

        // 3. Exact 3-way match before any edits
        var matchResult = await Ok(await c.GetAsync($"/api/purchases/{billId}/match-check"));
        Assert.True(matchResult.GetProperty("matched").GetBoolean());
        Assert.True(matchResult.GetProperty("hasPurchaseOrder").GetBoolean());
        Assert.Equal(0, matchResult.GetProperty("totalVariance").GetInt64());
        Assert.Contains("Exact 3-way match", matchResult.GetProperty("summary").GetString());

        // 4. Supplier overbills unit cost: update bill price to Ksh 2,500
        await Ok(await c.PutAsJsonAsync($"/api/purchases/{billId}", new
        {
            kind = "Bill",
            supplierId = supp,
            lines = new[] { new { itemId = item, quantity = 10, unitCost = 2_500_00 } }
        }));

        // 5. Match check now detects price discrepancy
        var mismatchedResult = await Ok(await c.GetAsync($"/api/purchases/{billId}/match-check"));
        Assert.False(mismatchedResult.GetProperty("matched").GetBoolean());
        Assert.Equal(300_00 * 10, mismatchedResult.GetProperty("totalVariance").GetInt64());
        var variances = mismatchedResult.GetProperty("variances").EnumerateArray().ToList();
        Assert.Single(variances);
        Assert.Equal("PriceMismatch", variances[0].GetProperty("status").GetString());
        Assert.Equal(300_00, variances[0].GetProperty("costDiff").GetInt64());
    }

    [Fact]
    public async Task Transportation_management_trip_lifecycle_waybills_and_epod_confirmation()
    {
        var (c, _) = await Owner("transportation");

        var cust = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new
        {
            name = "Rift Valley Supermarket",
            phone = "0733112233",
            address = "Nakuru Town CBD"
        })));
        var item = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Rice 25kg Pack", price = 3_200_00 })));

        // Create and issue sales invoice
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new
        {
            kind = "Invoice",
            customerId = cust,
            lines = new[] { new { itemId = item, quantity = 20 } }
        }));
        var invId = Id(inv);
        await Ok(await c.PostAsync($"/api/sales/{invId}/issue", null));

        // 1. Dispatch Trip Creation with delivery drop
        var trip = await Ok(await c.PostAsJsonAsync("/api/dispatch/trips", new
        {
            vehiclePlate = "KBZ 456T",
            driverName = "John Kamau",
            driverPhone = "0722123456",
            routeName = "Nairobi - Nakuru Highway",
            deliveries = new[]
            {
                new
                {
                    salesDocId = invId,
                    customerName = "Rift Valley Supermarket",
                    deliveryAddress = "Nakuru Town CBD",
                    contactPhone = "0733112233"
                }
            }
        }));

        var tripId = Id(trip);
        var tripNumber = trip.GetProperty("tripNumber").GetString();
        Assert.StartsWith("TRIP-", tripNumber!);
        Assert.Equal("Draft", trip.GetProperty("status").GetString());

        var deliveries = trip.GetProperty("deliveries").EnumerateArray().ToList();
        Assert.Single(deliveries);
        var deliveryId = Id(deliveries[0]);
        var waybillNumber = deliveries[0].GetProperty("waybillNumber").GetString();
        Assert.StartsWith("WB-", waybillNumber!);
        Assert.Equal("Pending", deliveries[0].GetProperty("status").GetString());

        // 2. Dispatch the trip
        var dispatchedTrip = await Ok(await c.PostAsync($"/api/dispatch/trips/{tripId}/dispatch", null));
        Assert.Equal("Dispatched", dispatchedTrip.GetProperty("status").GetString());
        Assert.NotNull(dispatchedTrip.GetProperty("dispatchedAt").GetString());

        // 3. Electronic Proof of Delivery (e-POD)
        var podResult = await Ok(await c.PostAsJsonAsync($"/api/dispatch/deliveries/{deliveryId}/pod", new
        {
            recipientName = "Francis Mwangi (Store Manager)",
            podNotes = "All 20 bags received in prime condition without damage.",
            status = "Delivered"
        }));
        Assert.Equal("Delivered", podResult.GetProperty("status").GetString());
        Assert.Equal("Francis Mwangi (Store Manager)", podResult.GetProperty("recipientName").GetString());
        Assert.NotNull(podResult.GetProperty("podConfirmedAt").GetString());

        // 4. Since all deliveries are confirmed, the trip transitions to Delivered
        var updatedTrip = await Ok(await c.GetAsync($"/api/dispatch/trips/{tripId}"));
        Assert.Equal("Delivered", updatedTrip.GetProperty("status").GetString());

        // 5. Complete trip
        var completedTrip = await Ok(await c.PostAsync($"/api/dispatch/trips/{tripId}/complete", null));
        Assert.Equal("Completed", completedTrip.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_delivery_business_is_asked_for_its_vehicles_and_a_returned_drop_can_go_out_again()
    {
        var (c, _) = await Owner("transportation");
        static JsonElement Step(JsonElement status) => status.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "fleet");
        Assert.False(Step(await Ok(await c.GetAsync("/api/onboarding/status"))).GetProperty("done").GetBoolean());
        var lorry = (await Ok(await c.PostAsJsonAsync("/api/assets", new { name = "Isuzu FRR", category = "VEH", serialNumber = "KCA 123B" }))).GetProperty("items")[0];
        Assert.True(Step(await Ok(await c.GetAsync("/api/onboarding/status"))).GetProperty("done").GetBoolean());

        var cust = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Ahero Hardware" })));
        var item = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Cement 50kg", price = 850_00 })));
        var inv = Id(await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust, lines = new[] { new { itemId = item, quantity = 10 } } })));
        await Ok(await c.PostAsync($"/api/sales/{inv}/issue", null));
        Assert.Contains((await Ok(await c.GetAsync("/api/dispatch/to-deliver"))).EnumerateArray(), i => Id(i) == inv);

        var trip = await Ok(await c.PostAsJsonAsync("/api/dispatch/trips", new { vehicleId = Id(lorry), driverName = "Peter Owino", routeName = "Ahero", deliveries = new[] { new { salesDocId = inv } } }));
        Assert.Equal("KCA 123B", trip.GetProperty("vehiclePlate").GetString());
        // On a trip: not waiting any more, can't go on a second one, and the lorry can't be sent out twice.
        Assert.DoesNotContain((await Ok(await c.GetAsync("/api/dispatch/to-deliver"))).EnumerateArray(), i => Id(i) == inv);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/dispatch/trips", new { vehiclePlate = "KDD 456Y", driverName = "Other Driver", routeName = "Ahero", deliveries = new[] { new { salesDocId = inv } } })).StatusCode);
        await Ok(await c.PostAsync($"/api/dispatch/trips/{Id(trip)}/dispatch", null));
        var second = await Ok(await c.PostAsJsonAsync("/api/dispatch/trips", new { vehicleId = Id(lorry), driverName = "Peter Owino", routeName = "Kondele", deliveries = new[] { new { customerName = "Kondele Builders", deliveryAddress = "Kondele" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/dispatch/trips/{Id(second)}/dispatch", null)).StatusCode);

        // The shop was closed: it came back, so the invoice is waiting to go out again.
        var drop = trip.GetProperty("deliveries")[0];
        await Ok(await c.PostAsJsonAsync($"/api/dispatch/deliveries/{Id(drop)}/pod", new { recipientName = "", podNotes = "Shop closed", status = "Failed" }));
        await Ok(await c.PostAsync($"/api/dispatch/trips/{Id(trip)}/complete", null));
        Assert.Contains((await Ok(await c.GetAsync("/api/dispatch/to-deliver"))).EnumerateArray(), i => Id(i) == inv);
        var fleet = await Ok(await c.GetAsync("/api/dispatch/fleet"));
        Assert.Equal("available", fleet.GetProperty("vehicles")[0].GetProperty("state").GetString());
    }
}
