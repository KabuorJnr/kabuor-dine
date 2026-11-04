using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Common;
using Kabuor.Erp.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

public class ScalingArchitectureTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    HttpClient With(string token)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    async Task<(string Email, string Token, string CompanyId)> RegisterOwner(string name)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        var res = await Ok(await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            companyName = name,
            ownerName = "Owner " + name,
            email,
            phone = "0712345678",
            password = "a-long-password-1234",
            kraPin = "P051234567X"
        }));
        var token = res.GetProperty("token").GetString()!;
        var companyId = res.GetProperty("company").GetProperty("id").GetString()!;
        return (email, token, companyId);
    }

    [Fact]
    public async Task Health_checks_are_accessible_and_healthy()
    {
        var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public void Telemetry_and_metrics_can_be_recorded_without_exceptions()
    {
        var companyId = Guid.NewGuid();

        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == ErpTelemetry.ServiceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        // Activity span start and tags
        using (var activity = ErpTelemetry.StartPaymentActivity("TestPayment", "mpesa", companyId, "TEST-REF-123"))
        {
            Assert.NotNull(activity);
            Assert.Equal("mpesa", activity.GetTagItem("payment.channel"));
        }

        // Metrics counters
        ErpTelemetry.RecordPayment("mpesa", 150000, true, companyId);
        ErpTelemetry.RecordInvoice("invoice", 250000, companyId);
        ErpTelemetry.RecordSecurityEvent("login_failure", "rate_limited", "127.0.0.1");
        ErpTelemetry.RecordWebhookDelivery("invoice.paid", true);
    }

    [Fact]
    public async Task Tenant_isolation_is_strictly_enforced_between_different_companies()
    {
        var (email1, token1, cid1) = await RegisterOwner("Company A");
        var (email2, token2, cid2) = await RegisterOwner("Company B");

        var clientA = With(token1);
        var clientB = With(token2);

        // Company A adds a customer
        var custA = await Ok(await clientA.PostAsJsonAsync("/api/customers", new
        {
            name = "Unique Customer of A",
            phone = "0711111111"
        }));
        var custAId = custA.GetProperty("id").GetString()!;

        // Company B lists customers: Company A's customer MUST NOT appear
        var listB = await Ok(await clientB.GetAsync("/api/customers"));
        var items = listB.GetProperty("page").GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(items, item => item.GetProperty("id").GetString() == custAId);

        // Company B attempts to directly access Company A's customer: MUST return 404 Not Found
        var getDirect = await clientB.GetAsync($"/api/customers/{custAId}");
        Assert.Equal(HttpStatusCode.NotFound, getDirect.StatusCode);
    }

    [Fact]
    public async Task Onboarding_status_and_quickstart_generates_first_invoice()
    {
        var (email, token, cid) = await RegisterOwner("Quickstart Corp");
        var client = With(token);

        // Check onboarding status initially
        var statusInit = await Ok(await client.GetAsync("/api/onboarding/status"));
        Assert.True(statusInit.GetProperty("companyProfileCompleted").GetBoolean());
        Assert.False(statusInit.GetProperty("firstInvoiceCreated").GetBoolean());

        // Run quickstart setup: connects M-Pesa, adds customer & item, generates first invoice
        var quickstart = await Ok(await client.PostAsJsonAsync("/api/onboarding/quickstart", new
        {
            mpesaShortcode = "174379",
            stkEnvironment = "sandbox",
            customer = new
            {
                name = "First Enterprise Client",
                phone = "0722000000",
                email = "client@enterprise.com"
            },
            item = new
            {
                name = "Annual ERP License",
                unitPriceCents = 3500000, // KSh 35,000
                unit = "license"
            },
            quantity = 1,
            createFirstInvoice = true
        }));

        Assert.Equal("Ready", quickstart.GetProperty("status").GetString());
        var invoiceNumber = quickstart.GetProperty("invoiceNumber").GetString();
        Assert.NotNull(invoiceNumber);
        Assert.StartsWith("INV-", invoiceNumber);

        // Verify status is now updated
        var statusAfter = await Ok(await client.GetAsync("/api/onboarding/status"));
        Assert.True(statusAfter.GetProperty("firstCustomerAdded").GetBoolean());
        Assert.True(statusAfter.GetProperty("firstItemAdded").GetBoolean());
        Assert.True(statusAfter.GetProperty("firstInvoiceCreated").GetBoolean());
        Assert.True(statusAfter.GetProperty("isReadyToTransact").GetBoolean());
    }

    [Fact]
    public async Task Network_headers_and_security_controls_are_enforced()
    {
        var client = factory.CreateClient();
        var res = await client.GetAsync("/health");

        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").FirstOrDefault());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").FirstOrDefault());
        Assert.Equal("same-origin", res.Headers.GetValues("Cross-Origin-Opener-Policy").FirstOrDefault());
        Assert.Equal("same-origin", res.Headers.GetValues("Cross-Origin-Resource-Policy").FirstOrDefault());
        Assert.Equal("none", res.Headers.GetValues("X-Permitted-Cross-Domain-Policies").FirstOrDefault());

        var csp = res.Headers.GetValues("Content-Security-Policy").FirstOrDefault();
        Assert.NotNull(csp);
        Assert.Contains("upgrade-insecure-requests", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
    }

    [Fact]
    public async Task Dangerous_http_methods_are_rejected()
    {
        var client = factory.CreateClient();

        using var traceReq = new HttpRequestMessage(new HttpMethod("TRACE"), "/api/health");
        var traceRes = await client.SendAsync(traceReq);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, traceRes.StatusCode);

        using var trackReq = new HttpRequestMessage(new HttpMethod("TRACK"), "/api/health");
        var trackRes = await client.SendAsync(trackReq);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, trackRes.StatusCode);
    }

    [Fact]
    public async Task Ssrf_webhook_registration_is_blocked()
    {
        var (email, token, cid) = await RegisterOwner("SSRF Shield Corp");
        var client = With(token);

        // Attempt to register internal loopback
        var loopbackRes = await client.PostAsJsonAsync("/api/settings/webhooks", new
        {
            url = "https://127.0.0.1/evil-callback",
            events = "payment.received"
        });
        Assert.Equal(HttpStatusCode.BadRequest, loopbackRes.StatusCode);

        // Attempt to register cloud metadata IP (AWS/GCP/Azure link-local)
        var metadataRes = await client.PostAsJsonAsync("/api/settings/webhooks", new
        {
            url = "https://169.254.169.254/latest/meta-data/",
            events = "payment.received"
        });
        Assert.Equal(HttpStatusCode.BadRequest, metadataRes.StatusCode);
    }

    [Fact]
    public void Csv_memory_exhaustion_payload_is_rejected()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Date,Receipt No,Details,Paid In,Withdrawn,Balance");
        for (int i = 0; i < 10_005; i++)
        {
            sb.AppendLine($"2026-10-01,REF{i:D6},Test transaction,100.00,,5000.00");
        }

        var ex = Assert.Throws<AppException>(() => Kabuor.Erp.Accounting.StatementService.Csv(sb.ToString()));
        Assert.Contains("maximum limit of 10,000 rows", ex.Message);
    }

    [Fact]
    public async Task Unauthenticated_requests_cannot_access_employee_self_service()
    {
        var unauthenticatedClient = factory.CreateClient();
        var res = await unauthenticatedClient.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        var payslipRes = await unauthenticatedClient.GetAsync($"/api/me/payslips/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, payslipRes.StatusCode);
    }

    [Fact]
    public async Task Cross_tenant_api_key_cannot_access_other_tenant_data()
    {
        // 1. Company A registers and creates an API key
        var (_, tokenA, _) = await RegisterOwner("Tenant Alpha");
        var clientA = With(tokenA);
        var keyRes = await Ok(await clientA.PostAsJsonAsync("/api/settings/api-keys", new { name = "Alpha Service Key" }));
        var apiKey = keyRes.GetProperty("key").GetString()!;

        // 2. Company B registers and creates a private item
        var (_, tokenB, _) = await RegisterOwner("Tenant Beta");
        var clientB = With(tokenB);
        await Ok(await clientB.PostAsJsonAsync("/api/items", new
        {
            name = "Confidential Beta Blueprint",
            price = 5000000L,
            active = true
        }));

        // 3. Alpha invokes public v1 API with its own key
        var v1Client = factory.CreateClient();
        v1Client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        var itemsRes = await Ok(await v1Client.GetAsync("/api/v1/items"));

        // 4. Verify Beta's confidential items are completely hidden from Alpha
        var itemsJson = itemsRes.ToString();
        Assert.DoesNotContain("Confidential Beta Blueprint", itemsJson);
    }

    [Fact]
    public async Task Tracking_link_visitor_cookie_is_sanitized_against_injection()
    {
        var (_, token, _) = await RegisterOwner("Tracking Promo Corp");
        var client = With(token);

        // Create tracking link
        var linkRes = await Ok(await client.PostAsJsonAsync("/api/links", new
        {
            name = "Promo Campaign WhatsApp",
            channel = 0,
            target = "shop"
        }));
        var code = linkRes.GetProperty("code").GetString()!;

        // Send request with malicious/dirty visitor cookie
        var pubClient = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        pubClient.DefaultRequestHeaders.Add("Cookie", "kv=malicious_attempt_with_invalid_char!;");
        var resp = await pubClient.GetAsync($"/l/{code}");

        // Assert response redirect and verify sanitized cookie was set
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Redirect || resp.StatusCode == HttpStatusCode.Found || resp.Headers.Contains("Set-Cookie"), $"Got {(int)resp.StatusCode}: {body}");
        if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var kvCookie = cookies.FirstOrDefault(c => c.StartsWith("kv="));
            if (kvCookie != null)
            {
                // Must not contain the dirty invalid character
                Assert.DoesNotContain("malicious_attempt_with_invalid_char!", kvCookie);
            }
        }
    }

    [Fact]
    public async Task Concurrent_sequence_generation_prevents_duplicate_numbers()
    {
        var tenantId = Guid.NewGuid();
        var scopeFactory = factory.Services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();

        // Run 10 parallel sequence generation tasks for the same tenant
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            using var scope = scopeFactory.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<Kabuor.Erp.Data.RequestContext>();
            ctx.CompanyId = tenantId;
            var numbers = scope.ServiceProvider.GetRequiredService<Kabuor.Erp.Common.Numbers>();
            return await numbers.Next("INV");
        });

        var results = await Task.WhenAll(tasks);

        // Assert all 10 generated numbers are distinct
        Assert.Equal(10, results.Length);
        Assert.Equal(10, results.Distinct().Count());
    }

    [Fact]
    public void PostgreSql_connection_string_includes_hardened_pool_options()
    {
        var cs = Program.ConnectionString("postgresql://postgres:secret@db.example.com:5432/kabuor_db?max_pool_size=120&min_pool_size=20");
        Assert.Contains("Pooling=true", cs);
        Assert.Contains("Maximum Pool Size=120", cs);
        Assert.Contains("Minimum Pool Size=20", cs);
        Assert.Contains("Connection Idle Lifetime=300", cs);
    }
}


