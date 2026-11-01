# Matgate agent

A small program on a machine that Matgate connects to - like an RDP server: host, port and a
password (the agent's token). What the machine offers is up to the agent; Matgate asks after
connecting and shows exactly that.

Plugins today:

- **Files** - the configured folders in Matgate's file manager (browse, upload, download, create,
  delete, copy between places).
- **Terminals** - programs in a terminal tab in the browser, e.g. **Claude Code** or PowerShell.
  A session keeps running when the browser goes away (a phone that loses the connection attaches
  again and sees what happened); closing its tab ends the program.

Windows first. The file plugin runs anywhere .NET runs; terminals use Windows' pseudo console
(ConPTY) and are only offered on Windows so far.

## Running it

```bash
dotnet publish agent/Matgate.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/agent
```

Start `matgate-agent.exe` once. It creates `matgate-agent.json` next to itself (or at
`--config <path>`) with a fresh token and a self-signed TLS certificate (`matgate-agent.pfx`), and
prints both:

```
Matgate agent 0.1.0
  configuration: C:\Matgate\matgate-agent.json (created)
  listening on:  0.0.0.0:7488
  token:         ...
  certificate:   SHA-256 ...
```

In Matgate: *Administration -> Connections -> New*, type **Agent**, host and port of the machine,
the token as *connection password*, and "Ignore certificate" for the self-signed certificate.
Opening the connection shows the files; the programs are buttons in its toolbar and start in the
folder that is open.

## Configuration

```json
{
  "Port": 7488,
  "ListenAddress": "0.0.0.0",
  "Token": "generated on the first start",
  "Name": "",
  "CertificatePath": "matgate-agent.pfx",
  "CertificatePassword": "",
  "Roots": [ { "Name": "Home", "Path": "C:\\Users\\me" } ],
  "Terminals": [
    { "Id": "claude", "Title": "Claude Code", "Command": "cmd.exe /c claude", "Enabled": true },
    { "Id": "powershell", "Title": "PowerShell", "Command": "powershell.exe -NoLogo", "Enabled": false }
  ],
  "DetachedSessionMinutes": 60
}
```

- `Roots` - the only folders the file plugin can see. `..` and links cannot lead out of them.
  No roots, no file plugin.
- `Terminals` - each entry is one plugin. `Command` is the whole command line; programs run as the
  user the agent runs as, in a pseudo console with `TERM=xterm-256color`.
- `ListenAddress` - `127.0.0.1` when Matgate runs on the same machine or reaches it through a tunnel.
- `DetachedSessionMinutes` - how long a terminal without a browser keeps running (0 = until the
  program ends).

## Security

A terminal on the machine can do whatever the agent's user can. So:

- Run the agent as a normal user, never as an administrator or SYSTEM.
- Keep the token secret; it is the only key. Change it by clearing `Token` and restarting.
- With "Ignore certificate" Matgate does not check whom it talks to; on an untrusted network give
  the agent a certificate Matgate trusts (`CertificatePath`).
- In Matgate, a user who is limited to a folder or to reading on this connection gets no terminal.

## Protocol

One WebSocket (`wss://host:port/matgate`, `Authorization: Bearer <token>`), opened by Matgate.
Every message is binary: a 4-byte header length, a JSON header (request, response or event) and an
optional raw payload. The format is `Matgate/Agent/AgentWire.cs`, compiled into both sides.
