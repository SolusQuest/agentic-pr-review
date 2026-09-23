namespace ReviewSandbox;

public static class Lookup
{
    public static string? Find(string key) => key == "missing" ? null : key.Trim();
}
