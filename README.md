<div align="center">

<img src="docs/images/logo.png" width="96" alt="Matgate" />

# Matgate

**One login for your whole home network – in the browser.**

Remote desktops (RDP/VNC), shell (SSH), file access (SFTP/FTP/SMB) and internal web UIs,
behind a single self-hosted gateway. One Docker stack, no cloud, no agents on your machines.

</div>

![The connections home with quick-connect, folders and favorites](docs/images/home.png)

---

## What this is about

Your home network is full of things you occasionally need to reach: a Windows box over RDP,
a Linux server over SSH, the NAS share, the router's web UI. Normally that means an RDP client
here, an SSH client there, a VPN, a bookmark, a password sheet. Matgate puts all of it behind
**one web UI and one login**: every machine as a tile, every session as a tab, every credential
stored **encrypted** in one place. For RDP, VNC and SSH it drives Apache Guacamole and `guacd`
under the hood; files, the website proxy, users, permissions and the UI are Matgate's own.

It is built to sit on a home server in Docker, optionally behind a reverse proxy, and to be
usable from a laptop or a phone – installable as a PWA.

## At a glance

**Remote sessions**
- **RDP, VNC and SSH** in the browser through Guacamole, no client install
- Several sessions open at once as **draggable tabs**, with session restore
- **Pop a session out into its own window** and re-attach it later (great for multi-monitor)
- **Real fullscreen / immersive mode** (safe-area aware on iPhone), clipboard sync, a live status bar
- Per-session **scale** for VNC (Auto / 75% / 50%) so more fits on screen

**Files**
- File gateway for **SFTP, FTP and SMB** – no extra privileges in the container
- Upload, download, **zip / unzip**, copy, move, delete, archive extraction
- **Preview** for images, video, audio, PDF and text

**Websites**
- **Native reverse-proxy** mode for internal admin UIs (router, NAS, …)
- **"via Chromium / Firefox VNC"** fallback: the page opens in a real browser on an optional
  **browser farm** and is streamed over VNC – for pages the plain proxy can't render. Kiosk by
  default, auto-sized to your window, with smart-reconnect on resize

**Organizing & sharing**
- **Quick connect** for ad-hoc sessions without saving anything
- **Folders**, per-user **favorites**, search across names, folders and hosts
- **Workspaces**: shareable bundles with public links, password protection, shared text and file exchange
- Live **network tools**: ping, DNS lookup, port check, streamed download test

**Users & operation**
- Local users with **username + email + password**, a **first-run setup wizard**
- Admin roles and **per-server access control**; global servers and user-owned servers
- Credentials **encrypted at rest**; `/guacamole` sits behind the Matgate login
- **English and German**, light / dark (follows the system), installable **PWA**

## Screenshots

### First run, then one login

| Setup wizard | Sign in |
|---|---|
| ![First-run setup wizard that creates the admin account](docs/images/setup.png) | ![The sign-in card](docs/images/login.png) |

On the very first start a **setup wizard** creates your administrator account; after that it is a
single sign-in. From there every machine is a tile, grouped into folders with favorites on top and
a search across names and hosts.

### Ad-hoc without saving anything

![Quick-connect dialog for one-off sessions](docs/images/quick-connect.png)

**Quick connect** starts a one-off RDP/VNC/SSH/website session without creating a saved server.

### A live session

![A remote session as a tab, with toolbar and status bar](docs/images/session.png)

Every connection opens as a tab with its own toolbar (fullscreen, clipboard, scale, disconnect)
and a status bar showing tunnel state and latency. Shown here: a website opened in a real browser
through the **browser farm** – the fallback for web UIs the native proxy can't display.

### Administration in one place

| Servers | Users |
|---|---|
| ![Server gallery with protocol, folder and scope badges](docs/images/admin-servers.png) | ![User management](docs/images/admin-users.png) |

Servers as a **gallery or a list**, each with its protocol, folder and scope. The server editor
covers every connection type; websites additionally pick their render mode.

