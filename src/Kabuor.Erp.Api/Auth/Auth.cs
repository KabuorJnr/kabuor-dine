using System.Security.Claims;
using System.Text;
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Etims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kabuor.Erp.Auth;

/// <summary>What each role may do. Endpoints ask for a policy, never for a role by name.</summary>
public static class Policies
{
    // Who sees what. One list, used three ways: API policies, the sign-in payload (so the app shows each person
    // only their own menu, pages and tiles), and the assistant's tools. A "view" policy opens the screens;
    // the plain one allows changes. Every change policy is a subset of its view policy.
    public const string Read = "read";            // any staff login (not the employee self-service portal)
    public const string SalesView = "sales.view"; // customers, quotes, invoices, money in
    public const string Sell = "sell";            // create and change those, record payments
    public const string Pos = "pos";              // the till: cashiers and the owner only
    public const string Crm = "crm";              // leads, pipeline, follow-ups, campaigns
    public const string Finance = "finance";      // match payments, books, VAT, pay suppliers, approve payroll
    public const string Reports = "reports";      // profit and loss, VAT, aging (read only)
    public const string StockView = "stock.view"; // stock levels and value
    public const string Stock = "stock";          // counts, transfers, adjustments
    public const string BuyView = "buy.view";     // suppliers, orders, bills
    public const string Buy = "buy";              // raise orders and bills
    public const string Make = "make";            // recipes and production runs
    public const string People = "people";        // employees, salaries, leave, payroll
    public const string Assets = "assets";        // the asset register: add, move, give to a custodian, mark repairs
    public const string MaintainView = "maintain.view"; // faults, work orders, maintenance plans and their costs
    public const string Maintain = "maintain";    // plan, schedule and do the work; issue parts to jobs
    public const string FleetView = "fleet.view"; // vehicles, trips, waybills and the live map
    public const string Fleet = "fleet";          // plan and send out trips, driver links, proof of delivery
    public const string Admin = "admin";          // users and company settings
    public const string VerifiedView = "verified.view";   // Kabuor Verified: the proof of revenue and eTIMS
    public const string VerifiedShare = "verified.share"; // sharing that proof with a lender: the owner alone
    public const string Dine = "dine";            // Kabuor Dine: the floor, the kitchen and bar screens, taking payment at the table
    public const string DineManage = "dine.manage"; // Kabuor Dine: the menu, tables, QR codes and how tables order and pay

    public static readonly Dictionary<string, Role[]> Roles = new()
    {
        [Read] = [Role.Owner, Role.Accountant, Role.Sales, Role.Storekeeper, Role.Viewer, Role.HR, Role.Procurement, Role.Dispatcher, Role.Maintenance],
        [SalesView] = [Role.Owner, Role.Accountant, Role.Sales, Role.Viewer],
        [Sell] = [Role.Owner, Role.Accountant, Role.Sales],
        [Pos] = [Role.Owner, Role.Sales],
        [Crm] = [Role.Owner, Role.Sales],
        [Finance] = [Role.Owner, Role.Accountant],
        [Reports] = [Role.Owner, Role.Accountant, Role.Viewer],
        [StockView] = [Role.Owner, Role.Accountant, Role.Storekeeper, Role.Procurement, Role.Viewer, Role.Maintenance],
        [Stock] = [Role.Owner, Role.Accountant, Role.Storekeeper, Role.Procurement],
        [BuyView] = [Role.Owner, Role.Accountant, Role.Procurement, Role.Storekeeper, Role.Viewer],
        [Buy] = [Role.Owner, Role.Accountant, Role.Procurement, Role.Storekeeper], // orders create no debt; bills still need Finance
        [Make] = [Role.Owner, Role.Storekeeper],
        [People] = [Role.Owner, Role.Accountant, Role.HR],
        [Assets] = [Role.Owner, Role.Accountant, Role.Storekeeper, Role.Procurement, Role.Maintenance],
        [MaintainView] = [Role.Owner, Role.Accountant, Role.Storekeeper, Role.Procurement, Role.Viewer, Role.Maintenance, Role.Dispatcher],
        [Maintain] = [Role.Owner, Role.Storekeeper, Role.Maintenance],
        [FleetView] = [Role.Owner, Role.Accountant, Role.Storekeeper, Role.Procurement, Role.Viewer, Role.Dispatcher],
        [Fleet] = [Role.Owner, Role.Accountant, Role.Storekeeper, Role.Procurement, Role.Dispatcher],
        [Admin] = [Role.Owner],
        [VerifiedView] = [Role.Owner, Role.Accountant],
        [VerifiedShare] = [Role.Owner],
        [Dine] = [Role.Owner, Role.Accountant, Role.Sales, Role.Storekeeper],
        [DineManage] = [Role.Owner, Role.Accountant],
    };

