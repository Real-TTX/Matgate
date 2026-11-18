# Matgate – Plan zum Weitermachen

Stand: 2026-11-18, Version 0.21.2 (Agent 0.9.0): auf `main`, als `v0.21.2` markiert und auf ve-docker-1
(matgate.schmoldt.it) ausgerollt. Geschrieben als Übergabe an eine andere Sitzung.

## Wo wir stehen

- **0.20.0:** RDP und VNC laufen über Matgates eigene Engine in C#, im Matgate-Container selbst
  (`Matgate/Display/`). Kein `display`-Container mehr. Guacamole bleibt als Unterart „Öffnen als“.
- **0.20.1:** Texteingabe vom Handy neu gebaut (Diktat, Autokorrektur, Android-Komposition), in der
  Bildschirmseite (`Matgate/Agent/AgentScreenPage.cs`: RDP, VNC, Agent-Bildschirm) und in der
  Terminalseite (`Matgate/Agent/AgentTerminalPage.cs`: SSH, Agent-Terminals).
  Prinzip: Das versteckte Textfeld behält seinen Text; zum Server geht nur der Unterschied
  (Rücktasten, dann neuer Text). Das Feld wird nie unter der Tastatur geleert, nur nach 2 s Ruhe.
  Test: `tests/text-input.js` (14 Prüfungen, spielt iPhone-Diktat und Android-Tastatur nach).
- **0.21.0:** Konfigurierbare Tastenleiste (Aufgabe 1, siehe unten) - ein Modell je Benutzer, eine
  Übersetzung in `Matgate/Models/KeyBar.cs`, Konto -> Tastenleiste. Dazu: Verbindungsformular und Ordnerverwaltung neu (Aufgabe 2), und der
  Agent (0.9.0) kennt die Fenster des Rechners und macht aus einem getippten Buchstaben bei gehaltenem
  Strg/Alt/Win ein Tastenkürzel.
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

## Aufgabe 1: Konfigurierbare Tastenleiste - erledigt (0.21.0)

Was es jetzt gibt:

- **Modell:** `MatgateUser.KeyBar` (Liste von `KeyBarButton { Id, Label, Keys, Sticky, Scope }`).
  `null` = Vorgabe (`KeyBar.Defaults()`), leere Liste = keine Leiste. `Scope` = überall / nur Desktop
  (RDP, VNC, Agent-Bildschirm) / nur Terminal (SSH, Agent-Programme).
- **Eine Übersetzung** in `Matgate/Models/KeyBar.cs`: `ForScreen` (Scancode + Keysym; ein Zeichen geht
  als Text `t: 'u'`, damit das Tastaturlayout des Zielrechners entscheidet), `ForTerminal` (Bytes wie
  xterm, je Knopf alle 8 Kombinationen mit haltenden Zusatztasten, dazu die Folge für den
  Anwendungsmodus der Pfeiltasten), `ForGuacamole` (Keysyms).
- **Konto:** Reiter `/account?tab=keys` (Liste, Reihenfolge je einen Platz, Zurücksetzen) und Unterseite
  `/account/keys/new` bzw. `/account/keys/{id}`; Endpunkte in `EndpointMapping.KeyBar.cs`.
- **Seiten:** `data-keys` am `<body>` von Bildschirm- und Terminalseite; in der Hülle `keyBarButtons`,
  dort ist die Leiste die oberste Reihe der Bildschirmtastatur (`buildSessionOsk`).
- **Sonderfälle:** Strg+V liest die Zwischenablage dieses Geräts (`navigator.clipboard.readText`) und
  fügt sie ein, ohne Zugriff geht nur die Taste; Strg+Alt+Entf geht als `screen.sas`.
- **Tests:** `tests/unit/KeyBar.Tests` (57), `tests/key-bar.js` (53 Prüfungen: Konto, VNC, RDP, SSH,
  Guacamole), dazu sieben Prüfungen in `tests/agent-screen.js`.

Offen dazu: am echten iPhone/Android ansehen (vor allem Strg+V - iOS fragt beim Lesen der Zwischenablage
mit einer eigenen "Einfügen"-Blase nach); eine Änderung gilt erst für neu geöffnete Sitzungen.

## Aufgabe 2: Review „fühlt sich nicht gut an“

