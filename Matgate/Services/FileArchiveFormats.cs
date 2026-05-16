namespace Matgate.Services;

internal static class FileArchiveFormats
{
    internal static readonly string[] SupportedExtensions =
    [
        ".zip",
        ".rar",
        ".7z",
        ".tar",
        ".gz",
        ".tgz",
        ".bz2",
        ".tbz2",
        ".xz",
        ".lz",
        ".lzip",
        ".tar.gz",
        ".tar.bz2",
        ".tar.xz"
    ];

    internal static bool IsArchiveFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var normalized = fileName.Trim().ToLowerInvariant();
        return SupportedExtensions.Any(normalized.EndsWith);
    }
}
