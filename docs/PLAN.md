# Matgate – Plan zum Weitermachen

Stand: 2026-11-18, Version 0.20.1. Geschrieben als Übergabe an eine andere Claude-Sitzung.

## Wo wir stehen

- **0.20.0:** RDP und VNC laufen über Matgates eigene Engine in C#, im Matgate-Container selbst
  (`Matgate/Display/`). Kein `display`-Container mehr. Guacamole bleibt als Unterart „Öffnen als“.
- **0.20.1:** Texteingabe vom Handy neu gebaut (Diktat, Autokorrektur, Android-Komposition), in der
  Bildschirmseite (`Matgate/Agent/AgentScreenPage.cs`: RDP, VNC, Agent-Bildschirm) und in der
  Terminalseite (`Matgate/Agent/AgentTerminalPage.cs`: SSH, Agent-Terminals).
  Prinzip: Das versteckte Textfeld behält seinen Text; zum Server geht nur der Unterschied
  (Rücktasten, dann neuer Text). Das Feld wird nie unter der Tastatur geleert, nur nach 2 s Ruhe.
  Test: `tests/text-input.js` (14 Prüfungen, spielt iPhone-Diktat und Android-Tastatur nach).
- **Nicht am echten Gerät geprüft:** iPhone-Diktat, Android (Gboard), ein echter Windows-RDP-Server.
  Alles ist nur gegen den Teststapel geprüft (xrdp, FreeRDP-Server mit NLA, TigerVNC, OpenSSH).

## So wird gearbeitet

- Teststapel: `docker compose -f tests/docker-compose.yml up -d --build`, dann `node tests/seed.js`.
  Er läuft auf `http://127.0.0.1:18091`, Anmeldung steht in `tests/docker-compose.yml`.
  Der Stapel verschwindet gelegentlich (andere Sitzungen räumen auf) – dann neu aufbauen.
- Dev-Stack: `docker compose up --build -d` im Repo-Wurzelverzeichnis (`http://localhost:8088`).
  Nach jeder Codeänderung aktualisieren. Dort liegen echte Verbindungen: Tests nur am Teststapel.
- Einzelner Test: `cd tests && node display.js rdp` (auch `vnc`, `ssh`, `guac`), `node text-input.js`.
  Höchstens rund 6 Skripte hintereinander, sonst greift die Anmeldebremse.
- Unit-Tests der Engine: `dotnet test tests/unit/<Teil>.Tests --artifacts-path <temp>`;
  Video nur aus seinem Ordner: `dotnet test --project Video.Tests.csproj`.
- Kommandozeilen-Client zum Nachsehen: `tests/unit/Rdp.Probe` (mit `MATGATE_RDP_TRACE=1`).
- Regeln: Bezeichner und Kommentare englisch, Oberflächentexte deutsch/englisch, Commit-Texte
  deutsch in ASCII-Umschrift, kein Hinweis auf Claude im Commit. Nach Versionssprung Tag
  `v<version>` pushen. Keine Browser-Dialoge (`confirm`/`prompt`), eigene Dialoge benutzen.
  In `$$"""`-Strings (HtmlViews.cs) kein `{{` oder `}}` im JavaScript.
- Patches an den großen C#-Dateien mit eingebettetem JavaScript: Node-Skript mit exakten Ankern
  (Beispiele liegen nicht im Repo; Muster: Datei lesen, `split(anchor).length === 2` prüfen, ersetzen).

## Aufgabe 1: Konfigurierbare Tastenleiste (vom Nutzer gewünscht)

Wunsch: Auf iOS und Android eigene Knöpfe einblenden – Strg, Alt, Strg+C, Strg+V und so weiter –,
und alles soll einstellbar sein.

Was es heute gibt (drei getrennte, fest verdrahtete Leisten):

| Wo | Datei | Form |
|---|---|---|
| Bildschirmseite (RDP, VNC, Agent) | `AgentScreenPage.cs`, `<div id="keys">` | `data-key="<Scancode hex>[e]"`, `data-sticky` für Strg/Alt |
| Terminalseite (SSH, Agent) | `AgentTerminalPage.cs`, `<div class="keys" id="keys">` | `data-send="<Bytes hex>"` |
| Guacamole-Sitzungen | `HtmlViews.cs`, `buildSessionOsk` (ab etwa Zeile 7227) | Keysyms über `client.sendKeyEvent` |

Vorschlag:

1. **Ein Modell** je Benutzer: Liste von Knöpfen `{ label, keys: ["Ctrl","C"], sticky: bool }`,
   gespeichert an `MatgateUser` (neues Feld, z. B. `KeyBar`), mit einer Vorgabe
   (Esc, Tab, Strg, Alt, Win, Entf, Pfeile, Strg+C, Strg+V, Strg+Alt+Entf).
   `sticky` = bleibt für die nächste Taste gedrückt (wie heute Strg/Alt).
2. **Eine Übersetzung** der Tastennamen in drei Ziele:
   - Scancodes für die Bildschirmseite (Tabellen `SCAN`/`EXTENDED` stehen dort schon),
   - Keysyms für Guacamole,
   - Bytes fürs Terminal (Strg+Buchstabe = Steuerzeichen, Pfeile = Escape-Folgen, Alt = ESC davor).
