// The Kabuor Dine demo restaurant: one tidy restaurant someone can sign in to and try, the guest's phone included.
// It exists only when the host sets Demo:Password (DEMO__PASSWORD, 12 or more characters), and its password always
// follows that setting: it is never in the code. Its menu and tables are the same starting point a new restaurant
// gets, with no made-up orders, so the floor, the kitchen and the reports start clean. Its M-Pesa prompts use the
// platform's Daraja app from the environment (Payments/DarajaKeys.cs): a prompt in the demo is a real one, for the
// real amount, paid to the platform's till.
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Payments;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Dine;

public static class DineDemo
{
    public static readonly Guid CompanyId = Guid.Parse("0d1e0000-0000-4000-8000-00000000d1e0");
    public const string Email = "demo@kabuor-dine.test";
    public const string Name = "Kabuor Demo Grill";

    public static async Task Ensure(IServiceProvider services, IConfiguration config)
    {
        var password = config["Demo:Password"] ?? "";
        if (password.Length < 12) return;

        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<RequestContext>();
        var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
        var hasher = new PasswordHasher<User>();
        (ctx.CompanyId, ctx.Actor, ctx.Role) = (CompanyId, "Kabuor Dine demo", Role.Owner);

        var company = await db.Companies.IgnoreQueryFilters().SingleOrDefaultAsync(c => c.Id == CompanyId);
        if (company is null)
        {
            company = new Company
            {
                Id = CompanyId,
                Name = Name,
                Phone = "254700000000",
                Email = Email,
                County = "Nairobi",
                Industry = "restaurant",
                Product = "dine",
                VatRegistered = false,
                OwnerOversight = false,
            };
            var owner = new User { CompanyId = CompanyId, Name = "Demo Manager", Email = Email, Phone = "254700000000", Role = Role.Owner };
            owner.PasswordHash = hasher.HashPassword(owner, password);
            db.Companies.Add(company);
            db.Users.Add(owner);
            db.Accounts.AddRange(SystemAccounts.Starter(CompanyId));
            db.Locations.Add(new Location { CompanyId = CompanyId, Name = "Main store", IsDefault = true });
            db.DineSettings.Add(new DineSettings { CompanyId = CompanyId, Welcome = "Karibu! Order from your phone and we’ll bring it to your table." });
            for (var i = 1; i <= 6; i++) db.DineTables.Add(new DineTable { CompanyId = CompanyId, Name = $"Table {i}", Area = "Main room", Token = DineService.NewToken() });
            await db.SaveChangesAsync();

            // The menu every new restaurant can start from, and a small terrace: tidy, nothing made up.
            ctx.UserId = owner.Id;
            var dine = scope.ServiceProvider.GetRequiredService<DineService>();
            await dine.SampleMenu();
            await dine.AddTables(new DineTableInput(null, "Terrace", 2, 4, "Terrace"));
        }

        // Every start: the password follows DEMO__PASSWORD, the plan stays paid, and bills show the platform's till.
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.CompanyId == CompanyId && u.Email == Email);
        if (user is not null && (user.PasswordHash.Length == 0 || hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed))
            user.PasswordHash = hasher.HashPassword(user, password);
        if (user is not null) (user.Active, user.MustChangePassword, user.LockedUntil) = (true, false, null);
        (company.AwaitingFirstPayment, company.Plan) = (false, "dine");
        if (company.PaidUntil is null || company.PaidUntil < DateTimeOffset.UtcNow.AddMonths(6)) company.PaidUntil = DateTimeOffset.UtcNow.AddYears(1);
        if (DarajaKeys.Platform(config) is { } keys) company.MpesaShortcode = keys.PayTo;
        await db.SaveChangesAsync();
    }
}
