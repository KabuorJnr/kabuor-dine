// Staying signed in. With no database configured, each copy of the server kept its own database file: a sign-in
// made on one copy named a login the next copy didn't have, so people were signed out straight after signing in.
// Production now refuses to start without a shared database, and a token is only good for the business its login
// belongs to.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kabuor.Erp.Tests;

public class SessionTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    async Task<JsonElement> Register(HttpClient c) =>
        await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"Session Shop {Guid.NewGuid():N}"[..22], ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));

    [Fact]
    public void Production_refuses_to_start_on_a_database_file_inside_the_server()
    {
        // Both database settings blanked, so this can never reach a real database even where one is configured.
        using var production = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Production").UseSetting("ConnectionStrings:Erp", "").UseSetting("DATABASE_URL", "")
            .UseSetting("Jwt:Key", ErpFactory.JwtKey));
        var e = Assert.ThrowsAny<Exception>(() => production.CreateClient());
        Assert.Contains("DATABASE_URL", e.ToString());
    }

    [Fact]
    public async Task A_signed_in_owner_stays_signed_in_across_the_first_screen()
    {
        var c = factory.CreateClient();
        var s = await Register(c);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        foreach (var path in new[] { "/api/today", "/api/billing", "/api/onboarding/status", "/api/insights/kpis?period=month&offset=0", "/api/insights/charts?months=12" })
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task A_token_naming_another_business_than_its_login_is_refused()
    {
        var a = await Register(factory.CreateClient());
        var b = await Register(factory.CreateClient());
        var claims = new Dictionary<string, object>
        {
            ["sub"] = a.GetProperty("user").GetProperty("id").GetString()!,
            ["cid"] = b.GetProperty("company").GetProperty("id").GetString()!,
            ["role"] = "Owner", ["name"] = "Owner Person", ["tv"] = "0",
        };
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims, Issuer = "kabuor-erp", Audience = "kabuor-erp", Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ErpFactory.JwtKey)), SecurityAlgorithms.HmacSha256),
        });
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/today")).StatusCode);
    }
}
