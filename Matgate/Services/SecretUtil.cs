namespace Matgate.Services;

// Resolves secrets from an env var, config, or a "*_FILE" path (Docker secret / shared-volume style),
// so a deployment can auto-generate a secret into a file without the operator setting anything.
public static class SecretUtil
{
    public static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    public static string? ReadSecretFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }
}
