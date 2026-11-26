using FluentFTP;
using Matgate.Models;
using Matgate.Services;
using Renci.SshNet;
using System.Text;
using System.Xml.Linq;
using System.Buffers;
using SMBLibrary;
using SMBLibrary.Client;
using SmbFileAttributes = SMBLibrary.FileAttributes;

namespace Matgate.Files;

// The file connections of Matgate and ThixGate (SFTP, FTP, SMB, WebDAV): one implementation for both ("Matgate first").
// Paths are virtual ("/" is the connection's root, FileRootPath on the target); every call opens its own connection.
// Matgate's FileGatewayService adds its own areas (local folders, agents) and the read-only check in front of it.
public static partial class RemoteFiles
{
    private const int SmbChunkSize = 1024 * 1024;

    public static Task<FileGatewayListResult> ListAsync(FileTarget server, string? path, CancellationToken cancellationToken = default)
    {
        return server.Protocol switch
        {
            FileProtocol.Sftp => ListSftpAsync(server, path, cancellationToken),
            FileProtocol.WebDav => ListWebDavAsync(server, path, cancellationToken),
            FileProtocol.Ftp => ListFtpAsync(server, path, cancellationToken),
            FileProtocol.Smb => ListSmbAsync(server, path, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public static Task<FileGatewayFileInfo> GetFileInfoAsync(FileTarget server, string? path, CancellationToken cancellationToken = default)
    {
        return server.Protocol switch
        {
            FileProtocol.Sftp => GetSftpFileInfoAsync(server, path, cancellationToken),
            FileProtocol.WebDav => GetWebDavFileInfoAsync(server, path, cancellationToken),
            FileProtocol.Ftp => GetFtpFileInfoAsync(server, path, cancellationToken),
            FileProtocol.Smb => GetSmbFileInfoAsync(server, path, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public static Task<FileGatewayDownload> DownloadAsync(FileTarget server, string? path, CancellationToken cancellationToken = default)
    {
        return server.Protocol switch
        {
            FileProtocol.Sftp => DownloadSftpAsync(server, path, cancellationToken),
            FileProtocol.WebDav => DownloadWebDavAsync(server, path, cancellationToken),
            FileProtocol.Ftp => DownloadFtpAsync(server, path, cancellationToken),
            FileProtocol.Smb => DownloadSmbAsync(server, path, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public static Task UploadAsync(FileTarget server, string? path, Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        return server.Protocol switch
        {
            FileProtocol.Sftp => UploadSftpAsync(server, path, content, fileName, cancellationToken),
            FileProtocol.WebDav => UploadWebDavAsync(server, path, content, fileName, cancellationToken),
            FileProtocol.Ftp => UploadFtpAsync(server, path, content, fileName, cancellationToken),
            FileProtocol.Smb => UploadSmbAsync(server, path, content, fileName, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public static Task CreateFileAsync(FileTarget server, string? path, string fileName, CancellationToken cancellationToken = default)
    {
        return server.Protocol switch
        {
            FileProtocol.Sftp => CreateSftpFileAsync(server, path, fileName, cancellationToken),
            FileProtocol.WebDav => CreateWebDavFileAsync(server, path, fileName, cancellationToken),
            FileProtocol.Ftp => CreateFtpFileAsync(server, path, fileName, cancellationToken),
            FileProtocol.Smb => CreateSmbFileAsync(server, path, fileName, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public static Task CreateDirectoryAsync(FileTarget server, string? path, string directoryName, CancellationToken cancellationToken = default)
    {
        return server.Protocol switch
        {
            FileProtocol.Sftp => CreateSftpDirectoryAsync(server, path, directoryName, cancellationToken),
            FileProtocol.WebDav => CreateWebDavDirectoryAsync(server, path, directoryName, cancellationToken),
            FileProtocol.Ftp => CreateFtpDirectoryAsync(server, path, directoryName, cancellationToken),
            FileProtocol.Smb => CreateSmbDirectoryAsync(server, path, directoryName, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public static Task DeleteAsync(FileTarget server, string? path, CancellationToken cancellationToken = default)
    {
        return server.Protocol switch
        {
            FileProtocol.Sftp => DeleteSftpAsync(server, path, cancellationToken),
            FileProtocol.WebDav => DeleteWebDavAsync(server, path, cancellationToken),
            FileProtocol.Ftp => DeleteFtpAsync(server, path, cancellationToken),
            FileProtocol.Smb => DeleteSmbAsync(server, path, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    public static Task CopyRangeAsync(FileTarget server, string? path, Stream output, long offset, long length, CancellationToken cancellationToken = default)
    {
        if (length <= 0)
        {
            return Task.CompletedTask;
        }

        return server.Protocol switch
        {
            FileProtocol.Sftp => CopySftpRangeAsync(server, path, output, offset, length, cancellationToken),
            FileProtocol.WebDav => CopyWebDavRangeAsync(server, path, output, offset, length, cancellationToken),
            FileProtocol.Ftp => CopyFtpRangeAsync(server, path, output, offset, length, cancellationToken),
            FileProtocol.Smb => CopySmbRangeAsync(server, path, output, offset, length, cancellationToken),
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung.")
        };
    }

    private static Task<FileGatewayListResult> ListSftpAsync(
        FileTarget server,
        string? path,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = NormalizeVirtualPath(path);
            var remotePath = ToRemotePosixPath(server.FileRootPath, virtualPath);

            using var client = CreateSftpClient(server);
            client.Connect();

            var entries = client
                .ListDirectory(remotePath)
                .Where(entry => entry.Name is not "." and not "..")
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(entry => new FileGatewayEntry(
                    entry.Name,
                    CombineVirtualPath(virtualPath, entry.Name),
                    entry.IsDirectory,
                    entry.IsDirectory ? null : entry.Length,
                    entry.LastWriteTimeUtc == default
                        ? null
                        : new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero)))
                .ToList();

            return new FileGatewayListResult(virtualPath, ParentVirtualPath(virtualPath), entries);
        }, cancellationToken);
    }

    private static Task<FileGatewayDownload> DownloadSftpAsync(
        FileTarget server,
        string? path,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = NormalizeVirtualPath(path);
            var remotePath = ToRemotePosixPath(server.FileRootPath, virtualPath);
            var content = new MemoryStream();

            using var client = CreateSftpClient(server);
            client.Connect();
            client.DownloadFile(remotePath, content);
            content.Position = 0;

            return new FileGatewayDownload(content, FileNameFromPath(virtualPath), ContentTypeFromPath(virtualPath));
        }, cancellationToken);
    }

    private static Task<FileGatewayFileInfo> GetSftpFileInfoAsync(
        FileTarget server,
        string? path,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = NormalizeVirtualPath(path);
            EnsureNotRootPath(virtualPath);
            var remotePath = ToRemotePosixPath(server.FileRootPath, virtualPath);

            using var client = CreateSftpClient(server);
            client.Connect();
            var attributes = client.GetAttributes(remotePath);
            if (attributes.IsDirectory)
            {
                throw new InvalidOperationException("Ordner koennen nicht gestreamt werden.");
            }

            return new FileGatewayFileInfo(
                FileNameFromPath(virtualPath),
                ContentTypeFromPath(virtualPath),
                attributes.Size);
        }, cancellationToken);
    }

    private static Task CopySftpRangeAsync(
        FileTarget server,
        string? path,
        Stream output,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = NormalizeVirtualPath(path);
            EnsureNotRootPath(virtualPath);
            var remotePath = ToRemotePosixPath(server.FileRootPath, virtualPath);

            using var client = CreateSftpClient(server);
            client.Connect();
            await using var input = client.OpenRead(remotePath);
            input.Seek(offset, SeekOrigin.Begin);
            await CopyExactlyAsync(input, output, length, cancellationToken);
        }, cancellationToken);
    }

    private static Task UploadSftpAsync(
        FileTarget server,
        string? path,
        Stream content,
        string fileName,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = NormalizeVirtualPath(path);
            var remotePath = ToRemotePosixPath(server.FileRootPath, CombineVirtualPath(directory, CleanLeafName(fileName)));

            using var client = CreateSftpClient(server);
            client.Connect();
            client.UploadFile(content, remotePath, true);
        }, cancellationToken);
    }

    private static Task CreateSftpFileAsync(
        FileTarget server,
        string? path,
        string fileName,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = NormalizeVirtualPath(path);
            var remotePath = ToRemotePosixPath(server.FileRootPath, CombineVirtualPath(directory, CleanLeafName(fileName)));

            using var client = CreateSftpClient(server);
            client.Connect();
            if (client.Exists(remotePath))
            {
                throw new InvalidOperationException("Die Datei existiert bereits.");
            }

            using var empty = new MemoryStream();
            client.UploadFile(empty, remotePath, false);
        }, cancellationToken);
    }

    private static Task CreateSftpDirectoryAsync(
        FileTarget server,
        string? path,
        string directoryName,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remotePath = ToRemotePosixPath(
                server.FileRootPath,
                CombineVirtualPath(NormalizeVirtualPath(path), CleanLeafName(directoryName)));

            using var client = CreateSftpClient(server);
            client.Connect();
            client.CreateDirectory(remotePath);
        }, cancellationToken);
    }

    private static Task DeleteSftpAsync(FileTarget server, string? path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = NormalizeVirtualPath(path);
            EnsureNotRootPath(virtualPath);
            var remotePath = ToRemotePosixPath(server.FileRootPath, virtualPath);

            using var client = CreateSftpClient(server);
            client.Connect();
            var attributes = client.GetAttributes(remotePath);
            if (attributes.IsDirectory)
            {
                DeleteSftpDirectoryRecursive(client, remotePath);
            }
            else
            {
                client.DeleteFile(remotePath);
            }
        }, cancellationToken);
    }

    private static void DeleteSftpDirectoryRecursive(SftpClient client, string remotePath)
    {
        foreach (var entry in client.ListDirectory(remotePath).Where(entry => entry.Name is not "." and not ".."))
        {
            if (entry.IsDirectory)
            {
                DeleteSftpDirectoryRecursive(client, entry.FullName);
            }
            else
            {
                client.DeleteFile(entry.FullName);
            }
        }

        client.DeleteDirectory(remotePath);
    }

    private static async Task<FileGatewayListResult> ListFtpAsync(
        FileTarget server,
        string? path,
        CancellationToken cancellationToken)
    {
        var virtualPath = NormalizeVirtualPath(path);
        var remotePath = ToRemotePosixPath(server.FileRootPath, virtualPath);
        using var client = CreateFtpClient(server);
        await client.Connect(cancellationToken);

        var listing = await client.GetListing(remotePath, cancellationToken);
        var entries = listing
            .Where(item => item.Name is not "." and not "..")
            .OrderByDescending(item => item.Type == FtpObjectType.Directory)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new FileGatewayEntry(
                item.Name,
                CombineVirtualPath(virtualPath, item.Name),
                item.Type == FtpObjectType.Directory,
                item.Type == FtpObjectType.Directory ? null : item.Size,
                item.Modified == default ? null : new DateTimeOffset(item.Modified)))
            .ToList();

        await client.Disconnect(cancellationToken);
        return new FileGatewayListResult(virtualPath, ParentVirtualPath(virtualPath), entries);
    }

    private static async Task<FileGatewayDownload> DownloadFtpAsync(
        FileTarget server,
        string? path,
        CancellationToken cancellationToken)
    {
        var virtualPath = NormalizeVirtualPath(path);
        var remotePath = ToRemotePosixPath(server.FileRootPath, virtualPath);
        var content = new MemoryStream();

        using var client = CreateFtpClient(server);
        await client.Connect(cancellationToken);
        await client.DownloadStream(content, remotePath, token: cancellationToken);
        await client.Disconnect(cancellationToken);
        content.Position = 0;

        return new FileGatewayDownload(content, FileNameFromPath(virtualPath), ContentTypeFromPath(virtualPath));
    }

    private static async Task<FileGatewayFileInfo> GetFtpFileInfoAsync(
        FileTarget server,
        string? path,
        CancellationToken cancellationToken)
    {
        var virtualPath = NormalizeVirtualPath(path);
        EnsureNotRootPath(virtualPath);
        var remotePath = ToRemotePosixPath(server.FileRootPath, virtualPath);

        using var client = CreateFtpClient(server);
        await client.Connect(cancellationToken);
        var length = await client.GetFileSize(remotePath, -1, cancellationToken);
        await client.Disconnect(cancellationToken);
        if (length < 0)
        {
            throw new InvalidOperationException("Die Dateigroesse konnte nicht ermittelt werden.");
        }

        return new FileGatewayFileInfo(
            FileNameFromPath(virtualPath),
            ContentTypeFromPath(virtualPath),
            length);
    }

    private static async Task CopyFtpRangeAsync(
        FileTarget server,
        string? path,
        Stream output,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        var virtualPath = NormalizeVirtualPath(path);
        EnsureNotRootPath(virtualPath);
        var remotePath = ToRemotePosixPath(server.FileRootPath, virtualPath);

        using var client = CreateFtpClient(server);
        await client.Connect(cancellationToken);
        await using (var input = await client.OpenRead(remotePath, FtpDataType.Binary, offset, -1, cancellationToken))
        {
            await CopyExactlyAsync(input, output, length, cancellationToken);
        }

        await client.Disconnect(cancellationToken);
    }

    private static async Task UploadFtpAsync(
        FileTarget server,
        string? path,
        Stream content,
        string fileName,
        CancellationToken cancellationToken)
    {
        var directory = NormalizeVirtualPath(path);
        var remotePath = ToRemotePosixPath(server.FileRootPath, CombineVirtualPath(directory, CleanLeafName(fileName)));

        using var client = CreateFtpClient(server);
        await client.Connect(cancellationToken);
        await client.UploadStream(content, remotePath, FtpRemoteExists.Overwrite, true, token: cancellationToken);
        await client.Disconnect(cancellationToken);
    }

    private static async Task CreateFtpFileAsync(
        FileTarget server,
        string? path,
        string fileName,
        CancellationToken cancellationToken)
    {
        var directory = NormalizeVirtualPath(path);
        var remotePath = ToRemotePosixPath(server.FileRootPath, CombineVirtualPath(directory, CleanLeafName(fileName)));
        using var client = CreateFtpClient(server);
        await client.Connect(cancellationToken);
        var parentListing = await client.GetListing(ParentVirtualPath(remotePath), cancellationToken);
        var existing = parentListing.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName, remotePath, StringComparison.Ordinal)
            || string.Equals(candidate.Name, FileNameFromPath(remotePath), StringComparison.Ordinal));

        if (existing is not null)
        {
            throw new InvalidOperationException("Die Datei existiert bereits.");
        }

        using var empty = new MemoryStream();
        await client.UploadStream(empty, remotePath, FtpRemoteExists.Overwrite, true, token: cancellationToken);
        await client.Disconnect(cancellationToken);
    }

    private static async Task CreateFtpDirectoryAsync(
        FileTarget server,
        string? path,
        string directoryName,
        CancellationToken cancellationToken)
    {
        var remotePath = ToRemotePosixPath(
            server.FileRootPath,
            CombineVirtualPath(NormalizeVirtualPath(path), CleanLeafName(directoryName)));

        using var client = CreateFtpClient(server);
        await client.Connect(cancellationToken);
        await client.CreateDirectory(remotePath, cancellationToken);
        await client.Disconnect(cancellationToken);
    }

    private static async Task DeleteFtpAsync(FileTarget server, string? path, CancellationToken cancellationToken)
    {
        var virtualPath = NormalizeVirtualPath(path);
        EnsureNotRootPath(virtualPath);
        var remotePath = ToRemotePosixPath(server.FileRootPath, virtualPath);

        using var client = CreateFtpClient(server);
        await client.Connect(cancellationToken);
        var item = (await client.GetListing(ParentVirtualPath(remotePath), cancellationToken))
            .FirstOrDefault(candidate => string.Equals(candidate.FullName, remotePath, StringComparison.Ordinal)
                || string.Equals(candidate.Name, FileNameFromPath(remotePath), StringComparison.Ordinal));

        if (item?.Type == FtpObjectType.Directory)
        {
            await client.DeleteDirectory(remotePath, cancellationToken);
        }
        else
        {
            await client.DeleteFile(remotePath, cancellationToken);
        }

        await client.Disconnect(cancellationToken);
    }

    private static Task<FileGatewayListResult> ListSmbAsync(
        FileTarget server,
        string? path,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = NormalizeVirtualPath(path);
            using var session = SmbSession.Connect(server);
            var smbPath = ResolveSmbPath(server, virtualPath);

            if (string.IsNullOrWhiteSpace(smbPath.ShareName))
            {
                var shares = session.Client.ListShares(out var shareStatus);
                CheckSmbStatus(shareStatus, "SMB-Shares lesen");
                var shareEntries = shares
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(name => new FileGatewayEntry(name, CombineVirtualPath("/", name), true, null, null))
                    .ToList();

                return new FileGatewayListResult("/", "/", shareEntries);
            }

            using var tree = session.TreeConnect(smbPath.ShareName);
            object? directoryHandle = null;
            try
            {
                var status = tree.Store.CreateFile(
                    out directoryHandle,
                    out _,
                    smbPath.Path,
                    AccessMask.GENERIC_READ,
                    SmbFileAttributes.Directory,
                    ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                    CreateDisposition.FILE_OPEN,
                    CreateOptions.FILE_DIRECTORY_FILE,
                    null!);
                CheckSmbStatus(status, "SMB-Ordner oeffnen");

                status = tree.Store.QueryDirectory(
                    out var directoryEntries,
                    directoryHandle,
                    "*",
                    FileInformationClass.FileDirectoryInformation);
                if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_NO_MORE_FILES)
                {
                    CheckSmbStatus(status, "SMB-Ordner lesen");
                }

                directoryEntries ??= [];
                var entries = directoryEntries
                    .OfType<FileDirectoryInformation>()
                    .Where(entry => entry.FileName is not "." and not "..")
                    .OrderByDescending(entry => entry.FileAttributes.HasFlag(SmbFileAttributes.Directory))
                    .ThenBy(entry => entry.FileName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(entry => new FileGatewayEntry(
                        entry.FileName,
                        CombineVirtualPath(virtualPath, entry.FileName),
                        entry.FileAttributes.HasFlag(SmbFileAttributes.Directory),
                        entry.FileAttributes.HasFlag(SmbFileAttributes.Directory) ? null : entry.EndOfFile,
                        entry.LastWriteTime == default
                            ? null
                            : new DateTimeOffset(DateTime.SpecifyKind(entry.LastWriteTime, DateTimeKind.Utc))))
                    .ToList();

                return new FileGatewayListResult(virtualPath, ParentVirtualPath(virtualPath), entries);
            }
            finally
            {
                if (directoryHandle is not null)
                {
                    tree.Store.CloseFile(directoryHandle);
                }
            }
        }, cancellationToken);
    }

    private static Task<FileGatewayDownload> DownloadSmbAsync(
        FileTarget server,
        string? path,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = NormalizeVirtualPath(path);
            EnsureNotRootPath(virtualPath);
            var smbPath = ResolveSmbPath(server, virtualPath);
            if (string.IsNullOrWhiteSpace(smbPath.ShareName))
            {
                throw new InvalidOperationException("Bitte zuerst einen SMB-Share auswaehlen.");
            }

            var content = new MemoryStream();
            using var session = SmbSession.Connect(server);
            using var tree = session.TreeConnect(smbPath.ShareName);
            object? fileHandle = null;

            try
            {
                var status = tree.Store.CreateFile(
                    out fileHandle,
                    out _,
                    smbPath.Path,
                    AccessMask.GENERIC_READ,
                    SmbFileAttributes.Normal,
                    ShareAccess.Read,
                    CreateDisposition.FILE_OPEN,
                    CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SEQUENTIAL_ONLY,
                    null!);
                CheckSmbStatus(status, "SMB-Datei oeffnen");

                long offset = 0;
                var chunkSize = Math.Min(SmbChunkSize, Math.Max(65536, (int)tree.Store.MaxReadSize));
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    status = tree.Store.ReadFile(out var data, fileHandle, offset, chunkSize);
                    if (status == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
                    {
                        break;
                    }

                    CheckSmbStatus(status, "SMB-Datei lesen");
                    content.Write(data, 0, data.Length);
                    offset += data.Length;
                }
            }
            finally
            {
                if (fileHandle is not null)
                {
                    tree.Store.CloseFile(fileHandle);
                }
            }

            content.Position = 0;
            return new FileGatewayDownload(content, FileNameFromPath(virtualPath), ContentTypeFromPath(virtualPath));
        }, cancellationToken);
    }

    private static async Task<FileGatewayFileInfo> GetSmbFileInfoAsync(
        FileTarget server,
        string? path,
        CancellationToken cancellationToken)
    {
        var virtualPath = NormalizeVirtualPath(path);
        EnsureNotRootPath(virtualPath);
        var parentPath = ParentVirtualPath(virtualPath);
        var listing = await ListSmbAsync(server, parentPath, cancellationToken);
        var entry = listing.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, virtualPath, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new InvalidOperationException("Datei wurde nicht gefunden.");
        }

        if (entry.IsDirectory)
        {
            throw new InvalidOperationException("Ordner koennen nicht gestreamt werden.");
        }

        return new FileGatewayFileInfo(
            FileNameFromPath(virtualPath),
            ContentTypeFromPath(virtualPath),
            entry.Size ?? 0);
    }

    private static Task CopySmbRangeAsync(
        FileTarget server,
        string? path,
        Stream output,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = NormalizeVirtualPath(path);
            EnsureNotRootPath(virtualPath);
            var smbPath = ResolveSmbPath(server, virtualPath);
            if (string.IsNullOrWhiteSpace(smbPath.ShareName))
            {
                throw new InvalidOperationException("Bitte zuerst einen SMB-Share auswaehlen.");
            }

            using var session = SmbSession.Connect(server);
            using var tree = session.TreeConnect(smbPath.ShareName);
            object? fileHandle = null;

            try
            {
                var status = tree.Store.CreateFile(
                    out fileHandle,
                    out _,
                    smbPath.Path,
                    AccessMask.GENERIC_READ,
                    SmbFileAttributes.Normal,
                    ShareAccess.Read,
                    CreateDisposition.FILE_OPEN,
                    CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SEQUENTIAL_ONLY,
                    null!);
                CheckSmbStatus(status, "SMB-Datei oeffnen");

                var remaining = length;
                var currentOffset = offset;
                var chunkSize = Math.Min(SmbChunkSize, Math.Max(65536, (int)tree.Store.MaxReadSize));
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (int)Math.Min(chunkSize, remaining);
                    status = tree.Store.ReadFile(out var data, fileHandle, currentOffset, requested);
                    if (status == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
                    {
                        break;
                    }

                    CheckSmbStatus(status, "SMB-Datei lesen");
                    var bytesToWrite = (int)Math.Min(data.Length, remaining);
                    await output.WriteAsync(data.AsMemory(0, bytesToWrite), cancellationToken);
                    remaining -= bytesToWrite;
                    currentOffset += bytesToWrite;
                }
            }
            finally
            {
                if (fileHandle is not null)
                {
                    tree.Store.CloseFile(fileHandle);
                }
            }
        }, cancellationToken);
    }

    private static Task UploadSmbAsync(
        FileTarget server,
        string? path,
        Stream content,
        string fileName,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = NormalizeVirtualPath(path);
            var targetPath = CombineVirtualPath(directory, CleanLeafName(fileName));
            var smbPath = ResolveSmbPath(server, targetPath);
            if (string.IsNullOrWhiteSpace(smbPath.ShareName))
            {
                throw new InvalidOperationException("Bitte zuerst einen SMB-Share auswaehlen.");
            }

            using var session = SmbSession.Connect(server);
            using var tree = session.TreeConnect(smbPath.ShareName);
            object? fileHandle = null;

            try
            {
                var status = tree.Store.CreateFile(
                    out fileHandle,
                    out _,
                    smbPath.Path,
                    AccessMask.GENERIC_WRITE,
                    SmbFileAttributes.Normal,
                    ShareAccess.None,
                    CreateDisposition.FILE_OVERWRITE_IF,
                    CreateOptions.FILE_NON_DIRECTORY_FILE,
                    null!);
                CheckSmbStatus(status, "SMB-Datei erstellen");

                var buffer = new byte[Math.Min(SmbChunkSize, Math.Max(65536, (int)tree.Store.MaxWriteSize))];
                long offset = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = content.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    var payload = read == buffer.Length ? buffer : buffer[..read];
                    status = tree.Store.WriteFile(out var bytesWritten, fileHandle, offset, payload);
                    CheckSmbStatus(status, "SMB-Datei schreiben");
                    offset += bytesWritten;
                }
            }
            finally
            {
                if (fileHandle is not null)
                {
                    tree.Store.CloseFile(fileHandle);
                }
            }
        }, cancellationToken);
    }

    private static Task CreateSmbFileAsync(
        FileTarget server,
        string? path,
        string fileName,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = CombineVirtualPath(NormalizeVirtualPath(path), CleanLeafName(fileName));
            var smbPath = ResolveSmbPath(server, targetPath);
            if (string.IsNullOrWhiteSpace(smbPath.ShareName))
            {
                throw new InvalidOperationException("Bitte zuerst einen SMB-Share auswaehlen.");
            }

            var parentPath = ParentVirtualPath(targetPath);
            var parentEntries = ListSmbAsync(server, parentPath, cancellationToken).GetAwaiter().GetResult().Entries;
            if (parentEntries.Any(entry => string.Equals(entry.Path, targetPath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Die Datei existiert bereits.");
            }

            using var session = SmbSession.Connect(server);
            using var tree = session.TreeConnect(smbPath.ShareName);
            object? fileHandle = null;

            try
            {
                var status = tree.Store.CreateFile(
                    out fileHandle,
                    out _,
                    smbPath.Path,
                    AccessMask.GENERIC_WRITE,
                    SmbFileAttributes.Normal,
                    ShareAccess.None,
                    CreateDisposition.FILE_CREATE,
                    CreateOptions.FILE_NON_DIRECTORY_FILE,
                    null!);
                CheckSmbStatus(status, "SMB-Datei erstellen");
            }
            finally
            {
                if (fileHandle is not null)
                {
                    tree.Store.CloseFile(fileHandle);
                }
            }
        }, cancellationToken);
    }

    private static Task CreateSmbDirectoryAsync(
        FileTarget server,
        string? path,
        string directoryName,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = CombineVirtualPath(NormalizeVirtualPath(path), CleanLeafName(directoryName));
            var smbPath = ResolveSmbPath(server, targetPath);
            if (string.IsNullOrWhiteSpace(smbPath.ShareName))
            {
                throw new InvalidOperationException("Bitte zuerst einen SMB-Share auswaehlen.");
            }

            using var session = SmbSession.Connect(server);
            using var tree = session.TreeConnect(smbPath.ShareName);
            object? directoryHandle = null;
            try
            {
                var status = tree.Store.CreateFile(
                    out directoryHandle,
                    out _,
                    smbPath.Path,
                    AccessMask.GENERIC_WRITE,
                    SmbFileAttributes.Directory,
                    ShareAccess.None,
                    CreateDisposition.FILE_CREATE,
                    CreateOptions.FILE_DIRECTORY_FILE,
                    null!);
                CheckSmbStatus(status, "SMB-Ordner erstellen");
            }
            finally
            {
                if (directoryHandle is not null)
                {
                    tree.Store.CloseFile(directoryHandle);
                }
            }
        }, cancellationToken);
    }

    private static Task DeleteSmbAsync(FileTarget server, string? path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = NormalizeVirtualPath(path);
            EnsureNotRootPath(virtualPath);
            var smbPath = ResolveSmbPath(server, virtualPath);
            if (string.IsNullOrWhiteSpace(smbPath.ShareName))
            {
                throw new InvalidOperationException("Ein SMB-Share kann nicht geloescht werden.");
            }

            if (string.IsNullOrWhiteSpace(smbPath.Path))
            {
                throw new InvalidOperationException("Ein SMB-Share kann nicht geloescht werden.");
            }

            IReadOnlyList<FileGatewayEntry>? directoryEntries = null;
            try
            {
                directoryEntries = ListSmbAsync(server, virtualPath, cancellationToken).GetAwaiter().GetResult().Entries;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // The path is probably a file. The direct delete below will surface real failures.
            }

            if (directoryEntries is not null)
            {
                foreach (var entry in directoryEntries)
                {
                    DeleteSmbAsync(server, entry.Path, cancellationToken).GetAwaiter().GetResult();
                }
            }

            using var session = SmbSession.Connect(server);
            using var tree = session.TreeConnect(smbPath.ShareName);
            if (!TryDeleteSmbPath(tree.Store, smbPath.Path, isDirectory: false))
            {
                CheckSmbStatus(DeleteSmbPath(tree.Store, smbPath.Path, isDirectory: true), "SMB-Ordner loeschen");
            }
        }, cancellationToken);
    }

    private static bool TryDeleteSmbPath(SMB2FileStore store, string path, bool isDirectory)
    {
        var status = DeleteSmbPath(store, path, isDirectory);
        return status == NTStatus.STATUS_SUCCESS;
    }

    private static NTStatus DeleteSmbPath(SMB2FileStore store, string path, bool isDirectory)
    {
        object? handle = null;
        try
        {
            var options = isDirectory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE;
            var status = store.CreateFile(
                out handle,
                out _,
                path,
                AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                isDirectory ? SmbFileAttributes.Directory : SmbFileAttributes.Normal,
                ShareAccess.Delete,
                CreateDisposition.FILE_OPEN,
                options | CreateOptions.FILE_DELETE_ON_CLOSE,
                null!);
            return status;
        }
        finally
        {
            if (handle is not null)
            {
                store.CloseFile(handle);
            }
        }
    }

    public static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken);
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

    // Where an SSH login lands after connecting. Used to pre-fill the target folder when files are
    // sent into a live SSH session: guacd exposes that session's SFTP with "/" as its root, so the
    // client has to name an absolute path, and dropping a file into "/" fails for anyone but root.
    public static Task<string> GetSftpHomeDirectoryAsync(FileTarget server, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateSftpClient(server);
            client.Connect();
            var home = client.WorkingDirectory;
            return string.IsNullOrWhiteSpace(home) ? "/" : home;
        }, cancellationToken);
    }

    // --- WebDAV -----------------------------------------------------------------------------------
    //
    // Plain HTTP with the methods WebDAV adds: PROPFIND to list, MKCOL to create a folder, PUT, GET
    // and DELETE for the rest. No library for this - the four verbs and a little XML are less code
    // than wiring one up, and nothing here needs locking or versioning.
    //
    // Host may be a full URL (https://cloud.example/remote.php/dav) or just a name, in which case the
    // port decides the scheme. The configured root is prepended to every path, exactly like the other
    // providers treat FileRootPath.

    private static readonly HttpClient WebDavStrict = new(new HttpClientHandler());

    private static readonly HttpClient WebDavRelaxed = new(new HttpClientHandler
    {
        // Only used when the connection is explicitly marked as "ignore certificate".
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    });

    private static readonly XNamespace Dav = "DAV:";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Host, int Port, bool Relaxed), HttpClient> WebDavDialed = new();

    // WebDAV through a relay or tunnel: the URL (and the certificate's name) stay the server's, the TCP connection goes to the dial address.
    private static HttpClient WebDavThrough(FileTarget server) =>
        WebDavDialed.GetOrAdd((server.ConnectHost, server.ConnectPort, server.IgnoreCertificate), key =>
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(key.Host, key.Port, cancellationToken);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            if (key.Relaxed)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            return new HttpClient(handler);
        });

    // SMB on another port than 445 (a relay, a tunnel): SMBLibrary 1.5 has that only as an internal overload - used when it is there,
    // otherwise only 445 works.
    private static bool ConnectSmb(SMB2Client client, FileTarget server)
    {
        var port = server.ConnectPort;
        if (port == 445)
        {
            return client.Connect(server.ConnectHost, SMBTransportType.DirectTCPTransport);
        }

        var withPort = typeof(SMB2Client).GetMethod("Connect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            [typeof(System.Net.IPAddress), typeof(SMBTransportType), typeof(int)]);
        if (withPort is null)
        {
            throw new InvalidOperationException("SMB geht mit dieser Bibliothek nur ueber Port 445.");
        }

        var address = System.Net.IPAddress.TryParse(server.ConnectHost, out var parsed)
            ? parsed
            : System.Net.Dns.GetHostAddresses(server.ConnectHost).First();
        return (bool)withPort.Invoke(client, [address, SMBTransportType.DirectTCPTransport, port])!;
    }

    private static async Task<FileGatewayListResult> ListWebDavAsync(FileTarget server, string? path, CancellationToken cancellationToken)
    {
        var virtualPath = NormalizeVirtualPath(path);
        using var request = WebDavRequest(server, "PROPFIND", virtualPath, true);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(
            """<?xml version="1.0"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>""",
            Encoding.UTF8,
            "application/xml");

        using var response = await WebDavSend(server, request, cancellationToken);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var self = Uri.UnescapeDataString(WebDavAbsolutePath(server, virtualPath)).TrimEnd('/');

        var entries = new List<FileGatewayEntry>();
        foreach (var item in document.Descendants(Dav + "response"))
        {
            var href = (item.Element(Dav + "href")?.Value ?? "").Trim();
            if (href.Length == 0)
            {
                continue;
            }

            // Servers answer with either an absolute URL or just a path.
            var hrefPath = Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : href;
            var trimmed = Uri.UnescapeDataString(hrefPath).TrimEnd('/');

            // The listing includes the folder itself; only its contents are wanted. Compared for
            // equality, not with EndsWith: at the root the folder's own path is empty, and every entry
            // ends with an empty string - which silently emptied the whole listing.
            if (string.Equals(trimmed, self, StringComparison.Ordinal))
            {
                continue;
            }

            var properties = item.Descendants(Dav + "prop").FirstOrDefault();
            var isDirectory = properties?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
            var name = Uri.UnescapeDataString(trimmed.Split('/').LastOrDefault() ?? "");
            if (name.Length == 0)
            {
                continue;
            }

            entries.Add(new FileGatewayEntry(
                name,
                CombineVirtualPath(virtualPath, name),
                isDirectory,
                long.TryParse(properties?.Element(Dav + "getcontentlength")?.Value, out var size) ? size : null,
                DateTimeOffset.TryParse(properties?.Element(Dav + "getlastmodified")?.Value, out var modified) ? modified : null));
        }

        return new FileGatewayListResult(
            virtualPath,
            ParentVirtualPath(virtualPath),
            entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList());
    }

    private static async Task<FileGatewayFileInfo> GetWebDavFileInfoAsync(FileTarget server, string? path, CancellationToken cancellationToken)
    {
        var virtualPath = NormalizeVirtualPath(path);
        using var request = WebDavRequest(server, "HEAD", virtualPath, false);
        using var response = await WebDavSend(server, request, cancellationToken);

        return new FileGatewayFileInfo(
            FileNameFromPath(virtualPath),
            ContentTypeFromPath(virtualPath),
            response.Content.Headers.ContentLength ?? 0);
    }

    private static async Task CopyWebDavRangeAsync(
        FileTarget server,
        string? path,
        Stream output,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        using var request = WebDavRequest(server, "GET", NormalizeVirtualPath(path), false);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
        using var response = await WebDavSend(server, request, cancellationToken);
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        await content.CopyToAsync(output, cancellationToken);
    }

    private static async Task<FileGatewayDownload> DownloadWebDavAsync(FileTarget server, string? path, CancellationToken cancellationToken)
    {
        var virtualPath = NormalizeVirtualPath(path);
        using var request = WebDavRequest(server, "GET", virtualPath, false);
        var response = await WebDavSend(server, request, cancellationToken);

        return new FileGatewayDownload(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            FileNameFromPath(virtualPath),
            ContentTypeFromPath(virtualPath));
    }

    private static async Task UploadWebDavAsync(
        FileTarget server,
        string? path,
        Stream content,
        string fileName,
        CancellationToken cancellationToken)
    {
        var target = CombineVirtualPath(NormalizeVirtualPath(path), SafeFileName(fileName));
        using var request = WebDavRequest(server, "PUT", target, false);
        request.Content = new StreamContent(content);
        using var response = await WebDavSend(server, request, cancellationToken);
    }

    private static async Task CreateWebDavFileAsync(FileTarget server, string? path, string fileName, CancellationToken cancellationToken)
    {
        var target = CombineVirtualPath(NormalizeVirtualPath(path), SafeFileName(fileName));
        using var request = WebDavRequest(server, "PUT", target, false);
        request.Content = new ByteArrayContent([]);
        using var response = await WebDavSend(server, request, cancellationToken);
    }

    private static async Task CreateWebDavDirectoryAsync(FileTarget server, string? path, string directoryName, CancellationToken cancellationToken)
    {
        var target = CombineVirtualPath(NormalizeVirtualPath(path), SafeFileName(directoryName));
        using var request = WebDavRequest(server, "MKCOL", target, true);
        using var response = await WebDavSend(server, request, cancellationToken);
    }

    private static async Task DeleteWebDavAsync(FileTarget server, string? path, CancellationToken cancellationToken)
    {
        var virtualPath = NormalizeVirtualPath(path);
        try
        {
            using var request = WebDavRequest(server, "DELETE", virtualPath, false);
            using var response = await WebDavSend(server, request, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // A folder without its slash: many servers redirect (301), and the redirect loses the login - again as a folder.
            using var request = WebDavRequest(server, "DELETE", virtualPath, true);
            using var response = await WebDavSend(server, request, cancellationToken);
        }
    }

    private static HttpRequestMessage WebDavRequest(FileTarget server, string method, string virtualPath, bool directory)
    {
        var path = WebDavAbsolutePath(server, virtualPath);
        if (directory && !path.EndsWith('/'))
        {
            // Collections are addressed with a trailing slash; some servers answer 301 without it.
            path += "/";
        }

        var request = new HttpRequestMessage(new HttpMethod(method), WebDavBaseUri(server) + path);
        if (!string.IsNullOrWhiteSpace(server.UserName))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{server.UserName}:{server.Password}")));
        }

        return request;
    }

    private static async Task<HttpResponseMessage> WebDavSend(FileTarget server, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = server.DialHost.Length > 0 ? WebDavThrough(server) : server.IgnoreCertificate ? WebDavRelaxed : WebDavStrict;
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The status is what a WebDAV server tells us; passing it on beats a generic failure.
            throw new InvalidOperationException($"WebDAV: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return response;
    }

    private static string WebDavBaseUri(FileTarget server)
    {
        var host = (server.Host ?? "").Trim().TrimEnd('/');
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return host;
        }

        var scheme = server.Port == 443 ? "https" : "http";
        var port = server.Port is 80 or 443 or 0 ? "" : $":{server.Port}";
        return $"{scheme}://{host}{port}";
    }

    private static string WebDavAbsolutePath(FileTarget server, string virtualPath)
    {
        var root = (server.FileRootPath ?? "").Replace('\\', '/').Trim('/');
        var relative = NormalizeVirtualPath(virtualPath).Trim('/');
        var parts = new[] { root, relative }.Where(part => part.Length > 0);
        var joined = string.Join('/', parts);

        return "/" + string.Join('/', joined
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
    }

    public static string SafeFileName(string name)
    {
        var cleaned = Path.GetFileName((name ?? "").Trim().Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
        {
            throw new InvalidOperationException("Ungueltiger Name.");
        }

        return cleaned;
    }

    private static SftpClient CreateSftpClient(FileTarget server)
    {
        if (string.IsNullOrWhiteSpace(server.UserName))
        {
            throw new InvalidOperationException("SFTP benoetigt einen Ziel-Benutzer.");
        }

        return new SftpClient(SshLogin.For(server.ConnectHost, server.ConnectPort, server.UserName, server.Password, server.PrivateKey));
    }

    private static AsyncFtpClient CreateFtpClient(FileTarget server)
    {
        return new AsyncFtpClient(server.ConnectHost, server.UserName, server.Password, server.ConnectPort);
    }

    private static void CheckSmbStatus(NTStatus status, string operation)
    {
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new InvalidOperationException($"{operation} fehlgeschlagen: {status}");
        }
    }

    public static string ToRemotePosixPath(string? configuredRoot, string virtualPath)
    {
        var root = NormalizeRemoteRoot(configuredRoot);
        if (virtualPath == "/")
        {
            return root;
        }

        return root == "/"
            ? virtualPath
            : $"{root}/{virtualPath.TrimStart('/')}";
    }

    public static string NormalizeRemoteRoot(string? path)
    {
        var root = (path ?? "").Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(root))
        {
            return "/";
        }

        if (!root.StartsWith('/'))
        {
            root = "/" + root;
        }

        var trimmed = root.TrimEnd('/');
        return string.IsNullOrEmpty(trimmed) ? "/" : trimmed;
    }

    public static string NormalizeVirtualPath(string? path)
    {
        var value = (path ?? "/").Replace('\\', '/');
        var parts = new List<string>();
        foreach (var rawPart in value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawPart == ".")
            {
                continue;
            }

            if (rawPart == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(rawPart);
        }

        return parts.Count == 0 ? "/" : "/" + string.Join('/', parts);
    }

    public static string ParentVirtualPath(string path)
    {
        path = NormalizeVirtualPath(path);
        if (path == "/")
        {
            return "/";
        }

        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    public static string CombineVirtualPath(string basePath, string name)
    {
        var cleanedName = CleanLeafName(name);
        var normalizedBase = NormalizeVirtualPath(basePath);
        return normalizedBase == "/" ? "/" + cleanedName : normalizedBase + "/" + cleanedName;
    }

    public static string CleanLeafName(string? name)
    {
        var cleaned = (name ?? "").Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
        {
            throw new InvalidOperationException("Ungueltiger Datei- oder Ordnername.");
        }

        return cleaned;
    }

    public static string FileNameFromPath(string path)
    {
        return NormalizeVirtualPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "download.bin";
    }

    public static string ContentTypeFromPath(string path)
    {
        return Path.GetExtension(FileNameFromPath(path)).ToLowerInvariant() switch
        {
            ".txt" or ".log" or ".ini" or ".cfg" => "text/plain; charset=utf-8",
            ".md" => "text/markdown; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            ".m4v" => "video/x-m4v",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".mpg" or ".mpeg" => "video/mpeg",
            ".webm" => "video/webm",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".ogg" or ".oga" => "audio/ogg",
            ".opus" => "audio/ogg",
            ".wav" => "audio/wav",
            _ => "application/octet-stream"
        };
    }

    public static void EnsureNotRootPath(string path)
    {
        if (NormalizeVirtualPath(path) == "/")
        {
            throw new InvalidOperationException("Der Root-Pfad kann nicht fuer diese Aktion verwendet werden.");
        }
    }

    private static SmbResolvedPath ResolveSmbPath(FileTarget server, string virtualPath)
    {
        var root = SmbConfiguredRoot.Parse(server);
        var virtualParts = NormalizeVirtualPath(virtualPath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (string.IsNullOrWhiteSpace(root.ShareName))
        {
            var share = virtualParts.FirstOrDefault() ?? "";
            var sharePath = string.Join('\\', virtualParts.Skip(1));
            return new SmbResolvedPath(share, sharePath);
        }

        var pathParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(root.BasePath))
        {
            pathParts.AddRange(root.BasePath.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        pathParts.AddRange(virtualParts);
        return new SmbResolvedPath(root.ShareName, string.Join('\\', pathParts));
    }

    private sealed record SmbResolvedPath(string ShareName, string Path);

    private sealed record SmbConfiguredRoot(string ShareName, string BasePath)
    {
        public static SmbConfiguredRoot Parse(FileTarget server)
        {
            var raw = (server.FileRootPath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new SmbConfiguredRoot("", "");
            }

            var normalized = raw.Replace('\\', '/');
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if ((normalized.StartsWith("//", StringComparison.Ordinal) || normalized.StartsWith("/", StringComparison.Ordinal))
                && parts.Count > 1
                && string.Equals(parts[0], server.Host, StringComparison.OrdinalIgnoreCase))
            {
                parts.RemoveAt(0);
            }

            var share = parts.FirstOrDefault() ?? "";
            var basePath = string.Join('\\', parts.Skip(1));
            return new SmbConfiguredRoot(share, basePath);
        }
    }

    private sealed class SmbSession : IDisposable
    {
        private SmbSession(SMB2Client client)
        {
            Client = client;
        }

        public SMB2Client Client { get; }

        public static SmbSession Connect(FileTarget server)
        {
            var client = new SMB2Client();
            if (!ConnectSmb(client, server))
            {
                throw new InvalidOperationException("SMB-Verbindung konnte nicht aufgebaut werden.");
            }

            var loginStatus = client.Login(server.Domain, server.UserName, server.Password);
            if (loginStatus != NTStatus.STATUS_SUCCESS)
            {
                client.Disconnect();
                throw new InvalidOperationException($"SMB-Anmeldung fehlgeschlagen: {loginStatus}");
            }

            return new SmbSession(client);
        }

        public SmbTree TreeConnect(string shareName)
        {
            if (string.IsNullOrWhiteSpace(shareName))
            {
                throw new InvalidOperationException("Bitte einen SMB-Share angeben oder auswaehlen.");
            }

            var fileStore = Client.TreeConnect(shareName, out var status);
            CheckSmbStatus(status, "SMB-Share verbinden");
            if (fileStore is not SMB2FileStore store)
            {
                fileStore.Disconnect();
                throw new InvalidOperationException("SMB-Dateizugriff wird fuer diesen Server nicht unterstuetzt.");
            }

            return new SmbTree(store);
        }

        public void Dispose()
        {
            try
            {
                Client.Logoff();
            }
            finally
            {
                Client.Disconnect();
            }
        }
    }

    private sealed class SmbTree : IDisposable
    {
        public SmbTree(SMB2FileStore store)
        {
            Store = store;
        }

        public SMB2FileStore Store { get; }

        public void Dispose()
        {
            Store.Disconnect();
        }
    }
}
