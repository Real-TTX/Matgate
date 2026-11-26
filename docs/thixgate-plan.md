# ThixGate – Plan

**ThixGate ist ein eigener Remote-Manager** aus der Reihe der Thix-Apps (Avalonia, Windows und macOS, eine Codebasis,
verteilt über ThixLauncher) – so wie Royal TS oder mRemoteNG: eigene Bibliothek, eigener Tresor für Zugangsdaten,
eigene Verbindungen. **Er braucht Matgate nicht.**

**Matgate ist optional** – ein Zusatz, den man in ThixGate einrichten kann:

1. **Bibliothek holen:** Ordner und Verbindungen aus einem Matgate-Konto übernehmen und aktuell halten.
2. **Über Matgate verbinden:** für Ziele, die man von hier aus nicht erreicht (anderes Netz, nur über das Gateway
   offen), geht die Verbindung durch Matgate.

---

## Aufbau

```
Mat.Remote            gemeinsame Bibliothek (aus Matgate herausgelöst): RDP, VNC, Zwischenablage, Ton, Kanäle
                      - genutzt von Matgate (Server, Bild als H.264 in den Browser)
                      - und von ThixGate (Bild direkt auf den Bildschirm, kein Video-Umweg)

ThixGate/             eigenes Repo, Aufbau wie ThixTunnel/ThixClip
  src/ThixGate.Core   Bibliothek, Tresor, Import/Export, Verbindungs-Logik, Matgate-Anbindung (optional)
  src/ThixGate        Avalonia-App: Baum, Tabs, Sitzungsansichten, Terminal, Einstellungen
  tests/
```

Warum das gut geht:

