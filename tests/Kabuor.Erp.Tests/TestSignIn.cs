// Signing in as a colleague the owner added: their starting password works once, so (as the app does) the first
// sign-in sets their own password and carries on with the session that comes back.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

internal static class TestSignIn
{
    public static async Task<JsonElement> First(HttpClient c, string email, string startingPassword)
    {
        var s = await Json(await c.PostAsJsonAsync("/api/auth/login", new { email, password = startingPassword }));
        if (!s.GetProperty("user").GetProperty("mustChangePassword").GetBoolean()) return s;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return await Json(await c.PostAsJsonAsync("/api/auth/password", new { current = startingPassword, @new = $"{startingPassword}-own" }));
    }

    static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
