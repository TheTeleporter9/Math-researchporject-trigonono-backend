using System.Text;

namespace Trigon.Api;

public static class Db
{
    public static string GetConnectionString(IConfiguration config)
    {
        var fromConnStr = config.GetConnectionString("Default");
        if (!string.IsNullOrWhiteSpace(fromConnStr)) return fromConnStr;

        var databaseUrl = config["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(databaseUrl))
        {
            return ConvertDatabaseUrlToNpgsql(databaseUrl);
        }

        // Reasonable local fallback for development
        return "Host=localhost;Port=5432;Database=trigon;Username=trigon;Password=trigon";
    }

    private static string ConvertDatabaseUrlToNpgsql(string databaseUrl)
    {
        // Supports: postgres://user:pass@host:5432/dbname
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(0) ?? "");
        var password = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(1) ?? "");

        var db = uri.AbsolutePath.TrimStart('/');
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 5432;

        var sb = new StringBuilder();
        sb.Append($"Host={host};Port={port};Database={db};Username={username};Password={password};");

        var query = uri.Query.TrimStart('?');
        if (!string.IsNullOrWhiteSpace(query))
        {
            // keep it simple: append any unknown query params as "key=value"
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2)
                {
                    sb.Append($"{kv[0]}={Uri.UnescapeDataString(kv[1])};");
                }
            }
        }

        return sb.ToString();
    }
}

