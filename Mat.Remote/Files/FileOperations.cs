using System.IO.Compression;
using Matgate.Models;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Matgate.Files;

/// <summary>One place of files (a file connection, or anything that lists, reads and writes like one).</summary>
public interface IFileStore
{
    Task<FileGatewayListResult> ListAsync(string? path, CancellationToken cancellationToken);

    Task<FileGatewayDownload> DownloadAsync(string path, CancellationToken cancellationToken);

    Task UploadAsync(string folder, Stream content, string fileName, CancellationToken cancellationToken);

    Task CreateDirectoryAsync(string folder, string name, CancellationToken cancellationToken);

    Task DeleteAsync(string path, CancellationToken cancellationToken);
}

/// <summary>A file connection of <see cref="RemoteFiles"/> as an <see cref="IFileStore"/>.</summary>
public sealed class RemoteFileStore(FileTarget target) : IFileStore
{
    public FileTarget Target { get; } = target;

    public Task<FileGatewayListResult> ListAsync(string? path, CancellationToken cancellationToken) => RemoteFiles.ListAsync(Target, path, cancellationToken);

    public Task<FileGatewayDownload> DownloadAsync(string path, CancellationToken cancellationToken) => RemoteFiles.DownloadAsync(Target, path, cancellationToken);

    public Task UploadAsync(string folder, Stream content, string fileName, CancellationToken cancellationToken) =>
        RemoteFiles.UploadAsync(Target, folder, content, fileName, cancellationToken);

    public Task CreateDirectoryAsync(string folder, string name, CancellationToken cancellationToken) =>
        RemoteFiles.CreateDirectoryAsync(Target, folder, name, cancellationToken);

    public Task DeleteAsync(string path, CancellationToken cancellationToken) => RemoteFiles.DeleteAsync(Target, path, cancellationToken);
}

/// <summary>What a copy or move did: how many files, and what failed (name: reason).</summary>
public sealed record FileCopyResult(int Copied, IReadOnlyList<string> Failed);

/// <summary>
/// What the file manager does with a selection - the same for every kind of file connection, as Matgate's file manager does it: copy
/// (also between two connections; a name that is taken gets " (2)"), move and rename (copy, then delete: one way for every protocol),
/// delete, pack into a ZIP (to a stream or into the folder), unpack an archive (zip, rar, 7z, tar, gz, …) into a folder.
/// </summary>
public static class FileOperations
{
    /// <summary>At most this many files and folders in one copy.</summary>
    public const int MaxEntries = 20000;

    private const int MaxDepth = 32;

