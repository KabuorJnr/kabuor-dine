// Sign-in security: two-step sign-in with an authenticator app (no code replays, recovery codes work once),
// sessions that end everywhere when asked or when the password changes, and the browser protections.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Auth;

namespace Kabuor.Erp.Tests;

public class SecurityTests(ErpFactory factory) : IClassFixture<ErpFactory>
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

    async Task<(string Email, string Token)> Owner()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        var s = await Ok(await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new { companyName = "Secure Ltd", ownerName = "Owner Person", email, phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        return (email, s.GetProperty("token").GetString()!);
    }

    [Fact]
    public async Task Two_step_sign_in_needs_the_app_code_and_recovery_codes_work_once()
    {
        var (email, token) = await Owner();
        var me = With(token);
        // Stay inside one 30-second code window for the replay check below.
        var into = DateTimeOffset.UtcNow.ToUnixTimeSeconds() % Totp.Step;
        if (into > 20) await Task.Delay(TimeSpan.FromSeconds(Totp.Step - into + 1));
        var start = await Ok(await me.PostAsync("/api/auth/security/2fa/start", null));
        var secret = start.GetProperty("secret").GetString()!;
        Assert.StartsWith("otpauth://totp/", start.GetProperty("uri").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync("/api/auth/security/2fa/enable", new { code = "000000" })).StatusCode);
        var enabled = await Ok(await me.PostAsJsonAsync("/api/auth/security/2fa/enable", new { code = Totp.Code(secret, Totp.StepNow()) }));
        var recovery = enabled.GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(8, recovery.Count);

        // Password alone is no longer a session.
        var anon = factory.CreateClient();
        var first = await Ok(await anon.PostAsJsonAsync("/api/auth/login", new { email, password = "a-long-password" }));
        Assert.True(first.GetProperty("needsCode").GetBoolean());
        Assert.False(first.TryGetProperty("token", out _));
        var challenge = first.GetProperty("challenge").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await With(challenge).GetAsync("/api/today")).StatusCode); // the ticket isn't a sign-in

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login/code", new { challenge, code = "123456" })).StatusCode);
        // The code from enabling was used: the same code can't be replayed; a recovery code works, once.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login/code", new { challenge, code = Totp.Code(secret, Totp.StepNow()) })).StatusCode);
        var session = await Ok(await anon.PostAsJsonAsync("/api/auth/login/code", new { challenge, code = recovery[0].ToUpperInvariant() }));
        Assert.True(session.GetProperty("user").GetProperty("twoFactorEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login/code", new { challenge, code = recovery[0] })).StatusCode);
        var status = await Ok(await With(session.GetProperty("token").GetString()!).GetAsync("/api/auth/security"));
        Assert.Equal(7, status.GetProperty("recoveryCodesLeft").GetInt32());

        // Turning it off needs the password, and ends other sessions.
        var signedIn = With(session.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, (await signedIn.PostAsJsonAsync("/api/auth/security/2fa/disable", new { password = "wrong-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await signedIn.PostAsJsonAsync("/api/auth/security/2fa/disable", new { password = "a-long-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await signedIn.GetAsync("/api/today")).StatusCode);
        Assert.True((await Ok(await anon.PostAsJsonAsync("/api/auth/login", new { email, password = "a-long-password" }))).TryGetProperty("token", out _));
    }

    [Fact]
    public async Task A_remembered_device_skips_the_code_until_the_user_signs_out_everywhere()
    {
        var (email, token) = await Owner();
        var me = With(token);
        var into = DateTimeOffset.UtcNow.ToUnixTimeSeconds() % Totp.Step;
        if (into > 20) await Task.Delay(TimeSpan.FromSeconds(Totp.Step - into + 1));
        var secret = (await Ok(await me.PostAsync("/api/auth/security/2fa/start", null))).GetProperty("secret").GetString()!;
        var recovery = (await Ok(await me.PostAsJsonAsync("/api/auth/security/2fa/enable", new { code = Totp.Code(secret, Totp.StepNow()) })))
            .GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList();
        var status = await Ok(await me.GetAsync("/api/auth/security"));
        Assert.True(status.GetProperty("twoFactorEnabled").GetBoolean());
        Assert.False(status.GetProperty("needsSetupAgain").GetBoolean());

        var anon = factory.CreateClient();
        var challenge = (await Ok(await anon.PostAsJsonAsync("/api/auth/login", new { email, password = "a-long-password" }))).GetProperty("challenge").GetString()!;
        var session = await Ok(await anon.PostAsJsonAsync("/api/auth/login/code", new { challenge, code = recovery[0], remember = true }));
        var device = session.GetProperty("device").GetString()!;

        // Next time on this device: the password is enough. A forged or other device still needs the code.
        Assert.True((await Ok(await anon.PostAsJsonAsync("/api/auth/login", new { email, password = "a-long-password", device }))).TryGetProperty("token", out _));
        Assert.True((await Ok(await anon.PostAsJsonAsync("/api/auth/login", new { email, password = "a-long-password", device = "not-a-token" }))).GetProperty("needsCode").GetBoolean());
        // The device ticket is not a sign-in by itself.
        Assert.Equal(HttpStatusCode.Unauthorized, (await With(device).GetAsync("/api/today")).StatusCode);

        // Signing out everywhere forgets every remembered device.
        await me.PostAsync("/api/auth/security/sign-out-everywhere", null);
        Assert.True((await Ok(await anon.PostAsJsonAsync("/api/auth/login", new { email, password = "a-long-password", device }))).GetProperty("needsCode").GetBoolean());
    }

    [Fact]
    public async Task Signing_out_everywhere_and_changing_the_password_end_other_sessions()
    {
        var (email, token) = await Owner();
        var laptop = With(token);
        var phone = With((await Ok(await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "a-long-password" }))).GetProperty("token").GetString()!);
        await Ok(await laptop.GetAsync("/api/today"));
        await Ok(await phone.GetAsync("/api/today"));

        // A new password: the device that changed it carries on with its new token; the other must sign in again.
        var changed = await Ok(await laptop.PostAsJsonAsync("/api/auth/password", new { current = "a-long-password", @new = "a-newer-password" }));
        var res = await phone.GetAsync("/api/today");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("session has ended", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/today")).StatusCode); // the old token too
        var fresh = With(changed.GetProperty("token").GetString()!);
        await Ok(await fresh.GetAsync("/api/today"));

        Assert.Equal(HttpStatusCode.NoContent, (await fresh.PostAsync("/api/auth/security/sign-out-everywhere", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.GetAsync("/api/today")).StatusCode);
    }

    [Fact]
    public async Task Pages_carry_the_browser_protections()
    {
        var res = await factory.CreateClient().GetAsync("/api/health");
        var csp = string.Join(";", res.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
    }
}