    public static bool Allows(this Role role, string policy) => Roles[policy].Contains(role);
    public static string[] For(Role role) => Roles.Where(kv => kv.Value.Contains(role)).Select(kv => kv.Key).ToArray();
}

public sealed class TokenIssuer(IConfiguration config)
{
    public SymmetricSecurityKey Key { get; } = new(Encoding.UTF8.GetBytes(SigningSecret(config)));

    static string SigningSecret(IConfiguration config)
    {
        // No fallback key, ever: a key written in the code is public, and anyone holding it can sign in as any owner.
        // JWT__KEY in the environment arrives here as Jwt:Key.
        var s = config["Jwt:Key"];
        if (string.IsNullOrEmpty(s) || s.Length < 32) throw new InvalidOperationException("Set Jwt:Key (at least 32 characters) in configuration or the JWT__KEY environment variable.");
        if (s.Contains("development-only") && !string.Equals(config["ASPNETCORE_ENVIRONMENT"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The development signing key can't be used outside Development. Set JWT__KEY to a long random secret.");
        return s;
    }

    public string Issue(User u, string? companyName = null)
    {
        var claims = new List<Claim>
        {
            new("sub", u.Id.ToString()),
            new("cid", u.CompanyId.ToString()),
            new("role", u.Role.ToString()),
            new("name", u.Name),
        };
        if (!string.IsNullOrEmpty(u.Email))
        {
            claims.Add(new Claim("email", u.Email));
            claims.Add(new Claim(ClaimTypes.Email, u.Email));
        }
        if (!string.IsNullOrEmpty(companyName)) claims.Add(new Claim("cname", companyName));
        if (u.EmployeeId is { } eid) claims.Add(new Claim("eid", eid.ToString()));
        claims.Add(new Claim("tv", u.TokenVersion.ToString()));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(12),
            Issuer = "kabuor-erp",
            Audience = "kabuor-erp",
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });
    }
}

public sealed record CodeInput(string Challenge, string Code, bool? Remember = null);
public sealed record TwoFactorInput(string? Code, string? Password);

public static class TokenIssuerChallenge
{
    const string Audience = "kabuor-erp-2fa";

    /// <summary>A five-minute ticket between "password right" and "code right". It can't be used as a sign-in.</summary>
    public static string Challenge(this TokenIssuer t, User u) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", u.Id.ToString()), new Claim("tv", u.TokenVersion.ToString())]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Issuer = "kabuor-erp",
            Audience = Audience,
            SigningCredentials = new SigningCredentials(t.Key, SecurityAlgorithms.HmacSha256),
        });

    const string DeviceAudience = "kabuor-erp-device";

    /// <summary>"Remember this device": skips the code for 30 days, until the password changes, the user signs out
    /// everywhere, or two-step sign-in is turned off (all of which bump the token version).</summary>
    public static string Device(this TokenIssuer t, User u) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", u.Id.ToString()), new Claim("tv", u.TokenVersion.ToString())]),
            Expires = DateTime.UtcNow.AddDays(30),
            Issuer = "kabuor-erp",
            Audience = DeviceAudience,
            SigningCredentials = new SigningCredentials(t.Key, SecurityAlgorithms.HmacSha256),
        });

    public static Task<(Guid User, int Version)?> ReadDevice(this TokenIssuer t, string? token) => Read(t, token, DeviceAudience);

    public static Task<(Guid User, int Version)?> ReadChallenge(this TokenIssuer t, string token) => Read(t, token, Audience);

    static async Task<(Guid User, int Version)?> Read(TokenIssuer t, string? token, string audience)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var r = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "kabuor-erp", ValidAudience = audience, IssuerSigningKey = t.Key, ClockSkew = TimeSpan.FromSeconds(30),
        });
        if (!r.IsValid || !Guid.TryParse(r.Claims["sub"]?.ToString(), out var id) || !int.TryParse(r.Claims["tv"]?.ToString(), out var tv)) return null;
        return (id, tv);
    }
}

