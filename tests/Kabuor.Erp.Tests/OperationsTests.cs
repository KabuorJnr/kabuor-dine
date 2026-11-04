// Operations end to end: something breaks and is reported from the portal, maintenance takes it on, issues parts,
// records what failed, completes it and closes it financially, with every cost in the books against the asset and the
// department; preventive plans make their own work; a department's request goes through its head and procurement,
// against its budget, and is issued from the store and ordered; a tender keeps its bids sealed until it's opened and
// its order waits out the standstill; a blind stock take books its differences.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

public class OperationsTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    static string Phone() => $"07{Random.Shared.Next(10_000_000, 99_999_999)}";

    async Task<HttpClient> Owner()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"Ops {Guid.NewGuid():N}", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    async Task<HttpClient> As(HttpClient owner, string role)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = $"{role} Person", email, phone = "0700 111 222", role, password = "role-password-1" }));
        var c = factory.CreateClient();
        var login = await TestSignIn.First(c, email, "role-password-1");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("token").GetString());
        return c;
    }

    async Task<HttpClient> Portal(HttpClient owner, Guid employeeId)
    {
        var grant = await Ok(await owner.PostAsync($"/api/employees/{employeeId}/access", null));
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/login", new { email = grant.GetProperty("phone").GetString(), password = grant.GetProperty("password").GetString() }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        var changed = await Ok(await c.PostAsJsonAsync("/api/auth/password", new { current = grant.GetProperty("password").GetString(), @new = "employee-own-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.GetProperty("token").GetString());
        return c;
    }

    static async Task<Guid> Employee(HttpClient owner, string name, string department, long salary = 60_000_00) =>
        (await Ok(await owner.PostAsJsonAsync("/api/employees", new { name, phone = Phone(), startDate = "2025-01-06", basicSalary = salary, department }))).GetProperty("id").GetGuid();

    static async Task<Guid> StockedItem(HttpClient owner, string name, decimal qty, long unitCost)
    {
        var item = await Ok(await owner.PostAsJsonAsync("/api/items", new { name, unit = "pc", price = unitCost * 2, trackStock = true }));
        var id = item.GetProperty("id").GetGuid();
        await Ok(await owner.PostAsJsonAsync("/api/stock/opening", new { itemId = id, quantity = qty, unitCost }));
        return id;
    }

    static async Task<Dictionary<string, long>> Balances(HttpClient c) =>
        (await Ok(await c.GetAsync("/api/reports/accounts"))).EnumerateArray()
            .Where(a => a.TryGetProperty("systemKey", out var k) && k.ValueKind == JsonValueKind.String)
            .ToDictionary(a => a.GetProperty("systemKey").GetString()!, a => a.GetProperty("balance").GetInt64());

    static async Task<Guid> Department(HttpClient owner, string name, Guid? head = null)
    {
        var all = await Ok(await owner.GetAsync("/api/departments"));
        if (all.EnumerateArray().FirstOrDefault(d => d.GetProperty("name").GetString() == name) is { ValueKind: JsonValueKind.Object } found)
        {
            var id = found.GetProperty("id").GetGuid();
            await Ok(await owner.PutAsJsonAsync($"/api/departments/{id}", new { name, headEmployeeId = head }));
            return id;
        }
        return (await Ok(await owner.PostAsJsonAsync("/api/departments", new { name, headEmployeeId = head }))).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_breakdown_reported_from_the_portal_is_repaired_coded_completed_and_closed_with_its_costs_in_the_books()
    {
        var owner = await Owner();
        var wanjiru = await Employee(owner, "Wanjiru Kamau", "Production");
        var otieno = await Employee(owner, "Peter Otieno", "Workshop", 86_665_00); // 500 an hour at 173.33 hours a month
        var production = await Department(owner, "Production");
        var generator = (await Ok(await owner.PostAsJsonAsync("/api/assets", new { name = "Standby generator 60 kVA", category = "EQP", departmentId = production, criticality = "High", cost = 1_200_000_00 })))
            .GetProperty("items")[0];
        var genId = generator.GetProperty("id").GetGuid();
        var alternator = (await Ok(await owner.PostAsJsonAsync("/api/assets", new { name = "Alternator", category = "EQP", parentId = genId }))).GetProperty("items")[0];
        // An asset can't be put inside one of its own parts.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/assets/{genId}", new { name = "Standby generator 60 kVA", parentId = alternator.GetProperty("id").GetGuid() })).StatusCode);
        var filter = await StockedItem(owner, "Oil filter", 10, 1_500_00);

        // Wanjiru reports it from her portal by its tag: it's down.
        var me = await Portal(owner, wanjiru);
        var fault = await Ok(await me.PostAsJsonAsync("/api/me/faults", new { assetTag = generator.GetProperty("tag").GetString(), title = "Generator won't start", symptom = "Won't start", assetDown = true, priority = "Emergency" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync("/api/me/faults", new { assetTag = "AST-XXX-9999", title = "Something" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await me.GetAsync("/api/maintenance/faults")).StatusCode);

        // Maintenance takes it on: a breakdown, down since the report.
        var tech = await As(owner, "Maintenance");
        var job = await Ok(await tech.PostAsJsonAsync($"/api/maintenance/faults/{fault.GetProperty("id")}/accept", new { assignedTo = "Peter Otieno", plannedStart = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).ToString("yyyy-MM-dd") }));
        var id = job.GetProperty("id").GetGuid();
        Assert.Equal("Breakdown", job.GetProperty("type").GetString());
        Assert.Equal("Planned", job.GetProperty("status").GetString());
        Assert.Equal(production, job.GetProperty("departmentId").GetGuid()); // charged to the asset's department
        Assert.NotEqual(JsonValueKind.Null, job.GetProperty("downFrom").ValueKind);
        await Ok(await tech.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/actions", new { kind = "start" }));
        Assert.Equal("UnderRepair", (await Ok(await owner.GetAsync($"/api/assets/{genId}"))).GetProperty("asset").GetProperty("status").GetString());

        // Parts from the store, a fundi paid by M-Pesa, and Otieno's hours.
        job = await Ok(await tech.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/parts", new { itemId = filter, quantity = 2 }));
        var part = job.GetProperty("parts")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await tech.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/parts/{part}/issue", new { quantity = 50 })).StatusCode); // only 10 in the store
        await Ok(await tech.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/parts/{part}/issue", new { quantity = 2 }));
        await Ok(await tech.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/costs", new { description = "Auto-electrician: starter motor", amount = 4_000_00, method = "Mpesa", reference = "SKL7H2", paidTo = "Juma Auto Electrics" }));
        job = await Ok(await tech.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/labour", new { employeeId = otieno, hours = 4 }));
        // The technician sees the hours but not what they cost (that would show Otieno's pay).
        Assert.Equal(JsonValueKind.Null, job.GetProperty("labour")[0].GetProperty("hourlyRate").ValueKind);
        Assert.True(job.GetProperty("totals").GetProperty("labourHidden").GetBoolean());
        var full = await Ok(await owner.GetAsync($"/api/maintenance/work-orders/{id}"));
        Assert.Equal(2_000_00, full.GetProperty("totals").GetProperty("labour").GetInt64());
        Assert.Equal(3_000_00, full.GetProperty("totals").GetProperty("parts").GetInt64());
        Assert.Equal(4_000_00, full.GetProperty("totals").GetProperty("services").GetInt64());

        var books = await Balances(owner);
        Assert.Equal(7_000_00, books["MAINTENANCE"]); // parts + the M-Pesa payment (labour is already in payroll)
        Assert.Equal(15_000_00 - 3_000_00, books["INVENTORY"]);

        // Completing a breakdown needs what failed and what was done; on a critical asset, why as well.
        Assert.Equal(HttpStatusCode.BadRequest, (await tech.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/actions", new { kind = "complete" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await tech.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/actions", new { kind = "complete", failureMode = "Won't start", remedy = "Part replaced" })).StatusCode);
        // Something still on order stops the financial close.
        var supplier = (await Ok(await owner.PostAsJsonAsync("/api/suppliers", new { name = "Genset Spares Ltd" }))).GetProperty("id").GetGuid();
        var po = await Ok(await tech.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/order", new { supplierId = supplier, serviceDescription = "Load test", serviceCost = 5_800_00 }));
        job = await Ok(await tech.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/actions", new { kind = "complete", failureMode = "Won't start", failureCause = "Wear and tear", remedy = "Part replaced", notes = "Starter motor brushes worn out" }));
        Assert.Equal("Completed", job.GetProperty("status").GetString());
        Assert.Equal("InUse", (await Ok(await owner.GetAsync($"/api/assets/{genId}"))).GetProperty("asset").GetProperty("status").GetString());
        var faults = await Ok(await tech.GetAsync("/api/maintenance/faults?status=all"));
        Assert.Equal("Resolved", faults.GetProperty("items").EnumerateArray().Single(f => f.GetProperty("id").GetGuid() == fault.GetProperty("id").GetGuid()).GetProperty("status").GetString());
        var refused = await tech.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/actions", new { kind = "close" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(po.GetProperty("number").GetString()!, await refused.Content.ReadAsStringAsync());
        await Ok(await owner.PostAsync($"/api/purchases/{po.GetProperty("id")}/void", null));

        // Closed: the costs are fixed and nothing more can be charged.
        var closed = await Ok(await owner.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/actions", new { kind = "close" }));
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await tech.PostAsJsonAsync($"/api/maintenance/work-orders/{id}/parts/{part}/issue", new { quantity = 1 })).StatusCode);

        // The asset's page carries its maintenance cost; the money side shows it against the asset and the department.
        Assert.Equal(7_000_00, (await Ok(await owner.GetAsync($"/api/assets/{genId}"))).GetProperty("maintenance").GetProperty("cost").GetInt64());
        var money = await Ok(await owner.GetAsync("/api/operations/finance"));
        Assert.Equal(7_000_00, money.GetProperty("maintenance").GetProperty("total").GetInt64());
        Assert.Equal(7_000_00, money.GetProperty("byDepartment").EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == production).GetProperty("maintenance").GetInt64());
        Assert.Equal(genId, money.GetProperty("maintenance").GetProperty("byAsset")[0].GetProperty("id").GetGuid());
        var insights = await Ok(await owner.GetAsync("/api/maintenance/insights"));
        Assert.Equal(1, insights.GetProperty("reliability").GetProperty("breakdowns").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, insights.GetProperty("reliability").GetProperty("mttrHours").ValueKind);
        Assert.True((await Ok(await owner.GetAsync("/api/reports/trial-balance"))).GetProperty("balanced").GetBoolean());
    }

    [Fact]
    public async Task A_preventive_plan_makes_one_work_order_when_due_and_moves_on_when_it_is_done()
    {
        var owner = await Owner();
        var pump = (await Ok(await owner.PostAsJsonAsync("/api/assets", new { name = "Borehole pump", category = "EQP" }))).GetProperty("items")[0].GetProperty("id").GetGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var plan = await Ok(await owner.PostAsJsonAsync("/api/maintenance/plans", new { name = "Pump: check seals and pressure", assetId = pump, every = 3, unit = "Months", nextDue = today.AddDays(3).ToString("yyyy-MM-dd"), leadDays = 7, assignedTo = "Peter Otieno", estimatedHours = 2 }));
        Assert.Equal(1, (await Ok(await owner.PostAsync("/api/maintenance/plans/generate", null))).GetProperty("made").GetInt32());
        Assert.Equal(0, (await Ok(await owner.PostAsync("/api/maintenance/plans/generate", null))).GetProperty("made").GetInt32()); // already has one
        var job = (await Ok(await owner.GetAsync("/api/maintenance/work-orders"))).GetProperty("items")[0];
        Assert.Equal("Preventive", job.GetProperty("type").GetString());
        Assert.Equal(today.AddDays(3).ToString("yyyy-MM-dd"), job.GetProperty("dueDate").GetString());
        await Ok(await owner.PostAsJsonAsync($"/api/maintenance/work-orders/{job.GetProperty("id")}/actions", new { kind = "complete" })); // no failure coding needed for planned work
        var after = (await Ok(await owner.GetAsync("/api/maintenance/plans"))).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == plan.GetProperty("id").GetGuid());
        Assert.Equal(today.AddDays(3).AddMonths(3).ToString("yyyy-MM-dd"), after.GetProperty("nextDue").GetString());
    }

    [Fact]
    public async Task A_department_request_goes_through_its_head_procurement_and_finance_against_an_approved_budget()
    {
        var owner = await Owner();
        var head = await Employee(owner, "Grace Head", "Workshop");
        var clerk = await Employee(owner, "Brian Clerk", "Workshop");
        var workshop = await Department(owner, "Workshop", head);
        // Gloves are stores: held for the workshop's own use, in their own account, never sold.
        var glovesItem = await Ok(await owner.PostAsJsonAsync("/api/items", new { name = "Work gloves", unit = "pair", price = 0, trackStock = true, use = "Stores", storesCategory = "Protective gear" }));
        var gloves = glovesItem.GetProperty("id").GetGuid();
        await Ok(await owner.PostAsJsonAsync("/api/stock/opening", new { itemId = gloves, quantity = 50, unitCost = 300_00 }));
        Assert.Equal(15_000_00, (await Balances(owner))["STORES"]);

        // Brian asks from his portal: 10 gloves from the store and a welding machine to buy.
        var me = await Portal(owner, clerk);
        var ops = await Ok(await me.GetAsync("/api/me/operations"));
        Assert.Equal(workshop, ops.GetProperty("myDepartmentId").GetGuid());
        var request = await Ok(await me.PostAsJsonAsync("/api/me/requests", new
        {
            departmentId = workshop, purpose = "New welding bay",
            lines = new object[] { new { itemId = gloves, quantity = 10 }, new { description = "Inverter welding machine 200A", quantity = 1, estimatedUnitCost = 18_000_00 } },
        }));
        Assert.Equal("WithHead", request.GetProperty("status").GetString());
        var rid = request.GetProperty("id").GetGuid();

        // Procurement can't skip the head; the head approves from their portal.
        var buyer = await As(owner, "Procurement");
        Assert.Equal(HttpStatusCode.BadRequest, (await buyer.PostAsync($"/api/requisitions/{rid}/approve", null)).StatusCode);
        var boss = await Portal(owner, head);
        Assert.Single((await Ok(await boss.GetAsync("/api/me/operations"))).GetProperty("requests").GetProperty("toApprove").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsync($"/api/me/requests/{rid}/approve", null)).StatusCode); // not the head
        await Ok(await boss.PostAsync($"/api/me/requests/{rid}/approve", null));

        // No budget approved by finance: no spending, whoever asks.
        var refused = await buyer.PostAsync($"/api/requisitions/{rid}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("no budget approved", await refused.Content.ReadAsStringAsync());

        // Procurement prepares the workshop's budget (KSh 10,000 a month); procurement can't approve it, finance does.
        var budget = await Ok(await buyer.PostAsJsonAsync("/api/budgets", new { departmentId = workshop, year = DateTime.UtcNow.AddHours(3).Year, months = Enumerable.Repeat(10_000_00L, 12), justification = "Consumables and small tools for the workshop", submit = true }));
        var bid = budget.GetProperty("budget").GetProperty("id").GetGuid();
        Assert.Equal("Submitted", budget.GetProperty("budget").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.PostAsJsonAsync($"/api/budgets/{bid}/approve", new { })).StatusCode);
        var accountant = await As(owner, "Accountant");
        await Ok(await accountant.PostAsJsonAsync($"/api/budgets/{bid}/approve", new { }));

        // It's KSh 21,000 against KSh 10,000: procurement agrees it's needed; it goes to finance, flagged.
        var withFinance = await Ok(await buyer.PostAsync($"/api/requisitions/{rid}/approve", null));
        Assert.Equal("WithFinance", withFinance.GetProperty("request").GetProperty("status").GetString());
        Assert.True(withFinance.GetProperty("budget").GetProperty("over").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.PostAsJsonAsync($"/api/requisitions/{rid}/sign-off", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await buyer.PostAsJsonAsync($"/api/requisitions/{rid}/issue", new { })).StatusCode); // not signed off yet
        // Over the budget, finance must write why.
        Assert.Equal(HttpStatusCode.Conflict, (await accountant.PostAsJsonAsync($"/api/requisitions/{rid}/sign-off", new { })).StatusCode);
        var approved = await Ok(await accountant.PostAsJsonAsync($"/api/requisitions/{rid}/sign-off", new { reason = "Welding bay is in the annual plan; next month's budget absorbs it" }));
        Assert.Equal("Approved", approved.GetProperty("request").GetProperty("status").GetString());
        Assert.True(approved.GetProperty("request").GetProperty("overBudget").GetBoolean());

        // Stores issues the gloves (charged to the workshop, out of stores); procurement orders the machine, and the order
        // goes to the supplier without another sign-off: its request was signed off.
        await Ok(await owner.PostAsJsonAsync($"/api/requisitions/{rid}/issue", new { }));
        var books = await Balances(owner);
        Assert.Equal(3_000_00, books["SUPPLIES_USED"]);
        Assert.Equal(12_000_00, books["STORES"]);
        var supplier = (await Ok(await owner.PostAsJsonAsync("/api/suppliers", new { name = "Weld Masters" }))).GetProperty("id").GetGuid();
        var po = await Ok(await buyer.PostAsJsonAsync($"/api/requisitions/{rid}/order", new { supplierId = supplier }));
        await Ok(await buyer.PostAsync($"/api/purchases/{po.GetProperty("id")}/post", null));
        var done = await Ok(await owner.GetAsync($"/api/requisitions/{rid}"));
        Assert.Equal("Fulfilled", done.GetProperty("request").GetProperty("status").GetString());
        Assert.Single(done.GetProperty("orders").EnumerateArray());
        // Every decision is in the request's trail: submitted, head, procurement, finance (with the reason).
        var trail = done.GetProperty("trail").EnumerateArray().Select(t => t.GetProperty("action").GetString()).ToList();
        Assert.Equal(["Submitted", "Approved by head of department", "Approved by procurement", "Signed off over budget"], trail);

        // The workshop has spent the gloves and committed the order (before VAT).
        var dept = (await Ok(await owner.GetAsync("/api/departments"))).EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == workshop);
        Assert.Equal(3_000_00, dept.GetProperty("spent").GetInt64());
        var order = await Ok(await owner.GetAsync($"/api/purchases/{po.GetProperty("id")}"));
        Assert.Equal(order.GetProperty("document").GetProperty("net").GetInt64(), dept.GetProperty("committed").GetInt64());
        Assert.Equal(10_000_00, dept.GetProperty("monthlyBudget").GetInt64());

        // The approvals register holds together: then someone edits a record behind the system's back, and the check
        // finds exactly where.
        var check = await Ok(await owner.GetAsync("/api/approvals/verify"));
        Assert.True(check.GetProperty("intact").GetBoolean());
        Assert.Equal(6, check.GetProperty("records").GetInt32()); // budget submitted + approved, request x4
        bool postgres;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
            postgres = db.Database.IsNpgsql();
            var record = await db.ApprovalRecords.IgnoreQueryFilters().Where(r => r.SubjectId == rid && r.Action == "Signed off over budget").SingleAsync();
            record.Amount = 1_00;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync()); // the app refuses outright
            const string tamper = "UPDATE \"ApprovalRecords\" SET \"Amount\" = 100 WHERE \"Id\" = {0}";
            // PostgreSQL (production) refuses even an edit made straight in the database: a trigger guards the register.
            if (postgres) await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(tamper, record.Id));
            else await db.Database.ExecuteSqlRawAsync(tamper, record.Id); // SQLite has no such guard: the edit lands, and the check must find it
        }
        check = await Ok(await owner.GetAsync("/api/approvals/verify"));
        if (postgres) Assert.True(check.GetProperty("intact").GetBoolean());
        else
        {
            Assert.False(check.GetProperty("intact").GetBoolean());
            Assert.Equal(6, check.GetProperty("brokenAt").GetInt64());
        }
    }

    [Fact]
    public async Task Tender_bids_stay_sealed_until_opening_and_the_award_order_waits_out_the_standstill()
    {
        var owner = await Owner();
        var closes = DateTimeOffset.UtcNow.AddDays(3);
        var created = await Ok(await owner.PostAsJsonAsync("/api/tenders", new
        {
            title = "Supply of office chairs", method = "Open", closesAt = closes, standstillDays = 14, requirements = "KRA PIN certificate",
            lines = new object[] { new { description = "Ergonomic office chair", specification = "Mesh back, adjustable arms", quantity = 20, unit = "pc" } },
        }));
        var id = created.GetProperty("id").GetGuid();
        var t = await Ok(await owner.PostAsync($"/api/tenders/{id}/publish", null));
        var token = t.GetProperty("token").GetString();
        var lineId = t.GetProperty("lines")[0].GetProperty("id").GetGuid();

        // Two bidders, without signing in. The same company can't bid twice.
        var anon = factory.CreateClient();
        var view = await Ok(await anon.GetAsync($"/api/bid/{token}"));
        Assert.True(view.GetProperty("taking").GetBoolean());
        await Ok(await anon.PostAsJsonAsync($"/api/bid/{token}", new { bidderName = "Cheap Chairs Ltd", kraPin = "P051111111A", phone = "0711000001", deliveryDays = 14, lines = new[] { new { tenderLineId = lineId, unitPrice = 9_000_00 } } }));
        await Ok(await anon.PostAsJsonAsync($"/api/bid/{token}", new { bidderName = "Quality Seats Ltd", kraPin = "P052222222B", phone = "0711000002", deliveryDays = 7, lines = new[] { new { tenderLineId = lineId, unitPrice = 11_000_00 } } }));
        Assert.Equal(HttpStatusCode.Conflict, (await anon.PostAsJsonAsync($"/api/bid/{token}", new { bidderName = "Cheap Chairs Ltd", kraPin = "P051111111A", phone = "0711000001", lines = new[] { new { tenderLineId = lineId, unitPrice = 1_00 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/bid/000000000000000000000000")).StatusCode);

        // Sealed: the business sees how many, not who or how much, and can't open early.
        var sealed_ = await Ok(await owner.GetAsync($"/api/tenders/{id}"));
        Assert.Equal(2, sealed_.GetProperty("bidCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, sealed_.GetProperty("bids").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/tenders/{id}/open", null)).StatusCode);

        // Time passes: it closes, bids are refused, and it's opened.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
            var tender = await db.Tenders.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
            tender.ClosesAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await anon.PostAsJsonAsync($"/api/bid/{token}", new { bidderName = "Late Ltd", phone = "0711000003", lines = new[] { new { tenderLineId = lineId, unitPrice = 5_00 } } })).StatusCode);
        var opened = await Ok(await owner.PostAsync($"/api/tenders/{id}/open", null));
        var bids = opened.GetProperty("bids").EnumerateArray().ToList();
        Guid Bid(string name) => bids.Single(b => b.GetProperty("bidderName").GetString() == name).GetProperty("id").GetGuid();

        // Evaluation: the cheap one misses a requirement, so the dearer responsive bid ranks first.
        await Ok(await owner.PostAsJsonAsync($"/api/tenders/{id}/bids/{Bid("Cheap Chairs Ltd")}/evaluate", new { responsive = false, note = "No KRA PIN certificate attached" }));
        var evaluated = await Ok(await owner.PostAsJsonAsync($"/api/tenders/{id}/bids/{Bid("Quality Seats Ltd")}/evaluate", new { responsive = true }));
        Assert.Equal(1, evaluated.GetProperty("bids").EnumerateArray().Single(b => b.GetProperty("bidderName").GetString() == "Quality Seats Ltd").GetProperty("rank").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/tenders/{id}/award", new { bidId = Bid("Cheap Chairs Ltd") })).StatusCode);
        var awarded = await Ok(await owner.PostAsJsonAsync($"/api/tenders/{id}/award", new { bidId = Bid("Quality Seats Ltd") }));
        Assert.Equal("Awarded", awarded.GetProperty("status").GetString());
        Assert.Contains("Quality Seats Ltd", awarded.GetProperty("notices").GetProperty("regret").GetString());

        // The order exists (with a new supplier made from the bid), but can't go out during the standstill.
        var po = awarded.GetProperty("purchaseOrder");
        Assert.Equal(220_000_00, po.GetProperty("total").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/purchases/{po.GetProperty("id")}/post", null)).StatusCode);
        var suppliers = await Ok(await owner.GetAsync("/api/suppliers"));
        Assert.Contains("Quality Seats Ltd", suppliers.GetRawText());
    }

    [Fact]
    public async Task A_blind_stock_take_hides_the_expected_figures_until_review_and_books_the_difference()
    {
        var owner = await Owner();
        var cement = await StockedItem(owner, "Cement 50kg", 40, 700_00);
        var take = await Ok(await owner.PostAsJsonAsync("/api/stock-takes", new { blind = true }));
        var id = take.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null, take.GetProperty("lines")[0].GetProperty("expected").ValueKind); // the counter doesn't see 40
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/stock-takes", new { blind = true })).StatusCode); // one count per store at a time
        await Ok(await owner.PostAsJsonAsync($"/api/stock-takes/{id}/counts", new[] { new { itemId = cement, counted = 37 } }));
        var review = await Ok(await owner.GetAsync($"/api/stock-takes/{id}?review=true"));
        Assert.Equal(-3, review.GetProperty("lines")[0].GetProperty("difference").GetDecimal());
        Assert.Equal(-2_100_00, review.GetProperty("summary").GetProperty("value").GetInt64());
        var posted = await Ok(await owner.PostAsync($"/api/stock-takes/{id}/post", null));
        Assert.Equal("Posted", posted.GetProperty("status").GetString());
        Assert.Equal(-2_100_00, posted.GetProperty("varianceValue").GetInt64());
        var books = await Balances(owner);
        Assert.Equal(40 * 700_00 - 2_100_00, books["INVENTORY"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/stock-takes/{id}/counts", new[] { new { itemId = cement, counted = 1 } })).StatusCode);
    }
}
