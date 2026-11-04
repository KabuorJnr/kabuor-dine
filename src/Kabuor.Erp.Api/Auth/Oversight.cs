// Owner oversight: what an owner who oversees (Company.OwnerOversight) may still change. Everything else they can read.
// Decisions stay with the owner: approving, rejecting and signing off (budgets, requests, payroll, leave, advances,
// transfers, agent proposals, tender awards), the monthly plan, KPI targets, Verified shares, and running the business
// itself (settings, users, billing, their own sign-in). The daily work is the teams'.
using System.Text.RegularExpressions;

namespace Kabuor.Erp.Auth;

public static partial class Oversight
{
    static readonly string[] Prefixes =
    [
        "/api/auth", "/api/billing", "/api/settings", "/api/platform", "/api/onboarding", "/api/me", "/api/assistant",
        "/api/insights/kpis/targets", "/api/os/plan", "/api/verified",
    ];

    [GeneratedRegex(@"/(approve|reject|decline|sign-off|award|revoke)$", RegexOptions.IgnoreCase)]
    private static partial Regex Decision();

    public static bool Allows(string path)
    {
        // Staff portal access (/api/employees/{id}/access) is the owner's to grant; the rest of HR is HR's.
        if (path.StartsWith("/api/employees/", StringComparison.OrdinalIgnoreCase)) return path.EndsWith("/access", StringComparison.OrdinalIgnoreCase);
        return Prefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || Decision().IsMatch(path);
    }
}