    public static async Task<FileCopyResult> CopyAsync(
        IFileStore source,
        IReadOnlyList<string> paths,
        IFileStore target,
        string targetFolder,
        bool sameStore,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        targetFolder = RemoteFiles.NormalizeVirtualPath(targetFolder);
        var copied = 0;
        var budget = MaxEntries;
        var failed = new List<string>();

        async Task<string> FreeNameAsync(string folder, string name)
        {
            var taken = (await target.ListAsync(folder, cancellationToken)).Entries.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(name))
            {
                return name;
            }

            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            for (var n = 2; ; n++)
            {
                var candidate = $"{stem} ({n}){extension}";
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        async Task CopyOneAsync(FileGatewayEntry entry, string intoFolder, int depth)
        {
            if (--budget < 0 || depth > MaxDepth)
            {
                throw new InvalidOperationException("Zu viele Dateien auf einmal.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var name = await FreeNameAsync(intoFolder, entry.Name);
            progress?.Report(entry.Path);
            if (entry.IsDirectory)
            {
                await target.CreateDirectoryAsync(intoFolder, name, cancellationToken);
                foreach (var child in (await source.ListAsync(entry.Path, cancellationToken)).Entries)
                {
                    await CopyOneAsync(child, RemoteFiles.CombineVirtualPath(intoFolder, name), depth + 1);
                }

                return;
            }

            var download = await source.DownloadAsync(entry.Path, cancellationToken);
            await using (download.Content)
            {
                await target.UploadAsync(intoFolder, download.Content, name, cancellationToken);
            }

            copied++;
        }

        foreach (var path in paths.Select(RemoteFiles.NormalizeVirtualPath))
        {
            try
            {
                var entry = await FindAsync(source, path, cancellationToken);
                if (entry.IsDirectory && sameStore && (targetFolder.TrimEnd('/') + "/").StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Ein Ordner kann nicht in sich selbst kopiert werden.");
                }

                await CopyOneAsync(entry, targetFolder, 0);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failed.Add($"{RemoteFiles.FileNameFromPath(path)}: {exception.Message}");
            }
        }

        return new FileCopyResult(copied, failed);
    }

    /// <summary>Moves within one connection (or to another): copy, then delete what was copied without a problem.</summary>
    public static async Task<FileCopyResult> MoveAsync(
        IFileStore source,
        IReadOnlyList<string> paths,
        IFileStore target,
        string targetFolder,
        bool sameStore,
        CancellationToken cancellationToken = default)
    {
        var copied = 0;
        var failed = new List<string>();
        foreach (var path in paths.Select(RemoteFiles.NormalizeVirtualPath))
        {
            if (sameStore && RemoteFiles.ParentVirtualPath(path) == RemoteFiles.NormalizeVirtualPath(targetFolder))
            {
                continue; // already there
            }

            var result = await CopyAsync(source, [path], target, targetFolder, sameStore, cancellationToken: cancellationToken);
            copied += result.Copied;
            if (result.Failed.Count > 0)
            {
                failed.AddRange(result.Failed);
                continue;
            }

            await source.DeleteAsync(path, cancellationToken);
        }

        return new FileCopyResult(copied, failed);
    }

    /// <summary>A new name in the same folder (copy under the new name, then delete the old).</summary>
    public static async Task RenameAsync(IFileStore store, string path, string newName, CancellationToken cancellationToken = default)
    {
        path = RemoteFiles.NormalizeVirtualPath(path);
        RemoteFiles.EnsureNotRootPath(path);
        var name = RemoteFiles.SafeFileName(newName);
        var folder = RemoteFiles.ParentVirtualPath(path);
        if (RemoteFiles.FileNameFromPath(path) == name)
        {
            return;
        }

        var siblings = (await store.ListAsync(folder, cancellationToken)).Entries;
        if (siblings.Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(entry.Path, path, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"„{name}“ gibt es hier schon.");
        }

        var entry = siblings.FirstOrDefault(candidate => candidate.Path == path) ?? throw new InvalidOperationException("Diese Datei gibt es nicht mehr.");
        if (entry.IsDirectory)
        {
            await store.CreateDirectoryAsync(folder, name, cancellationToken);
            var into = RemoteFiles.CombineVirtualPath(folder, name);
            var result = await CopyAsync(store, [.. (await store.ListAsync(path, cancellationToken)).Entries.Select(child => child.Path)], store, into, sameStore: true, cancellationToken: cancellationToken);
            if (result.Failed.Count > 0)
            {
                throw new InvalidOperationException(string.Join("; ", result.Failed));
            }
        }
        else
        {
            var download = await store.DownloadAsync(path, cancellationToken);
            await using (download.Content)
            {
                await store.UploadAsync(folder, download.Content, name, cancellationToken);
            }
        }

        await store.DeleteAsync(path, cancellationToken);
    }

    /// <summary>The selection (files and folders, recursively) as a ZIP into <paramref name="output"/>.</summary>
    public static async Task ZipAsync(IFileStore store, IReadOnlyList<string> paths, Stream output, CancellationToken cancellationToken = default)
    {
        var budget = MaxEntries;
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        async Task AddAsync(FileGatewayEntry entry, string prefix, int depth)
        {
            if (--budget < 0 || depth > MaxDepth)
            {
                throw new InvalidOperationException("Zu viele Dateien auf einmal.");
            }

            var name = prefix + entry.Name;
            if (entry.IsDirectory)
            {
                archive.CreateEntry(name + "/");
                foreach (var child in (await store.ListAsync(entry.Path, cancellationToken)).Entries)
                {
                    await AddAsync(child, name + "/", depth + 1);
                }

                return;
            }

            var zipEntry = archive.CreateEntry(name, CompressionLevel.Optimal);
            if (entry.ModifiedAt is { } modified)
            {
                zipEntry.LastWriteTime = modified;
            }

            var download = await store.DownloadAsync(entry.Path, cancellationToken);
            await using (download.Content)
            await using (var target = zipEntry.Open())
            {
                await download.Content.CopyToAsync(target, cancellationToken);
            }
        }

        foreach (var path in paths.Select(RemoteFiles.NormalizeVirtualPath))
        {
            await AddAsync(await FindAsync(store, path, cancellationToken), "", 0);
        }
    }

    /// <summary>Packs the selection into a ZIP stored in <paramref name="folder"/> (the name gets " (2)" when it is taken).</summary>
    public static async Task<string> ZipIntoAsync(IFileStore store, IReadOnlyList<string> paths, string folder, string archiveName, CancellationToken cancellationToken = default)
    {
        var name = RemoteFiles.SafeFileName(archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? archiveName : archiveName + ".zip");
        var temp = Path.Combine(Path.GetTempPath(), "matgate-zip-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var file = File.Create(temp))
            {
                await ZipAsync(store, paths, file, cancellationToken);
            }

            var taken = (await store.ListAsync(folder, cancellationToken)).Entries.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stem = Path.GetFileNameWithoutExtension(name);
            for (var n = 2; taken.Contains(name); n++)
            {
                name = $"{stem} ({n}).zip";
            }

            await using var read = File.OpenRead(temp);
            await store.UploadAsync(folder, read, name, cancellationToken);
            return name;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Whether a file name is an archive <see cref="ExtractAsync"/> can unpack.</summary>
    public static bool IsArchive(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tar.bz2", StringComparison.Ordinal) || lower.EndsWith(".tar.xz", StringComparison.Ordinal)
               || Path.GetExtension(lower) is ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".tbz2" or ".xz" or ".lz" or ".lzip";
    }

    /// <summary>Unpacks an archive of the store into a folder of the store (with its folders; nothing leaves that folder).</summary>
    public static async Task<int> ExtractAsync(IFileStore store, string archivePath, string destinationFolder, CancellationToken cancellationToken = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), "matgate-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var download = await store.DownloadAsync(archivePath, cancellationToken);
            await using (download.Content)
            await using (var reader = await ReaderFactory.OpenAsyncReader(download.Content, ReaderOptions.ForExternalStream, cancellationToken))
            {
                await reader.WriteAllToDirectoryAsync(temp + Path.DirectorySeparatorChar, new ExtractionOptions { ExtractFullPath = true, Overwrite = true }, cancellationToken);
            }

            return await UploadTreeAsync(store, temp, destinationFolder, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    /// <summary>A local folder's content (folders and files) into a folder of the store: unpacking, uploading a folder.</summary>
    public static async Task<int> UploadTreeAsync(IFileStore store, string localFolder, string destinationFolder, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(localFolder);
        var destination = RemoteFiles.NormalizeVirtualPath(destinationFolder);
        var known = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        async Task EnsureFolderAsync(string folder, string name)
        {
            if (!known.TryGetValue(folder, out var names))
            {
                names = (await store.ListAsync(folder, cancellationToken)).Entries.Where(entry => entry.IsDirectory).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
                known[folder] = names;
            }

            if (names.Add(name))
            {
                await store.CreateDirectoryAsync(folder, name, cancellationToken);
            }
        }

        string RemoteFolderOf(string relative)
        {
            var folder = destination;
            foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                folder = RemoteFiles.CombineVirtualPath(folder, RemoteFiles.SafeFileName(part));
            }

            return folder;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, directory);
            await EnsureFolderAsync(RemoteFolderOf(Path.GetDirectoryName(relative) ?? ""), RemoteFiles.SafeFileName(Path.GetFileName(relative)));
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (++count > MaxEntries)
            {
                throw new InvalidOperationException("Zu viele Dateien auf einmal.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file);
            await using var content = File.OpenRead(file);
            await store.UploadAsync(RemoteFolderOf(Path.GetDirectoryName(relative) ?? ""), content, RemoteFiles.SafeFileName(Path.GetFileName(relative)), cancellationToken);
        }

        return count;
    }

    /// <summary>The entry of a path (from its folder's listing); a path that is not listed counts as a file.</summary>
    public static async Task<FileGatewayEntry> FindAsync(IFileStore store, string path, CancellationToken cancellationToken)
    {
        path = RemoteFiles.NormalizeVirtualPath(path);
        var entries = (await store.ListAsync(RemoteFiles.ParentVirtualPath(path), cancellationToken)).Entries;
        return entries.FirstOrDefault(entry => string.Equals(entry.Path.TrimEnd('/'), path.TrimEnd('/'), StringComparison.Ordinal))
               ?? new FileGatewayEntry(RemoteFiles.FileNameFromPath(path), path, false, null, null);
    }
}