public sealed record RegisterInput(string CompanyName, string OwnerName, string Email, string Phone, string Password, string? KraPin, bool? VatRegistered, string? Industry = null, string? Product = null);
/// <summary>Email, or (for employees) the phone number HR registered.</summary>
public sealed record LoginInput(string Email, string Password, string? Device = null);
public sealed record PasswordInput(string Current, string New);
public sealed record AccessGrant(string Phone, string Password, bool Reset);
public sealed record Session(string Token, object User, object Company, string? Device = null);

public sealed class AuthService(ErpDb db, RequestContext ctx, TokenIssuer tokens, SecretBox box, IConfiguration config, IHostEnvironment env)
{
    static readonly PasswordHasher<User> Hasher = new();

    static string CleanEmail(string? e) => (e ?? "").Trim().ToLowerInvariant();

    static void CheckPassword(string? p)
    {
        if (string.IsNullOrEmpty(p) || p.Length < 10) throw new AppException("Use a password of at least 10 characters.");
    }

    /// <summary>Opens a new company: the company, its owner, and a starter chart of accounts.</summary>
    public async Task<Session> Register(RegisterInput input)
    {
        var name = (input.CompanyName ?? "").Trim();
        var email = CleanEmail(input.Email);
        if (name.Length < 2) throw new AppException("Enter your business name.");
        if ((input.OwnerName ?? "").Trim().Length < 2) throw new AppException("Enter your name.");
        if (!email.Contains('@') || email.Length > 200) throw new AppException("Enter a valid email address.");
        var phone = Phones.ToMsisdn(input.Phone) ?? throw new AppException("Enter a Safaricom or Airtel number, e.g. 0712 345 678.");
        CheckPassword(input.Password);
        var pin = (input.KraPin ?? "").Trim().ToUpperInvariant();
        if (pin.Length > 0 && !SandboxEtimsClient.IsKraPin(pin)) throw new AppException("That KRA PIN doesn't look right (e.g. P051234567X).");
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email)) throw new AppException("That email already has an account. Sign in instead.", 409);

        // Kabuor Dine: a restaurant on the same books, with its own screens. The owner usually runs the floor too.
        var dine = string.Equals(input.Product?.Trim(), "dine", StringComparison.OrdinalIgnoreCase);
        var industry = dine ? "restaurant" : string.IsNullOrWhiteSpace(input.Industry) ? "general" : input.Industry.Trim().ToLowerInvariant();
        var company = new Company
        {
            Name = name,
            KraPin = pin,
            Phone = phone,
            Email = email,
            VatRegistered = input.VatRegistered ?? pin.Length > 0,
            Industry = industry,
            PreventNegativeStock = industry is "distribution" or "retail",
            // New businesses start with the owner overseeing (Owners:OversightByDefault); Settings can change it.
            OwnerOversight = !dine && config.GetValue("Owners:OversightByDefault", true),
            Product = dine ? "dine" : "erp",
        };
        // Paying to start: the account opens with the first M-Pesa payment, and there's no free trial to run out.
        if (Billing.Plans.PayToStart(config)) (company.AwaitingFirstPayment, company.TrialEndsAt) = (true, company.CreatedAt);
        (ctx.CompanyId, ctx.Actor) = (company.Id, input.OwnerName!.Trim());
        var owner = new User { CompanyId = company.Id, Name = input.OwnerName.Trim(), Email = email, Phone = phone, Role = Role.Owner, LastSignInAt = DateTimeOffset.UtcNow };
        owner.PasswordHash = Hasher.HashPassword(owner, input.Password);
        ctx.UserId = owner.Id;
        db.Companies.Add(company);
        db.Users.Add(owner);
        db.Accounts.AddRange(SystemAccounts.Starter(company.Id));
        db.Locations.Add(new Location { CompanyId = company.Id, Name = "Main store", IsDefault = true });
        if (dine)
        {
            // Six tables with their QR codes, ready to print on day one.
            db.DineSettings.Add(new DineSettings { CompanyId = company.Id });
            for (var i = 1; i <= 6; i++) db.DineTables.Add(new DineTable { CompanyId = company.Id, Name = $"Table {i}", Token = Kabuor.Erp.Dine.DineService.NewToken() });
        }
        await db.SaveChangesAsync();
        return Session(owner, company);
    }

    public async Task<object> Login(LoginInput input)
    {
        var id = (input.Email ?? "").Trim();
        const string wrong = "Wrong email, phone or password.";
        User? user;
        if (id.Contains('@'))
        {
            var email = CleanEmail(id);
            user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.Email == email);
        }
        else
        {
            // Employees sign in with the phone HR registered (many have no email).
            var phone = Phones.ToMsisdn(id) ?? throw new AppException(wrong, 401);
            var matches = await db.Users.IgnoreQueryFilters().Where(u => u.Phone == phone && u.Role == Role.Employee && u.Active).Take(2).ToListAsync();
            if (matches.Count > 1) throw new AppException("This phone has logins at more than one business. Ask HR to add your email and sign in with it.", 409);
            user = matches.SingleOrDefault();
        }
        if (user is null || !user.Active) throw new AppException(wrong, 401);
        if (user.LockedUntil > DateTimeOffset.UtcNow) throw new AppException("Too many wrong tries. Wait 15 minutes, or ask your admin to reset your password.", 429);
        (ctx.CompanyId, ctx.UserId, ctx.Actor) = (user.CompanyId, user.Id, user.Name);

        var ok = Hasher.VerifyHashedPassword(user, user.PasswordHash, input.Password ?? "");
        if (ok == PasswordVerificationResult.Failed)
        {
            user.FailedLogins++;
            if (user.FailedLogins >= 5) (user.LockedUntil, user.FailedLogins) = (DateTimeOffset.UtcNow.AddMinutes(15), 0);
            await db.SaveChangesAsync();
            throw new AppException(wrong, 401);
        }
        if (ok == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = Hasher.HashPassword(user, input.Password!);
        if (user.TwoFactorEnabled && !(await tokens.ReadDevice(input.Device) is { } known && known.User == user.Id && known.Version == user.TokenVersion))
        {
            await db.SaveChangesAsync();
            return new { needsCode = true, challenge = tokens.Challenge(user) };
        }
        (user.FailedLogins, user.LockedUntil, user.LastSignInAt) = (0, null, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        var company = await db.Companies.SingleAsync(c => c.Id == user.CompanyId);
        return Session(user, company);
    }

    /// <summary>The second step: a code from the authenticator app, or one of the recovery codes.</summary>
    public async Task<Session> LoginCode(CodeInput input)
    {
        var who = await tokens.ReadChallenge(input.Challenge) ?? throw new AppException("That took too long. Enter your password again.", 401);
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.Id == who.User);
        if (user is null || !user.Active || user.TokenVersion != who.Version || !user.TwoFactorEnabled) throw new AppException("Enter your password again.", 401);
        if (user.LockedUntil > DateTimeOffset.UtcNow) throw new AppException("Too many wrong tries. Wait 15 minutes, or ask your admin to reset your password.", 429);
        (ctx.CompanyId, ctx.UserId, ctx.Actor) = (user.CompanyId, user.Id, user.Name);
        var code = (input.Code ?? "").Trim();
        // The secret is encrypted with a key derived from the server's signing key; if that key changed, it can't be
        // read any more and only a recovery code can get the user in (they then set two-step up again).
        var secret = box.TryOpen(user.TotpSecret);
        if (secret is not null && Totp.Match(secret, code, user.TotpLastStep) is { } step) user.TotpLastStep = step;
        else if (RecoveryCodes.Use(user.RecoveryCodes, code) is { } left) user.RecoveryCodes = left;
        else
        {
            user.FailedLogins++;
            if (user.FailedLogins >= 5) (user.LockedUntil, user.FailedLogins) = (DateTimeOffset.UtcNow.AddMinutes(15), 0);
            await db.SaveChangesAsync();
            throw new AppException(secret is null
                ? "Your two-step setup can't be read after a server change, so app codes won't work. Sign in with one of your recovery codes, then turn two-step sign-in off and on again in Security."
                : "That code isn't right. Use the 6 digits in your authenticator app now, or a recovery code.", 401);
        }
        (user.FailedLogins, user.LockedUntil, user.LastSignInAt) = (0, null, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        var session = Session(user, await db.Companies.SingleAsync(c => c.Id == user.CompanyId));
        return input.Remember == true ? session with { Device = tokens.Device(user) } : session;
    }

    // ---------- two-step sign-in and sessions (for the signed-in person) ----------

    async Task<User> Me() => await db.Users.SingleOrDefaultAsync(u => u.Id == ctx.UserId) ?? throw new AppException("Please sign in again.", 401);

    public async Task<object> Security()
    {
        var u = await Me();
        return new { twoFactorEnabled = u.TwoFactorEnabled, recoveryCodesLeft = u.TwoFactorEnabled ? RecoveryCodes.Left(u.RecoveryCodes) : 0, needsSetupAgain = u.TwoFactorEnabled && box.TryOpen(u.TotpSecret) is null };
    }

    /// <summary>Step 1 of turning it on: a new secret to scan. Nothing changes until a code from it is confirmed.</summary>
    public async Task<object> StartTwoFactor()
    {
        var u = await Me();
        if (u.TwoFactorEnabled) throw new AppException("Two-step sign-in is already on.");
        var secret = Totp.NewSecret();
        u.TotpSecret = box.Seal(secret);
        await db.SaveChangesAsync();
        return new { secret, uri = Totp.Uri(secret, string.IsNullOrEmpty(u.Email) ? u.Name : u.Email) };
    }

    /// <summary>Step 2: the first code proves the app is set up. Returns the recovery codes, shown once.</summary>
    public async Task<object> EnableTwoFactor(TwoFactorInput input)
    {
        var u = await Me();
        if (u.TwoFactorEnabled) throw new AppException("Two-step sign-in is already on.");
        if (u.TotpSecret.Length == 0) throw new AppException("Start again: scan the code first.");
        var step = Totp.Match(box.Open(u.TotpSecret), input.Code ?? "", 0) ?? throw new AppException("That code isn't right. Check the time on your phone is automatic, and try the newest code.");
        var codes = RecoveryCodes.New();
        (u.TwoFactorEnabled, u.TotpLastStep, u.RecoveryCodes) = (true, step, string.Join(';', codes.Select(RecoveryCodes.Hash)));
        await db.SaveChangesAsync();
        return new { recoveryCodes = codes };
    }

    public async Task DisableTwoFactor(TwoFactorInput input)
    {
        var u = await Me();
        if (Hasher.VerifyHashedPassword(u, u.PasswordHash, input.Password ?? "") == PasswordVerificationResult.Failed) throw new AppException("Your password isn't right.");
        (u.TwoFactorEnabled, u.TotpSecret, u.RecoveryCodes, u.TotpLastStep) = (false, "", "", 0);
        u.TokenVersion++; // other devices sign in again
        await db.SaveChangesAsync();
    }

    /// <summary>Ends every session of this person, including this one (a lost phone, a shared computer).</summary>
    public async Task SignOutEverywhere()
    {
        var u = await Me();
        u.TokenVersion++;
        await db.SaveChangesAsync();
    }

    /// <summary>The owner adds a colleague with a role and a starting password they'll share privately (and that only
    /// works once: they choose their own at first sign-in).</summary>
    public async Task<User> AddUser(string name, string email, string phone, Role role, string password)
    {
        email = CleanEmail(email);
        if ((name ?? "").Trim().Length < 2 || !email.Contains('@')) throw new AppException("Enter their name and email.");
        CheckPassword(password);
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email)) throw new AppException("That email already has an account.", 409);
        var company = await db.Companies.SingleAsync(c => c.Id == db.CompanyId);
        var plan = Billing.Plans.All(config).FirstOrDefault(p => p.Id == company.Plan);
        if (plan is not null && await db.Users.CountAsync(x => x.Active && x.Role != Role.Employee) >= plan.Users)
            throw new AppException($"The {plan.Name} plan has room for {plan.Users} users. Choose a bigger plan in Settings → Plan to add more.");
        if (role == Role.Employee) throw new AppException("Employees get their app access from their record in People (Give app access).");
        // The starting password is known to whoever typed it: they replace it with their own when they first sign in.
        var u = new User { Name = name!.Trim(), Email = email, Phone = Phones.ToMsisdn(phone) ?? "", Role = role, MustChangePassword = true };
        u.PasswordHash = Hasher.HashPassword(u, password);
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    // ---------- the owner looking after the team ----------

    async Task<User> Colleague(Guid id)
    {
        var u = await db.Users.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        if (u.Role == Role.Employee) throw new AppException($"{u.Name} signs in to the staff portal: manage that from their record in People.");
        return u;
    }

    /// <summary>Someone always has to be able to run the business: the last active owner can't be demoted or switched off.</summary>
    async Task KeepAnOwner(User u)
    {
        if (u.Role == Role.Owner && u.Active && !await db.Users.AnyAsync(x => x.Id != u.Id && x.Role == Role.Owner && x.Active))
            throw new AppException($"{u.Name} is the only owner. Make someone else an owner first.");
    }

    /// <summary>A new role takes effect at once: their sessions end and they sign in again with the new menu.</summary>
    public async Task<User> ChangeRole(Guid id, Role role)
    {
        var u = await Colleague(id);
        if (role == Role.Employee) throw new AppException("Employees get their app access from their record in People.");
        if (u.Role == role) return u;
        if (role != Role.Owner) await KeepAnOwner(u);
        u.Role = role;
        u.TokenVersion++;
        await db.SaveChangesAsync();
        return u;
    }

    /// <summary>Switching a login off ends its sessions; nothing they did is removed. Switching it on again counts
    /// against the plan's users.</summary>
    public async Task<User> SetActive(Guid id, bool active)
    {
        var u = await Colleague(id);
        if (u.Active == active) return u;
        if (!active)
        {
            if (u.Id == ctx.UserId) throw new AppException("You can't switch off your own login.");
            await KeepAnOwner(u);
            u.TokenVersion++;
        }
        else
        {
            var company = await db.Companies.SingleAsync(c => c.Id == db.CompanyId);
            var plan = Billing.Plans.All(config).FirstOrDefault(p => p.Id == company.Plan);
            if (plan is not null && await db.Users.CountAsync(x => x.Active && x.Role != Role.Employee) >= plan.Users)
                throw new AppException($"The {plan.Name} plan has room for {plan.Users} users. Choose a bigger plan, or switch someone else off first.");
            (u.FailedLogins, u.LockedUntil) = (0, null);
        }
        u.Active = active;
        await db.SaveChangesAsync();
        return u;
    }

    /// <summary>A forgotten password: a one-time one, shown once, that they must replace when they sign in. Their other
    /// sessions end and a lockout is lifted.</summary>
    public async Task<AccessGrant> ResetPassword(Guid id)
    {
        var u = await Colleague(id);
        if (u.Id == ctx.UserId) throw new AppException("Change your own password from Sign-in security.");
        if (!u.Active) throw new AppException($"Switch {u.Name}'s login on first.");
        var password = NewPassword();
        u.PasswordHash = Hasher.HashPassword(u, password);
        (u.MustChangePassword, u.FailedLogins, u.LockedUntil) = (true, 0, null);
        u.TokenVersion++;
        await db.SaveChangesAsync();
        return new AccessGrant(u.Email, password, true);
    }

    /// <summary>HR gives an employee a self-service login (or resets it): their phone and a one-time password
    /// they must replace on first sign-in. The password is returned once, to hand over in person or on WhatsApp.</summary>
    public async Task<AccessGrant> GrantAccess(Guid employeeId)
    {
        var e = await db.Employees.SingleOrDefaultAsync(x => x.Id == employeeId) ?? throw new AppException("Not found.", 404);
        if (!e.Active(Clock.Today)) throw new AppException($"{e.Name} no longer works here.");
        var phone = Phones.ToMsisdn(e.Phone) ?? throw new AppException($"Add {e.Name}'s Safaricom or Airtel number first: they sign in with it.");
        var clash = await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Phone == phone && u.Role == Role.Employee && u.Active && u.EmployeeId != e.Id && u.CompanyId == e.CompanyId);
        if (clash) throw new AppException("Another employee here already signs in with that phone.", 409);
        var password = NewPassword();
        var user = await db.Users.SingleOrDefaultAsync(u => u.EmployeeId == e.Id);
        var reset = user is not null;
        if (user is null)
        {
            var email = CleanEmail(e.Email);
            if (email.Length > 0 && await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email)) email = "";
            user = new User { Name = e.Name, Email = email, Phone = phone, Role = Role.Employee, EmployeeId = e.Id };
            db.Users.Add(user);
        }
        (user.Phone, user.Active, user.MustChangePassword, user.FailedLogins, user.LockedUntil) = (phone, true, true, 0, null);
        user.PasswordHash = Hasher.HashPassword(user, password);
        await db.SaveChangesAsync();
        return new AccessGrant($"0{phone[3..]}", password, reset);
    }

    public async Task RevokeAccess(Guid employeeId)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.EmployeeId == employeeId) ?? throw new AppException("They don't have app access.", 404);
        user.Active = false;
        user.TokenVersion++;
        await db.SaveChangesAsync();
    }

    /// <summary>Easy to read out or type on a phone; no 0/O or 1/l.</summary>
    static string NewPassword()
    {
        const string letters = "abcdefghjkmnpqrstuvwxyz", digits = "23456789";
        string Pick(string set, int n) => new(Enumerable.Range(0, n).Select(_ => set[System.Security.Cryptography.RandomNumberGenerator.GetInt32(set.Length)]).ToArray());
        return $"{Pick(letters, 4)}-{Pick(digits, 4)}-{Pick(letters, 4)}";
    }

    public async Task<Session> ChangePassword(PasswordInput input)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == ctx.UserId) ?? throw new AppException("Please sign in again.", 401);
        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, input.Current ?? "") == PasswordVerificationResult.Failed) throw new AppException("Your current password isn't right.");
        CheckPassword(input.New);
        if (input.New == input.Current) throw new AppException("Choose a new password, not the one you were given.");
        user.PasswordHash = Hasher.HashPassword(user, input.New);
        user.MustChangePassword = false;
        user.TokenVersion++; // signed-in devices elsewhere sign in again; this one gets a new token below
        await db.SaveChangesAsync();
        return Session(user, await db.Companies.SingleAsync(c => c.Id == user.CompanyId));
    }

    Session Session(User u, Company c)
    {
        var isPlatformAdmin = Platform.PlatformService.IsAdmin(config, u.Email);
        return new(
            tokens.Issue(u, c.Name),
            new { u.Id, u.Name, u.Email, Role = u.Role.ToString(), u.Language, u.EmployeeId, u.MustChangePassword, u.TwoFactorEnabled, isPlatformAdmin, permissions = Policies.For(u.Role) },
            new { c.Id, c.Name, c.KraPin, c.VatRegistered, c.Industry, c.AccountingLockDate, c.AwaitingFirstPayment, c.OwnerOversight, c.Product });
    }
}
