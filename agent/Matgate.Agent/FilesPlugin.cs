using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Matgate.Agent;

// The file explorer: the configured folders as one tree, "/<root name>/...". Every path that comes in is
// a path in that tree and is resolved here - nothing from the gateway is ever used as a real path.
public sealed class FilesPlugin
{
    private static readonly char[] ForbiddenNameChars = ['/', (char)92, ':', '*', '?', '"', '<', '>', '|'];
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly List<(string Name, string FullPath)> _roots;
    private readonly ConcurrentDictionary<string, Upload> _uploads = new();

    public FilesPlugin(IEnumerable<AgentRoot> roots)
    {
        _roots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root.Name) && !string.IsNullOrWhiteSpace(root.Path))
            .Select(root => (root.Name.Trim(), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path))))
            .Where(root => Directory.Exists(root.Item2) && root.Item1.IndexOfAny(ForbiddenNameChars) < 0)
            .ToList();
    }

    public bool IsAvailable => _roots.Count > 0;

    public Task<AgentFrame> HandleAsync(AgentFrame request, CancellationToken cancellationToken)
    {
        return request.Op switch
        {
            "files.list" => Task.FromResult(List(request)),
            "files.stat" => Task.FromResult(Stat(request)),
            "files.read" => ReadAsync(request, cancellationToken),
            "files.write.open" => Task.FromResult(WriteOpen(request)),
            "files.write" => WriteAsync(request, cancellationToken),
            "files.write.close" => WriteCloseAsync(request),
            "files.write.abort" => Task.FromResult(WriteAbort(request)),
            "files.mkdir" => Task.FromResult(MakeDirectory(request)),
            "files.create" => Task.FromResult(CreateFile(request)),
            "files.delete" => Task.FromResult(Delete(request)),
            _ => throw new InvalidOperationException("Unknown file operation."),
        };
    }

    // The machine's real folder for a path in the tree - for a terminal that should start "here".
    public string? DirectoryFor(string? virtualPath)
    {
        try
        {
            var resolved = Resolve(virtualPath);
            return resolved is { } path && Directory.Exists(path) ? path : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public string? FirstRoot => _roots.Count > 0 ? _roots[0].FullPath : null;

    private AgentFrame List(AgentFrame request)
    {
        var path = request.Text("path");
        var entries = new JsonArray();
        var full = Resolve(path);
        if (full is null)
        {
            foreach (var root in _roots)
            {
                var info = new DirectoryInfo(root.FullPath);
                entries.Add(Entry(root.Name, true, null, info.LastWriteTimeUtc));
            }
        }
        else
        {
            var directory = new DirectoryInfo(full);
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException("This folder does not exist.");
            }

            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                // A link could lead out of the shared folder - links are not offered.
                if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.Hidden) != 0 && entry.Name.StartsWith(".matgate-upload-", StringComparison.Ordinal))
                {
                    continue;
                }

                entries.Add(Entry(entry.Name, entry is DirectoryInfo, entry is FileInfo file ? file.Length : null, entry.LastWriteTimeUtc));
            }
        }

        return Result(request, new JsonObject { ["entries"] = entries });
    }

    private AgentFrame Stat(AgentFrame request)
    {
        var full = Resolve(request.Text("path"));
        if (full is null)
        {
            return Result(request, Entry("/", true, null, DateTime.UtcNow));
        }

        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        if (!info.Exists)
        {
            throw new FileNotFoundException("This path does not exist.");
        }

        return Result(request, Entry(info.Name, info is DirectoryInfo, info is FileInfo file ? file.Length : null, info.LastWriteTimeUtc));
    }

    private async Task<AgentFrame> ReadAsync(AgentFrame request, CancellationToken cancellationToken)
    {
        var full = Resolve(request.Text("path")) ?? throw new InvalidOperationException("This is a folder.");
        var offset = request.Long("offset");
        var length = (int)Math.Clamp(request.Long("length", AgentWire.ChunkSize), 0, AgentWire.ChunkSize);

        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, useAsync: true);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var total = 0;
        while (total < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, length - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var response = Result(request, new JsonObject { ["read"] = total });
        response.Payload = buffer.AsMemory(0, total);
        return response;
    }

    private AgentFrame WriteOpen(AgentFrame request)
    {
        var directory = Resolve(request.Text("path")) ?? throw new InvalidOperationException("Files cannot be placed next to the shared folders.");
        var name = SafeName(request.Text("name"));
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("This folder does not exist.");
        }

        var handle = Guid.NewGuid().ToString("N");
        // Written beside the target and renamed at the end: a half-transferred file never carries the
        // real name, and an existing file stays intact until the new one is complete.
        var temporary = Path.Combine(directory, ".matgate-upload-" + handle);
        var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true);
        File.SetAttributes(temporary, File.GetAttributes(temporary) | FileAttributes.Hidden);
        _uploads[handle] = new Upload(stream, temporary, Path.Combine(directory, name));
        return Result(request, new JsonObject { ["handle"] = handle });
    }

    private async Task<AgentFrame> WriteAsync(AgentFrame request, CancellationToken cancellationToken)
    {
        var upload = UploadFor(request);
        await upload.Stream.WriteAsync(request.Payload, cancellationToken);
        return Result(request);
    }

    private async Task<AgentFrame> WriteCloseAsync(AgentFrame request)
    {
        var upload = UploadFor(request);
        _uploads.TryRemove(request.Text("handle") ?? "", out _);
        await upload.Stream.DisposeAsync();
        File.SetAttributes(upload.Temporary, File.GetAttributes(upload.Temporary) & ~FileAttributes.Hidden);
        File.Move(upload.Temporary, upload.Target, overwrite: true);
        return Result(request);
    }

    private AgentFrame WriteAbort(AgentFrame request)
    {
        if (_uploads.TryRemove(request.Text("handle") ?? "", out var upload))
        {
            upload.Stream.Dispose();
            File.Delete(upload.Temporary);
        }

        return Result(request);
    }

    // Uploads the gateway gave up on without saying so (it went away mid-transfer).
    public void DropAllUploads()
    {
        foreach (var handle in _uploads.Keys.ToList())
        {
            if (_uploads.TryRemove(handle, out var upload))
            {
                try
                {
                    upload.Stream.Dispose();
                    File.Delete(upload.Temporary);
                }
                catch (IOException)
                {
                    // Left for the next start; the name says what it is.
                }
            }
        }
    }

    private AgentFrame MakeDirectory(AgentFrame request)
    {
        var parent = Resolve(request.Text("path")) ?? throw new InvalidOperationException("Folders cannot be created next to the shared folders.");
        Directory.CreateDirectory(Path.Combine(parent, SafeName(request.Text("name"))));
        return Result(request);
    }

    private AgentFrame CreateFile(AgentFrame request)
    {
        var parent = Resolve(request.Text("path")) ?? throw new InvalidOperationException("Files cannot be created next to the shared folders.");
        var target = Path.Combine(parent, SafeName(request.Text("name")));
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new InvalidOperationException("There is already an entry with this name here.");
        }

        File.WriteAllBytes(target, []);
        return Result(request);
    }

    private AgentFrame Delete(AgentFrame request)
    {
        var target = Resolve(request.Text("path")) ?? throw new InvalidOperationException("The shared folders themselves cannot be deleted.");
        if (_roots.Any(root => string.Equals(root.FullPath, target, PathComparison)))
        {
            throw new InvalidOperationException("The shared folders themselves cannot be deleted.");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
        else if (File.Exists(target))
        {
            File.Delete(target);
        }
        else
        {
            throw new FileNotFoundException("This path does not exist.");
        }

        return Result(request);
    }

    private Upload UploadFor(AgentFrame request)
    {
        return _uploads.TryGetValue(request.Text("handle") ?? "", out var upload)
            ? upload
            : throw new InvalidOperationException("This upload is no longer open.");
    }

    // Null for "/", the list of shared folders itself.
    private string? Resolve(string? virtualPath)
    {
        var segments = (virtualPath ?? "/")
            .Replace((char)92, '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOfAny(ForbiddenNameChars) >= 0 || segment.Any(char.IsControl))
            {
                throw new InvalidOperationException("This path is not allowed.");
            }
        }

        var root = _roots.FirstOrDefault(candidate => string.Equals(candidate.Name, segments[0], StringComparison.OrdinalIgnoreCase));
        if (root.FullPath is null)
        {
            throw new DirectoryNotFoundException("This shared folder does not exist.");
        }

        var current = root.FullPath;
        foreach (var segment in segments.Skip(1))
        {
            current = Path.Combine(current, segment);
            // A link anywhere on the way could lead out of the shared folder.
            var info = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                throw new InvalidOperationException("This path is not allowed.");
            }
        }

        var full = Path.GetFullPath(current);
        if (!string.Equals(full, root.FullPath, PathComparison)
            && !full.StartsWith(root.FullPath + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidOperationException("This path is not allowed.");
        }

        return full;
    }

    private static string SafeName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0 || trimmed is "." or ".." || trimmed.IndexOfAny(ForbiddenNameChars) >= 0 || trimmed.Any(char.IsControl))
        {
            throw new InvalidOperationException("This name is not allowed.");
        }

        return trimmed;
    }

    private static JsonObject Entry(string name, bool isDirectory, long? size, DateTime modifiedUtc)
    {
        var entry = new JsonObject
        {
            ["name"] = name,
            ["dir"] = isDirectory,
            ["mtime"] = new DateTimeOffset(DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        };
        if (size is not null)
        {
            entry["size"] = size.Value;
        }

        return entry;
    }

    private static AgentFrame Result(AgentFrame request, JsonObject? data = null)
    {
        return new AgentFrame { Id = request.Id, Kind = "res", Op = request.Op, Data = data };
    }

    private sealed record Upload(FileStream Stream, string Temporary, string Target);
}
