// The sharper agents, checked against how the big ERPs behave:
//  - review policy: alone only when sure and within the owner's limit; a guess always asks; some agents always ask;
//  - business events wake an agent within a minute;
//  - intake agents: a customer's message becomes a draft quote (unmatched lines named, never guessed), and a
//    supplier's KRA invoice becomes a bill matched to its purchase order;
//  - the studio: an agent the owner describes runs only with the skills it was given, and files findings.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kabuor.Erp.Agents;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

/// <summary>Plays Claude for the studio test: looks something up, files a finding, then says it's done.</summary>
public sealed class ScriptedClaude : HttpMessageHandler
{
    public readonly ConcurrentQueue<JsonObject> Requests = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!.AsObject();
        Requests.Enqueue(body);
        var turns = body["messages"]!.AsArray().Count;
        JsonObject reply = turns switch
        {
            1 => ToolUse("t1", "today_summary", new JsonObject()),
            3 => ToolUse("t2", "note_finding", new JsonObject { ["title"] = "Three things for today", ["detail"] = "Chase the overdue invoice first." }),
            _ => new JsonObject { ["stop_reason"] = "end_turn", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Wrote today's briefing." }) },
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply) };
    }

    static JsonObject ToolUse(string id, string name, JsonObject input) => new()
    {
        ["stop_reason"] = "tool_use",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input }),
    };
}

public sealed class StudioFactory : WebApplicationFactory<Program>
{
    public readonly ScriptedClaude Claude = new();
    readonly string _db = Path.Combine(Path.GetTempPath(), $"kabuor-erp-studio-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Erp", ErpFactory.Pg is null ? $"Data Source={_db}" : $"{ErpFactory.Pg};Database=kabuor_erp_studio_{Guid.NewGuid():N}");
        builder.UseSetting("Jwt:Key", "test-key-test-key-test-key-test-key-0123456789");
        builder.UseSetting("Agents:Disabled", "true");
        builder.UseSetting("Owners:OversightByDefault", "false"); // the owner does the work in these tests
        builder.UseSetting("Billing:PayToStart", "false");
        builder.UseSetting("Anthropic:ApiKey", "test-key");
        builder.ConfigureTestServices(s => s.AddHttpClient("anthropic", c => c.BaseAddress = new Uri("https://claude.test/")).ConfigurePrimaryHttpMessageHandler(() => Claude));
    }
}

