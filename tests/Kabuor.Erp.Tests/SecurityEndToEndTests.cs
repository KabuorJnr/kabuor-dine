// End-to-end security checks that don't trust any one endpoint to remember the rules. They read every route the
// server actually maps and then try it: without signing in, as every role that shouldn't reach it, and as another
// business holding this one's record ids. Plus forged and stale tokens, the owner's team controls, and secrets
// staying out of the audit trail.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kabuor.Erp.Tests;

public class SecurityEndToEndTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    sealed record Api(string Method, string Pattern, string[] Policies);

    /// <summary>What anyone may call without signing in: health, signing up and in, Safaricom's callbacks (each
    /// checks its own secret in the URL), the web app's public settings, a business's public shop, the API, a driver's
    /// trip link (its secret token opens one trip on the road), and the
    /// fixed "not found" for API addresses that match nothing (it holds no data).</summary>
    static readonly HashSet<string> Public =
    [
        "/api/health", "/api/health/ready", "/api/auth/register", "/api/auth/login", "/api/auth/login/code",
        "/api/billing/mpesa/{secret}", "/api/client-config", "/api/legal",
        "/api/mpesa/stk/{key}", "/api/mpesa/c2b/{key}/validation", "/api/mpesa/c2b/{key}/confirmation",
        "/api/{**path}",
        "/api/drive/{token}/", "/api/drive/{token}/ping", "/api/drive/{token}/drops/{dropId:guid}",
        "/api/verified/s/{token}", "/api/verified/key",
        // Suppliers bid on a tender through its link (found by its secret token), and read a business's open tenders.
        "/api/bid/{token}/", "/api/tender-board/{slug}",
    ];
    // The public API (/api/v1) answers to an API key the owner made, checked on every call.
    static bool IsPublic(string pattern) => Public.Contains(pattern) || pattern.StartsWith("/api/public/{slug}") || pattern.StartsWith("/api/dine/m/{token}") || pattern.StartsWith("/api/v1/");

    List<Api> Endpoints() => factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
        .Where(e => ("/" + (e.RoutePattern.RawText ?? "").TrimStart('/')).StartsWith("/api/"))
        .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m =>
            new Api(m, "/" + e.RoutePattern.RawText!.TrimStart('/'), e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "").ToArray())))
        .ToList();

    static string Fill(string pattern, string? id = null) =>
        Regex.Replace(pattern, @"\{([^}:]+)(:[^}]+)?\}", m => m.Groups[2].Value.Contains("guid") ? id ?? Guid.NewGuid().ToString() : m.Groups[2].Value.Contains("int") ? "1" : "x");

    static Task<HttpResponseMessage> Send(HttpClient c, string method, string path) =>
        c.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = method is "GET" or "HEAD" or "DELETE" ? null : new StringContent("{}", Encoding.UTF8, "application/json") });

    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    async Task<(HttpClient Client, JsonElement Session)> Owner(string company)
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = company, ownerName = $"{company} Owner", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return (c, s);
    }

    async Task<HttpClient> Colleague(HttpClient owner, string role)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = $"{role} Person", email, role, password = "starting-password-1" }));
        var c = factory.CreateClient();
        var s = await TestSignIn.First(c, email, "starting-password-1");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    async Task<HttpClient> StaffPortal(HttpClient owner)
    {
        var phone = $"07{Random.Shared.Next(10_000_000, 99_999_999)}";
        var e = await Ok(await owner.PostAsJsonAsync("/api/employees", new { name = "Portal Person", phone, startDate = "2025-01-06", basicSalary = 30_000_00 }));
        var grant = await Ok(await owner.PostAsync($"/api/employees/{Id(e)}/access", null));
        var c = factory.CreateClient();
        var s = await TestSignIn.First(c, phone, grant.GetProperty("password").GetString()!);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    [Fact]
    public async Task Every_endpoint_except_the_public_ones_needs_a_sign_in()
    {
        var all = Endpoints();
        Assert.True(all.Count > 150, $"Only {all.Count} endpoints found: the route list didn't load.");
        var open = all.Where(e => e.Policies.Length == 0 && !IsPublic(e.Pattern)).Select(e => $"{e.Method} {e.Pattern}").Distinct().ToList();
        Assert.True(open.Count == 0, "Reachable without signing in: " + string.Join(", ", open));

        // And they really refuse: no token, then a well-formed token signed with someone else's key.
        var anon = factory.CreateClient();
        var forged = factory.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["cid"] = Guid.NewGuid().ToString(), ["role"] = "Owner", ["tv"] = "0" }, "someone-elses-key-someone-elses-key-0123456789"));
        var leaks = new List<string>();
        foreach (var e in all.Where(e => e.Policies.Length > 0))
        {
            var path = Fill(e.Pattern);
            if ((int)(await Send(anon, e.Method, path)).StatusCode != 401) leaks.Add($"no token: {e.Method} {path}");
            if ((int)(await Send(forged, e.Method, path)).StatusCode != 401) leaks.Add($"forged token: {e.Method} {path}");
        }
        Assert.True(leaks.Count == 0, string.Join("\n", leaks));
    }

    static string Token(Dictionary<string, object> claims, string key, string alg = SecurityAlgorithms.HmacSha256) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims, Issuer = "kabuor-erp", Audience = "kabuor-erp", Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), alg),
        });

    [Fact]
    public async Task Every_role_is_refused_every_endpoint_its_role_does_not_cover()
    {
        var (owner, _) = await Owner($"Roles Matrix {Guid.NewGuid():N}");
        var clients = new Dictionary<Role, HttpClient> { [Role.Owner] = owner };
        foreach (var role in new[] { Role.Accountant, Role.Sales, Role.Storekeeper, Role.Procurement, Role.HR, Role.Viewer })
            clients[role] = await Colleague(owner, role.ToString());
        clients[Role.Employee] = await StaffPortal(owner);

        var wrong = new List<string>();
        var tried = 0;
        foreach (var e in Endpoints().Where(e => e.Policies.Length > 0))
        {
            // A role gets in only if every policy on the route (its group's and its own) lets it.
            var allowed = Enum.GetValues<Role>().Where(r => e.Policies.All(p => p == "" || (Policies.Roles.TryGetValue(p, out var roles) && roles.Contains(r)))).ToHashSet();
            foreach (var (role, c) in clients.Where(kv => !allowed.Contains(kv.Key)))
            {
                tried++;
                var res = await Send(c, e.Method, Fill(e.Pattern));
                if (res.StatusCode != HttpStatusCode.Forbidden) wrong.Add($"{role}: {e.Method} {e.Pattern} → {(int)res.StatusCode}");
            }
        }
        Assert.True(tried > 300, $"Only {tried} refusals tried.");
        Assert.True(wrong.Count == 0, "Not refused:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public async Task Another_business_can_not_read_or_change_anything_by_its_ids()
    {
        const string marker = "ZQXSECRETA";
        var (a, aSession) = await Owner($"{marker} Traders");
        var ids = new List<Guid>();
        Guid Keep(JsonElement e) { var id = Id(e); ids.Add(id); return id; }

        var customer = Keep(await Ok(await a.PostAsJsonAsync("/api/customers", new { name = $"{marker} Customer", phone = "0722000111", address = $"{marker} Street" })));
        var item = Keep(await Ok(await a.PostAsJsonAsync("/api/items", new { name = $"{marker} Cement", price = 85_000 })));
        var invoice = await Ok(await a.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, notes = marker, lines = new[] { new { itemId = item, quantity = 2 } } }));
        Keep(invoice);
        await Ok(await a.PostAsync($"/api/sales/{Id(invoice)}/issue", null));
        Keep(await Ok(await a.PostAsJsonAsync("/api/sales", new { kind = "Quote", customerId = customer, lines = new[] { new { itemId = item, quantity = 1 } } })));
        var supplier = Keep(await Ok(await a.PostAsJsonAsync("/api/suppliers", new { name = $"{marker} Supplier" })));
        Keep(await Ok(await a.PostAsJsonAsync("/api/purchases", new { kind = "Order", supplierId = supplier, lines = new[] { new { itemId = item, quantity = 3, unitCost = 50_000 } } })));
        Keep(await Ok(await a.PostAsJsonAsync("/api/employees", new { name = $"{marker} Employee", phone = "0733000222", startDate = "2025-01-06", basicSalary = 40_000_00 })));
        Keep(await Ok(await a.PostAsJsonAsync("/api/crm/leads", new { name = $"{marker} Lead", phone = "0744000333" })));
        Keep((await Ok(await a.PostAsJsonAsync("/api/assets", new { name = $"{marker} Lorry", category = "VEH", serialNumber = "KZZ 001Z" }))).GetProperty("items")[0]);
        var trip = await Ok(await a.PostAsJsonAsync("/api/dispatch/trips", new { vehiclePlate = "KZZ 001Z", driverName = $"{marker} Driver", routeName = $"{marker} Route", deliveries = new[] { new { salesDocId = Id(invoice) } } }));
        Keep(trip);
        Keep(trip.GetProperty("deliveries")[0]);
        var opening = Keep(await Ok(await a.PostAsJsonAsync("/api/recruitment/openings", new { title = $"{marker} Driver job" })));
        Keep(await Ok(await a.PostAsJsonAsync("/api/recruitment/candidates", new { openingId = opening, name = $"{marker} Applicant", phone = "0755000444" })));
        Keep(await Ok(await a.PostAsJsonAsync("/api/settings/users", new { name = $"{marker} Clerk", email = $"{Guid.NewGuid():N}@example.com", role = "Sales", password = "starting-password-1" })));
        ids.Add(aSession.GetProperty("user").GetProperty("id").GetGuid());

        // The check can see a leak: A's own reads show the marker.
        Assert.Contains(marker, await (await a.GetAsync($"/api/customers/{customer}")).Content.ReadAsStringAsync());

        var (b, bSession) = await Owner($"Other Business {Guid.NewGuid():N}");
        var leaks = new List<string>();
        var endpoints = Endpoints().Where(e => e.Policies.Length > 0 && !e.Pattern.StartsWith("/api/auth/")).ToList();
        // Every route that takes a record id, with every one of A's ids.
        foreach (var e in endpoints.Where(e => Regex.Matches(e.Pattern, @":guid").Count == 1))
            foreach (var id in ids)
            {
                var res = await Send(b, e.Method, Fill(e.Pattern, id.ToString()));
                var body = await res.Content.ReadAsStringAsync();
                if (body.Contains(marker)) leaks.Add($"{e.Method} {e.Pattern} with {id} showed A's data ({(int)res.StatusCode})");
            }
        // And every list B can read.
        // (Not the live stream: it never ends. LiveSyncTests checks another business hears nothing on it.)
        foreach (var e in endpoints.Where(e => e.Method == "GET" && !e.Pattern.Contains('{') && e.Pattern != "/api/live"))
        {
            var body = await (await b.GetAsync(e.Pattern)).Content.ReadAsStringAsync();
            if (body.Contains(marker)) leaks.Add($"GET {e.Pattern} listed A's data");
        }
        Assert.True(leaks.Count == 0, string.Join("\n", leaks));

        // Nothing B sent changed anything in A: every change is audited, and none in A carries B's name.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
        var aId = aSession.GetProperty("company").GetProperty("id").GetGuid();
        var bName = bSession.GetProperty("user").GetProperty("name").GetString();
        var touched = await db.AuditEntries.IgnoreQueryFilters().Where(x => x.CompanyId == aId && x.Actor == bName).Select(x => $"{x.Action} {x.Entity} {x.EntityId}").ToListAsync();
        Assert.True(touched.Count == 0, "B changed A's records: " + string.Join(", ", touched));
    }

    [Fact]
    public async Task Tampered_and_stale_tokens_are_refused()
    {
        var (owner, session) = await Owner($"Tokens {Guid.NewGuid():N}");
        var token = session.GetProperty("token").GetString()!;
        var parts = token.Split('.');

        // The same claims with "no signature", and with another business's id swapped in under the real signature.
        var none = factory.CreateClient();
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        none.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{header}.{parts[1]}.");
        Assert.Equal(HttpStatusCode.Unauthorized, (await none.GetAsync("/api/customers")).StatusCode);

        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1]));
        var swapped = payload.Replace(session.GetProperty("company").GetProperty("id").GetString()!, Guid.NewGuid().ToString());
        var tampered = factory.CreateClient();
        tampered.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{parts[0]}.{Base64UrlEncoder.Encode(swapped)}.{parts[2]}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await tampered.GetAsync("/api/customers")).StatusCode);

        // A 2FA challenge ticket isn't a sign-in.
        var tokens = factory.Services.GetRequiredService<TokenIssuer>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == session.GetProperty("user").GetProperty("id").GetGuid());
            var challenge = factory.CreateClient();
            challenge.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Challenge(user));
            Assert.Equal(HttpStatusCode.Unauthorized, (await challenge.GetAsync("/api/customers")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/customers")).StatusCode);
    }

    [Fact]
    public async Task The_owner_controls_logins_and_every_change_ends_the_old_sessions()
    {
        var (owner, session) = await Owner($"Team {Guid.NewGuid():N}");
        var me = session.GetProperty("user").GetProperty("id").GetGuid();
        var email = $"{Guid.NewGuid():N}@example.com";
        var person = Id(await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = "Wanjiru Clerk", email, role = "Sales", password = "starting-password-1" })));

        // The starting password opens nothing but choosing your own.
        var first = factory.CreateClient();
        var s = await Ok(await first.PostAsJsonAsync("/api/auth/login", new { email, password = "starting-password-1" }));
        Assert.True(s.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        first.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        var blocked = await first.GetAsync("/api/customers");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("set-password", await blocked.Content.ReadAsStringAsync());
        var mine = await Ok(await first.PostAsJsonAsync("/api/auth/password", new { current = "starting-password-1", @new = "wanjiru-own-password" }));
        var clerk = factory.CreateClient();
        clerk.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mine.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await clerk.GetAsync("/api/customers")).StatusCode);

        // Only the owner manages logins.
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsync($"/api/settings/users/{me}/reset-password", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PutAsJsonAsync($"/api/settings/users/{person}/role", new { role = "Owner" })).StatusCode);

        // A new role takes effect at once: the old session ends.
        await Ok(await owner.PutAsJsonAsync($"/api/settings/users/{person}/role", new { role = "Viewer" }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await clerk.GetAsync("/api/customers")).StatusCode);
        var viewer = await TestSignIn.First(factory.CreateClient(), email, "wanjiru-own-password");
        Assert.Equal("Viewer", viewer.GetProperty("user").GetProperty("role").GetString());

        // A reset: the old password stops working, the one-time one must be replaced.
        var grant = await Ok(await owner.PostAsync($"/api/settings/users/{person}/reset-password", null));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "wanjiru-own-password" })).StatusCode);
        var again = await Ok(await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = grant.GetProperty("password").GetString() }));
        Assert.True(again.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());

        // Switched off: can't sign in, and nothing they did is lost.
        await Ok(await owner.PostAsync($"/api/settings/users/{person}/deactivate", null));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = grant.GetProperty("password").GetString() })).StatusCode);

        // Someone always runs the business; nobody locks themselves out; staff portal logins aren't made here.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/settings/users/{me}/role", new { role = "Viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"/api/settings/users/{me}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/settings/users", new { name = "Sneaky", email = $"{Guid.NewGuid():N}@example.com", role = "Employee", password = "starting-password-1" })).StatusCode);

        // Another business's owner can't touch these logins.
        var (other, _) = await Owner($"Other {Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/settings/users/{person}/reset-password", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/settings/users/{me}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task The_audit_trail_never_holds_keys_or_two_step_secrets()
    {
        var (owner, _) = await Owner($"Audit {Guid.NewGuid():N}");
        // Made-up values, different every run.
        var (key, secret, passkey) = ($"key-{Guid.NewGuid():N}", $"secret-{Guid.NewGuid():N}", $"pass-{Guid.NewGuid():N}");
        await Ok(await owner.PutAsJsonAsync("/api/settings/mpesa-prompt", new { environment = "sandbox", kind = "paybill", shortcode = "174379", consumerKey = key, consumerSecret = secret, passkey }));
        var twoStep = await Ok(await owner.PostAsync("/api/auth/security/2fa/start", null));
        var audit = await (await owner.GetAsync("/api/audit?take=500")).Content.ReadAsStringAsync();
        Assert.Contains("StkShortcode", audit);
        foreach (var value in new[] { key, secret, passkey, twoStep.GetProperty("secret").GetString()! })
            Assert.DoesNotContain(value, audit);
        Assert.DoesNotContain("TotpSecret\":\"", audit.Replace("TotpSecret\":\"(changed)", ""));
    }
}
