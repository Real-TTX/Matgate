using System.Text.Json.Nodes;
using Matgate.Agent;
using Matgate.Models;

namespace Matgate.Services;

// The file explorer of a Matgate agent. The agent's tree is "/<shared folder>/..."; a per-user
// restriction (FileRootPath on the effective server) narrows it further, exactly as for SFTP.
public sealed partial class FileGatewayService
{
    private static string AgentPath(ServerEndpoint server, string? path)
    {
        var root = NormalizeVirtualPath(server.FileRootPath);
        var relative = NormalizeVirtualPath(path);
        return root == "/" ? relative : relative == "/" ? root : root + relative;
    }

    private async Task<FileGatewayListResult> ListAgentAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken)
    {
        var virtualPath = NormalizeVirtualPath(path);
        var link = await agents.GetAsync(server, cancellationToken);
        var response = await link.RequestAsync("files.list", new JsonObject { ["path"] = AgentPath(server, virtualPath) }, cancellationToken: cancellationToken);
        var entries = (response.Data?["entries"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(entry =>
            {
                var name = entry["name"]?.GetValue<string>() ?? "";
                return new FileGatewayEntry(
                    name,
                    CombineVirtualPath(virtualPath, name),
                    entry["dir"]?.GetValue<bool>() ?? false,
                    entry["size"]?.GetValue<long>(),
                    DateTimeOffset.FromUnixTimeMilliseconds(entry["mtime"]?.GetValue<long>() ?? 0));
            })
            .Where(entry => entry.Name.Length > 0)
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new FileGatewayListResult(virtualPath, ParentVirtualPath(virtualPath), entries);
    }

    private async Task<FileGatewayFileInfo> GetAgentFileInfoAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken)
    {
        var link = await agents.GetAsync(server, cancellationToken);
        var response = await link.RequestAsync("files.stat", new JsonObject { ["path"] = AgentPath(server, path) }, cancellationToken: cancellationToken);
        if (response.Bool("dir"))
        {
            throw new InvalidOperationException("Datei nicht gefunden.");
        }

        var name = response.Text("name") ?? FileNameFromPath(path ?? "");
        return new FileGatewayFileInfo(name, ContentTypeFromPath(name), response.Long("size"));
    }

    private async Task CopyAgentRangeAsync(ServerEndpoint server, string? path, Stream output, long offset, long length, CancellationToken cancellationToken)
    {
        var link = await agents.GetAsync(server, cancellationToken);
        var agentPath = AgentPath(server, path);
        var remaining = length;
        var position = offset;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, AgentWire.ChunkSize);
            var response = await link.RequestAsync(
                "files.read",
                new JsonObject { ["path"] = agentPath, ["offset"] = position, ["length"] = chunk },
                cancellationToken: cancellationToken);
            if (response.Payload.Length == 0)
            {
                break;
            }

            await output.WriteAsync(response.Payload, cancellationToken);
            position += response.Payload.Length;
            remaining -= response.Payload.Length;
        }
    }

    private async Task<FileGatewayDownload> DownloadAgentAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken)
    {
        var info = await GetAgentFileInfoAsync(server, path, cancellationToken);
        var link = await agents.GetAsync(server, cancellationToken);
        return new FileGatewayDownload(new AgentReadStream(link, AgentPath(server, path), info.Length), info.FileName, info.ContentType);
    }

    private async Task UploadAgentAsync(ServerEndpoint server, string? path, Stream content, string fileName, CancellationToken cancellationToken)
    {
        var link = await agents.GetAsync(server, cancellationToken);
        var opened = await link.RequestAsync(
            "files.write.open",
            new JsonObject { ["path"] = AgentPath(server, path), ["name"] = SafeFileName(fileName) },
            cancellationToken: cancellationToken);
        var handle = opened.Text("handle") ?? throw new InvalidOperationException("The agent did not open the file.");
        var buffer = new byte[AgentWire.ChunkSize];
        try
        {
            while (true)
            {
                // Fill the chunk: request bodies arrive in small pieces, and every piece would be a round trip.
                var filled = 0;
                while (filled < buffer.Length)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(filled), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    filled += read;
                }

                if (filled == 0)
                {
                    break;
                }

                await link.RequestAsync("files.write", new JsonObject { ["handle"] = handle }, buffer.AsMemory(0, filled), cancellationToken);
                if (filled < buffer.Length)
                {
                    break;
                }
            }

            await link.RequestAsync("files.write.close", new JsonObject { ["handle"] = handle }, cancellationToken: cancellationToken);
        }
        catch
        {
            try
            {
                await link.RequestAsync("files.write.abort", new JsonObject { ["handle"] = handle }, cancellationToken: CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // The agent drops unfinished uploads itself when the connection goes.
            }

            throw;
        }
    }

    private async Task CreateAgentFileAsync(ServerEndpoint server, string? path, string fileName, CancellationToken cancellationToken)
    {
        var link = await agents.GetAsync(server, cancellationToken);
        await link.RequestAsync("files.create", new JsonObject { ["path"] = AgentPath(server, path), ["name"] = SafeFileName(fileName) }, cancellationToken: cancellationToken);
    }

    private async Task CreateAgentDirectoryAsync(ServerEndpoint server, string? path, string directoryName, CancellationToken cancellationToken)
    {
        var link = await agents.GetAsync(server, cancellationToken);
        await link.RequestAsync("files.mkdir", new JsonObject { ["path"] = AgentPath(server, path), ["name"] = SafeFileName(directoryName) }, cancellationToken: cancellationToken);
    }

    private async Task DeleteAgentAsync(ServerEndpoint server, string? path, CancellationToken cancellationToken)
    {
        var agentPath = AgentPath(server, path);
        if (agentPath == NormalizeVirtualPath(server.FileRootPath))
        {
            throw new InvalidOperationException("Die Ablage selbst kann nicht geloescht werden.");
        }

        var link = await agents.GetAsync(server, cancellationToken);
        await link.RequestAsync("files.delete", new JsonObject { ["path"] = agentPath }, cancellationToken: cancellationToken);
    }

    // A download read in chunks as it is consumed - a large file never sits in the gateway's memory.
    private sealed class AgentReadStream(AgentLink link, string path, long length) : Stream
    {
        private long _position;
        private ReadOnlyMemory<byte> _buffered;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (_buffered.IsEmpty)
            {
                if (_position >= length)
                {
                    return 0;
                }

                var response = await link.RequestAsync(
                    "files.read",
                    new JsonObject { ["path"] = path, ["offset"] = _position, ["length"] = AgentWire.ChunkSize },
                    cancellationToken: cancellationToken);
                if (response.Payload.IsEmpty)
                {
                    return 0;
                }

                _buffered = response.Payload;
            }

            var count = Math.Min(destination.Length, _buffered.Length);
            _buffered[..count].CopyTo(destination);
            _buffered = _buffered[count..];
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