- Die Engine in `Matgate/Display` (~19.000 Zeilen C#) hängt nicht an ASP.NET; sie spricht nur mit einer kleinen
  Schnittstelle (`IScreenHost`: geänderte Bildbereiche, Mauszeiger, Ton, Zwischenablage, Anmeldedaten nachfragen).
  ThixGate implementiert diese Schnittstelle und zeichnet das Bild direkt – einfacher und schneller als im Browser.
- SSH gibt es mit SSH.NET schon in Matgate; Sprungserver und Tunnel hat ThixTunnel – dessen Kern kann mitgenutzt werden.
- Design, Verteilung, Updates: wie bei den anderen Thix-Apps.

---

## Was geteilt wird – und was nicht

Geteilt wird alles **ohne Oberfläche und ohne Server-Eigenheiten**, in drei Paketen:

| Paket | Inhalt | Nutzen |
|---|---|---|
| `Mat.Remote` | RDP, VNC (Engines, Kanäle, Zwischenablage, Ton); Datei-Clients SFTP/SMB/FTP/WebDAV; Datenbanken (SQL Server, MySQL, PostgreSQL) | eine Engine, Fehler werden einmal behoben |
| `Mat.Remote.Connections` | das **Verbindungsmodell** (Art, Host, Port, Optionen je Protokoll: Tastatur, Farbtiefe, Laufwerke, Gateway …), Ordner, „gespeicherte Zugangsdaten“ als Verweis; Prüfung und Normalisierung; daraus die Engine-Optionen bauen; **Import/Export** (`.rdp`, RDCMan, mRemoteNG) | eine Verbindung bedeutet in beiden dasselbe; Import gibt es dann auch in Matgate |
| `Mat.Remote.Contracts` | die Typen der App-Schnittstelle (Bibliothek, Ereignisse, Durchleitung) | Matgate und ThixGate können nicht auseinanderlaufen |

**Nicht** geteilt wird: Speicherung (Matgates JSON-Store mit Benutzern, Gruppen, Rechten vs. ThixGates Datei),
Verschlüsselung (Matgates SecretProtector vs. DPAPI/Schlüsselbund), Rechte-Logik (bleibt auf dem Server, die App
bekommt nur das Ergebnis) und natürlich jede Oberfläche.

Dafür muss Matgates `ServerEndpoint` aufgeteilt werden: der Teil „was ist die Verbindung“ wandert ins gemeinsame Modell,
der Teil „wem gehört sie, wer darf was, wie wird sie gespeichert“ bleibt in Matgate. Das ist der heikelste Umbau –
Matgates gespeicherte Daten müssen unverändert lesbar bleiben.

## Stufe 0 – Engine herauslösen (in Matgate) – **erledigt (0.55.1)**

- `Rdp`, `Vnc`, `Video`, `RdpBackend`, `VncBackend`, `ScreenBackend`, `ScreenAudio` liegen jetzt im Projekt
  `Mat.Remote/` (net10.0, ohne ASP.NET); Matgate verweist darauf. In Matgate bleibt nur `ScreenSession` (Bild als H.264
  in den Browser). Der H.264-Encoder (`Video`) ist mit umgezogen, weil `ScreenQuality` (das auch RDP steuert) an ihm hängt –
  so musste keine Zeile Engine-Code geändert werden.
- Die Namespaces bleiben `Matgate.Display.*`: nichts an Matgates Code oder Daten ändert sich.
- Neu: `ScreenBackends.Create(target, host)` – der öffentliche Weg in die Engine (Matgates `DisplayBridge` nutzt ihn auch).
- Unit-Tests, Agent, Dockerfile und Workflows zeigen auf die neuen Pfade; alle Tests grün, das Docker-Image baut.
- Weitergabe an ThixGate: **Git-Submodul** (`external/Matgate` in ThixGate, Projektverweis auf `Mat.Remote`).

**Größe:** 1–2 Releases für `Mat.Remote`; 2–3 weitere für `Mat.Remote.Connections` (Modell aus `ServerEndpoint`
herauslösen, Daten bleiben kompatibel, Import/Export) – das kann auch parallel zu Stufe 1 laufen.

## Stufe 1 – ThixGate für sich (ohne Matgate)

- **Bibliothek:** Ordner, Unterordner, Verbindungen (RDP, VNC, SSH; Name, Adresse, Port, Optionen, Notiz, Farbe),
  Favoriten, zuletzt benutzt, Suche, Reihenfolge per Ziehen. Gespeichert als Datei im Benutzerprofil.
- **Tresor:** Zugangsdaten zentral, mehrfach verwendbar („gespeicherte Zugangsdaten“ wie in Matgate), verschlüsselt mit
  DPAPI (Windows) bzw. Schlüsselbund (macOS), optional zusätzlich mit Master-Passwort.
- **Sitzungen direkt:** RDP und VNC über `Mat.Remote`, Bild nativ gezeichnet; Maus, Tastatur (Scancodes, Win-Taste,
  Alt+Tab im Vollbild), Rad, Auflösung folgt dem Fenster, mehrere Monitore später. **Zwischenablage mit echten Dateien in
  beide Richtungen** (nativ möglich, im Browser nicht). Ton.
- **SSH-Terminal** (SSH.NET + Terminal-Steuerelement), Schlüssel, Sprungserver über ThixTunnel-Kern.
- **Oberfläche wie Matgate:** Seitenleiste, Tabs (auch als eigene Fenster), Statusleiste (Zustand, Bilder/s, Latenz),
  Rechtsklick-Menüs, eigene Dialoge.
- **Import:** `.rdp`-Dateien, RDCMan, mRemoteNG (CSV/XML); **Export** der eigenen Bibliothek.
- Verpackung, Signierung, ThixLauncher-Eintrag.

**Größe:** der größte Brocken, etwa 8–12 Releases.

## Stufe 2 – Matgate als Zusatz – **erledigt (Matgate 0.56.0, ThixGate 0.2)**

So ist es gebaut (`Matgate/Web/EndpointMapping.App.cs`, `Services/AppDeviceStore.cs`, `AppLibrary.cs`, `AppRelay.cs`):

| Weg | Was |
|---|---|
| `GET /app/authorize?client_id=thixgate&redirect_uri=http://127.0.0.1:<port>/…&code_challenge=…&code_challenge_method=S256&state=…&device=<Name>` | „ThixGate auf *Gerät* zulassen?“ für den im Browser Angemeldeten (sonst erst `/login`, auch SSO und TOTP); nur Rücksprung auf Loopback mit Port |
| `POST /app/authorize` | Zulassen → Einmal-Code (2 Minuten) an die Rücksprungadresse; Abbrechen → `error=access_denied` |
| `POST /app/api/token` `{code, codeVerifier, redirectUri}` | Code + PKCE-Verifier → Geräte-Token `mgd_…` (nur sein SHA-256 in `app-devices.json`); Ratenbremse wie die Anmeldung |
| `GET /app/api/me`, `GET /app/api/library` | Benutzer; RDP/VNC/SSH mit mindestens „Verbinden“, Ordner, **ohne** Passwort/Schlüssel; `ETag` = Revision (304) |
| `GET /app/api/relay/{id}` (WebSocket) | eine TCP-Verbindung zu Host/Port genau dieser Verbindung (auch durch ihren Tunnel), binär in beide Richtungen; protokolliert |
| `POST /app/api/logout`, Einstellungen → Sicherheit → Geräte | Gerät abmelden; mit dem Benutzer gehen auch seine Geräte |

Alles unter `/app/api` gilt nur mit `Authorization: Bearer` – das Cookie des Browsers zählt dort nicht. „Nur lesen“-Verbindungen
gehen nicht durch die Durchleitung: eine App mit eigener Engine könnte sie nicht schreibgeschützt halten (seit Stufe 3: als Sitzung auf Matgate). Die Bibliothek wird abgefragt
(alle 5 Minuten und auf Knopfdruck, dank ETag billig), einen Ereignis-Strom gibt es noch nicht.

In ThixGate: Matgate › Anmelden (Browser, Rückkehr auf 127.0.0.1), Tab „Verbindungen“ (Verbinden, In Bibliothek übernehmen),
je eigener Verbindung „Verbinden über“ ein Matgate **und** die Verbindung dort, über die durchgeleitet wird – die
Durchleitung ist kein offener Proxy. Die Anmeldung und das Passwort kommen von ThixGate.

**Ursprünglicher Plan – auf Matgate-Seite (2–3 Releases):**

- **App-Anmeldung:** ThixGate öffnet den Browser, Anmeldung wie gewohnt (Passwort, TOTP, MatIdentity, Entra),
  „ThixGate auf *Gerät* zulassen“ → Geräte-Token (PKCE, Rückkehr über 127.0.0.1). Token nur als Hash in Matgate; in
  ThixGate im Tresor. **Konto → Geräte** zum Abmelden je Gerät.
- **Bibliothek:** `GET /app/api/library` – Ordner und Verbindungen, die der Benutzer sehen darf, mit seinen Rechten,
  **ohne Passwörter**; Änderungen per Ereignis-Strom.
- **Durchleitung (Gateway):** `/app/api/relay/{verbindung}` – ein WebSocket, über den Matgate eine TCP-Verbindung zum
  Host/Port **genau dieser Verbindung** öffnet (kein offener Proxy; Rechte „verbinden“ geprüft; protokolliert). ThixGate
  fährt darüber seine eigene Engine – volle Qualität, Zugangsdaten aus dem eigenen Tresor.

**In ThixGate (1–2 Releases):**

- Einstellungen → **Matgate-Konten** (mehrere möglich): verbinden, abmelden.
- Die Matgate-Bibliothek erscheint als eigener Bereich im Baum („Aus Matgate: *Server*“), schreibgeschützt und
  aktuell gehalten; einzelne Verbindungen lassen sich in die eigene Bibliothek **kopieren**.
- Je Verbindung **„Verbinden über“: direkt / Matgate *X*** – auch für eigene Verbindungen.

## Stufe 3 – Sitzung auf Matgate, Zurückschreiben – **erledigt (Matgate 0.58.0, ThixGate 0.3)**

So ist es gebaut (`Matgate/Web/EndpointMapping.AppScreen.cs`, `Services/AppLibrary.cs`, `Display/ScreenSession.cs`):

| Weg | Was |
|---|---|
| `GET /app/api/screen/{id}?quality=&resolution=&fitWidth=&fitHeight=` (WebSocket) | eine Sitzung von Matgates eigener Engine (RDP, VNC; auch durch den Tunnel) mit **Matgates** Anmeldung – fehlt sie, fragt Matgate (`screen.auth`) und die Antwort geht an Matgate. Dasselbe Bildschirm-Protokoll wie die Bildschirm-Seite des Browsers, nur das Bild kommt als `screen.tiles`: das geänderte Rechteck als BGRX-Zeilen in zlib, je Stück mit `screen.ack` quittiert (dieselbe Taktung wie H.264). Mit „Nur lesen“ kommt das Bild, aber keine Taste und kein Zeiger geht durch. Noch ohne Zwischenablage und Ton; endet mit dem Socket; protokolliert |
| `GET /app/api/library` | zusätzlich RDP/VNC mit „Nur lesen“ (Level `ReadOnly`: nur als Sitzung auf Matgate), je Verbindung `canDelete`, beim Benutzer `canCreatePrivate`/`canCreateShared` |
| `POST /app/api/connections` | neue Verbindung (RDP, VNC, SSH): eigene mit „Verbindungen anlegen“, geteilte mit „Verbindungen verwalten“ |
| `PUT /app/api/connections/{id}` | ändern mit „Bearbeiten“ – nur was die Verbindung ist (Name, Adresse, Port, Benutzer, Domäne, Ordner, Notiz, Tastatur, Zertifikat); Protokoll, Passwort, Schlüssel, Tunnel, Besitzer und Rechte bleiben |
| `DELETE /app/api/connections/{id}` | löschen mit „Verwalten“ (oder als Verwalter einer geteilten), wie im Formular |

**Warum Kacheln statt H.264:** ThixGate läuft auf Windows und macOS; ein H.264-Decoder wäre je Plattform eigener
nativer Code (Media Foundation, VideoToolbox), auf dem Mac von hier aus nicht prüfbar. Die Kacheln sind verlustfrei
(Text sofort scharf), brauchen auf Matgate keine OpenH264-Bibliothek und auf dem Gerät nur .NET. Mehr Daten bei
bewegten Bildern – H.264 kann als weiteres Format dazukommen (`picture` in `screen.ready` nennt das Format).

In ThixGate: je Verbindung über Matgate (RDP/VNC) **„Sitzung auf Matgate“** (Gateway-Tab); aus der Matgate-Bibliothek
gehen Verbindungen mit in Matgate gespeichertem Passwort und „Nur lesen“ von selbst so. Im Tab „Verbindungen“ eines
Matgate: **Neu in Matgate, Bearbeiten, Löschen** – soweit Matgate es erlaubt.

## Stufe 4 – Dateien, Datenbanken, Websites – **erledigt (Matgate 0.59.0, ThixGate 0.4)**

**In Matgate:** die Datei-Clients (SFTP, FTP, SMB, WebDAV) stehen jetzt in `Mat.Remote/Files` (`RemoteFiles` mit
`FileTarget`, dazu `FileOperations`: kopieren auch zwischen zwei Verbindungen, verschieben, umbenennen, ZIP, entpacken,
einen Ordner hochladen), die Datenbank-Anbindung (SQL Server, MySQL, PostgreSQL: Katalog, Abfragen mit mehreren Ergebnissen,
Abbrechen, Zeilen bearbeiten, Tabellen entwerfen, Plan, Export, Nur-lesen-Schutz) in `Mat.Remote/Database` (`DatabaseGateway`
mit `DatabaseTarget`). Matgates `FileGatewayService` behält nur seine eigenen Ablagen (lokal, Agent) und die Prüfung auf
Nur lesen; `FileTargets` / `DatabaseTargets` machen aus einem `ServerEndpoint` das Ziel.

**App-Schnittstelle:** die Bibliothek enthält auch Dateien (`sftp`, `ftp`, `smb`, `webdav`), Websites (`website`) und
Datenbanken (`mssql`, `mysql`, `postgres`) mit „Verbinden“ – dazu `url`, `rootPath` und `database`; Zurückschreiben
kennt dieselben Arten und Felder. Die Durchleitung trägt alle außer FTP (das öffnet weitere Verbindungen für seine Daten); für
eine Website geht sie zu Host und Port ihrer Adresse.

**In ThixGate:** Dateimanager, SQL-Browser und Website als Tabs – mit ThixGates eigener Anmeldung (Matgate gibt kein Passwort
heraus).

## Stufe 5 – später

- Zwischenablage und Ton auch für die Sitzung auf Matgate; H.264 als zweites Bildformat.
- Ereignis-Strom statt Abfragen der Bibliothek.
- Matgates Datei-Endpunkte auf `FileOperations` umstellen (heute noch eigener Code für Kopieren, ZIP, Entpacken).
- Websites mit Matgates Anmeldung (Browser-Farm, Proxmox), weitere Protokolle, iPhone/Android über Avalonia.

## Offene Entscheidungen

- ~~`Mat.Remote` als Submodul oder NuGet-Paket?~~ Submodul.
- Master-Passwort für den Tresor: Pflicht, optional oder gar nicht?
- Import aus welchen Programmen zuerst (RDCMan, mRemoteNG, Royal TS, `.rdp`)?

## Risiken

- Bild schnell genug zeichnen (große Auflösungen, viele kleine Änderungen) – Avalonia `WriteableBitmap` bzw. GPU-Fläche.
- RDP gegen echte Windows-Server: Matgates Engine ist bisher vor allem gegen xrdp geprüft.
- macOS: Signierung, Notarisierung, Freigaben für das Abfangen von Tasten.
- Terminal-Steuerelement für Avalonia.
