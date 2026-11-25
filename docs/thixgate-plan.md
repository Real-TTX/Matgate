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
| `Mat.Remote` | RDP, VNC (Engines, Kanäle, Zwischenablage, Ton); Datei-Clients SFTP/SMB/FTP/WebDAV | eine Engine, Fehler werden einmal behoben |
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

## Stufe 2 – Matgate als Zusatz

**Auf Matgate-Seite (2–3 Releases):**

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

## Stufe 3 – später

- **Sitzung auf Matgate** statt Durchleitung: für Verbindungen, deren Passwort nur Matgate kennt (gespeicherte
  Zugangsdaten auf dem Server, die nie aufs Gerät sollen) – Bild als H.264 vom bestehenden `screen-socket`, auf dem
  Gerät per Hardware dekodiert.
- Zurückschreiben nach Matgate (Verbindungen anlegen/ändern mit Bearbeiten-Recht).
- SFTP-/SMB-Dateimanager, weitere Protokolle, iPhone/Android über Avalonia.

## Offene Entscheidungen

- ~~`Mat.Remote` als Submodul oder NuGet-Paket?~~ Submodul.
- Master-Passwort für den Tresor: Pflicht, optional oder gar nicht?
- Import aus welchen Programmen zuerst (RDCMan, mRemoteNG, Royal TS, `.rdp`)?

## Risiken

- Bild schnell genug zeichnen (große Auflösungen, viele kleine Änderungen) – Avalonia `WriteableBitmap` bzw. GPU-Fläche.
- RDP gegen echte Windows-Server: Matgates Engine ist bisher vor allem gegen xrdp geprüft.
- macOS: Signierung, Notarisierung, Freigaben für das Abfangen von Tasten.
- Terminal-Steuerelement für Avalonia.