![The server editor](docs/images/new-server.png)

### The optional browser farm

![Browser-service admin: pool status, settings and history](docs/images/admin-browser.png)

A pool of isolated Chromium/Firefox sessions (each on its own VNC port) powers the
"via … VNC" websites. Pool size and resolution are live-configurable, with active sessions and
a history right in the admin area. It's an **optional** sidecar – without it, only "Native"
websites are offered.

### Light theme and phones

| Light | Phone |
|---|---|
| ![The connections screen in the light theme](docs/images/home-light.png) | ![Matgate on a phone](docs/images/mobile-home.png) |

## Quick start

Prebuilt images are published to the GitHub Container Registry:

| Image | Tag | Use it for |
|---|---|---|
| `ghcr.io/real-ttx/matgate` | `latest` | the current release |
| `ghcr.io/real-ttx/matgate` | `0.9.60`, `sha-…` | pinning an exact build |
| `ghcr.io/real-ttx/matgate-browser-farm` | `latest` | the optional browser farm |

Matgate needs Guacamole + `guacd` for RDP/VNC/SSH and a small edge proxy that keeps `/guacamole`
behind the login. Two ready-made stacks live in this repo — grab one and run it, no build required:

| File | What you get |
|---|---|
| [`docker-compose.simple.yml`](docker-compose.simple.yml) | RDP, VNC, SSH, files and **native** websites — the minimal stack |
| [`docker-compose.browser.yml`](docker-compose.browser.yml) | the same **plus the browser farm** ("via Chromium / Firefox VNC" websites) |

```bash
docker compose -f docker-compose.simple.yml up -d      # minimal
# or: everything, including the browser farm
docker compose -f docker-compose.browser.yml up -d
```

Open **http://localhost:8088** — the first start shows a **setup wizard** that creates your
administrator account (username, email, password). Both files are self-contained and use the prebuilt images. **Matgate generates and persists its
own keys** (the Guacamole key and the at-rest key) into the `matgate-secrets` volume — nothing to
configure by hand. For reference, the whole `docker-compose.simple.yml`:

```yaml
# Matgate - minimal stack (RDP / VNC / SSH / files / native websites).
#   docker compose -f docker-compose.simple.yml up -d
# Open http://localhost:8088 - the first start shows a setup wizard.
#
# Matgate generates and persists its own keys (guac + at-rest) into the matgate-secrets volume;
# the guacamole container reads the shared guac key from there. Nothing to configure by hand.
name: matgate

services:
  edge:
    image: caddy:2
    depends_on: [matgate, guacamole]
    ports:
      - "8088:8088"
    entrypoint:
      - /bin/sh
      - -c
      - |
        printf "%s\n" \
          ":8088 {" \
          "  encode zstd gzip" \
          "  handle /guacamole* {" \
          "    forward_auth matgate:8080 {" \
          "      uri /internal/guac-authz" \
          "    }" \
          "    reverse_proxy guacamole:8080" \
          "  }" \
          "  handle {" \
          "    reverse_proxy matgate:8080" \
          "  }" \
          "}" > /tmp/Caddyfile && exec caddy run --config /tmp/Caddyfile --adapter caddyfile
    restart: unless-stopped

  matgate:
    image: ghcr.io/real-ttx/matgate:latest
    environment:
      # Keep transferred files on the host (the guacd volume below). Remove this line and guacd uses
      # a scratch folder inside its own container instead: transfer still works with nothing mounted,
      # the files just live in that container until it is recreated.
      MATGATE_GUAC_DRIVE_PATH: /drive
    volumes:
      - ./data:/data
      - matgate-secrets:/run/matgate-secrets
    extra_hosts:
      - "host.docker.internal:host-gateway"
    healthcheck:
      test: ["CMD-SHELL", "test -s /run/matgate-secrets/guac.key"]
      interval: 3s
      timeout: 3s
      retries: 20
    restart: unless-stopped

  guacd:
    image: guacamole/guacd:1.6.0
    volumes:
      # Shared folder that RDP sessions redirect as the "Matgate" drive (file upload/download).
      # Must match drive-path in GuacamoleLauncher; matgate creates the per-server subfolders.
      - ./data/guac-drives:/drive
    restart: unless-stopped

  guacamole:
    image: guacamole/guacamole:1.6.0
    depends_on:
      guacd:
        condition: service_started
      matgate:
        condition: service_healthy
    environment:
      GUACD_HOSTNAME: guacd
      GUACD_PORT: "4822"
      JSON_ENABLED: "true"
    entrypoint:
      - /bin/sh
      - -c
      - export JSON_SECRET_KEY="$$(cat /run/matgate-secrets/guac.key)"; exec /opt/guacamole/bin/entrypoint.sh
    volumes:
      - ./data:/etc/guacamole
      - matgate-secrets:/run/matgate-secrets:ro
    restart: unless-stopped

volumes:
  matgate-secrets:
```

