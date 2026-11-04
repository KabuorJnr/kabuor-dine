// Database addresses from the hosts we run on: Neon is always encrypted with its certificate checked; sslmode in a
// URL is honoured; passwords with special characters survive.
namespace Kabuor.Erp.Tests;

public class HostingTests
{
    [Fact]
    public void A_Neon_url_becomes_an_encrypted_verified_connection()
    {
        var cs = Program.ConnectionString("postgresql://kabuor:p%40ss%2Fw0rd@ep-cool-sun-123456.eu-central-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require");
        Assert.Contains("Host=ep-cool-sun-123456.eu-central-1.aws.neon.tech", cs);
        Assert.Contains("Database=neondb", cs);
        Assert.Contains("Password=p@ss/w0rd", cs);
        Assert.Contains("SSL Mode=VerifyFull", cs);
        Assert.Contains("Channel Binding=Require", cs);
        Assert.DoesNotContain("Trust Server Certificate", cs);
    }

    [Fact]
    public void Other_urls_keep_their_sslmode()
    {
        Assert.Contains("SSL Mode=Require", Program.ConnectionString("postgres://u:p@db.example.com:6543/app?sslmode=require"));
        Assert.Contains("SSL Mode=Prefer", Program.ConnectionString("postgres://u:p@postgres.internal:5432/app"));
        Assert.Equal("Data Source=local.db", Program.ConnectionString("Data Source=local.db"));
    }
}
