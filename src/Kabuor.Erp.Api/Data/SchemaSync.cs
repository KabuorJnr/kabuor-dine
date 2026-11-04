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
                    await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type}{(notNull ? $" NOT NULL DEFAULT {fallback}" : "")}");
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
}