Der Nutzer will die Verbindungsverwaltung überarbeitet und die Software durchgesehen haben
(„Bugs raus, Feinheiten passen nicht“). Am 2026-11-18 genannt: **Verbindungen anlegen und bearbeiten,
der Dialog ist unübersichtlich; Ordnerverwaltung. Die Startseite ist „relativ hübsch“** (sie ist das
Vorbild für das Aussehen).

### Erledigt am 2026-11-18 (in 0.21.0)

- **Verbindungsformular** (`HtmlViews.ServerFields`, Seiten `ServerCreate`/`ServerDetail`): eine Spalte
  (`.conn-page`, 880 px), Reihenfolge Art → Ziel (Name, Host, Port) → Anmeldung (Benutzer, Passwort,
  Domäne) → Einordnung (Ordner, Sichtbarkeit, Symbol, Aktiv) → „Weitere Einstellungen“ (zugeklappt:
  Öffnen als, Tastaturlayout, Zertifikat, Schriftgröße, Startpfad, Notizen; offen, wenn dort etwas
  gesetzt ist). Das Symbol ist eingeklappt, der Port zeigt die Vorgabe der Art, das Passwortfeld sagt
  „gespeichert“/„keins gespeichert“. Speichern/Zurück/Löschen kleben unten. Die Seite heißt
  „Neue Verbindung“ bzw. „Verbindung“ wie auf der Startseite.
- **Rücksprung:** Von der Startseite geöffnet (`?return=home`) schließt sich der Tab nach Speichern,
  Zurück oder Löschen wieder (`/admin/servers/done` ruft `window.parent.MatgateCloseShellTabOf`), und
  die Startseite liest die Verbindungen neu. Aus der Verwaltung geht es zurück in deren Liste.
- **Ordner:** bleiben der Name, den die Verbindungen tragen (`ServerFolders` in `Matgate/Models`) -
  aber: im Formular eine Auswahl der vorhandenen Ordner plus „Neuer Ordner ...“ statt Freitext; das
  Ordnersymbol gehört dem Ordner (wer dazukommt, übernimmt es). Verwaltung unter
  `/admin?tab=folders` mit Unterseite je Ordner (`/admin/folders/new`, `/admin/folders/edit?name=`):
  Name, Symbol, welche Verbindungen darin liegen; „Ordner auflösen“ lässt die Verbindungen stehen.
  Endpunkte in `EndpointMapping.Folders.cs`. Von der Startseite führt „Ordner verwalten“ dorthin.
- WebDAV ohne Port nahm 3389; jetzt 443.
- Test: `tests/connection-form.js` (38 Prüfungen).

Noch nicht angesehen aus dem Review: Sitzung am Telefon, Sitzung am Desktop, Dateien, der
Quick-Connect-Dialog (sah beim Durchsehen ordentlich aus). Der Nutzer hat nur die oben genannten
Stellen genannt - bei Gelegenheit nachfragen, ob der Umbau trifft, was ihn gestört hat.

### Was sonst noch dazugehört

- ~~Agent-Bildschirm: getippter Buchstabe bei gehaltenem Strg/Alt kam als Zeichen an.~~ Erledigt mit
  Agent 0.8.1 (`InputInjector.Shortcut`: `VkKeyScanEx` im Layout des Vordergrundfensters). Am echten
  Windows-Desktop noch nicht ausprobiert, nur im Testmodus des Agents.
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

## Sitzung am Telefon: Skalierung und Tastatur (Meldung vom 2026-11-18, 0.21.1)

Der Nutzer: „warum habe ich nicht meine 50 % Scale ... wieso muss ich meine Auflösung angeben“, und vor
allem: „wenn die Tastatur aufgeht, ist sie einfach vor dem Bildschirm“ - man kam nicht mehr an das,
was dahinter liegt.

- **Skalierung statt Auflösung** (`AgentScreenPage.cs`): „An Fenster anpassen“ hat einen Maßstab
  (25-300 %, je Verbindung in `matgate.screen.scale.<id>`). 100 % = ein Pixel des Rechners je Punkt der
  Seite, 50 % = doppelt so großer Bildschirm, verkleinert gezeigt, 150 % = kleinerer, vergrößert.
  Vorher hieß „anpassen“ Gerätepixel - am Telefon 1170 x 2532. Vorgabe: was die Verbindung als
  Guacamole-Sitzung hatte (`fit50`), sonst am Telefon 50 %, am Tablet 100 %, am Rechner Gerätepixel
  (wie bisher). Im Dialog der Sitzungsleiste zuerst: Stufen 50-200 %, Regler, „Auflösung des
  Rechners: B x H“; die festen Auflösungen liegen hinter einem Knopf.
