namespace Adelaide.Core;

/// <summary>Minimal .env reader (dev bootstrap). Production reads config from Key Vault via managed identity.</summary>
public static class DotEnv
{
    public static IReadOnlyDictionary<string, string> Read(string? path = null)
    {
        path ??= FindUp(".env");
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (path is null || !File.Exists(path)) return d;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('=')) continue;
            var i = line.IndexOf('=');
            d[line[..i].Trim()] = line[(i + 1)..].Trim().Trim('"').Trim('\'');
        }
        return d;
    }

    private static string? FindUp(string name)
    {
        var dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }
}
