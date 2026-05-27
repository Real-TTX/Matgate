using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.StaticFiles;
using Matgate.Models;

namespace Matgate.Services;

public sealed class WorkspaceService
{
    private const string SessionCookieName = "Matgate.Workspace.Session";
    private const string AccessCookiePrefix = "Matgate.Workspace.Access.";
    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly JsonDataStore _store;
    private readonly PasswordHasher _hasher;
    private readonly IDataProtector _protector;
    private readonly ILogger<WorkspaceService> _logger;
    private readonly FileExtensionContentTypeProvider _contentTypeProvider = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, WorkspacePresence>> _presences = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _activityLocks = new();

    public WorkspaceService(
        JsonDataStore store,
        PasswordHasher hasher,
        IDataProtectionProvider protectionProvider,
        ILogger<WorkspaceService> logger)
    {
        _store = store;
        _hasher = hasher;
        _logger = logger;
        _protector = protectionProvider.CreateProtector("Matgate.Workspace.Access");
    }

    public string WorkspaceSessionCookieName => SessionCookieName;

    public string GetAccessCookieName(Guid workspaceId) => $"{AccessCookiePrefix}{workspaceId:N}";

    public string GetWorkspaceRoot(WorkspaceDefinition workspace)
    {
        var root = string.IsNullOrWhiteSpace(workspace.RootPath)
            ? Path.Combine(_store.WorkspaceRootDirectory, $"{workspace.Id:N}-{SanitizeSegment(workspace.Name)}")
            : workspace.RootPath.Trim();

        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "files"));
        return root;
    }

    public string GetWorkspaceFilesRoot(WorkspaceDefinition workspace)
    {
        return Path.Combine(GetWorkspaceRoot(workspace), "files");
    }

    public string GetSharedNotePath(WorkspaceDefinition workspace)
    {
        return Path.Combine(GetWorkspaceRoot(workspace), NormalizeFileName(workspace.SharedNoteFileName, "shared-note.md"));
    }

    public bool HasAccessPassword(WorkspaceDefinition workspace)
    {
        return !string.IsNullOrWhiteSpace(workspace.AccessPasswordHash);
    }

    public bool VerifyAccessPassword(WorkspaceDefinition workspace, string password)
    {
        return _hasher.Verify(password, workspace.AccessPasswordHash);
    }

    public void SetAccessPassword(WorkspaceDefinition workspace, string password)
    {
        workspace.AccessPasswordHash = string.IsNullOrWhiteSpace(password) ? "" : _hasher.Hash(password);
    }

    public void ClearAccessPassword(WorkspaceDefinition workspace)
    {
        workspace.AccessPasswordHash = "";
    }

    public bool HasPublicAccessExpired(WorkspaceDefinition workspace, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        return workspace.PublicAccessExpiresAt is { } expiresAt && expiresAt <= current;
    }

    public bool IsPublicAccessActive(WorkspaceDefinition workspace, DateTimeOffset? now = null)
    {
        return !HasPublicAccessExpired(workspace, now);
    }

    public TimeSpan? GetPublicAccessRemaining(WorkspaceDefinition workspace, DateTimeOffset? now = null)
    {
        if (workspace.PublicAccessExpiresAt is not { } expiresAt)
        {
            return null;
        }

        var remaining = expiresAt - (now ?? DateTimeOffset.UtcNow);
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    public void SetPublicAccessDuration(WorkspaceDefinition workspace, TimeSpan duration, DateTimeOffset? now = null)
    {
        workspace.PublicAccessExpiresAt = (now ?? DateTimeOffset.UtcNow).Add(duration);
    }

    public void ExtendPublicAccess(WorkspaceDefinition workspace, TimeSpan duration, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var baseline = workspace.PublicAccessExpiresAt is { } expiresAt && expiresAt > current
            ? expiresAt
            : current;
        workspace.PublicAccessExpiresAt = baseline.Add(duration);
    }

    public string EnsureSessionId(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId)
            && Guid.TryParse(sessionId, out _))
        {
            return sessionId;
        }

        sessionId = Guid.NewGuid().ToString("N");
        context.Response.Cookies.Append(
            SessionCookieName,
            sessionId,
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps
            });
        return sessionId;
    }

    public bool HasValidAccessGrant(HttpContext context, WorkspaceDefinition workspace)
    {
        var cookieName = GetAccessCookieName(workspace.Id);
        if (!context.Request.Cookies.TryGetValue(cookieName, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<WorkspaceAccessGrant>(_protector.Unprotect(raw), JsonOptions);
            return payload is not null
                && payload.WorkspaceId == workspace.Id
                && payload.ExpiresAt > DateTimeOffset.UtcNow;
        }
        catch
        {
            return false;
        }
    }

    public void IssueAccessGrant(HttpContext context, WorkspaceDefinition workspace)
    {
        var payload = JsonSerializer.Serialize(
            new WorkspaceAccessGrant(workspace.Id, DateTimeOffset.UtcNow.AddDays(30)),
            JsonOptions);
        var protectedValue = _protector.Protect(payload);
        context.Response.Cookies.Append(
            GetAccessCookieName(workspace.Id),
            protectedValue,
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps
            });
    }

    public async Task<FileGatewayListResult> ListFilesAsync(
        WorkspaceDefinition workspace,
        string? path,
        CancellationToken cancellationToken = default)
    {
        var virtualPath = NormalizeVirtualPath(path);
        var directoryPath = ResolveWorkspacePath(workspace, virtualPath);
        if (!Directory.Exists(directoryPath))
        {
            return new FileGatewayListResult(virtualPath, ParentVirtualPath(virtualPath), []);
        }

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = Directory.EnumerateFileSystemEntries(directoryPath)
                .Select(entryPath =>
                {
                    var isDirectory = Directory.Exists(entryPath);
                    var name = Path.GetFileName(entryPath);
                    var modified = File.GetLastWriteTimeUtc(entryPath);
                    return new FileGatewayEntry(
                        name,
                        CombineVirtualPath(virtualPath, name),
                        isDirectory,
                        isDirectory ? null : new FileInfo(entryPath).Length,
                        modified == default ? null : new DateTimeOffset(modified, TimeSpan.Zero));
                })
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return new FileGatewayListResult(virtualPath, ParentVirtualPath(virtualPath), entries);
        }, cancellationToken);
    }

    public async Task<FileGatewayDownload> DownloadAsync(
        WorkspaceDefinition workspace,
        string? path,
        CancellationToken cancellationToken = default)
    {
        var virtualPath = NormalizeVirtualPath(path);
        var filePath = ResolveWorkspacePath(workspace, virtualPath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Die Datei wurde nicht gefunden.");
        }

        var content = new MemoryStream();
        await using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await stream.CopyToAsync(content, cancellationToken);
        }
        content.Position = 0;
        return new FileGatewayDownload(content, Path.GetFileName(filePath), ContentTypeFromPath(filePath));
    }

    public async Task<FileGatewayFileInfo> GetFileInfoAsync(
        WorkspaceDefinition workspace,
        string? path,
        CancellationToken cancellationToken = default)
    {
        var virtualPath = NormalizeVirtualPath(path);
        var filePath = ResolveWorkspacePath(workspace, virtualPath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Die Datei wurde nicht gefunden.");
        }

        var info = new FileInfo(filePath);
        return new FileGatewayFileInfo(
            info.Name,
            ContentTypeFromPath(filePath),
            info.Length);
    }

    public async Task UploadAsync(
        WorkspaceDefinition workspace,
        string? path,
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var directory = ResolveWorkspacePath(workspace, NormalizeVirtualPath(path));
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, CleanFileName(fileName));

        await using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    public async Task CreateFileAsync(
        WorkspaceDefinition workspace,
        string? path,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var directory = ResolveWorkspacePath(workspace, NormalizeVirtualPath(path));
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, CleanFileName(fileName));
        if (File.Exists(targetPath))
        {
            throw new InvalidOperationException("Die Datei existiert bereits.");
        }

        await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true);
        await output.FlushAsync(cancellationToken);
    }

    public Task CreateDirectoryAsync(
        WorkspaceDefinition workspace,
        string? path,
        string directoryName,
        CancellationToken cancellationToken = default)
    {
        var directory = ResolveWorkspacePath(workspace, CombineVirtualPath(NormalizeVirtualPath(path), CleanFileName(directoryName)));
        Directory.CreateDirectory(directory);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(
        WorkspaceDefinition workspace,
        string? path,
        CancellationToken cancellationToken = default)
    {
        var virtualPath = NormalizeVirtualPath(path);
        EnsureNotRootPath(virtualPath);
        var targetPath = ResolveWorkspacePath(workspace, virtualPath);
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
            return;
        }

        if (Directory.Exists(targetPath))
        {
            await Task.Run(() => Directory.Delete(targetPath, recursive: true), cancellationToken);
        }
    }

    public async Task<string> ReadSharedTextAsync(
        WorkspaceDefinition workspace,
        CancellationToken cancellationToken = default)
    {
        var path = GetSharedNotePath(workspace);
        if (!File.Exists(path))
        {
            return "";
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    public async Task WriteSharedTextAsync(
        WorkspaceDefinition workspace,
        string text,
        CancellationToken cancellationToken = default)
    {
        var path = GetSharedNotePath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text ?? "", cancellationToken);
    }

    public WorkspacePresenceSnapshot[] GetPresenceSnapshot(Guid workspaceId)
    {
        CleanupPresence(workspaceId);
        if (!_presences.TryGetValue(workspaceId, out var workspacePresences))
        {
            return [];
        }

        return workspacePresences.Values
            .OrderByDescending(entry => entry.LastSeenAt)
            .Select(entry => new WorkspacePresenceSnapshot(entry.SessionId, entry.DisplayName, entry.Mode, entry.LastSeenAt))
            .ToArray();
    }

    public void TouchPresence(WorkspaceDefinition workspace, HttpContext context, MatgateUser? user, string mode)
    {
        var sessionId = EnsureSessionId(context);
        var workspacePresences = _presences.GetOrAdd(workspace.Id, _ => new ConcurrentDictionary<string, WorkspacePresence>());
        workspacePresences[sessionId] = new WorkspacePresence(
            sessionId,
            string.IsNullOrWhiteSpace(user?.DisplayName) ? user?.UserName ?? "Guest" : user!.DisplayName,
            mode,
            DateTimeOffset.UtcNow);
        CleanupPresence(workspace.Id);
    }

    public async Task RecordActivityAsync(
        WorkspaceDefinition workspace,
        HttpContext context,
        MatgateUser? user,
        string action,
        string? path = null,
        string? details = null,
        string mode = "Admin",
        CancellationToken cancellationToken = default)
    {
        var entry = new WorkspaceActivityEntry(
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(user?.DisplayName) ? (user?.UserName ?? mode) : user.DisplayName!,
            string.IsNullOrWhiteSpace(mode) ? "Admin" : mode,
            action,
            string.IsNullOrWhiteSpace(path) ? "/" : path,
            details ?? "",
            EnsureSessionId(context));

        var logPath = GetActivityLogPath(workspace.Id);
        var gate = _activityLocks.GetOrAdd(workspace.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            await File.AppendAllTextAsync(logPath, line + Environment.NewLine, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workspace activity logging failed for {WorkspaceId} {Action}", workspace.Id, action);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkspaceActivityEntry>> GetActivityAsync(
        WorkspaceDefinition workspace,
        int maxEntries = 200,
        CancellationToken cancellationToken = default)
    {
        var logPath = GetActivityLogPath(workspace.Id);
        if (!File.Exists(logPath))
        {
            return [];
        }

        var gate = _activityLocks.GetOrAdd(workspace.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entries = File.ReadLines(logPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line =>
                {
                    try
                    {
                        return JsonSerializer.Deserialize<WorkspaceActivityEntry>(line, JsonOptions);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(entry => entry is not null)
                .Select(entry => entry!)
                .TakeLast(Math.Max(1, maxEntries))
                .Reverse()
                .ToArray();

            return entries;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkspaceDefinition>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        return await _store.GetWorkspacesAsync(cancellationToken);
    }

    public async Task<WorkspaceDefinition?> FindWorkspaceByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _store.FindWorkspaceByIdAsync(id, cancellationToken);
    }

    public async Task UpdateWorkspacesAsync(Action<List<WorkspaceDefinition>> update, CancellationToken cancellationToken = default)
    {
        await _store.UpdateWorkspacesAsync(update, cancellationToken);
    }

    private void CleanupPresence(Guid workspaceId)
    {
        if (!_presences.TryGetValue(workspaceId, out var workspacePresences))
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - PresenceTimeout;
        foreach (var entry in workspacePresences.Where(entry => entry.Value.LastSeenAt < cutoff))
        {
            workspacePresences.TryRemove(entry.Key, out _);
        }

        if (workspacePresences.IsEmpty)
        {
            _presences.TryRemove(workspaceId, out _);
        }
    }

    private string ResolveWorkspacePath(WorkspaceDefinition workspace, string virtualPath)
    {
        var root = GetWorkspaceFilesRoot(workspace);
        var relative = virtualPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(root, relative));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!string.Equals(combined, root, StringComparison.OrdinalIgnoreCase)
            && !combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Der Pfad ist ungueltig.");
        }

        return combined;
    }

    private string GetActivityLogPath(Guid workspaceId)
    {
        return Path.Combine(_store.DataDirectory, "workspace-logs", $"{workspaceId:N}.jsonl");
    }

    private static string NormalizeVirtualPath(string? path)
    {
        var cleaned = (path ?? "").Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "/";
        }

        while (cleaned.Contains("//", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("//", "/", StringComparison.Ordinal);
        }

        if (!cleaned.StartsWith('/'))
        {
            cleaned = "/" + cleaned.TrimStart('/');
        }

        if (cleaned.Length > 1 && cleaned.EndsWith('/'))
        {
            cleaned = cleaned.TrimEnd('/');
        }

        return cleaned;
    }

    private static string CombineVirtualPath(string left, string right)
    {
        var cleanedRight = CleanFileName(right);
        if (string.IsNullOrWhiteSpace(left) || left == "/")
        {
            return "/" + cleanedRight;
        }

        return $"{left.TrimEnd('/')}/{cleanedRight}";
    }

    private static string ParentVirtualPath(string path)
    {
        var normalized = NormalizeVirtualPath(path);
        if (normalized == "/")
        {
            return "/";
        }

        var trimmed = normalized.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private static void EnsureNotRootPath(string path)
    {
        if (path == "/" || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Das Stammverzeichnis kann nicht bearbeitet werden.");
        }
    }

    private static string CleanFileName(string value)
    {
        var cleaned = Path.GetFileName((value ?? "").Trim());
        return NormalizeFileName(cleaned, "untitled");
    }

    private static string NormalizeFileName(string? value, string fallback)
    {
        var cleaned = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = fallback;
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(invalid, '_');
        }

        return cleaned;
    }

    private static string SanitizeSegment(string value)
    {
        var cleaned = NormalizeFileName(value, "workspace");
        return string.IsNullOrWhiteSpace(cleaned) ? "workspace" : cleaned;
    }

    private string ContentTypeFromPath(string path)
    {
        return _contentTypeProvider.TryGetContentType(path, out var contentType)
            ? contentType
            : "application/octet-stream";
    }

    private sealed record WorkspacePresence(string SessionId, string DisplayName, string Mode, DateTimeOffset LastSeenAt);
}

public sealed record WorkspacePresenceSnapshot(
    string SessionId,
    string DisplayName,
    string Mode,
    DateTimeOffset LastSeenAt);

public sealed record WorkspaceAccessGrant(
    Guid WorkspaceId,
    DateTimeOffset ExpiresAt);