After the first admin is created you add your first server and connect.

> Prefer building from source, or want home-DNS resolution and a larger Tomcat header limit? The
> repository's `docker-compose.yml` builds Matgate locally and wires those extras; add the browser
> farm to it with `docker compose --profile browser up -d`.

### Pin your keys (recommended)

Matgate generates two secrets on first start into the `matgate-secrets` volume:

- `guac.key` – signs the Guacamole session tokens (**must match** between `matgate` and `guacamole`)
- `master.key` – encrypts stored device passwords **at rest**

If that volume is ever lost or recreated, new keys are generated and the **stored passwords can no
longer be decrypted**. To make the keys survive any redeploy, pin the current values into a `.env`
next to the compose file – once, non-destructively:

```bash
docker exec matgate-matgate-1 sh -c \
  'printf "MATGATE_GUACAMOLE_JSON_SECRET_KEY=%s\nMATGATE_SECRET_KEY=%s\n" \
   "$(cat /run/matgate-secrets/guac.key)" "$(cat /run/matgate-secrets/master.key)"' >> .env

docker compose up -d
```

From then on the keys come from `.env` and never regenerate. `MATGATE_GUACAMOLE_JSON_SECRET_KEY`
is 32 hex characters, `MATGATE_SECRET_KEY` is 64.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `MATGATE_DATA_DIR` | `/data` | Persistent data directory inside the container |
| `MATGATE_ADMIN_USER` / `MATGATE_ADMIN_PASSWORD` / `MATGATE_ADMIN_EMAIL` | – | Seed the admin unattended and skip the setup wizard |
| `MATGATE_GUACAMOLE_JSON_SECRET_KEY` | auto | 32 hex chars for Guacamole JSON auth (see *Pin your keys*) |
| `MATGATE_SECRET_KEY` | auto | 64 hex chars for at-rest encryption of credentials |
| `MATGATE_REQUIRE_HTTPS` | `false` | Force HTTPS / secure cookies behind a TLS proxy |
| `MATGATE_DNS_SERVER` / `MATGATE_DNS_SEARCH` | – | Point the containers at your home DNS so `nas`, `pc-terminal`, … resolve |
| `MATGATE_GUAC_DRIVE_PATH` | – (scratch) | Where **guacd** keeps files exchanged with RDP sessions. Set to `/drive` together with the matching guacd volume to keep them on the host (see *Files exchanged with RDP sessions*) |
| `MATGATE_GUAC_DRIVE_ROOT` | `<data>/guac-drives` | Matgate's own view of that same folder; only needed if you mount it somewhere other than `./data/guac-drives` |
| `BrowserFarm__BaseUrl` | `http://browser-farm:8090` | Where the optional browser farm lives |

## Data & persistence

Everything lives under the data directory (`./data` in the examples, mounted at `/data`):

```
/data
├─ users.json               local users, permissions, favorites
├─ servers.json             global + user-owned servers and folders
├─ workspaces.json          workspace definitions and share settings
├─ guacamole.properties     generated Guacamole config
├─ guac-drives/             files exchanged with RDP sessions (only in "kept" mode, see below)
└─ user-mapping.xml         generated Guacamole mapping (no cleartext credentials)
```