public class AgentPolicyTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    internal static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    internal static async Task<(HttpClient c, string company)> Owner(WebApplicationFactory<Program> f)
    {
        var c = f.CreateClient();
        var company = $"Agents {Guid.NewGuid():N}";
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = company, ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return (c, company);
    }

    static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();
    static async Task<List<JsonElement>> Tasks(HttpClient c, string status = "") =>
        (await Ok(await c.GetAsync($"/api/agents/tasks?status={status}"))).GetProperty("items").EnumerateArray().ToList();

    async Task<Guid> Invoice(HttpClient c, Guid customer, long amount)
    {
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, lines = new[] { new { description = "Goods", quantity = 1, unitPrice = amount } } }));
        await Ok(await c.PostAsync($"/api/sales/{Id(inv)}/issue", null));
        return Id(inv);
    }

    [Fact]
    public async Task Matching_acts_alone_only_on_a_likely_match_within_the_limit()
    {
        var (c, _) = await Owner(factory);
        var shop = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Wanjiru Shop", phone = "0722333444" })));
        await Invoice(c, shop, 10_000_00);
        var other = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Kamau Traders" })));
        await Invoice(c, other, 777_00);

        // Some agents always ask: the policy can't be loosened for them.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/agents/orders", new { enabled = true, settings = new { review = "sure" } })).StatusCode);
        await Ok(await c.PutAsJsonAsync("/api/agents/matching", new { enabled = true, settings = new { review = "sure", limit = 5_000 } }));

        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = 4_000_00, reference = "TKA1111111", payerPhone = "0722333444" })); // her phone, part payment
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = 6_000_00, reference = "TKA2222222", payerPhone = "0722333444" })); // her phone, over the limit
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = 777_00, reference = "TKA3333333", payerPhone = "0799000000" }));  // a stranger, same amount: a guess

        await Ok(await c.PostAsync("/api/agents/matching/run", null));
        var done = await Tasks(c, "done");
        var alone = done.Single(t => t.GetProperty("byItself").GetBoolean() && t.GetProperty("title").GetString()!.Contains("4,000"));
        Assert.Contains("within your KSh 5,000 limit", string.Join(" ", alone.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("text").GetString())));
        var asking = await Tasks(c);
        Assert.Contains(asking, t => t.GetProperty("title").GetString()!.Contains("6,000"));
        Assert.Contains(asking, t => t.GetProperty("title").GetString()!.Contains("777"));

        var card = (await Ok(await c.GetAsync("/api/agents"))).EnumerateArray().Single(a => a.GetProperty("key").GetString() == "matching");
        Assert.Equal(1, card.GetProperty("month").GetProperty("byItself").GetInt32());
        Assert.Equal(2, card.GetProperty("toReview").GetInt32());
    }

    [Fact]
    public async Task A_payment_nobody_can_place_wakes_the_matching_agent_within_a_minute()
    {
        var (c, company) = await Owner(factory);
        await Ok(await c.PutAsJsonAsync("/api/agents/matching", new { enabled = true }));
        await Ok(await c.PostAsync("/api/agents/matching/run", null)); // ran just now: not due for an hour
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
            var cid = await db.Companies.IgnoreQueryFilters().Where(x => x.Name == company).Select(x => x.Id).SingleAsync();
            await db.AgentSetups.IgnoreQueryFilters().Where(s => s.CompanyId == cid).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastRunAt, DateTimeOffset.UtcNow.AddMinutes(-2)));
        }
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = 1_234_00, reference = "TKB9999999", payerPhone = "0700111222" }));
        await factory.Services.GetRequiredService<AgentScheduler>().RunDue();
        Assert.Contains(await Tasks(c), t => t.GetProperty("title").GetString()!.Contains("1,234"));
    }

    [Fact]
    public async Task A_pasted_WhatsApp_order_becomes_a_draft_quote_and_unknown_lines_are_named_not_guessed()
    {
        var (c, _) = await Owner(factory);
        var soda = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Soda 500ml", unit = "bottle", price = 100_00 })));
        await Ok(await c.PutAsJsonAsync($"/api/items/{soda}/units", new[] { new { name = "Crate", factor = 24, price = 2_200_00 } }));
        await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Milk 500ml", unit = "packet", price = 60_00 }));
        await Ok(await c.PostAsJsonAsync("/api/items", new { name = "White bread", unit = "loaf", price = 70_00 }));
        await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Brown bread", unit = "loaf", price = 75_00 }));
        var hotel = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Hotel Jambo", phone = "0711000222" })));

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/agents/orders/intake", new { text = "2 milk", customerId = hotel })).StatusCode); // off
        await Ok(await c.PutAsJsonAsync("/api/agents/orders", new { enabled = true }));
        var r = await Ok(await c.PostAsJsonAsync("/api/agents/orders/intake", new { text = "Habari, naomba 2 crates soda, milk 500ml x 3, 4 bread na 1 unicorn cake. Asante", customerId = hotel }));
        var unmatched = r.GetProperty("unmatched").EnumerateArray().Select(u => u.GetString()!).ToList();
        Assert.Contains(unmatched, u => u.Contains("unicorn"));
        Assert.Contains(unmatched, u => u.Contains("White bread") && u.Contains("Brown bread")); // a tie is a question
        Assert.Equal(2 * 2_200_00 + 3 * 60_00, r.GetProperty("quote").GetProperty("total").GetInt64());

        var task = (await Tasks(c)).Single(t => t.GetProperty("title").GetString()!.StartsWith("Send quote"));
        Assert.Contains("unicorn", string.Join(" ", task.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("text").GetString())));
        var approved = await Ok(await c.PostAsync($"/api/agents/tasks/{task.GetProperty("id")}/approve", null));
        Assert.StartsWith("https://wa.me/254711000222", approved.GetProperty("result").GetString());
        var quote = (await Ok(await c.GetAsync($"/api/sales/{r.GetProperty("quote").GetProperty("id")}"))).GetProperty("document");
        Assert.Equal("Sent", quote.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_supplier_invoice_from_KRA_matching_an_open_order_is_recorded_alone_within_the_limit()
    {
        var (c, company) = await Owner(factory);
        var supplier = Id(await Ok(await c.PostAsJsonAsync("/api/suppliers", new { name = "Bidco Kenya", kraPin = "P000111222B" })));
        var po = await Ok(await c.PostAsJsonAsync("/api/purchases", new { kind = "Order", supplierId = supplier, lines = new[] { new { description = "Cooking oil 20L", quantity = 2, unitCost = 4_500_00 } } }));
        await Ok(await c.PostAsync($"/api/purchases/{Id(po)}/post", null)); // sent to the supplier
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
            var cid = await db.Companies.IgnoreQueryFilters().Where(x => x.Name == company).Select(x => x.Id).SingleAsync();
            db.EtimsPurchases.Add(new EtimsPurchase { CompanyId = cid, SupplierPin = "P000111222B", SupplierName = "BIDCO KENYA LTD", SupplierInvoiceNo = 5521, Date = DateOnly.FromDateTime(DateTime.UtcNow), Total = 9_000_00, Tax = 1_241_38,
                Lines = """[{"name":"Cooking oil 20L","quantity":2,"unitPrice":450000,"taxBand":"B","total":900000,"tax":124138}]""" });
            db.EtimsPurchases.Add(new EtimsPurchase { CompanyId = cid, SupplierPin = "P999888777Z", SupplierName = "NEW SUPPLIER LTD", SupplierInvoiceNo = 77, Date = DateOnly.FromDateTime(DateTime.UtcNow), Total = 1_000_00, Tax = 0,
                Lines = """[{"name":"Sundries","quantity":1,"unitPrice":100000,"taxBand":"D","total":100000,"tax":0}]""" });
            await db.SaveChangesAsync();
        }
        await Ok(await c.PutAsJsonAsync("/api/agents/payables", new { enabled = true, settings = new { review = "sure", limit = 20_000 } }));
        var run = await Ok(await c.PostAsync("/api/agents/payables/run", null));
        Assert.Contains("Recorded 1", run.GetProperty("summary").GetString());

        var order = (await Ok(await c.GetAsync($"/api/purchases/{Id(po)}"))).ToString();
        Assert.Contains("Received", order);
        var waiting = await Tasks(c);
        var ask = waiting.Single(t => t.GetProperty("title").GetString()!.Contains("NEW SUPPLIER"));
        Assert.Contains("isn't one of your suppliers", string.Join(" ", ask.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("text").GetString())));
    }
}

