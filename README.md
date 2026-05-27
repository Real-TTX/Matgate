# Matgate

Matgate is a self-hosted gateway for your home network. It gives you one web UI and one login for remote desktop sessions, shell access, website proxying, and file access.

It is designed to sit behind a reverse proxy such as Caddy and to run entirely in Docker. For RDP, VNC, and SSH it uses Apache Guacamole and `guacd` behind the scenes, while Matgate itself handles auth, permissions, tabs, file management, theme and language preferences, and local data storage.

## What Matgate gives you

- Web-based RDP, VNC, and SSH sessions
- File gateway for SFTP, FTP, and SMB
- Website proxy mode for browser-based admin interfaces
- Upload, download, delete, move, copy, archive extraction, and media preview in the file manager
- Shareable Workspaces with public links, password protection, shared text, and file exchange
- Live network tools for ping, lookup, port checking, and streamed download tests
- Multiple open connections as draggable tabs
- Session restore in the web UI
- Local users stored in JSON files
- Global servers and user-owned servers
- Server folders for grouping, plus per-user favorites
- Admin roles and per-server access control
- English and German UI
- Theme follows system settings by default, with per-user override
- Clipboard integration and a status bar for the active tab
- Server icons with protocol defaults and per-server overrides
- GitHub Actions build for the Docker image
- Installable PWA mode with desktop and home-screen app icons

## Supported connection types

| Type | What it is for |
| --- | --- |
| RDP | Remote Windows desktops through Guacamole |
| VNC | Remote desktop access through Guacamole |
| SSH | Remote shell access through Guacamole |
| SFTP | File access through the Matgate file gateway |
| FTP | File access through the Matgate file gateway |
| SMB | File access through the Matgate file gateway |
| Website (Beta) | Reverse-proxied browser access to internal web UIs |

## How it works

```text
Browser -> Matgate -> Guacamole / guacd -> RDP, VNC or SSH
Browser -> Matgate -> File gateway backend -> SFTP / FTP / SMB
Browser -> Matgate -> Website proxy -> Internal web UI
```

Matgate keeps the UI, permissions, and session state in one place. The client only talks to Matgate, which then talks to the remote systems in your network.

## Quick start

1. Install Docker and Docker Compose.
2. Create an optional `.env` file in the repository root.
3. Start the local stack with Docker Compose. The default stack builds Matgate from `Matgate/Dockerfile` and does not need a separate `Caddyfile`.
4. Open Matgate on port `8088`.

```powershell
docker compose up --build -d
```

Matgate is available at:

```text
http://localhost:8088
```

For a shareable stack, use one of:

```powershell
docker compose -f docker-compose-simple.yaml up -d
```

```powershell
docker compose -f docker-compose-dockhand.yaml up -d
```

Both stacks include the reverse proxy and use a relative data folder, so your users, servers, and Guacamole config stay where you expect them.

> Important: if no admin credentials are set, Matgate creates `admin` / `change-me-now`. Change that before exposing the service anywhere beyond a trusted home network.

## PWA / App mode

Matgate ships with a web app manifest, service worker, and app icons. On supported browsers you can install it to the desktop or home screen and launch it in a standalone app window.

- On iPhone and iPad, use Safari's "Add to Home Screen"
- On desktop browsers, use the install action from the browser menu
- In installed mode, Matgate behaves much more like a normal app and keeps the browser chrome out of the way

If you are behind HTTPS or using `localhost`, the install experience is usually best.

## Recommended environment variables

```env
MATGATE_ADMIN_USER=admin
MATGATE_ADMIN_PASSWORD=change-me-now
MATGATE_GUACAMOLE_JSON_SECRET_KEY=0123456789abcdeffedcba9876543210
MATGATE_DNS_SERVER=10.10.0.1
MATGATE_DNS_SEARCH=example.home
MATGATE_WORKSPACE_ROOT=/data/workspaces
```

| Variable | Purpose |
| --- | --- |
| `MATGATE_ADMIN_USER` | Initial admin username |
| `MATGATE_ADMIN_PASSWORD` | Initial admin password |
| `MATGATE_DATA_DIR` | Persistent data directory inside the container |
| `MATGATE_GUACAMOLE_JSON_SECRET_KEY` | 32 hex characters used for Guacamole JSON auth |
| `MATGATE_DNS_SERVER` | DNS server used inside Docker containers |
| `MATGATE_DNS_SEARCH` | DNS search domain used inside Docker containers |
| `MATGATE_WORKSPACE_ROOT` | Default filesystem root for workspace folders |

`MATGATE_GUACAMOLE_JSON_SECRET_KEY` must be exactly 32 hex characters. Use your own random value.
The Compose stack provides sane defaults, but you should override secrets and DNS settings for real use.

## Data and persistence

Matgate stores all persistent data under the configured data directory. In the default local compose setup that is the relative `./data` folder, mounted at `/data`.

| Path | Purpose |
| --- | --- |
| `/data/users.json` | Local users, permissions, and favorites |
| `/data/servers.json` | Global servers, user-owned servers, and folders |
| `/data/workspaces.json` | Workspace definitions and share settings |
| `data/guacamole.properties` | Guacamole runtime config |
| `data/user-mapping.xml` | Guacamole user-to-connection mapping |

Back up the whole volume regularly. It contains the users, server definitions, and Guacamole sync files.

## Permission model

Matgate uses a small, explicit permission model:

- `Admin` can manage users and all servers
- `CanManageServers` can manage global servers
- `CanCreateServers` can create own servers
- `ServerAccessAll` gives access to all global servers
- Users can also be granted access to individual global servers

There are two server scopes:

- `Global` for shared servers that admins can manage and grant access to
- `Own` for servers owned by a specific user

Own servers automatically belong to their owner. They are still visible to admins for oversight and support.

## Reverse proxy and networking

The included Compose stack exposes the app through Caddy on port `8088`.

If you already run your own reverse proxy, forward it to the Matgate edge service or directly to the published host port.

Inside Docker, Matgate can be pointed at your home DNS server so hostnames like `pc-terminal` or `nas` resolve correctly from the containers.

## Development

```powershell
dotnet build Matgate.slnx
docker compose up --build -d
```

The repository uses GitHub Actions to build and publish the Docker image on pushes and tags. The workflow also attaches a CI version suffix during image builds.

## Repository structure

| Path | Purpose |
| --- | --- |
| `Matgate/` | ASP.NET Core application |
| `docker-compose.yml` | Full local stack with Caddy, Matgate, Guacamole, and guacd |
| `docker-compose-simple.yaml` | Shareable local stack with Caddy, Matgate, Guacamole, and guacd |
| `docker-compose-dockhand.yaml` | Copy/paste stack for Dockhand-based deployments |
| `Matgate/Dockerfile` | Application container image |
| `.github/workflows/` | CI and Docker image build workflow |

## Project status

Matgate is an actively evolving self-hosted project. The current focus is on:

- stable remote sessions
- file gateway workflows
- user and server management
- workspace polish
- keeping the UI simple enough for everyday home-network use
- making the website proxy mode more robust and integrated

## Contributing

Issues and pull requests are welcome. If you are opening a PR, please include the context for the change and how you verified it.

## License

Matgate is licensed under the MIT License. See [LICENSE](LICENSE).
