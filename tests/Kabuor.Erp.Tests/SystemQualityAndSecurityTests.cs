using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Domain;

namespace Kabuor.Erp.Tests;

public class SystemQualityAndSecurityTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    async Task<(HttpClient client, JsonElement session)> RegisterCompany(string name, string industry = "general")
    {
        var c = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.com";
        var res = await c.PostAsJsonAsync("/api/auth/register", new
        {
            companyName = name,
            ownerName = "Chief Executive",
            email,
            phone = "0711002233",
            password = "complex-system-password-100",
            industry
        });
        var s = await Ok(res);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return (c, s);
    }

    HttpClient WithToken(string token)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    [Fact]
    public async Task MultiTenant_Isolation_Strictly_Enforces_Boundary_On_Dispatch_And_Deliveries()
    {
        var (clientA, _) = await RegisterCompany("Enterprise Logistics A", "transportation");
        var (clientB, _) = await RegisterCompany("Enterprise Logistics B", "transportation");

        // Company A sets up an invoice and dispatch trip
        var custA = Id(await Ok(await clientA.PostAsJsonAsync("/api/customers", new { name = "Customer Corp A" })));
        var itemA = Id(await Ok(await clientA.PostAsJsonAsync("/api/items", new { name = "Cargo Pallet", price = 10_000_00 })));
        var invA = await Ok(await clientA.PostAsJsonAsync("/api/sales", new
        {
            kind = "Invoice",
            customerId = custA,
            lines = new[] { new { itemId = itemA, quantity = 5 } }
        }));
        var invAId = Id(invA);
        await Ok(await clientA.PostAsync($"/api/sales/{invAId}/issue", null));

        var tripA = await Ok(await clientA.PostAsJsonAsync("/api/dispatch/trips", new
        {
            vehiclePlate = "KCD 100Z",
            driverName = "Driver One",
            routeName = "Mombasa Port - Inland",
            deliveries = new[]
            {
                new { salesDocId = invAId, customerName = "Customer Corp A", deliveryAddress = "Depot 1" }
            }
        }));
        var tripAId = Id(tripA);
        var deliveryAId = Id(tripA.GetProperty("deliveries").EnumerateArray().First());

        // Attack Vector 1: Company B attempts direct ID access to Company A's trip -> 404
        var tripCrossAccess = await clientB.GetAsync($"/api/dispatch/trips/{tripAId}");
        Assert.Equal(HttpStatusCode.NotFound, tripCrossAccess.StatusCode);

        // Attack Vector 2: Company B attempts to forge e-POD for Company A's delivery -> 404
        var podTamperRes = await clientB.PostAsJsonAsync($"/api/dispatch/deliveries/{deliveryAId}/pod", new
        {
            recipientName = "Fraudulent Receiver",
            podNotes = "Attempting cross-tenant tampering",
            status = "Delivered"
        });
        Assert.Equal(HttpStatusCode.NotFound, podTamperRes.StatusCode);

        // Attack Vector 3: Company B lists trips -> strictly returns 0 records
        var tripsListB = await Ok(await clientB.GetAsync("/api/dispatch/trips"));
        Assert.Empty(tripsListB.EnumerateArray().ToList());

        // Attack Vector 4: Company B attempts to read Company A's invoice -> 404
        var invoiceCrossAccess = await clientB.GetAsync($"/api/sales/{invAId}");
        Assert.Equal(HttpStatusCode.NotFound, invoiceCrossAccess.StatusCode);
    }

    [Fact]
    public async Task Role_Based_Access_Control_Prevents_Unauthorized_Dispatch_Or_Settings_Changes()
    {
        var (ownerClient, _) = await RegisterCompany("Access Governed Corp", "distribution");

        // 1. Owner adds Storekeeper and Viewer colleagues
        var storekeeperEmail = $"{Guid.NewGuid():N}@example.com";
        var viewerEmail = $"{Guid.NewGuid():N}@example.com";

        await Ok(await ownerClient.PostAsJsonAsync("/api/settings/users", new
        {
            name = "Storekeeper Sam",
            email = storekeeperEmail,
            role = "Storekeeper",
            password = "store-keeper-password-123"
        }));

        await Ok(await ownerClient.PostAsJsonAsync("/api/settings/users", new
        {
            name = "Viewer Victor",
            email = viewerEmail,
            role = "Viewer",
            password = "viewer-secure-password-123"
        }));

        // Sign in as Storekeeper
        var skSession = await TestSignIn.First(factory.CreateClient(), storekeeperEmail, "store-keeper-password-123");
        var skClient = WithToken(skSession.GetProperty("token").GetString()!);

        // Sign in as Viewer
        var vwSession = await TestSignIn.First(factory.CreateClient(), viewerEmail, "viewer-secure-password-123");
        var vwClient = WithToken(vwSession.GetProperty("token").GetString()!);

        // RBAC Enforcement 1: Storekeeper cannot modify company settings or period lock date -> 403 Forbidden
        var skSettingsTamper = await skClient.PutAsJsonAsync("/api/settings/company", new
        {
            name = "Hacked Name",
            vatRegistered = false,
            accountingLockDate = "2026-01-01"
        });
        Assert.Equal(HttpStatusCode.Forbidden, skSettingsTamper.StatusCode);

        // RBAC Enforcement 2: Storekeeper CAN create and dispatch trips
        var cust = Id(await Ok(await ownerClient.PostAsJsonAsync("/api/customers", new { name = "Consignment Buyer" })));
        var skTripRes = await skClient.PostAsJsonAsync("/api/dispatch/trips", new
        {
            vehiclePlate = "KCA 400B",
            driverName = "Peter Mwangi",
            routeName = "Thika Superhighway",
            deliveries = new[] { new { customerName = "Consignment Buyer", deliveryAddress = "Thika Gate 2" } }
        });
        Assert.Equal(HttpStatusCode.Created, skTripRes.StatusCode);
        var skTrip = await Ok(skTripRes);
        var tripId = Id(skTrip);

        // RBAC Enforcement 3: Viewer can view trips (Read), but CANNOT dispatch -> 403 Forbidden
        var vwReadTrips = await vwClient.GetAsync("/api/dispatch/trips");
        Assert.Equal(HttpStatusCode.OK, vwReadTrips.StatusCode);

        var vwDispatchAttempt = await vwClient.PostAsync($"/api/dispatch/trips/{tripId}/dispatch", null);
        Assert.Equal(HttpStatusCode.Forbidden, vwDispatchAttempt.StatusCode);

        // Storekeeper can dispatch
        var skDispatchRes = await skClient.PostAsync($"/api/dispatch/trips/{tripId}/dispatch", null);
        Assert.Equal(HttpStatusCode.OK, skDispatchRes.StatusCode);
    }

    [Fact]
    public async Task Dispatch_State_Machine_Enforces_Strict_Lifecycle_Transitions()
    {
        var (client, _) = await RegisterCompany("State Machine Logistics", "transportation");

        // Rule 1: Empty trip with 0 deliveries rejected
        var emptyTripRes = await client.PostAsJsonAsync("/api/dispatch/trips", new
        {
            vehiclePlate = "KBY 999K",
            driverName = "Hassan Juma",
            routeName = "Eldoret Town",
            deliveries = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, emptyTripRes.StatusCode);

        // Create valid trip
        var trip = await Ok(await client.PostAsJsonAsync("/api/dispatch/trips", new
        {
            vehiclePlate = "KBY 999K",
            driverName = "Hassan Juma",
            routeName = "Eldoret Town",
            deliveries = new[]
            {
                new { customerName = "Shop 1", deliveryAddress = "Market St" },
                new { customerName = "Shop 2", deliveryAddress = "Post Office Way" }
            }
        }));
        var tripId = Id(trip);
        var deliveryIds = trip.GetProperty("deliveries").EnumerateArray().Select(Id).ToList();

        // Rule 2: Cannot complete trip directly from Draft
        var prematureComplete = await client.PostAsync($"/api/dispatch/trips/{tripId}/complete", null);
        Assert.Equal(HttpStatusCode.BadRequest, prematureComplete.StatusCode);

        // Rule 3: Cannot confirm POD before trip is dispatched
        var prematurePod = await client.PostAsJsonAsync($"/api/dispatch/deliveries/{deliveryIds[0]}/pod", new
        {
            recipientName = "John Doe"
        });
        Assert.Equal(HttpStatusCode.BadRequest, prematurePod.StatusCode);

        // Dispatch trip
        await Ok(await client.PostAsync($"/api/dispatch/trips/{tripId}/dispatch", null));

        // Rule 4: Cannot dispatch an already dispatched trip
        var duplicateDispatch = await client.PostAsync($"/api/dispatch/trips/{tripId}/dispatch", null);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateDispatch.StatusCode);

        // Rule 5: Cannot confirm POD with invalid status (e.g. Pending)
        var invalidPodStatus = await client.PostAsJsonAsync($"/api/dispatch/deliveries/{deliveryIds[0]}/pod", new
        {
            recipientName = "John Doe",
            status = "Pending"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidPodStatus.StatusCode);

        // Confirm delivery 1: Trip should remain Dispatched
        await Ok(await client.PostAsJsonAsync($"/api/dispatch/deliveries/{deliveryIds[0]}/pod", new
        {
            recipientName = "Mary Wambui",
            status = "Delivered"
        }));
        var midTrip = await Ok(await client.GetAsync($"/api/dispatch/trips/{tripId}"));
        Assert.Equal("Dispatched", midTrip.GetProperty("status").GetString());

        // Confirm delivery 2: All resolved -> Trip automatically transitions to Delivered
        await Ok(await client.PostAsJsonAsync($"/api/dispatch/deliveries/{deliveryIds[1]}/pod", new
        {
            recipientName = "Grace Achieng",
            status = "PartialReturn"
        }));
        var deliveredTrip = await Ok(await client.GetAsync($"/api/dispatch/trips/{tripId}"));
        Assert.Equal("Delivered", deliveredTrip.GetProperty("status").GetString());

        // Complete trip
        await Ok(await client.PostAsync($"/api/dispatch/trips/{tripId}/complete", null));
        var completedTrip = await Ok(await client.GetAsync($"/api/dispatch/trips/{tripId}"));
        Assert.Equal("Completed", completedTrip.GetProperty("status").GetString());

        // Rule 6: Completed trip rejects further POD alterations
        var postCompletePod = await client.PostAsJsonAsync($"/api/dispatch/deliveries/{deliveryIds[0]}/pod", new
        {
            recipientName = "Tamper Attempt",
            status = "Delivered"
        });
        Assert.Equal(HttpStatusCode.BadRequest, postCompletePod.StatusCode);
    }

    [Fact]
    public async Task ThreeWayMatch_Evaluates_All_Variance_Permutations_Accurately()
    {
        var (c, _) = await RegisterCompany("P2P Controls Ltd", "manufacturing");
        var supp = Id(await Ok(await c.PostAsJsonAsync("/api/suppliers", new { name = "Industrial Chemicals Kenya" })));
        var rawMaterialA = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Caustic Soda Flakes", price = 5_000_00, trackStock = true })));
        var rawMaterialB = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Palm Oil 20L", price = 4_000_00, trackStock = true })));

        // Unlinked Bill (no PO) reports recommendation
        var standaloneBill = await Ok(await c.PostAsJsonAsync("/api/purchases", new
        {
            kind = "Bill",
            supplierId = supp,
            lines = new[] { new { itemId = rawMaterialA, quantity = 2, unitCost = 3_500_00 } }
        }));
        var unlinkedCheck = await Ok(await c.GetAsync($"/api/purchases/{Id(standaloneBill)}/match-check"));
        Assert.False(unlinkedCheck.GetProperty("hasPurchaseOrder").GetBoolean());
        Assert.False(unlinkedCheck.GetProperty("matched").GetBoolean());
        Assert.Contains("No purchase order linked", unlinkedCheck.GetProperty("summary").GetString());

        // PO with 2 lines: 10 Caustic @ 3,500_00, 5 Palm Oil @ 3,000_00
        var po = await Ok(await c.PostAsJsonAsync("/api/purchases", new
        {
            kind = "Order",
            supplierId = supp,
            lines = new object[]
            {
                new { itemId = rawMaterialA, quantity = 10, unitCost = 3_500_00 },
                new { itemId = rawMaterialB, quantity = 5, unitCost = 3_000_00 }
            }
        }));
        var poId = Id(po);

        // Receive into draft bill
        var bill = await Ok(await c.PostAsync($"/api/purchases/{poId}/receive", null));
        var billId = Id(bill);

        // Case 1: Exact Match
        var exactMatch = await Ok(await c.GetAsync($"/api/purchases/{billId}/match-check"));
        Assert.True(exactMatch.GetProperty("matched").GetBoolean());
        Assert.Equal(0, exactMatch.GetProperty("totalVariance").GetInt64());

        // Case 2: Supplier bills extra quantity and higher price on line 1, and omits line 2
        await Ok(await c.PutAsJsonAsync($"/api/purchases/{billId}", new
        {
            kind = "Bill",
            supplierId = supp,
            lines = new[]
            {
                new { itemId = rawMaterialA, quantity = 12, unitCost = 3_800_00 } // Qty 12 vs 10, Cost 3800 vs 3500
            }
        }));

        var complexMismatch = await Ok(await c.GetAsync($"/api/purchases/{billId}/match-check"));
        Assert.False(complexMismatch.GetProperty("matched").GetBoolean());
        var variances = complexMismatch.GetProperty("variances").EnumerateArray().ToList();
        Assert.Equal(2, variances.Count);

        // Check line 1: PriceMismatch detected
        var varLine1 = variances.First(v => v.GetProperty("itemId").GetGuid() == rawMaterialA);
        Assert.Equal("PriceMismatch", varLine1.GetProperty("status").GetString());
        Assert.Equal(300_00, varLine1.GetProperty("costDiff").GetInt64());
        Assert.Equal(2, varLine1.GetProperty("quantityDiff").GetDecimal());

        // Check line 2: MissingLine detected
        var varLine2 = variances.First(v => v.GetProperty("itemId").GetGuid() == rawMaterialB);
        Assert.Equal("MissingLine", varLine2.GetProperty("status").GetString());
        Assert.Equal(-5, varLine2.GetProperty("quantityDiff").GetDecimal());
    }

    [Fact]
    public async Task AccountingLockDate_Strictly_Guards_Every_Transaction_Lifecycle_Stage()
    {
        var (c, _) = await RegisterCompany("Audit Compliance Ltd");

        // Lock books as of 2026-09-30
        await c.PutAsJsonAsync("/api/settings/company", new
        {
            name = "Audit Compliance Ltd",
            phone = "0700112233",
            email = "audit@compliance.ke",
            vatRegistered = false,
            accountingLockDate = "2026-09-30"
        });

        var cust = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Audit Customer" })));
        var item = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Standard Stock Unit", price = 2_000_00 })));

        // Create draft invoice on unlocked date 2026-10-05
        var invoice = await Ok(await c.PostAsJsonAsync("/api/sales", new
        {
            kind = "Invoice",
            customerId = cust,
            issueDate = "2026-10-05",
            lines = new[] { new { itemId = item, quantity = 1 } }
        }));
        var invoiceId = Id(invoice);

        // Tamper 1: Attempt to backdate draft invoice to 2026-09-30 (locked boundary)
        var backdateUpdate = await c.PutAsJsonAsync($"/api/sales/{invoiceId}", new
        {
            kind = "Invoice",
            customerId = cust,
            issueDate = "2026-09-30",
            lines = new[] { new { itemId = item, quantity = 1 } }
        });
        Assert.Equal(HttpStatusCode.Conflict, backdateUpdate.StatusCode);

        // Tamper 2: Voiding invoice in locked period is prohibited
        // (Create invoice unlocked, but if lock date advances, voiding is prevented)
        await Ok(await c.PostAsync($"/api/sales/{invoiceId}/issue", null));
        // Move lock date past invoice issue date (2026-10-15)
        await c.PutAsJsonAsync("/api/settings/company", new
        {
            name = "Audit Compliance Ltd",
            phone = "0700112233",
            email = "audit@compliance.ke",
            vatRegistered = false,
            accountingLockDate = "2026-10-15"
        });
        var voidAttempt = await c.PostAsync($"/api/sales/{invoiceId}/void", null);
        Assert.Equal(HttpStatusCode.Conflict, voidAttempt.StatusCode);
    }

    [Fact]
    public async Task Input_Sanitization_Resists_Malicious_Payloads_And_Normalizes_Fields()
    {
        var (c, _) = await RegisterCompany("Security Hardened Ltd", "transportation");

        // 1. Vehicle plate input with extra spaces and lowercase
        var trip = await Ok(await c.PostAsJsonAsync("/api/dispatch/trips", new
        {
            vehiclePlate = "  kdg 789z  ",
            driverName = "  Dennis Kipruto  ",
            driverPhone = "+254 712 345 678",
            routeName = "  Nairobi - Kisumu  ",
            deliveries = new[] { new { customerName = "Store A", contactPhone = "0722 000 111" } }
        }));

        Assert.Equal("KDG 789Z", trip.GetProperty("vehiclePlate").GetString());
        Assert.Equal("Dennis Kipruto", trip.GetProperty("driverName").GetString());
        Assert.Equal("254712345678", trip.GetProperty("driverPhone").GetString());

        // 2. SQL injection string in trip status search parameter
        var sqlInjRes = await c.GetAsync("/api/dispatch/trips?status='; DROP TABLE DispatchTrips; --");
        Assert.Equal(HttpStatusCode.OK, sqlInjRes.StatusCode);
        var parsed = await Ok(sqlInjRes);
        // Table still intact and query executed safely
        Assert.Empty(parsed.EnumerateArray().ToList());

        // 3. Payload bombing attack (> 100 delivery drops in 1 trip)
        var massiveDrops = Enumerable.Range(1, 105).Select(i => new { customerName = $"Drop {i}" }).ToArray();
        var bombRes = await c.PostAsJsonAsync("/api/dispatch/trips", new
        {
            vehiclePlate = "KCD 111A",
            driverName = "Valid Driver",
            routeName = "City Center",
            deliveries = massiveDrops
        });
        Assert.Equal(HttpStatusCode.BadRequest, bombRes.StatusCode);
        var bombText = await bombRes.Content.ReadAsStringAsync();
        Assert.Contains("100", bombText);
    }
}
