// Getting the database to the shape of the model at start-up.
//  - PostgreSQL (production): EF migrations (Data/Migrations). A database first made by EnsureCreated, before
//    migrations existed, is recognised and marked as being at the first migration, then brought up to date.
//  - SQLite (development files and the tests): EnsureCreated, then any table or column the model has gained
//    since the file was made is added. Columns are only ever added, never changed or dropped.
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Kabuor.Erp.Data;

public static partial class SchemaSync
{
    public static async Task Prepare(ErpDb db, Microsoft.Extensions.Configuration.IConfiguration? config = null)
    {
        if (db.Database.IsNpgsql()) await Migrate(db);
        else await SyncSqlite(db);

        await SeedSampleRestaurant(db);
    }

    static async Task Migrate(ErpDb db)
    {
        var first = db.Database.GetMigrations().First();
        // A brand-new server has no database yet: migrations create it from scratch.
        if (!await db.GetService<IRelationalDatabaseCreator>().ExistsAsync())
        {
            await db.Database.MigrateAsync();
            return;
        }
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            async Task<bool> Exists(string table)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT to_regclass('public.\"{table}\"') IS NOT NULL";
                return (bool)(await cmd.ExecuteScalarAsync())!;
            }
            if (!await Exists("__EFMigrationsHistory") && await Exists("Companies"))
            {
                var version = typeof(DbContext).Assembly.GetName().Version!.ToString(3);
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE "__EFMigrationsHistory" ("MigrationId" character varying(150) NOT NULL PRIMARY KEY, "ProductVersion" character varying(32) NOT NULL);
                    """);
                await db.Database.ExecuteSqlRawAsync("""INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({0}, {1})""", first, version);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
        await db.Database.MigrateAsync();
        await SyncPostgreSqlColumns(db);
    }

    static async Task SyncPostgreSqlColumns(ErpDb db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var entity in db.Model.GetEntityTypes())
                {
                    var table = entity.GetTableName();
                    if (string.IsNullOrEmpty(table)) continue;
                    var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, null);
                    foreach (var p in entity.GetProperties())
                    {
                        var column = p.GetColumnName(store);
                        if (string.IsNullOrEmpty(column)) continue;
                        var type = p.GetColumnType();
                        var notNull = !p.IsNullable;
                        var fallback = p.Name == "UnitFactor" ? "'1.0'"
                            : type.Contains("text", StringComparison.OrdinalIgnoreCase) || type.Contains("varchar", StringComparison.OrdinalIgnoreCase) ? "''"
                            : type.Contains("uuid", StringComparison.OrdinalIgnoreCase) ? "'00000000-0000-0000-0000-000000000000'"
                            : type.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ? "NOW()"
                            : type.Contains("bool", StringComparison.OrdinalIgnoreCase) ? "FALSE"
                            : "0";
                        sb.AppendLine($"ALTER TABLE IF EXISTS \"{table}\" ADD COLUMN IF NOT EXISTS \"{column}\" {type}{(notNull ? $" NOT NULL DEFAULT {fallback}" : "")};");
                    }
                }
                if (sb.Length > 0)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = sb.ToString();
                    cmd.CommandTimeout = 120;
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch
        {
            // Non-blocking schema safeguard
        }
    }

    static async Task SyncSqlite(ErpDb db)
    {
        try 
        { 
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 15000;"); 
        } 
        catch { }
        await db.Database.EnsureCreatedAsync();
        try { await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 15000;"); } catch { }
        // Tables first, then missing columns, then indexes (a new index can be on a column added just now).
        var indexes = new List<string>();
        foreach (var raw in db.Database.GenerateCreateScript().Split(";", StringSplitOptions.RemoveEmptyEntries))
        {
            var sql = raw.Trim();
            if (sql.StartsWith("CREATE TABLE ", StringComparison.Ordinal)) sql = "CREATE TABLE IF NOT EXISTS " + sql["CREATE TABLE ".Length..];
            else if (Index().IsMatch(sql)) { indexes.Add(Index().Replace(sql, "$1 IF NOT EXISTS ", 1)); continue; }
            else continue;
            await db.Database.ExecuteSqlRawAsync(sql.Replace("{", "{{").Replace("}", "}}"));
        }

        // Columns added to existing tables since the file was created.
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            foreach (var entity in db.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (table is null) continue;
                var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
                    await using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync()) have.Add(r.GetString(1));
                }
                var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, null);
                foreach (var p in entity.GetProperties())
                {
                    var column = p.GetColumnName(store);
                    if (column is null || have.Contains(column)) continue;
                    var type = p.GetColumnType();
                    var notNull = !p.IsNullable;
                    var enumType = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
                    var fallback = p.Name == "UnitFactor" ? "'1.0'" // lines from before units: one base unit each
                        : enumType.IsEnum && type.Contains("TEXT", StringComparison.OrdinalIgnoreCase) ? $"'{Enum.GetNames(enumType)[0]}'" // enums are stored by name
                        : type.Contains("TEXT", StringComparison.OrdinalIgnoreCase) ? "''" : "0";
                    // Table, column, type and default all come from the compiled EF model, never from a request.
                    #pragma warning disable EF1002
                    await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type}{(notNull ? $" NOT NULL DEFAULT {fallback}" : "")}");
                    #pragma warning restore EF1002
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
        foreach (var sql in indexes) await db.Database.ExecuteSqlRawAsync(sql.Replace("{", "{{").Replace("}", "}}"));
    }

    [GeneratedRegex(@"^(CREATE (?:UNIQUE )?INDEX) ")]
    private static partial Regex Index();

    /// <summary>Businesses erased from the platform console, by id and by name. Erasing one leaves a record (see
    /// PlatformService.WipeBusiness); the seeders here run on every start, so without this check they quietly put an
    /// erased demo business back the next time the server started, and it reappeared on refresh.</summary>
    public static async Task<(HashSet<Guid> Ids, HashSet<string> Names)> Erased(ErpDb db)
    {
        var rows = await db.AuditEntries.IgnoreQueryFilters().Where(a => a.Entity == nameof(Domain.Company) && a.Action == "Erased")
            .Select(a => new { a.EntityId, a.Changes }).ToListAsync();
        var (ids, names) = (new HashSet<Guid>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var r in rows)
        {
            if (Guid.TryParse(r.EntityId, out var id)) ids.Add(id);
            try
            {
                using var doc = JsonDocument.Parse(r.Changes is { Length: > 0 } c ? c : "{}");
                if (doc.RootElement.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) names.Add(name);
            }
            catch (JsonException) { }
        }
        return (ids, names);
    }

    public static readonly Guid SampleRestaurantCompanyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid SampleChefUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static async Task SeedSampleRestaurant(ErpDb db)
    {
        try
        {
            var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == SampleRestaurantCompanyId || c.Name == "Ziwa Grill & Lounge");
            if (company is null)
            {
                company = new Domain.Company
                {
                    Id = SampleRestaurantCompanyId,
                    Name = "Ziwa Grill & Lounge",
                    Email = "chef@kabuor.co.ke",
                    Phone = "0712345678",
                    County = "Nairobi",
                    Plan = "dine",
                    Product = "dine",
                    Slug = "ziwa-grill",
                    PortalEnabled = true,
                    PaidUntil = DateTimeOffset.UtcNow.AddYears(10)
                };
                db.Companies.Add(company);
                await db.SaveChangesAsync();
            }

            var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<Domain.User>();
            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == "chef@kabuor.co.ke" || u.Id == SampleChefUserId);
            if (user is null)
            {
                user = new Domain.User
                {
                    Id = SampleChefUserId,
                    CompanyId = company.Id,
                    Name = "Chef Rooney",
                    Email = "chef@kabuor.co.ke",
                    Phone = "0712345678",
                    Role = Domain.Role.Owner,
                    TwoFactorEnabled = false,
                    Active = true,
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
                    LastSignInAt = DateTimeOffset.UtcNow,
                    PasswordHash = hasher.HashPassword(null!, "admin")
                };
                db.Users.Add(user);
                await db.SaveChangesAsync();
            }

            // DineSettings
            var hasSettings = await db.DineSettings.IgnoreQueryFilters().AnyAsync(s => s.CompanyId == company.Id);
            if (!hasSettings)
            {
                db.DineSettings.Add(new Domain.DineSettings { CompanyId = company.Id, Welcome = "Welcome to Ziwa Grill & Lounge! Tap below to order." });
                await db.SaveChangesAsync();
            }

            // Tables
            var hasTables = await db.DineTables.IgnoreQueryFilters().AnyAsync(t => t.CompanyId == company.Id);
            if (!hasTables)
            {
                var areas = new[] { "Main Dining", "Main Dining", "Terrace", "Terrace", "VIP Lounge", "Bar" };
                for (var i = 1; i <= 6; i++)
                {
                    db.DineTables.Add(new Domain.DineTable
                    {
                        CompanyId = company.Id,
                        Name = $"Table {i}",
                        Area = areas[i - 1],
                        Seats = i == 5 ? 8 : i == 6 ? 2 : 4,
                        Token = Dine.DineService.NewToken()
                    });
                }
                await db.SaveChangesAsync();
            }

            // Dishes & Menu entries
            var hasMenu = await db.DineMenu.IgnoreQueryFilters().AnyAsync(m => m.CompanyId == company.Id);
            if (!hasMenu)
            {
                (string Name, long Price, string Cat, string Station, string Desc, string Options, bool Pop)[] dishes =
                [
                    ("Nyama Choma (1 kg)", 1_800_00, "Grill", "Kitchen", "Tender prime goat slow-roasted over natural charcoal with fresh kachumbari.", "Doneness: Medium | Well Done\nSide: Ugali | Chips | Mukimo", true),
                    ("Whole Tilapia Wet-Fry", 1_200_00, "Fish & Seafood", "Kitchen", "Fresh lake tilapia deep-fried and simmered in rich tomato, garlic, and coriander sauce.", "Side: Brown Ugali | White Ugali | Chips", true),
                    ("Kuku Choma (Full)", 1_600_00, "Grill", "Kitchen", "Marinated organic kienyeji chicken charbroiled to perfection.", "Spice: Mild | Pili Pili Hot\nSide: Ugali | Chips", false),
                    ("Loaded Beef Burger", 850_00, "Quick Bites", "Kitchen", "Juicy 200g beef patty with melted cheddar, caramelized onions, and house barbecue sauce.", "Side: French Fries | Onion Rings", true),
                    ("Cold Tusker Lager (500ml)", 350_00, "Drinks", "Bar", "Ice-cold Kenyan classic lager brewed from golden malt.", "Temperature: Ice Cold | Room Temp", false),
                    ("Signature Dawa Cocktail", 650_00, "Drinks", "Bar", "Vodka, fresh lime juice, and pure wild acacia honey muddled with cracked ice.", "Strength: Regular | Extra Strong", true),
                    ("Fresh Passion Juice", 300_00, "Soft Drinks", "Bar", "Freshly squeezed sweet passion fruit juice.", "Ice: With Ice | No Ice", false),
                    ("Masala Chips", 400_00, "Sides", "Kitchen", "Crispy potato chips tossed in spicy masala sauce, fresh coriander, and lemon.", "Spice: Medium | Hot", false)
                ];

                foreach (var d in dishes)
                {
                    var item = new Domain.Item
                    {
                        CompanyId = company.Id,
                        Name = d.Name,
                        Price = d.Price,
                        Unit = "portion",
                        Active = true
                    };
                    db.Items.Add(item);
                    await db.SaveChangesAsync();

                    db.DineMenu.Add(new Domain.DineMenuEntry
                    {
                        CompanyId = company.Id,
                        ItemId = item.Id,
                        Category = d.Cat,
                        Station = d.Station,
                        Description = d.Desc,
                        Options = d.Options,
                        Popular = d.Pop
                    });
                }
                await db.SaveChangesAsync();
            }
        }
        catch { /* best effort sample seed */ }
    }
}
