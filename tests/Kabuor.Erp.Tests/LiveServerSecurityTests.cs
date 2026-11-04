// A live server (Production) has no shared logins: an admin's account opens only with its own password (never one of
// the old shared ones or the "admin" sign-in name), and only the addresses in Platform:Admins are platform admins.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Kabuor.Erp.Tests;

public sealed class LiveServerFactory : ErpFactory
{
    public const string Admin = "platform-admin@kabuor-dine.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
        builder.UseSetting("Database:AllowLocalFile", "true"); // the tests' file database stands in for PostgreSQL
        builder.UseSetting("Platform:Admins", Admin);
    }
}

public class LiveServerSecurityTests(LiveServerFactory factory) : IClassFixture<LiveServerFactory>
{
    async Task<JsonElement> Register(string email)
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new { companyName = $"Restaurant {Guid.NewGuid():N}", ownerName = "Some One", email, phone = "0712345678", password = "a-long-password", product = "dine" });
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.True(res.IsSuccessStatusCode, body.ToString());
        return body;
    }

    async Task<HttpStatusCode> Login(string id, string password) =>
        (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = id, password })).StatusCode;

    [Fact]
    public async Task A_platform_admin_opens_only_with_their_own_password()
    {
        var admin = await Register(LiveServerFactory.Admin);
        Assert.True(admin.GetProperty("user").GetProperty("isPlatformAdmin").GetBoolean());
        foreach (var shared in new[] { "admin", "password", "admin123" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await Login(LiveServerFactory.Admin, shared));
            Assert.Equal(HttpStatusCode.Unauthorized, await Login("admin", shared));
        }
        Assert.Equal(HttpStatusCode.OK, await Login(LiveServerFactory.Admin, "a-long-password"));
    }

    [Fact]
    public async Task Only_listed_addresses_are_platform_admins()
    {
        foreach (var email in new[] { "admin@kabuor.com", "info@kabuor.com", $"{Guid.NewGuid():N}@kabuor-dine.test" })
            Assert.False((await Register(email)).GetProperty("user").GetProperty("isPlatformAdmin").GetBoolean());
    }
}
