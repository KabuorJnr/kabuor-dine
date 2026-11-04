// Kabuor Verified, step 3: sharing with lenders, the lender's page, and the consent record.
// Facts only: Kabuor provides verified facts, not a credit score.
// Every share is explicit, time-limited, purpose-limited, and revocable at any time.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

public class VerifiedSharingTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : Json(body);
    }

    async Task<(HttpClient Client, JsonElement Session, Guid CompanyId, string Password)> SignUp(string company)
    {
        var c = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.com";
        const string password = "a-long-secure-password-1";
        var res = await c.PostAsJsonAsync("/api/auth/register", new
        {
            companyName = company,
            ownerName = "Owner Person",
            email,
            phone = "0711223344",
            password,
            kraPin = "P051234567X",
            vatRegistered = true
        });
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, body);
        var session = Json(body);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("token").GetString());
        var companyId = session.GetProperty("company").GetProperty("id").GetGuid();
        return (c, session, companyId, password);
    }

    async Task<HttpClient> Colleague(HttpClient owner, string role)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        var res = await owner.PostAsJsonAsync("/api/settings/users", new
        {
            name = $"{role} User",
            email,
            phone = "0722000000",
            role,
            password = "starting-password-1"
        });
        Assert.True(res.IsSuccessStatusCode);
        var c = factory.CreateClient();
        var s = await TestSignIn.First(c, email, "starting-password-1");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    [Fact]
    public async Task Owner_creates_share_with_password_or_totp_accountant_cannot_create_or_revoke()
    {
        var (owner, _, companyId, password) = await SignUp("Verified Lending Co");
        var accountant = await Colleague(owner, "Accountant");

        // 1. Accountant cannot create a share (403 Forbidden)
        var accCreateRes = await accountant.PostAsJsonAsync("/api/verified/shares", new
        {
            lenderName = "Equity Bank",
            months = 6,
            days = 30,
            password = "starting-password-1"
        });
        Assert.Equal(HttpStatusCode.Forbidden, accCreateRes.StatusCode);

        // 2. Owner wrong password refused
        var wrongPassRes = await owner.PostAsJsonAsync("/api/verified/shares", new
        {
            lenderName = "Equity Bank",
            months = 6,
            days = 30,
            password = "wrong-password"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassRes.StatusCode);

        // 3. Owner correct password succeeds
        var okCreateRes = await owner.PostAsJsonAsync("/api/verified/shares", new
        {
            lenderName = "Equity Bank",
            lenderContact = "officer@equity.co.ke",
            months = 6,
            days = 30,
            password
        });
        var createdJson = await Ok(okCreateRes);
        var shareId = createdJson.GetProperty("id").GetGuid();
        var token = createdJson.GetProperty("token").GetString()!;
        Assert.NotEmpty(token);
        Assert.Contains(token, createdJson.GetProperty("url").GetString()!);

        // 4. Accountant can view shares list
        var accListRes = await accountant.GetAsync("/api/verified/shares");
        var listJson = await Ok(accListRes);
        Assert.True(listJson.GetArrayLength() >= 1);
        var item = listJson.EnumerateArray().First(x => x.GetProperty("id").GetGuid() == shareId);
        Assert.Equal("Equity Bank", item.GetProperty("lenderName").GetString());
        Assert.Equal("active", item.GetProperty("status").GetString());

        // 5. Accountant cannot revoke (403 Forbidden)
        var accRevokeRes = await accountant.PostAsync($"/api/verified/shares/{shareId}/revoke", null);
        Assert.Equal(HttpStatusCode.Forbidden, accRevokeRes.StatusCode);

        // 6. Two-Factor Authentication TOTP re-auth
        // Turn on 2FA for owner
        string secret;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
            var box = scope.ServiceProvider.GetRequiredService<SecretBox>();
            var u = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.CompanyId == companyId && x.Role == Role.Owner);
            secret = Totp.NewSecret();
            u.TotpSecret = box.Seal(secret);
            u.TwoFactorEnabled = true;
            await db.SaveChangesAsync();
        }

        // Invalid TOTP code fails
        var wrongCodeRes = await owner.PostAsJsonAsync("/api/verified/shares", new
        {
            lenderName = "Co-op Bank",
            months = 3,
            days = 7,
            code = "000000"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCodeRes.StatusCode);

        // Valid TOTP code succeeds
        var validCode = Totp.Code(secret, Totp.StepNow());
        var okRes = await owner.PostAsJsonAsync("/api/verified/shares", new
        {
            lenderName = "Co-op Bank",
            months = 3,
            days = 7,
            code = validCode
        });
        var created = await Ok(okRes);
        Assert.NotEmpty(created.GetProperty("token").GetString()!);
    }

    [Fact]
    public async Task Public_link_returns_totals_only_logs_view_verifies_signature_and_handles_revocation_and_expiry()
    {
        var (owner, _, companyId, password) = await SignUp("Verified Public Co");

        // Seed some customer payments in prior month (complete months)
        const string secretCustomerPhone = "0799887766";
        const string secretCustomerName = "Confidential Payer Limited";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
            db.Payments.Add(new Payment
            {
                CompanyId = companyId,
                Amount = 500_000,
                ReceivedAt = DateTimeOffset.UtcNow.AddMonths(-1),
                Method = PaymentMethod.Mpesa,
                Source = PaymentSource.MpesaConfirmation,
                FromSafaricom = true,
                PayerPhone = secretCustomerPhone,
                PayerName = secretCustomerName,
                Reference = "CONF_PUBLIC_01",
                Status = PaymentStatus.Matched,
            });
            await db.SaveChangesAsync();
        }

        // Create share
        var createRes = await owner.PostAsJsonAsync("/api/verified/shares", new
        {
            lenderName = "NCBA Bank",
            months = 3,
            days = 30,
            password
        });
        var created = await Ok(createRes);
        var shareId = created.GetProperty("id").GetGuid();
        var token = created.GetProperty("token").GetString()!;

        // 1. Token is never stored in clear (hash only)
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
            var share = await db.VerifiedShares.IgnoreQueryFilters().SingleAsync(s => s.Id == shareId);
            Assert.NotEqual(token, share.TokenHash);
            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
            Assert.Equal(expectedHash, share.TokenHash);

            // Audit entries do not contain clear token or TokenHash
            var auditEntries = await db.AuditEntries.IgnoreQueryFilters().Where(a => a.Entity == "VerifiedShare" && a.EntityId == shareId.ToString()).ToListAsync();
            foreach (var a in auditEntries)
            {
                Assert.DoesNotContain(token, a.Changes);
                Assert.DoesNotContain("TokenHash", a.Changes);
            }
        }

        // 2. Public link call without authentication
        var anon = factory.CreateClient();
        anon.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 Test Suite Lender Browser");
        var publicRes = await anon.GetAsync($"/api/verified/s/{token}");
        var publicBody = await publicRes.Content.ReadAsStringAsync();
        Assert.True(publicRes.IsSuccessStatusCode, publicBody);
        var publicJson = Json(publicBody);

        // Data minimisation: assert customer names and phones never appear
        Assert.DoesNotContain(secretCustomerPhone, publicBody);
        Assert.DoesNotContain(secretCustomerName, publicBody);

        // Assert payload structure
        Assert.Equal("NCBA Bank", publicJson.GetProperty("lenderName").GetString());
        var summary = publicJson.GetProperty("summary");
        Assert.True(summary.GetProperty("averageMonthlyConfirmed").GetInt64() > 0);

        // Signature check with published key info
        var keyRes = await anon.GetAsync("/api/verified/key");
        var keyJson = await Ok(keyRes);
        var keyId = keyJson.GetProperty("keyId").GetString();
        Assert.Equal(keyId, publicJson.GetProperty("keyId").GetString());

        var signature = publicJson.GetProperty("signature").GetString()!;
        var canonical = publicJson.GetProperty("canonical").GetString()!;
        using (var scope = factory.Services.CreateScope())
        {
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var signingKey = config["Verified:SigningKey"] ?? config["JWT__KEY"] ?? "kabuor-verified-default-signing-key-32";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
            var expectedSig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            Assert.Equal(expectedSig, signature);
        }

        // 3. View log check
        var viewsRes = await owner.GetAsync($"/api/verified/shares/{shareId}/views");
        var viewsJson = await Ok(viewsRes);
        var viewItem = viewsJson.EnumerateArray().First();
        Assert.NotEmpty(viewItem.GetProperty("ipHash").GetString()!);
        Assert.DoesNotContain("127.0.0.1", viewItem.GetProperty("ipHash").GetString()!); // hashed, not raw IP

        // 4. Revocation takes effect immediately
        var revokeRes = await owner.PostAsync($"/api/verified/shares/{shareId}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revokeRes.StatusCode);

        var revokedPublicRes = await anon.GetAsync($"/api/verified/s/{token}");
        Assert.Equal(HttpStatusCode.NotFound, revokedPublicRes.StatusCode);
        var endedJson = Json(await revokedPublicRes.Content.ReadAsStringAsync());
        Assert.Equal("This link has ended. Ask the business for a new one.", endedJson.GetProperty("error").GetString());

        // 5. Expired share returns same message
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
            var s = await db.VerifiedShares.IgnoreQueryFilters().SingleAsync(x => x.Id == shareId);
            s.RevokedAt = null; // un-revoke
            s.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10); // make expired
            await db.SaveChangesAsync();
        }
        var expiredPublicRes = await anon.GetAsync($"/api/verified/s/{token}");
        Assert.Equal(HttpStatusCode.NotFound, expiredPublicRes.StatusCode);
        var expiredJson = Json(await expiredPublicRes.Content.ReadAsStringAsync());
        Assert.Equal("This link has ended. Ask the business for a new one.", expiredJson.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Invalid_token_returns_ended_message()
    {
        var anon = factory.CreateClient();
        var res = await anon.GetAsync("/api/verified/s/non-existent-token-12345");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var json = Json(await res.Content.ReadAsStringAsync());
        Assert.Equal("This link has ended. Ask the business for a new one.", json.GetProperty("error").GetString());
    }
}