public class AgentStudioTests(StudioFactory factory) : IClassFixture<StudioFactory>
{
    [Fact]
    public async Task An_agent_described_in_plain_words_works_only_with_its_skills_and_files_findings()
    {
        var (c, _) = await AgentPolicyTests.Owner(factory);
        var studio = await AgentPolicyTests.Ok(await c.GetAsync("/api/agents/studio"));
        Assert.True(studio.GetProperty("aiReady").GetBoolean());
        Assert.True(studio.GetProperty("templates").GetArrayLength() >= 3);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/agents/custom", new { name = "Bad", instructions = "Do everything you can think of for me.", skills = new[] { "drop_tables" }, every = "daily" })).StatusCode);
        var made = await AgentPolicyTests.Ok(await c.PostAsJsonAsync("/api/agents/custom", new { name = "Morning briefing", instructions = "Every morning, look at today's summary and write one finding with the three things to do first.", skills = new[] { "today_summary" }, every = "daily" }));
        var key = made.GetProperty("key").GetString()!;
        await AgentPolicyTests.Ok(await c.PutAsJsonAsync($"/api/agents/{key}", new { enabled = true }));
        var run = await AgentPolicyTests.Ok(await c.PostAsync($"/api/agents/{key}/run", null));
        Assert.Contains("1 finding", run.GetProperty("summary").GetString());

        // It was only offered the skills it was given (plus writing findings).
        var tools = factory.Claude.Requests.First()["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).OrderBy(x => x).ToArray();
        Assert.Equal(["note_finding", "today_summary"], tools);
        Assert.Contains("<job>Every morning", factory.Claude.Requests.First()["system"]!.GetValue<string>());

        var tasks = (await AgentPolicyTests.Ok(await c.GetAsync("/api/agents/tasks"))).GetProperty("items").EnumerateArray().ToList();
        var finding = tasks.Single(t => t.GetProperty("agent").GetString() == "Morning briefing");
        Assert.Equal("Three things for today", finding.GetProperty("title").GetString());

        // Changing its job switches it off until the owner switches it on again.
        await AgentPolicyTests.Ok(await c.PutAsJsonAsync($"/api/agents/custom/{key}", new { name = "Morning briefing", instructions = "Every morning, look at today's summary and tell me only the single most urgent thing.", skills = new[] { "today_summary" }, every = "daily" }));
        var card = (await AgentPolicyTests.Ok(await c.GetAsync("/api/agents"))).EnumerateArray().Single(a => a.GetProperty("key").GetString() == key);
        Assert.False(card.GetProperty("enabled").GetBoolean());
    }
}