- **Mindestgröße** in `ScreenSession.WantedSize`: 320 x 200 statt 640 x 480, und in der Form, die
  gewünscht war (ein hochkant gehaltenes Telefon wurde sonst nur in der Breite angehoben).
- **Tastatur des Geräts:** Eine Sitzung der eigenen Engine ist eine eigene Seite im Tab und erfuhr
  nicht, dass die Tastatur offen ist. Die Hülle sagt es ihr jetzt (`updatePageKeyboardInset` ->
  `window.MatgateKeyboardInset(px)`; allein geöffnet misst die Seite selbst). Die Seite endet über der
  Tastatur: die Tastenleiste sitzt direkt darauf (auch ohne dass man sie einschaltet), das Bild behält
  seine Größe und wird mit zwei Fingern verschoben bzw. folgt dem Zeiger; am Ende der Ansicht wird
  auf dem Rechner gescrollt. Option im Dialog „Auflösung an die Tastatur anpassen“
  (`matgate.screen.keyboard.resize`): dann bekommt der Rechner eine Auflösung, die über die
  Tastatur passt. Die Terminalseite endet ebenfalls über der Tastatur (sie verliert Zeilen, wie ein
  Terminal, dessen Fenster kleiner wird).
- **Tastenleiste (0.21.2):** In der Hülle trägt sie die Farben der App (`--kb-*`, aus dem Thema der Hülle
  gelesen) wie die Bildschirmtastatur dort, und hat keinen Sicherheitsrand mehr unter sich - den zieht die
  Hülle schon ab; vorher war der Leerraum unter den Knöpfen höher als die Knöpfe (Meldung des Nutzers).
- Test: `tests/screen-scale.js` (32 Prüfungen, iPhone-Tastatur nachgestellt über `visualViewport`).
  Falle dabei: Mausrad über dem leeren Test-Desktop lässt openbox die Arbeitsfläche wechseln - das
  Terminal ist dann „weg“ und alle VNC-Tests schlagen fehl; `tests/desktop/start.sh` räumt jetzt auch
  alte X-Sockets weg (nach `docker restart` lag die RDP-Sitzung sonst auf :11 statt :10).
- **Nicht am echten Gerät geprüft** - weder iPhone noch Android. Android meldet die Tastatur über
  `navigator.virtualKeyboard`; ob das in der Hülle ankommt, wenn das Eingabefeld im Tab-Dokument
  liegt, ist offen.
- **Offen, Diktat:** Der Nutzer hat am 2026-11-18 diktierten Text gezeigt, der mehrfach ankam
  (Korrekturen wurden angehängt statt ersetzt). Unklar, ob über Matgate und in welche Sitzung/welches
  Programm - nachfragen. Verdacht für Terminal-Programme mit eigener Eingabezeile (Claude Code):
  viele Rücktasten und der neue Text kommen in einem Stück an und werden dort als Einfügung
  behandelt; dann hülfe, die Rücktasten einzeln mit kleinem Abstand zu schicken.
- Was noch gehen würde: dem Windows-Rechner bei RDP einen Skalierungsfaktor mitgeben
  (DesktopScaleFactor), dann wäre das Bild groß UND scharf; der eigene RDP-Client sendet keinen.

## Eigene Engine: unscharf beim Scrollen (Meldung vom 2026-11-18)

Der Nutzer: „beim Scrollen manchmal sehr unscharf - der ganze Bildschirm, auch Bereiche, die sich nicht
geändert haben“. Ursache: Über Quantisierer 46 mittelte die Notstufe (`Mosaic`) das GANZE Bild in
Blöcke von 4/8/16 Pixeln. Geändert in `Matgate/Display/Video` und `ScreenSession.Tick()`:

- **Mosaik nur im geänderten Bereich.** `I420Frame` merkt sich das Rechteck der Änderungen; der Encoder
  hält `_shown` (was die Bibliothek zuletzt bekam) und mittelt nur dieses Rechteck (auf Makroblöcke
  erweitert). `IsSharp` wird erst wahr, wenn kein Makroblock mehr Mosaik ist.
