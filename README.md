# Matgate

Matgate is a self-hosted gateway for your home network. It gives you one web UI and one login for remote desktop sessions, shell access, and file access.

It is designed to sit behind a reverse proxy such as Caddy and to run entirely in Docker. Matgate is not a Microsoft RD Gateway. For RDP and SSH it uses Apache Guacamole and `guacd` behind the scenes, while Matgate itself handles auth, permissions, tabs, file management, and local data storage.

## What Matgate gives you

- Web-based RDP and SSH sessions
- File gateway for SFTP, FTP, and SMB
- Upload, download, delete, move, copy, archive extraction, and media preview in the file manager
- Multiple open connections as tabs
- Session restore in the web UI
- Local users stored in JSON files
- Global servers and user-owned servers
- Admin roles and per-server access control
- English and German UI
- Clipboard integration and a status bar for the active tab
- Server icons with protocol defaults and per-server overrides
- GitHub Actions build for the Docker image

## Supported connection types

| Type | What it is for |
| --- | --- |
| RDP | Remote Windows desktops through Guacamole |
| SSH | Remote shell access through Guacamole |
| SFTP | File access through the Matgate file gateway |
| FTP | File access through the Matgate file gateway |
| SMB | File access through the Matgate file gateway |

## How it works

```text
Browser -> Matgate -> Guacamole / guacd -> RDP or SSH
Browser -> Matgate -> File gateway backend -> SFTP / FTP / SMB
```

Matgate keeps the UI, permissions, and session state in one place. The client only talks to Matgate, which then talks to the remote systems in your network.

## Quick start

1. Install Docker and Docker Compose.
2. Create an optional `.env` file in the repository root.
3. Start the stack with Docker Compose.
4. Open Matgate on port `8088`.

```powershell
docker compose up --build
```

Matgate is available at:

```text
http://localhost:8088
```

> Important: if no admin credentials are set, Matgate creates `admin` / `change-me-now`. Change that before exposing the service anywhere beyond a trusted home network.

## Recommended environment variables

```env
MATGATE_ADMIN_USER=admin
MATGATE_ADMIN_PASSWORD=change-me-now
MATGATE_DATA_DIR=./data
MATGATE_GUACAMOLE_JSON_SECRET_KEY=0123456789abcdeffedcba9876543210
MATGATE_DNS_SERVER=10.10.0.1
MATGATE_DNS_SEARCH=example.home
```

| Variable | Purpose |
| --- | --- |
| `MATGATE_ADMIN_USER` | Initial admin username |
| `MATGATE_ADMIN_PASSWORD` | Initial admin password |
| `MATGATE_DATA_DIR` | Persistent data directory |
| `MATGATE_GUACAMOLE_JSON_SECRET_KEY` | 32 hex characters used for Guacamole JSON auth |
| `MATGATE_DNS_SERVER` | DNS server used inside Docker containers |
| `MATGATE_DNS_SEARCH` | DNS search domain used inside Docker containers |

`MATGATE_GUACAMOLE_JSON_SECRET_KEY` must be exactly 32 hex characters. Use your own random value.
The Compose stack provides sane defaults, but you should override secrets and DNS settings for real use.

## Data and persistence

Matgate stores all persistent data under the configured data directory. In the default compose setup that is `./data`.

| Path | Purpose |
| --- | --- |
| `data/users.json` | Local users and permissions |
| `data/servers.json` | Global servers and user-owned servers |
| `data/guacamole/` | Guacamole runtime and sync data |

Back up the whole data directory regularly. It contains the users and server definitions.

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
docker compose up --build
```

The repository uses GitHub Actions to build and publish the Docker image on pushes and tags. The workflow also attaches a CI version suffix during image builds.

## Repository structure

| Path | Purpose |
| --- | --- |
| `Matgate/` | ASP.NET Core application |
| `docker-compose.yml` | Full local stack with Caddy, Matgate, Guacamole, and guacd |
| `Matgate/Dockerfile` | Application container image |
| `.github/workflows/` | CI and Docker image build workflow |

## Project status

Matgate is an actively evolving self-hosted project. The current focus is on:

- stable remote sessions
- file gateway workflows
- user and server management
- workspace polish
- keeping the UI simple enough for everyday home-network use

## Contributing

Issues and pull requests are welcome. If you are opening a PR, please include the context for the change and how you verified it.

## License

A license has not been chosen yet in this repository. Add one before using the project for wider public distribution.
