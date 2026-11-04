using Kabuor.Erp.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kabuor.Erp.Data;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ErpDb>
{
    public ErpDb CreateDbContext(string[] args)
    {
        DotEnv.Load();
        var raw = Environment.GetEnvironmentVariable("DATABASE_URL_UNPOOLED")
               ?? Environment.GetEnvironmentVariable("DATABASE_URL");
        var cs = !string.IsNullOrWhiteSpace(raw)
            ? Program.ConnectionString(raw)
            : "Host=localhost;Database=kabuor_design;Username=design;Password=design";
        return new(new DbContextOptionsBuilder<ErpDb>().UseNpgsql(cs, npgsql => npgsql.CommandTimeout(300)).Options, new RequestContext());
    }
}