- **Erst weniger Bilder, dann gröber.** `H264Encoder.SuggestedInterval` / `RateController.Interval`:
  Ab Quantisierer 34 (`Readable`) wartet ein Bild mit viel Neuem bis zu 50 ms (also bis herunter auf
  20 Bilder/s) statt gröber zu werden - nur wenn der Encoder der Bewegung nicht folgt. Kleine
  Änderungen (Zeiger, Tippen) warten nie.
- Gemessen (1920x1080, 12 Mbit/s, erzeugte Textseite, `tests/unit/Video.Tests/ScrollingTests.cs`):
  stehender Text neben einer scrollenden Fläche blieb bei 47,3 dB (vorher bis 18,1 dB); eine Sekunde
  Scrollen 26-36 dB ohne Mosaik (vorher 19-26 dB mit Mosaik). Suite im Container: 118 von 118.
- **Bekannter Preis:** ein gleichmäßiges, schnelles Scrollen von mehr als etwa einer Sekunde ist rund
  3 s lang schlechter als ohne Warten (33 statt 40-43 dB), bis der Encoder wieder im vollen Takt ist.
- **Nicht am echten Windows-Server geprüft.** Der Teststapel (xrdp, kleines xterm) lastet den Encoder
  beim Scrollen nicht aus (0,1-0,5 Mbit/s) - dort ist nur belegt, dass nichts gebrochen ist
  (`tests/display.js` 49 von 49).
- **Der eigentliche Hebel, noch nicht gezogen:** OpenH264 im Modus `ScreenContentRealTime` kostet beim
  Scrollen 5- bis 20-mal weniger Bits bei gleicher Qualität, braucht bei 1080p aber 14-44 ms je Bild
  (statt 8-17 ms). Eine einzige Messung; braucht eine eigene Bewertung (die Vorgabe „Kamera“ war
  gemessen gewählt). Ein ganzer Schirm dichter Text, schnell gescrollt, bleibt bei 12 Mbit/s auch
  jetzt schwer lesbar - dafür bräuchte es diesen Modus oder eine höhere Obergrenze.

## Agent: Fenster des Rechners (Wunsch vom 2026-11-18, erledigt mit Agent 0.9.0)

„Wie eine externe Steuerung“: Der Agent meldet seine Fenster (was Alt+Tab dort anbietet), ein Tipp holt
eines nach vorn, und mit „Nur das gewählte Fenster zeigen“ ist dieses Fenster das ganze Bild und
bleibt es, wenn es verschoben oder in der Größe geändert wird.

- Agent: `Screen/WindowList.cs` (`IWindowList`, echte Liste über `EnumWindows`, Vordergrund über
  `AttachThreadInput`/`SwitchToThisWindow`; `TestWindowList` für den Testmodus), in
  `ScreenSession` die Ereignisse `screen.windows` (Liste) und `screen.window { id, only }`, der
  Ausschnitt `_crop` (`UpdateCrop`/`Fit`): das Bild ist der Platz des Fensters auf dem Monitor, mit
  geraden Seiten; Maus und Zeiger rechnen den Ausschnitt ein. Eine neue Größe gilt erst, wenn sie
  300 ms steht (ein Encoder wird nicht je Bild neu gebaut).
- Matgate: beide Ereignisse stehen auf der Liste dessen, was ein Browser schicken darf
  (`EndpointMapping.AgentScreen.cs`); die Liste selbst gehört der Bildschirmseite (`#windows` in
  `AgentScreenPage.cs`, am Telefon ein Blatt von unten), die Sitzungsleiste hat nur den Knopf.
- Grenzen: Was das Fenster auf dem Rechner verdeckt, verdeckt es auch im Bild (es wird der Platz auf
  dem Monitor gezeigt, kein eigenes Fensterbild). Fenster mit Administratorrechten lassen sich im
  Benutzermodus nicht bedienen (wie bisher). Symbole der Fenster gibt es nicht.
- Tests: 14 Prüfungen in `tests/agent-screen.js`; `matgate-agent screen-probe --windows` listet die
  echten Fenster (Programme und Größen, keine Titel) - auf diesem Rechner 14 Stück.
- Der auf dem Entwicklungsrechner installierte Agent wurde von Hand auf 0.9.0.0 gebracht
  (Aufgabe stoppen, exe tauschen, Aufgabe starten). Am echten Desktop über Matgate noch nicht
  ausprobiert: Fenster nach vorn holen und das Zuschneiden.

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
