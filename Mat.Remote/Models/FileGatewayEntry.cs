namespace Matgate.Models;

public sealed record FileGatewayEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long? Size,
    DateTimeOffset? ModifiedAt);

public sealed record FileGatewayListResult(
    string Path,
    string ParentPath,
    IReadOnlyList<FileGatewayEntry> Entries);

public sealed record FileGatewayDownload(
    Stream Content,
    string FileName,
    string ContentType);

public sealed record FileGatewayFileInfo(
    string FileName,
    string ContentType,
    long Length);
