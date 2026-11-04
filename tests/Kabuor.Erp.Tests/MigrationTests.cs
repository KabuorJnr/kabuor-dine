// The live database was first made by EnsureCreated, before migrations existed. On start-up it must be
// recognised as being at the first migration and then upgraded, keeping its data. PostgreSQL only (CI).
using Kabuor.Erp.Data;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Tests;

public class MigrationTests
{
    [Fact]
    public async Task A_database_made_before_migrations_is_adopted_and_upgraded()
    {
        if (ErpFactory.Pg is null) return; // runs where PostgreSQL is available (CI)
        var name = $"kabuor_erp_baseline_{Guid.NewGuid():N}";
        var cs = $"{ErpFactory.Pg};Database={name}";
        try
        {
            // Today's production: tables from the first migration's model, made without a migrations history.
            await using (var db = new ErpDb(new DbContextOptionsBuilder<ErpDb>().UseNpgsql(cs).Options, new RequestContext()))
            {
                var initial = db.Database.GetMigrations().First();
                await db.Database.MigrateAsync(initial);
                await db.Database.ExecuteSqlRawAsync("""DROP TABLE "__EFMigrationsHistory" """);
                await db.Database.ExecuteSqlRawAsync("""INSERT INTO "Companies" ("Id","Name","KraPin","Phone","Email","County","VatRegistered","MpesaShortcode","MpesaCallbackKey","CreatedAt") VALUES (gen_random_uuid(),'Kept Ltd','','','','',false,'','k',now())""");
            }
            await using (var db = new ErpDb(new DbContextOptionsBuilder<ErpDb>().UseNpgsql(cs).Options, new RequestContext()))
            {
                await SchemaSync.Prepare(db);
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.Equal(1, await db.Companies.IgnoreQueryFilters().CountAsync(c => c.Name == "Kept Ltd"));
                Assert.Equal(0, await db.Users.IgnoreQueryFilters().CountAsync(u => u.TwoFactorEnabled)); // new column exists
            }
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var conn = new Npgsql.NpgsqlConnection($"{ErpFactory.Pg};Database=postgres");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
