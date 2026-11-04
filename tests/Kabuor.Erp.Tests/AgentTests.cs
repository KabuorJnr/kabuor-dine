// Agents do safe work by themselves, ask before anything leaves the business or moves money, never repeat
// themselves, keep a timeline, and never act beyond the rights of the people involved.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

public class AgentTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    async Task<HttpClient> Owner(string company)
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = company, ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    async Task<HttpClient> Staff(HttpClient owner, string role)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = $"{role} Person", email, role, password = "another-long-password" }));
        var c = factory.CreateClient();
        var s = await TestSignIn.First(c, email, "another-long-password");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    static async Task<List<JsonElement>> Review(HttpClient c) => (await Ok(await c.GetAsync("/api/agents/tasks"))).GetProperty("items").EnumerateArray().ToList();

    [Fact]
    public async Task Collections_drafts_one_reminder_per_customer_and_sends_only_when_approved()
    {
        var c = await Owner("Collections Ltd");
        var customer = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Late Payer Shop", phone = "0722111222" }));
        var issued = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        foreach (var amount in new[] { 116000, 58000 })
        {
            var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer.GetProperty("id").GetGuid(), issueDate = issued, dueInDays = 7, lines = new[] { new { description = "Sauce", quantity = 1, unitPrice = amount } } }));
            await Ok(await c.PostAsync($"/api/sales/{inv.GetProperty("id")}/issue", null));
        }

        // Off until the owner switches it on; an accountant can't switch agents on.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/agents/collections/run", null)).StatusCode);
        var accountant = await Staff(c, "Accountant");
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutAsJsonAsync("/api/agents/collections", new { enabled = true })).StatusCode);
        await Ok(await c.PutAsJsonAsync("/api/agents/collections", new { enabled = true, settings = new { remindEveryDays = 7, minDaysLate = 1, language = "en" } }));

        var run = await Ok(await c.PostAsync("/api/agents/collections/run", null));
        Assert.Contains("Drafted 1 reminder", run.GetProperty("summary").GetString());
        await Ok(await c.PostAsync("/api/agents/collections/run", null)); // again: no second reminder for the same customer
        var tasks = await Review(c);
        var task = Assert.Single(tasks);
        Assert.Equal("Remind Late Payer Shop: KSh 1,740 overdue", task.GetProperty("title").GetString());
        Assert.Contains("KSh 1,740", task.GetProperty("draft").GetString());
        Assert.True(task.GetProperty("steps").GetArrayLength() >= 2);
        Assert.Equal(1, (await Ok(await c.GetAsync("/api/today"))).GetProperty("agentTasks").GetInt32());

        // HR can't see or approve it; a salesperson can, and can reword it first.
        var hr = await Staff(c, "HR");
        Assert.Empty(await Review(hr));
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsJsonAsync($"/api/agents/tasks/{task.GetProperty("id")}/approve", new { })).StatusCode);
        var sales = await Staff(c, "Sales");
        var done = await Ok(await sales.PostAsJsonAsync($"/api/agents/tasks/{task.GetProperty("id")}/approve", new { text = "Hello, KSh 1,740 is overdue. Kindly pay by M-Pesa. Thanks!" }));
        Assert.StartsWith("https://wa.me/254722111222?text=Hello", done.GetProperty("result").GetString()); // no SMS key in tests: WhatsApp, person taps send
        Assert.Empty(await Review(c));
        var activities = await Ok(await c.GetAsync($"/api/crm/activities?customerId={customer.GetProperty("id")}"));
        Assert.Contains("Payment reminder", activities[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Restock_drafts_an_order_to_the_last_supplier_and_sending_needs_approval()
    {
        var c = await Owner("Restock Ltd");
        var supplier = await Ok(await c.PostAsJsonAsync("/api/suppliers", new { name = "Mwangi Farm" }));
        var item = await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Fresh chillies", price = 0, trackStock = true, reorderLevel = 10, unit = "kg" }));
        var bill = await Ok(await c.PostAsJsonAsync("/api/purchases", new { kind = "Bill", supplierId = supplier.GetProperty("id").GetGuid(), supplierRef = "MF-1", lines = new[] { new { itemId = item.GetProperty("id").GetGuid(), quantity = 4, unitCost = 20000 } } }));
        await Ok(await c.PostAsync($"/api/purchases/{bill.GetProperty("id")}/post", null));

        await Ok(await c.PutAsJsonAsync("/api/agents/restock", new { enabled = true }));
        Assert.Contains("Drafted 1 purchase order", (await Ok(await c.PostAsync("/api/agents/restock/run", null))).GetProperty("summary").GetString());
        Assert.Contains("already on order", (await Ok(await c.PostAsync("/api/agents/restock/run", null))).GetProperty("summary").GetString());

        var task = Assert.Single(await Review(c));
        Assert.Contains("to Mwangi Farm", task.GetProperty("title").GetString());
        var orders = await Ok(await c.GetAsync("/api/purchases?kind=Order"));
        var order = orders.GetProperty("page").GetProperty("items")[0];
        Assert.Equal("Draft", order.GetProperty("status").GetString()); // drafted by the agent, not sent
        await Ok(await c.PostAsJsonAsync($"/api/agents/tasks/{task.GetProperty("id")}/approve", new { }));
        var sent = await Ok(await c.GetAsync($"/api/purchases/{order.GetProperty("id")}"));
        Assert.Equal("Sent", sent.GetProperty("document").GetProperty("status").GetString());
        Assert.Equal(16m, sent.GetProperty("document").GetProperty("lines")[0].GetProperty("quantity").GetDecimal()); // back to twice the reorder level
    }

    [Fact]
    public async Task Matching_asks_about_payments_it_cant_place_and_applies_on_approval()
    {
        var c = await Owner("Matching Ltd");
        var customer = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Exact Amount Buyer" }));
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer.GetProperty("id").GetGuid(), lines = new[] { new { description = "Sauce", quantity = 1, unitPrice = 77700 } } }));
        await Ok(await c.PostAsync($"/api/sales/{inv.GetProperty("id")}/issue", null));
        // A payment from an unknown phone for the exact amount: a suggestion, not certain enough to apply alone.
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = 77700, reference = "TQZ9Y8X7W6", payerPhone = "0799000111", payerName = "SOMEONE ELSE" }));

        await Ok(await c.PutAsJsonAsync("/api/agents/matching", new { enabled = true }));
        await Ok(await c.PostAsync("/api/agents/matching/run", null));
        var task = Assert.Single(await Review(c));
        Assert.StartsWith("Apply KSh 777 from SOMEONE ELSE to", task.GetProperty("title").GetString());
        await Ok(await c.PostAsJsonAsync($"/api/agents/tasks/{task.GetProperty("id")}/approve", new { }));
        var doc = await Ok(await c.GetAsync($"/api/sales/{inv.GetProperty("id")}"));
        Assert.Equal("Paid", doc.GetProperty("document").GetProperty("status").GetString());
        var done = (await Ok(await c.GetAsync("/api/agents/tasks?status=done"))).GetProperty("items")[0];
        Assert.Contains(done.GetProperty("steps").EnumerateArray(), s => s.GetProperty("who").GetString() == "Owner Person");
    }

    [Fact]
    public async Task The_scheduler_runs_switched_on_agents_as_themselves()
    {
        var c = await Owner("Scheduled Ltd");
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = 12300, reference = "TSC1D2E3F4", payerPhone = "0799000222", payerName = "UNKNOWN PAYER" }));
        await Ok(await c.PutAsJsonAsync("/api/agents/matching", new { enabled = true }));
        await factory.Services.GetRequiredService<Kabuor.Erp.Agents.AgentScheduler>().RunDue();
        var task = Assert.Single(await Review(c));
        Assert.Equal("What is KSh 123 from UNKNOWN PAYER for?", task.GetProperty("title").GetString());
        var agents = (await Ok(await c.GetAsync("/api/agents"))).EnumerateArray().ToList();
        Assert.Contains("needs you", agents.Single(a => a.GetProperty("key").GetString() == "matching").GetProperty("lastRunSummary").GetString());
        var audit = await Ok(await c.GetAsync("/api/audit?entity=AgentTask"));
        Assert.Equal("Payment matching agent", audit[0].GetProperty("actor").GetString());
    }
}