3. **Einstellungsseite** im Konto (`/account`), als Liste mit Unterseite je Knopf (UI-Regel: CRUD =
   Liste + Unterseiten), Reihenfolge änderbar, „Auf Vorgabe zurücksetzen“.
   Strg+V ist ein Sonderfall: In der Bildschirmseite soll er die Zwischenablage dieses Geräts
   einfügen (`window.MatgateScreen.pasteText` bzw. der vorhandene Einfügen-Weg), nicht nur die Taste senden.
4. **Ausliefern:** Die Seiten bekommen die Liste als JSON im `data-`Attribut und bauen die Leiste
   daraus; die festen Knöpfe im HTML entfallen. In der Hülle (Guacamole) ersetzt die Liste die obere
   Reihe der Bildschirmtastatur.
5. **Wichtig für die Texteingabe:** Ein Knopf der Leiste ist eine Grenze – danach `textBoundary()`
   (Bildschirmseite) bzw. `boundary()` (Terminalseite) aufrufen, wie es die heutigen Knöpfe tun.
   Sticky-Tasten lösen sich nach dem nächsten getippten Zeichen (`typeText` macht das schon).
6. **Test:** neues `tests/key-bar.js` – Knopf anlegen, in RDP/VNC/SSH drücken, Wirkung auf dem
   Test-Desktop lesen (Muster in `tests/text-input.js`).

## Aufgabe 2: Review „fühlt sich nicht gut an“ (vom Nutzer gewünscht, noch unscharf)

Der Nutzer will die Verbindungsverwaltung überarbeitet und die Software durchgesehen haben
(„Bugs raus, Feinheiten passen nicht“). **Zuerst nachfragen, was konkret stört** – drei bis fünf
Beispiele mit Gerät und Verbindungsart reichen, sonst wird es Raten. Was beim letzten Umbau
aufgefallen ist und sicher dazugehört:

- **Agent-Bildschirm:** getippter Buchstabe bei gehaltenem Strg/Alt aus der Leiste kommt als Zeichen
  an, nicht als Tastenkürzel. Für RDP ist das gelöst (`RdpBackend.KeyOf`), für VNC wirkt es von
  selbst; im Agent (`agent/Matgate.Agent/Screen`, Eingabe `t: 'u'`) fehlt es.
- **Guacamole-Sitzungen** haben eine eigene, ältere Fassung der Text-Differenz (`HtmlViews.cs`, um
  Zeile 10700, `oskInput`) ohne Platzhalter für die Rücktaste. Auf denselben Stand bringen.
- **RDP, eigene Engine – was fehlt:** Laufwerk in der Sitzung, Terminalserver-Lizenzen, Kerberos,
  Weiterleitung durch einen Verbindungsbroker. Bis dahin Guacamole als Unterart.
- **NLA-Testserver (Port 3390)** zeigt Streifen im Bild. Vermutlich sein eigener Encoder (FreeRDPs
  Decoder macht dasselbe Bild), aber nicht abschließend geprüft.
- **`docker-compose.simple.yml`** braucht den Caddy (`edge`) nur noch fürs Komprimieren. Mit
  Kompression in Matgate selbst wäre es ein einziger Container.
- **ve-docker-1** läuft noch auf einer alten Version; ein Update braucht dort keinen neuen Dienst mehr.

Vorgehen für das Review: Bereich für Bereich mit dem Nutzer (Startseite/Tabs, Verbindung anlegen und
bearbeiten, Sitzung am Telefon, Sitzung am Desktop, Dateien), je Bereich Befunde sammeln, dann
gebündelt beheben und mit einem Skript unter `tests/` absichern.

## Aufgabe 3: Agent für Linux und macOS (Frage des Nutzers)

Heute gibt es den Agent nur für Windows, weil fast alles an Windows-Schnittstellen hängt: Terminals
(ConPTY), Bildschirm (Desktop Duplication, Media Foundation), Eingaben (SendInput), Dienst und
Sitzungswechsel, Tray und Einrichtung (WinForms).

Plattformneutral und wiederverwendbar: Verbindung und Protokoll (`Connection`, `AgentWire`),
Dateien, Updates – und seit 0.20.0 der H.264-Weg über OpenH264 (`Matgate/Display/Video`).

Neu je Plattform:

| Teil | Linux | macOS |
|---|---|---|
| Terminals | `forkpty` | `forkpty` |
| Bildschirm | X11 (XShm/XDamage), Wayland nur über PipeWire-Portal | ScreenCaptureKit, braucht Freigabe |
| Eingaben | XTest bzw. uinput | CGEvent, braucht Bedienungshilfen-Freigabe |
| Dienst | systemd-Unit | launchd |
| Oberfläche (Tray, Einrichtung) | Avalonia | Avalonia |

Für die Oberfläche ist Avalonia eine passende Grundlage; ob ThixClip als Vorlage taugt, hat diese
Sitzung nicht angesehen (anderes Repository). Sinnvolle Reihenfolge: erst Agent ohne Oberfläche für
Linux mit Dateien und Terminals (wenig Risiko), dann Bildschirm unter X11, dann Oberfläche und macOS.

## Offene Prüfungen am echten Gerät

- iPhone: Diktat in RDP/VNC und im Terminal – kommt jeder Satz einmal an, auch nach Korrekturen?
- Android (Gboard): Tippen mit Wortvorschlägen, Wischen, Diktat.
- Windows-Server über die eigene Engine: Anmeldung (NLA), Bild, Ton, Dateien per Strg+V in den Explorer.