### Files exchanged with RDP sessions

RDP sessions get a redirected drive named **Matgate** - that is what *Send files* and drag & drop use.
It works out of the box and has two modes:

| | where the files live | kept until | setup |
|---|---|---|---|
| **Scratch** (default) | `/tmp` inside the guacd container | that container is recreated | none |
| **Kept** | `./data/guac-drives/<connection>` on the host | you delete them | one volume + one env var |

The shipped compose files use **kept**: they mount `./data/guac-drives` into guacd as `/drive` and set
`MATGATE_GUAC_DRIVE_PATH=/drive`. Drop either of those and you are back to scratch mode - file transfer
keeps working either way, and the gateway log says on every connect which mode it picked.

Be precise about what "scratch" means: the files are not in `./data`, but they are still on the host,
inside the guacd container's writable layer. They survive a restart and are discarded when that
container is recreated (image update, `compose down`). So scratch mode is the lower-maintenance
option, not the more private one - **kept** mode is the one where you can actually find, inspect and
delete what people transferred.

Neither mode cleans up by itself: one folder per connection, and it stays. In kept mode you can prune
them under `./data/guac-drives`. For scratch mode, adding a size-capped RAM disk to the guacd service
makes them genuinely temporary and bounds the growth - at the cost of that size being the largest
transfer you can make:

```yaml
  guacd:
    tmpfs:
      - /tmp:size=512m,mode=1777
```

Two details worth knowing if you write your own compose file. guacd runs as a non-root user, so it
cannot create folders inside a freshly mounted (root-owned) volume - in kept mode Matgate creates the
per-connection folder itself and makes it writable for guacd, which is why it needs the same host
folder under its own data directory (`MATGATE_GUAC_DRIVE_ROOT` if you mount it somewhere else than
`./data/guac-drives`). And setting `MATGATE_GUAC_DRIVE_PATH` *without* giving guacd the matching mount
is the one broken combination: the drive is announced but every transfer fails silently. In scratch
mode nothing has to be mounted or prepared at all.

The encryption keys live **outside** `./data` in the `matgate-secrets` volume, so a stolen `./data`
backup can't decrypt your device passwords. Back up **both** the data directory and the secrets
volume (or pin the keys in `.env`, see above).

## Permission model

- `Admin` manages users and all servers
- `CanManageServers` manages global servers, `CanCreateServers` may create own servers
- `ServerAccessAll`, or access granted per individual global server
- **Global** servers are shared and admin-managed; **Own** servers belong to a user (still visible
  to admins for support)

## How it's built

- **ASP.NET Core (.NET 10)**, server-rendered HTML; the client is **plain JavaScript**, no build step
- **RDP / VNC / SSH** via **Apache Guacamole + guacd**; Matgate mints short-lived, encrypted
  Guacamole auth tokens per launch
- File access through in-process **SFTP (SSH.NET)**, **FTP (FluentFTP)** and **SMB (SMBLibrary)**
- The **browser farm** is a small stdlib-Python control API over a pool of `Xvfb + x11vnc + browser`
  slots – no Docker socket, isolated per Linux user
- Data is stored as JSON files; device passwords are encrypted with AES-GCM

## Status

Matgate is an actively developed self-hosted project. Working today: remote sessions with tabs and
session restore, the file gateway, the native + browser-farm website modes, quick connect, folders
and favorites, workspaces with sharing, network tools, the setup wizard and per-server access
control, PWA and mobile layout. On the list: TLS polish and further hardening.

## Development

```bash
docker compose up -d --build            # full local stack (build from source)
docker compose --profile browser up -d  # add the optional browser farm
```

```bash
dotnet build Matgate/Matgate.csproj      # build without Docker
```

Images are built and published by GitHub Actions on pushes and tags (amd64 + a separate arm64
image).

## License

Matgate is licensed under the MIT License. See [LICENSE](LICENSE).
