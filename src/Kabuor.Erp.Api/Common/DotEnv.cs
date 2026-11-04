// Local development: settings from a .env file in the repository root, so `dotnet run` uses the same values as the
// scripts in tools/. Real environment variables always win (Render's settings are never overridden), empty values
// are skipped, and .env is kept out of git and out of the Docker image.
namespace Kabuor.Erp.Common;

public static class DotEnv
{
    public static void Load()
    {
        // From src/Kabuor.Erp.Api (or the repository root) up to the first .env found.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; depth < 4 && dir is not null; depth++, dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, ".env");
            if (!File.Exists(file)) continue;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ")) line = line[7..].TrimStart();
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1];
                if (value.Length > 0 && Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, value);
            }
            return;
        }
    }
}
