// The module workspaces stay in step: a save in one is announced on the live stream of every open screen of that
// business (and no other), labelled only for people who may open that module; each workspace's home shows its own
// activity and what the other modules handed it, and answers 403 to people who can't open the module.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class LiveSyncTests(ErpFactory factory) : IClassFixture<ErpFactory>
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
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"Live {Guid.NewGuid():N}", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
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

    /// <summary>Opens the live stream and reads "change" events from it until one matches or time runs out.</summary>
    sealed class Stream(HttpResponseMessage res, StreamReader reader) : IAsyncDisposable
    {
        public static async Task<Stream> Open(HttpClient c)
        {
            var res = await c.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/live"), HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
            var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
            // The greeting arrives first, so the subscription is in place before the test changes anything.
            while (await reader.ReadLineAsync() is { } line && !line.StartsWith(": connected")) { }
            return new Stream(res, reader);
        }

        public async Task<JsonElement?> Next(Func<JsonElement, bool> match, TimeSpan within)
        {
            using var cts = new CancellationTokenSource(within);
            try
            {
                while (await reader.ReadLineAsync(cts.Token) is { } line)
                {
                    if (!line.StartsWith("data: ")) continue;
                    var e = JsonDocument.Parse(line[6..]).RootElement;
                    if (match(e)) return e;
                }
            }
            catch (OperationCanceledException) { }
            return null;
        }

        public ValueTask DisposeAsync()
        {
            reader.Dispose();
            res.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    static bool Has(JsonElement e, string entity) => e.GetProperty("changes").EnumerateArray().Any(c => c.GetProperty("entity").GetString() == entity);

    [Fact]
    public async Task A_save_in_one_workspace_reaches_the_other_screens_of_that_business_only()
    {
        var owner = await Owner();
        var sales = await As(owner, "Sales");
        var other = await Owner();
        await using var ownerLive = await Stream.Open(owner);
        await using var otherLive = await Stream.Open(other);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/customers") { Content = JsonContent.Create(new { name = "Live Shop Ltd", phone = "0711000111" }) };
        req.Headers.Add("X-Client", "tab-123");
        await Ok(await sales.SendAsync(req));

        var e = await ownerLive.Next(x => Has(x, "Customer"), TimeSpan.FromSeconds(10));
        Assert.NotNull(e);
        var ev = e.Value;
        Assert.Contains("sales", ev.GetProperty("modules").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal("tab-123", ev.GetProperty("client").GetString()); // the tab that saved can tell it was its own
        Assert.Equal("Sales Person", ev.GetProperty("actor").GetString());
        var change = ev.GetProperty("changes").EnumerateArray().First(c => c.GetProperty("entity").GetString() == "Customer");
        Assert.Equal("sales", change.GetProperty("module").GetString());
        Assert.Equal("Customer Live Shop Ltd added", change.GetProperty("what").GetString());

        // Another business hears nothing of it.
        Assert.Null(await otherLive.Next(x => Has(x, "Customer"), TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public async Task People_who_cant_open_a_module_hear_that_it_changed_but_not_what_or_who()
    {
        var owner = await Owner();
        var hr = await As(owner, "HR");
        await using var hrLive = await Stream.Open(hr);

        await Ok(await owner.PostAsJsonAsync("/api/customers", new { name = "Private Client", phone = "0711000222" }));
        var e = await hrLive.Next(x => x.GetProperty("modules").EnumerateArray().Any(m => m.GetString() == "sales"), TimeSpan.FromSeconds(10));
        Assert.NotNull(e);
        Assert.Empty(e.Value.GetProperty("changes").EnumerateArray());
        Assert.Equal("", e.Value.GetProperty("actor").GetString());
        Assert.DoesNotContain("Private Client", e.Value.GetRawText());
    }

    [Fact]
    public async Task Each_workspace_shows_its_own_activity_and_the_handoffs_from_the_others()
    {
        var owner = await Owner();
        await Ok(await owner.PostAsJsonAsync("/api/customers", new { name = "Workspace Buyer", phone = "0711000333" }));
        await Ok(await owner.PostAsJsonAsync("/api/items", new { name = "Workspace Widget", unit = "pc", price = 100_00, trackStock = true }));

        var ws = await Ok(await owner.GetAsync("/api/workspaces/sales"));
        Assert.Equal("sales", ws.GetProperty("module").GetString());
        Assert.Contains(ws.GetProperty("activity").EnumerateArray(), a => a.GetProperty("what").GetString() == "Customer Workspace Buyer added");
        // Items belong to procurement & supply; sales hears about them as a handoff (prices and stock it sells from).
        Assert.Contains(ws.GetProperty("handoffs").EnumerateArray(), a => a.GetProperty("entity").GetString() == "Item" && a.GetProperty("module").GetString() == "procurement");
        Assert.DoesNotContain(ws.GetProperty("activity").EnumerateArray(), a => a.GetProperty("entity").GetString() == "Item");
        Assert.All(ws.GetProperty("links").EnumerateArray(), l => Assert.Contains(l.GetProperty("key").GetString(), new[] { "sales", "marketing" }));
        Assert.True(ws.GetProperty("today").GetProperty("changes").GetInt32() >= 1);

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/workspaces/nowhere")).StatusCode);
    }

    [Fact]
    public async Task A_workspace_opens_only_for_people_who_can_open_its_module()
    {
        var owner = await Owner();
        var hr = await As(owner, "HR");
        Assert.Equal(["people"], (await Ok(await hr.GetAsync("/api/workspaces"))).EnumerateArray().Select(m => m.GetString()).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.GetAsync("/api/workspaces/finance")).StatusCode);
        await Ok(await hr.GetAsync("/api/workspaces/people"));
        // HR's handoffs never include modules HR can't open.
        var people = await Ok(await hr.GetAsync("/api/workspaces/people"));
        Assert.Empty(people.GetProperty("watches").EnumerateArray());
        Assert.Empty(people.GetProperty("handoffs").EnumerateArray());
        Assert.Equal(6, (await Ok(await owner.GetAsync("/api/workspaces"))).GetArrayLength());
    }
}
