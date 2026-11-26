using Matgate.Files;
using Matgate.Models;
using System.Buffers;
using static Matgate.Files.RemoteFiles;

namespace Matgate.Services;

public interface IFileGatewayService
{
    Task<FileGatewayListResult> ListAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken = default);

    Task<FileGatewayDownload> DownloadAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken = default);

    Task<string> GetSftpHomeDirectoryAsync(ServerEndpoint server, CancellationToken cancellationToken = default);

    Task<FileGatewayFileInfo> GetFileInfoAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken = default);

    Task CopyRangeAsync(
        ServerEndpoint server,
        string? path,
        Stream output,
        long offset,
        long length,
        CancellationToken cancellationToken = default);

    Task UploadAsync(
        ServerEndpoint server,
        string? path,
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default);

    Task CreateFileAsync(
        ServerEndpoint server,
        string? path,
        string fileName,
        CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(
        ServerEndpoint server,
        string? path,
        string directoryName,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken = default);
}

public sealed partial class FileGatewayService(Matgate.Agent.AgentClientPool agents) : IFileGatewayService
{
    public Task<FileGatewayListResult> ListAsync(
        ServerEndpoint server,
        string? path,
        CancellationToken cancellationToken = default)
    {
        return server.Protocol switch
        {
            ServerProtocol.Sftp => RemoteFiles.ListAsync(FileTargets.Of(server), path, cancellationToken),
            ServerProtocol.Local => ListLocalAsync(server, path, cancellationToken),
            ServerProtocol.WebDav => RemoteFiles.ListAsync(FileTargets.Of(server), path, cancellationToken),
            ServerProtocol.Agent => ListAgentAsync(server, path, cancellationToken),
            ServerProtocol.Ftp => RemoteFiles.ListAsync(FileTargets.Of(server), path, cancellationToken),
            ServerProtocol.Smb => RemoteFiles.ListAsync(FileTargets.Of(server), path, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public Task<FileGatewayFileInfo> GetFileInfoAsync(
        ServerEndpoint server,
        string? path,
        CancellationToken cancellationToken = default)
    {
        return server.Protocol switch
        {
            ServerProtocol.Sftp => RemoteFiles.GetFileInfoAsync(FileTargets.Of(server), path, cancellationToken),
            ServerProtocol.Local => GetLocalFileInfoAsync(server, path, cancellationToken),
            ServerProtocol.WebDav => RemoteFiles.GetFileInfoAsync(FileTargets.Of(server), path, cancellationToken),
            ServerProtocol.Agent => GetAgentFileInfoAsync(server, path, cancellationToken),
            ServerProtocol.Ftp => RemoteFiles.GetFileInfoAsync(FileTargets.Of(server), path, cancellationToken),
            ServerProtocol.Smb => RemoteFiles.GetFileInfoAsync(FileTargets.Of(server), path, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public Task CopyRangeAsync(
        ServerEndpoint server,
        string? path,
        Stream output,
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        if (length <= 0)
        {
            return Task.CompletedTask;
        }

        return server.Protocol switch
        {
            ServerProtocol.Sftp => RemoteFiles.CopyRangeAsync(FileTargets.Of(server), path, output, offset, length, cancellationToken),
            ServerProtocol.Local => CopyLocalRangeAsync(server, path, output, offset, length, cancellationToken),
            ServerProtocol.WebDav => RemoteFiles.CopyRangeAsync(FileTargets.Of(server), path, output, offset, length, cancellationToken),
            ServerProtocol.Agent => CopyAgentRangeAsync(server, path, output, offset, length, cancellationToken),
            ServerProtocol.Ftp => RemoteFiles.CopyRangeAsync(FileTargets.Of(server), path, output, offset, length, cancellationToken),
            ServerProtocol.Smb => RemoteFiles.CopyRangeAsync(FileTargets.Of(server), path, output, offset, length, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public Task<FileGatewayDownload> DownloadAsync(
        ServerEndpoint server,
        string? path,
        CancellationToken cancellationToken = default)
    {
        return server.Protocol switch
        {
            ServerProtocol.Sftp => RemoteFiles.DownloadAsync(FileTargets.Of(server), path, cancellationToken),
            ServerProtocol.Local => DownloadLocalAsync(server, path, cancellationToken),
            ServerProtocol.WebDav => RemoteFiles.DownloadAsync(FileTargets.Of(server), path, cancellationToken),
            ServerProtocol.Agent => DownloadAgentAsync(server, path, cancellationToken),
            ServerProtocol.Ftp => RemoteFiles.DownloadAsync(FileTargets.Of(server), path, cancellationToken),
            ServerProtocol.Smb => RemoteFiles.DownloadAsync(FileTargets.Of(server), path, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    // A place can be read-only - today the folder of a workspace whose uploads are switched off. The
    // check sits in front of the protocol switch so it holds for every protocol and does not have to
    // be repeated five times.
    private static void EnsureWritable(ServerEndpoint server)
    {
        if (server.IsReadOnly)
        {
            throw new InvalidOperationException("In diese Ablage darf nicht geschrieben werden.");
        }
    }

    public Task UploadAsync(
        ServerEndpoint server,
        string? path,
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        // Read-only means read-only - enforced in one place, not in five, one per protocol. The UI hides
        // the button on top of that; that alone must not be relied on.
        EnsureWritable(server);
        return server.Protocol switch
        {
            ServerProtocol.Sftp => RemoteFiles.UploadAsync(FileTargets.Of(server), path, content, fileName, cancellationToken),
            ServerProtocol.Local => UploadLocalAsync(server, path, content, fileName, cancellationToken),
            ServerProtocol.WebDav => RemoteFiles.UploadAsync(FileTargets.Of(server), path, content, fileName, cancellationToken),
            ServerProtocol.Agent => UploadAgentAsync(server, path, content, fileName, cancellationToken),
            ServerProtocol.Ftp => RemoteFiles.UploadAsync(FileTargets.Of(server), path, content, fileName, cancellationToken),
            ServerProtocol.Smb => RemoteFiles.UploadAsync(FileTargets.Of(server), path, content, fileName, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public Task CreateFileAsync(
        ServerEndpoint server,
        string? path,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        // Read-only means read-only - enforced in one place, not in five, one per protocol. The UI hides
        // the button on top of that; that alone must not be relied on.
        EnsureWritable(server);
        return server.Protocol switch
        {
            ServerProtocol.Sftp => RemoteFiles.CreateFileAsync(FileTargets.Of(server), path, fileName, cancellationToken),
            ServerProtocol.Local => CreateLocalFileAsync(server, path, fileName, cancellationToken),
            ServerProtocol.WebDav => RemoteFiles.CreateFileAsync(FileTargets.Of(server), path, fileName, cancellationToken),
            ServerProtocol.Agent => CreateAgentFileAsync(server, path, fileName, cancellationToken),
            ServerProtocol.Ftp => RemoteFiles.CreateFileAsync(FileTargets.Of(server), path, fileName, cancellationToken),
            ServerProtocol.Smb => RemoteFiles.CreateFileAsync(FileTargets.Of(server), path, fileName, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public Task CreateDirectoryAsync(
        ServerEndpoint server,
        string? path,
        string directoryName,
        CancellationToken cancellationToken = default)
    {
        // Read-only means read-only - enforced in one place, not in five, one per protocol. The UI hides
        // the button on top of that; that alone must not be relied on.
        EnsureWritable(server);
        return server.Protocol switch
        {
            ServerProtocol.Sftp => RemoteFiles.CreateDirectoryAsync(FileTargets.Of(server), path, directoryName, cancellationToken),
            ServerProtocol.Local => CreateLocalDirectoryAsync(server, path, directoryName, cancellationToken),
            ServerProtocol.WebDav => RemoteFiles.CreateDirectoryAsync(FileTargets.Of(server), path, directoryName, cancellationToken),
            ServerProtocol.Agent => CreateAgentDirectoryAsync(server, path, directoryName, cancellationToken),
            ServerProtocol.Ftp => RemoteFiles.CreateDirectoryAsync(FileTargets.Of(server), path, directoryName, cancellationToken),
            ServerProtocol.Smb => RemoteFiles.CreateDirectoryAsync(FileTargets.Of(server), path, directoryName, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public Task DeleteAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken = default)
    {
        // Read-only means read-only - enforced in one place, not in five, one per protocol. The UI hides
        // the button on top of that; that alone must not be relied on.
        EnsureWritable(server);
        return server.Protocol switch
        {
            ServerProtocol.Sftp => RemoteFiles.DeleteAsync(FileTargets.Of(server), path, cancellationToken),
            ServerProtocol.Local => DeleteLocalAsync(server, path, cancellationToken),
            ServerProtocol.WebDav => RemoteFiles.DeleteAsync(FileTargets.Of(server), path, cancellationToken),
            ServerProtocol.Agent => DeleteAgentAsync(server, path, cancellationToken),
            ServerProtocol.Ftp => RemoteFiles.DeleteAsync(FileTargets.Of(server), path, cancellationToken),
            ServerProtocol.Smb => RemoteFiles.DeleteAsync(FileTargets.Of(server), path, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    // --- The gateway's own file areas -------------------------------------------------------------
    //
    // These live on this machine rather than on a remote host, so they are plain file system calls.
    // FileRootPath carries the area's directory; the caller has already decided that this user may
    // see it. Everything below goes through ResolveLocalPath, which is the only thing standing
    // between a path from the browser and the rest of the disk.

    private static Task<FileGatewayListResult> ListLocalAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = NormalizeVirtualPath(path);
            var directory = ResolveLocalPath(server, virtualPath);
            Directory.CreateDirectory(directory);

            var entries = new DirectoryInfo(directory)
                .EnumerateFileSystemInfos()
                // A link inside an area would be a way out of it - they are never created here, and
                // one that shows up anyway is not offered.
                .Where(entry => entry.LinkTarget is null)
                .OrderByDescending(entry => entry is DirectoryInfo)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(entry => new FileGatewayEntry(
                    entry.Name,
                    CombineVirtualPath(virtualPath, entry.Name),
                    entry is DirectoryInfo,
                    entry is FileInfo file ? file.Length : null,
                    new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero)))
                .ToList();

            return new FileGatewayListResult(virtualPath, ParentVirtualPath(virtualPath), entries);
        }, cancellationToken);
    }

    private static Task<FileGatewayFileInfo> GetLocalFileInfoAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(ResolveLocalPath(server, path));
            if (!file.Exists)
            {
                throw new InvalidOperationException("Datei nicht gefunden.");
            }

            return new FileGatewayFileInfo(file.Name, ContentTypeFromPath(file.Name), file.Length);
        }, cancellationToken);
    }

    private static async Task CopyLocalRangeAsync(
        ServerEndpoint server,
        string? path,
        Stream output,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(ResolveLocalPath(server, path));
        source.Seek(offset, SeekOrigin.Begin);

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Task<FileGatewayDownload> DownloadLocalAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = ResolveLocalPath(server, path);
        if (!File.Exists(file))
        {
            throw new InvalidOperationException("Datei nicht gefunden.");
        }

        var name = Path.GetFileName(file);
        return Task.FromResult(new FileGatewayDownload(File.OpenRead(file), name, ContentTypeFromPath(name)));
    }

    private static async Task UploadLocalAsync(
        ServerEndpoint server,
        string? path,
        Stream content,
        string fileName,
        CancellationToken cancellationToken)
    {
        var directory = ResolveLocalPath(server, path);
        Directory.CreateDirectory(directory);
        var target = ResolveLocalPath(server, CombineVirtualPath(NormalizeVirtualPath(path), SafeFileName(fileName)));

        await using var output = File.Create(target);
        await content.CopyToAsync(output, cancellationToken);
    }

    private static Task CreateLocalFileAsync(ServerEndpoint server, string? path, string fileName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ResolveLocalPath(server, CombineVirtualPath(NormalizeVirtualPath(path), SafeFileName(fileName)));
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new InvalidOperationException("Es gibt hier bereits einen Eintrag mit diesem Namen.");
        }

        File.WriteAllBytes(target, []);
        return Task.CompletedTask;
    }

    private static Task CreateLocalDirectoryAsync(ServerEndpoint server, string? path, string directoryName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(ResolveLocalPath(server, CombineVirtualPath(NormalizeVirtualPath(path), SafeFileName(directoryName))));
        return Task.CompletedTask;
    }

    private static Task DeleteLocalAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ResolveLocalPath(server, path);
        var root = Path.GetFullPath(server.FileRootPath ?? "");
        if (string.Equals(target, root, StringComparison.Ordinal))
        {
            // Emptying an area is fine; removing the area itself is not.
            throw new InvalidOperationException("Die Ablage selbst kann nicht geloescht werden.");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
        else if (File.Exists(target))
        {
            File.Delete(target);
        }

        return Task.CompletedTask;
    }

    // Keeps every path inside the area it belongs to. The comparison is made on the resolved path, so
    // ".." cannot climb out of it, and a name is reduced to its last component so nothing can be
    // smuggled in through a file name either.
    private static string ResolveLocalPath(ServerEndpoint server, string? virtualPath)
    {
        if (string.IsNullOrWhiteSpace(server.FileRootPath))
        {
            throw new InvalidOperationException("Diese Ablage hat kein Verzeichnis.");
        }

        var root = Path.GetFullPath(server.FileRootPath).TrimEnd(Path.DirectorySeparatorChar);
        var relative = NormalizeVirtualPath(virtualPath).Trim('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relative));

        if (!string.Equals(full, root, StringComparison.Ordinal)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Ungueltiger Pfad.");
        }

        return full;
    }

    public Task<string> GetSftpHomeDirectoryAsync(ServerEndpoint server, CancellationToken cancellationToken = default) =>
        RemoteFiles.GetSftpHomeDirectoryAsync(FileTargets.Of(server), cancellationToken);
}
