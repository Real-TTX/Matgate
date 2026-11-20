# Matgate – Plan zum Weitermachen

Stand: 2026-11-20, Version 0.29.2 auf `main` (`v0.29.2`), Agent 0.14.0; auf ve-docker-1
(matgate.schmoldt.it) läuft seit dem 2026-11-20 ebenfalls 0.29.2 (0.29.1 lief dort schon ab 02:19 UTC am
selben Tag, nicht von dieser Sitzung eingespielt; Websites dort weiter über den Pfad, bis
Route und Variable stehen; Einspielen trennt laufende Sitzungen, nur auf ausdrücklichen Wunsch). Der Agent
auf dem Entwicklungsrechner ist der offizielle Build aus dem Release `agent-latest`. Geschrieben als Übergabe an eine andere Sitzung.

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
- **ve-docker-1** (matgate.schmoldt.it) läuft auf 0.22.0; Ablauf des Einspielens steht im Gedächtnis
  (`production-deploy`).

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
- **Vollbild am Telefon (0.21.3):** Der Nutzer kam in der installierten App nicht mehr an die Leiste. Bei
  einer Sitzung der eigenen Engine erreichen Berührungen die Hülle nicht (eigenes Dokument im Tab): drei
  Finger ins Bild getippt holen die Leiste (`toggleControls` -> `window.parent.MatgateToggleControls`), der
  Streifen oben ist für Finger höher, und beim ersten Verschwinden der Leiste sagt ein Hinweis, wie sie
  zurückkommt (`#immersive-hint`).
- **Zahnrad am Ende der Tastenleiste (0.21.3):** führt zu Konto -> Tastenleiste - der Nutzer fragte nach
  einer einstellbaren Leiste, obwohl es sie schon gab.
- **Leistungsanzeige im Bild (0.21.4, Wunsch des Nutzers):** Option im Auflösungsdialog
  (`matgate.screen.perf`, je Gerät): oben links Bilder/s gesendet/gezeigt, Laufzeit, Datenrate, darunter
  Bildgröße, Maßstab, Encoder, Zeit je Bild (`#perf`, `paintPerf`). Für RDP, VNC und Agent-Bildschirm;
  Guacamole-Sitzungen haben keine solchen Zahlen.
- Test: `tests/screen-scale.js` (47 Prüfungen, iPhone-Tastatur nachgestellt über `visualViewport`).
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

## Sitzungen, die den Browser überdauern (0.22.0, Wunsch vom 2026-11-18)

Der Nutzer: „Können wir serverseitige Sitzungen einbauen? Wenn man den Browser lädt bzw. neu lädt, dass
dann alles immer wieder da ist - als Option.“, bestätigt mit „Sitzung pro User, Dauer einstellbar“.

- **Option je Benutzer:** Konto -> Sitzung, „Weiterlaufen lassen“ (`SessionPreferences.KeepSessionMinutes`:
  aus, 5/15/30 Minuten, 1/2/8/24 Stunden). Aus ist der Vorgabewert - dann ist alles wie vorher.
- **Was weiterläuft:** RDP und VNC (`DisplayBridge.Held` / `HeldScreen`, `ScreenSession.Attach/Detach`)
  und SSH (`SshShell` in `EndpointMapping.Ssh.cs`, mit 256 KB der letzten Ausgabe) der eigenen Engine.
  Ohne Betrachter läuft die Sitzung die eingestellte Zeit weiter (der Encoder ruht), dann endet sie.
  Höchstens 16 gehaltene Sitzungen je Benutzer und Art; darüber endet eine neue mit ihrem Browser.
  Nicht: Guacamole-Sitzungen (verbinden neu wie bisher), Agent-Bildschirm (ist der Desktop selbst),
  Agent-Terminals (liefen schon immer auf dem Agent weiter). Ein Neustart von Matgate beendet alle.
- **Wiederfinden:** Die Sitzungskennung steht in der Adresse der Seite (`?session=`); die Seite, die
  wiederkommt, bekommt dieselbe Sitzung (`screen.ready` mit `attached`, beim Terminal `ready` mit
  `attached` und der letzten Ausgabe). Ein Betrachter zur Zeit: der nächste nimmt sie, der vorige
  bekommt „in einem anderen Fenster geöffnet“.
- **Tabs auf dem Server:** `ShellStateStore` (`<data>/shell-state/<Benutzer>.json`), `PUT
  /api/shell-state`. Die Hülle legt die drei Werte ihres `localStorage` dort ab und nimmt beim Laden die
  des Servers. Ganze Liste, der letzte Schreiber gewinnt.
- **Ende:** Ein geschlossener Tab beendet seine Sitzung sofort (`POST /api/kept-sessions/end`,
  `endKeptSession` in `closeShellTab`). Option aus: der Server vergisst die Tabs.
- **Test:** `tests/kept-sessions.js` (29 Prüfungen): gleiche Shell nach Neuladen, kein zweiter
  VNC-Verbindungsaufbau, Netz weg und wieder da, zweiter Browser übernimmt Tab und Sitzung, Tab
  schließen, Ablauf der Zeit. Der Teststapel verkürzt die Zeit mit `MATGATE_KEEP_SESSION_SECONDS` (nur
  dafür gedacht; schaltet die Option für niemanden ein).
- **Bekannte Grenzen:** (1) Zwei offene Browser schreiben beide die ganze Tab-Liste: ein Browser, der
  einen andernorts geschlossenen Tab noch zeigt, bringt ihn mit seiner nächsten Änderung zurück (dann
  als neue Verbindung). (2) Abmelden beendet gehaltene Sitzungen nicht - sie laufen aus oder enden mit
  ihrem Tab. (3) Die Dauer gilt ab dem Abreißen und wird beim Verbindungsaufbau der Seite gelesen; eine
  geänderte Dauer gilt für Seiten, die danach verbinden. (4) Nicht am echten Windows-Server und nicht
  am echten Telefon geprüft (RDP im Teststapel ist xrdp).

## Websites unter eigener Adresse (0.23.0, Wunsch vom 2026-11-18)

Der Nutzer: „Kannst du das Website-Feature mit dem Proxy nochmal überarbeiten und besser machen“, dazu
„bei Quick-Connect mit Website einen Browser ohne URL starten, wo ich einfach surfen kann über den Proxy“.

- **Das Grundproblem des alten Wegs** (`WebsiteProxyService`, Pfad `/website/<id>/<tab>/proxy/`): Eine
  Oberfläche, die ihre eigene Adresse auswertet oder nach `/login` an der Wurzel schickt (UniFi, SLZB),
  kann unter einem Pfad nicht laufen - `location` lässt sich nicht fälschen. Der Weg bleibt als Rückfall.
- **Neu: eigene Hostnamen** (`WebsiteSiteService`, `EndpointMapping.WebsiteSites.cs`). Mit
  `MATGATE_WEBSITE_HOST=*.web.example.com` bekommt jede Website `<Label>.web…`; das Label (20 Zeichen
  a-z2-7) ist ein Hash aus Benutzer, Verbindung und Ziel-Origin, also stabil (der Browser behält
  Anmeldung und Einstellungen der Seite). Die Middleware `UseWebsiteSites` beantwortet solche Hosts
  ganz, vor Anmeldung und Routing. Nichts wird umgeschrieben außer: `Location`, `Set-Cookie`
  (Domain weg), CSP ohne `frame-ancestors`, absolute Adressen des echten Hosts in Link-Attributen,
  und ein kleines Skript `/__matgate/site.js` in ganzen Seiten.
- **Zutritt:** Die Hülle holt per `POST /api/website/{id}/site` eine Adresse mit Einmal-Ticket
  (`/__matgate/enter?t=…&to=…`, 60 s); die setzt das Cookie `Matgate-Site` nur für diesen Host
  (HttpOnly, wird nie an die Website weitergegeben, kann von ihr nicht gesetzt werden). Je Anfrage wird
  geprüft, dass Benutzer und Verbindung noch gelten. Abmelden (`ForgetUser`), Löschen/Ändern der
  Verbindung (`ForgetServer`, Signatur) schließen die Adresse.
- **Hülle:** Der Rahmen ist dann ein fremder Origin (sandbox ohne Top-Navigation). Das Seitenskript
  meldet Adresse und Titel per `postMessage` und nimmt Zurück/Vor/Neu laden/Gehe-zu entgegen; die
  Adressleiste ist editierbar. Meldet die Seite „kein Zutritt“, holt die Hülle einmal eine neue Adresse
  und fällt danach auf den Pfad-Weg zurück (Browser behält das Cookie nicht: Matgate und Website-Host
  sind nicht dieselbe Site).
- **Andere Adressen:** derselbe Rechner unter anderem Schema/Port (http→https) bekommt einen eigenen
  Hostnamen und wird per Ticket betreten (`Location`, Links über `/__matgate/go?u=`). Fremde Hosts:
  bei einer Verbindung öffnet der Link einen eigenen Browser-Tab; in einem **Browser-Tab**
  (`ServerEndpoint.WebsiteBrowse`, nur Quick-Connect, nur im Speicher) läuft alles über Matgate weiter.
- **Der Browser im Quick-Connect (0.26.0):** Der Chip „HTTP / HTTPS“ heißt **„Browser“** und ist immer
  Matgates eigener (die Seite holt Matgate und zeigt sie im Tab) - nie mehr das Bild aus der Browser-Farm;
  die bleibt gespeicherten Verbindungen mit „via Chromium/Firefox VNC“. Der Dialog fragt nach der
  **Start-URL**. Mit eigenen Hostnamen darf sie leer bleiben: leerer Tab mit Adressleiste, jede Adresse,
  Links laufen über Matgate weiter. Ohne Hostnamen (Pfad-Weg) ist sie Pflicht - dort zeigt ein Tab genau
  eine Website, die Adressleiste ist nur Anzeige, Links zu anderen Rechnern verlassen Matgate (leere
  Anfrage: 400 mit Klartext) - **überholt durch 0.27.0, siehe den nächsten Punkt**. Damals bewusst nicht gebaut: freies Surfen unter dem Pfad - die Seiten liefen
  dann unter Matgates eigener Adresse und könnten als der angemeldete Benutzer mit Matgate reden; dafür
  sind die Hostnamen da. 0.25.1 hatte dafür kurz einen eigenen Chip `browser` vorn in der Reihe mit
  Farm-Rückfall; der Nutzer wollte den eingebauten Browser mit Start-URL und fragte, warum er vorn steht.
  Tests: `tests/browser-under-path.js` gegen ein zweites Matgate ohne Hostnamen im Teststapel
  (`matgate-plain`, Port 18092), `tests/website.js` mit Hostnamen.
- **Surfen unter dem Pfad (0.27.0):** Der Nutzer fand den Browser auf dem Homelab nicht und wollte „einfach
  im Netzwerk browsen“ - ohne Route im Matcad (seine Wahl, 2026-11-19). Seitdem ist ein Browser-Tab auch
  ohne Hostnamen ein Browser: Die Adressleiste nimmt jede Adresse (auch ohne `http://` - Matgate probiert
  https, sonst http: `WebsiteProxyService.WithSchemeAsync`), die Start-URL darf leer bleiben, und Links,
  Formulare und Weiterleitungen zu **anderen Rechnern im Netz** laufen über Matgate weiter. Dafür bekommt
  jede Website im Tab einen **eigenen Rahmen unter dem Pfad** (`/website/<Verbindung>/<Rahmen>/proxy/`;
  Rahmen = Tab-ID für die Start-Website, sonst aus Tab und Origin abgeleitet: `WebsiteProxyService.Browse`,
  `_browseTargets`) - Cookies hängen am Pfad, also bekommt keine Website die einer anderen. Der Sprung geht
  über `/website/<id>/<tab>/go?u=…` (`WebsiteGoAsync`): nur für Browser-Tabs, nur zu Adressen, die auf
  private Netze auflösen (`IsInsideNetworkAsync`); was draußen liegt, zeigt eine kleine Seite mit „In
  meinem Browser öffnen“. Getippte Adressen sind nicht beschränkt (wie die Start-URL schon immer). Im
  Seitenskript: Klick-, Formular- und Navigation-API-Haken (`hopUrl`); `location.assign` ist in Chromium
  nicht patchbar, deshalb die Navigation API - **Safari vor 26.2 hat sie nicht**, dort fängt Matgate nur
  Klicks, Formulare und Weiterleitungen. **Grenzen bleiben:** Die Seiten laufen unter Matgates eigener
  Adresse (schwächere Trennung als mit Hostnamen), und Oberflächen wie UniFi, die ihre eigene Adresse
  auswerten, gehen unter einem Pfad weiterhin nicht - dafür bleiben die Hostnamen (eine Route im Matcad).
  Nebenbei behoben: Die Seite meldete der Hülle ihren Ort nie (`window.name` kam leer an) - die Tab-ID
  kommt jetzt aus der Sitzung; Proxy-Sitzungen wurden nie aufgeräumt (jetzt nach 13 h ohne Zugriff).
  **Chips am Telefon:** Die Reihe „Schnell verbinden“ scrollte seitwärts ohne sichtbaren Hinweis, der
  Browser stand als sechster Chip außerhalb des Bildschirms. Jetzt bricht die Reihe um, am Telefon als
  kleine Kacheln (fünf je Reihe). Test: `tests/browser-under-path.js`.
- **Browser ohne Grenze, Chip ohne Dialog (0.27.1):** 0.27.0 ließ eine Seite den Tab nur zu Rechnern im
  privaten Netz schicken und zeigte sonst eine Zwischenseite („Outside the network“). Auf dem Homelab traf
  das sofort die eigenen Namen (`*.schmoldt.it`, `*.rltx.de` lösen im Container auf die öffentliche
  Adresse auf), und der Nutzer wollte es ausdrücklich nicht („Es muss doch alles tun“, „kann dann nicht
  frei surfen“). Die Prüfung und die Zwischenseite sind wieder weg: `WebsiteGoAsync` folgt jeder Adresse.
  **Der Preis ist bekannt und dem Nutzer gesagt:** Unter dem Pfad läuft jede Seite unter Matgates eigener
  Adresse und könnte, wenn sie es gezielt darauf anlegt, als der angemeldete Benutzer mit Matgate reden
  (der Rahmen ist dieselbe Origin wie die Hülle - dagegen hilft kein Header und keine CSP, nur eine andere
  Origin: eigene Hostnamen, oder ein noch nicht gebauter Sandkasten-Weg mit undurchsichtiger Origin und
  Zutritt per Pfad-Token). Außerdem öffnet der **Chip „Browser“** den Browser jetzt sofort (leerer Tab,
  Adressleiste im Fokus) statt den Dialog - der Nutzer: „muss eine URL eingeben?“; Start-URL und
  Zugangsdaten bleiben im Dialog unter „Mehr“. Am Telefon ist die Browser-Leiste eine Zeile statt drei.
- **Wurzel-Modus unter dem Pfad (0.28.0) - UniFi lädt:** Der Nutzer: „10.10.0.1 geht immer noch nicht.
  denk dir was aus ... man kann doch auch ein script injecten“. Die Adresse einer Seite lässt sich nicht
  fälschen, aber **ändern**: innerhalb einer Origin darf eine Seite ihren Pfad per `history.replaceState`
  setzen. Das Bootstrap-Skript gibt der Seite also als Erstes den Pfad, den sie auf ihrer Website hat -
  `location.pathname` sagt dann `/login` statt `/website/<...>/proxy/login`, und Anwendungen, die ihre
  Route aus der eigenen Adresse lesen, finden sich zurecht (`RootMode` der Proxy-Sitzung, `pretending` im
  Skript). Was dazugehört: `rewriteUrl` löst im Wurzel-Modus auch relative Adressen auf und legt alles, was
  auf Matgates Origin zeigt, unter den Proxy-Pfad; `pushState/replaceState` behalten Website-Pfade;
  die Seite meldet der Hülle ihre echte Adresse (`__matgateRealUrl`); ein Sprung auf einen Wurzelpfad
  (`location.href = '/login?...'`, Neu laden) wird über die Navigation API in den Proxy umgelenkt, und wo
  ein Browser die nicht hat (Safari vor 26.2), fängt die Hülle den Rahmen wieder ein (`rescueWebsiteFrame`);
  `document.cookie` liest die Cookies der Website über eine leere Seite unter dem Proxy-Pfad
  (`?__matgate=cookies`), bis die geladen ist aus einem Abbild vom Seitenstart. **Browser-Tabs starten im
  Wurzel-Modus; gespeicherte Verbindungen wechseln von selbst hinein, sobald ihre Seite auf einen
  Wurzelpfad springt** (`POST .../state?root=1`). Geprüft: `tests/browser-under-path.js` und
  `tests/website.js` gegen die UniFi-artige Testanwendung `/app`; und **echt über den Dev-Stack, ohne
  Anmeldung:** UniFi OS 10.10.0.1 (Anmeldeseite vollständig, Adresse `https://10.10.0.1/login`, keine
  fehlgeleitete Anfrage), Synology DSM 10.10.1.1:5001 und Proxmox 10.10.2.11:8006 (Anmeldeseiten ohne
  Fehler). **Ungeprüft:** alles hinter der Anmeldung (UniFi Network, WebSockets dort), iOS. Bekannte
  Lücken: `<base href>` liest eine Anwendung weiter mit Proxy-Pfad; Anfragen, die an den Haken vorbeigehen
  (dynamisches `import()`, `url()` in zur Laufzeit gesetztem CSS), landen auf Matgates Wurzel - dafür wäre
  der Service Worker der richtige Ort (Zuordnung je Client), noch nicht gebaut.
- **Platz für die Tastatur blieb stehen (0.27.3):** Der Nutzer: beim Wechseln am Handy sei manchmal „die
  iOS-Tastatur zu, aber der Platz für die Sondertasten und Tastatur noch reserviert“. Ursache in der Hülle:
  `updatePageKeyboardInset` sagte nur dem Tab **vorn**, was die Tastatur verdeckt - ein Tab, der mit offener
  Tastatur verlassen wurde, behielt seinen Platz; und der Platz hing allein an dem, was das iPhone über den
  Viewport meldet (als installierte App meldet es das Schließen nicht immer). Jetzt: Platz gibt es nur,
  solange ein Feld den Cursor hat (`keyboardHolder`, auch in Rahmen); alle Seiten-Tabs werden informiert
  (die hinten mit 0); beim Tab-Wechsel und beim Verlassen der App lässt das Feld los (nur Touch); nach
  Wechsel/Rückkehr wird mehrfach nachgemessen, und solange Platz reserviert ist, alle 500 ms. Test in
  `tests/screen-scale.js` (Viewport bleibt „geschrumpft“, Feld lässt los / Tab wechselt). **Nicht am
  echten iPhone geprüft.**
- **Zoom am iPhone (0.27.2):** Der Nutzer meldete nach 0.27.0 erneut, dass Quick-Connect am Handy „komisch
  zoomt“ - die 16px-Felder aus 0.25.1 haben das auf seinem Gerät also nicht beendet. Zwei Dinge dagegen:
  Auf iOS bekommt der Viewport per Skript im Kopf `maximum-scale=1` (dort kann die Seite beim Fokus nicht
  mehr hineinzoomen, Kneifen geht weiter; auf Android bleibt der Viewport, weil die Zeile dort das Kneifen
  nähme), und der Quick-Connect-Dialog setzt auf Touch-Geräten den Cursor nicht mehr ins erste Feld (keine
  Tastatur beim Öffnen). **Nicht am echten iPhone geprüft** - `tests/phone-field-zoom.js` prüft nur, dass
  beides gesetzt ist.
- **Chips sortierbar (0.26.0):** Konto → Sitzung → „Schnell verbinden“ ist jetzt dieselbe Ziehliste wie die
  Aktionen (`data-order-group`): Reihenfolge und Haken. `SessionPreferences.QuickProtocolOrder` +
  `HiddenQuickProtocols`, `OrderedQuickProtocols` hängt Unbekanntes hinten an. Startseite und Dialog
  folgen; der Dialog beginnt auf dem ersten Chip. Test: `tests/quick-connect-order.js`.
- **Rechtsklick (0.26.0):** Ein Menü am Zeiger für Verbindungskarten (was die Knöpfe der Karte tun, dazu
  „In neuem Browser-Tab öffnen“ und „Adresse kopieren“), Tabs (Neu laden bzw. eigenes Fenster, Schließen,
  Andere schließen mit eigener Rückfrage), Chips (verbinden, Reihenfolge ändern) und Dateizeilen (die
  Aktionen der Zeile). Es ist das vorhandene `MatgateOpenMenu` mit `at: { x, y }` - am Telefon also ein
  Blatt. Karten- und Zeilenmenü werden aus den vorhandenen Knöpfen gelesen und klicken sie, damit es nur
  eine Stelle gibt, die sagt, was sie tun. Nicht in Feldern, nicht in Sitzungen, mit Umschalt nie (der
  Weg zum Menü des Browsers). Am Telefon nur, wo der Browser aus dem langen Druck ein `contextmenu`
  macht (Android) - **ein iPhone tut das nicht**, dort bleiben die sichtbaren Knöpfe. Test:
  `tests/context-menu.js`.
- **Felder am Telefon (0.25.1):** iOS zoomt in jedes Feld, dessen Schrift kleiner als 16px ist, und bleibt
  gezoomt - Quick-Connect tat das schon beim Öffnen, weil es den Cursor ins erste Feld setzt (13px). Am
  Ende des Stylesheets steht jetzt für `(pointer: coarse)` eine Regel für alle Felder
  (`max(16px, 1em) !important`); mit der Maus bleibt alles, wie es war. Test: `tests/phone-field-zoom.js`
  (prüft die Ursache, kein Browser im Test zoomt wie ein iPhone - **am echten iPhone ungeprüft**).
- **Anmeldung für die Seite:** Das Formular hat jetzt Benutzer/Passwort auch für Websites. Sie gehen
  bei `401 Basic` mit; bei Proxmox (`pvemanagerlib.js`/`proxmox-backup-gui.js` in der Seite) holt
  Matgate das Ticket und setzt `PVEAuthCookie`/`PBSAuthCookie` - seit **0.25.2** einmal **je Browser**
  (Merker-Cookie `Matgate-SignIn` mit dem Zeichen der Website-Instanz, erreicht die Website nie). Davor
  galt der eine Versuch für alle Browser bis zum Neustart: wer Proxmox erst am Rechner und dann am
  Telefon öffnete, stand dort vor dem Anmeldedialog. Wer sich in Proxmox abmeldet, bleibt abgemeldet.
- **Adresse wie geschrieben:** Gespeichert wird ohne angehängten Schrägstrich (`CleanWebsiteUrl`);
  der Pfad-Weg normalisiert weiter selbst.
- **Verbindung:** Haken „Unter Matgates eigener Adresse öffnen“ (`WebsitePathMode`) erzwingt den alten Weg.
- **Test:** `tests/website.js` (61 Prüfungen) gegen `tests/site/server.js` (Dienst `site`/`other` im
  Teststapel, `MATGATE_WEBSITE_HOST=*.mg.localhost`, Matgate als `http://mg.localhost:18091`).
- **Nicht geprüft / offen:** echte Geräte (UniFi, Synology, Proxmox samt Konsole/noVNC, SLZB) - nur
  die Testseite; der Farm-Browser ohne Adresse (keine Farm im Teststapel); Safari/iPhone. Auf
  ve-docker-1 fehlen die Route im Matcad (Wildcard `*.schmoldt.it` und Zertifikat sind da, jede
  unbekannte Subdomain antwortet 503 „no upstream“) und die Variable im Stack - Entscheidung des Nutzers.
- **Bekannte Grenzen:** (1) Unter-Ressourcen fremder Hosts lädt der Browser direkt, nicht über Matgate
  (auch im Browser-Tab). (2) Skripte, die mit einer vollen Adresse des echten Hosts *navigieren*
  (`location.href = 'http://gerät/…'`), verlassen die Website; `fetch`/XHR/WebSocket/Links sind
  abgedeckt. (3) Website-Hosts sind dieselbe Site wie Matgate: eine bösartige Seite könnte per Skript ein
  Cookie für die gemeinsame Domain setzen. Matgates Anmelde-Cookie mit `__Host-`-Präfix wäre die
  Antwort darauf (meldet alle einmal ab) - nicht gemacht. (4) Der alte Pfad-Weg ist unverändert, mit
  allen bekannten Schwächen. (5) `Clear-Site-Data` der Website wird verworfen (träfe Matgates Cookie).

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

## Agent: Logo, Startseite, Mikrofon, Controller (0.24.0 / Agent 0.10.0, Wunsch vom 2026-11-18)

Der Nutzer: „Ich möchte, dass man ein Logo im Agent hinterlegen kann, das dann auf der Home-Seite des
Agents angezeigt wird. Das UI vom Agent (wenn man die Verbindung aufmacht) muss deutlich schöner werden,
UI/UX-Fokus. Auch brauchen wir Mikrofon- und Controller-Support.“

- **Logo:** `AgentConfig.Logo` bzw. `logo.<png|jpg|webp|gif|svg>` neben der Konfiguration
  (`agent/Matgate.Agent/AgentLogo.cs`, höchstens 1 MB, wird je Anfrage gesucht - kein Neustart). Setup:
  Seite „Was Matgate sehen darf“ (Auswählen/Entfernen, Vorschau außer SVG/WebP), `setup --logo|--no-logo`.
  Draht: `logo.stat` / `logo.read`. Matgate: `status.logo` und `GET /api/agent/{id}/logo?v=<Stempel>`
  mit `nosniff` und CSP-`sandbox` (ein SVG darf unter Matgates Adresse kein Skript ausführen).
- **Startseite** (`renderAgentHome` in `HtmlViews.cs`, Klassen `agent-hero`, `agent-features`,
  `agent-chip`, `agent-badge`): Kopfkarte mit Logo/Icon, Name, Fakten und Zustand; laufende Sitzungen
  darüber, „Fortsetzen“ als Hauptknopf; Bildschirm und Dateien als zwei große Karten (Bildschirm mit dem,
  was er kann; Dateien mit je einem Chip pro freigegebenem Ordner, der direkt dorthin führt); Programme
  als Karten; Gerüst statt Text beim Laden; Telefon einspaltig, Name in eigener Zeile.
- **Mikrofon:** Browser (`getUserMedia`, AudioWorklet, WebCodecs-Opus oder PCM, 20 ms, 48 kHz mono) →
  `screen.mic` → Agent `Screen/Microphone.cs`: `MicrophonePlayer` spielt per WASAPI in das
  Wiedergabe-Ende eines virtuellen Audiokabels (automatisch erkannt: CABLE Input, VoiceMeeter, Steam
  Streaming Microphone …; `Microphone` in der Konfiguration: Gerätename, `speakers`, `off`). Ohne Kabel
  `microphone: "nodevice"` - der Knopf sagt, was fehlt.
- **Controller:** Browser (Gamepad-API, Standardbelegung, je Bild abgefragt, nur Änderungen) →
  `screen.pad` → Agent `Screen/Gamepads.cs`: `ViGEmGamepads` spricht den Treiber ViGEmBus direkt per
  IOCTL (Schnittstelle aus dessen `BusShared.h`), bis zu vier Xbox-360-Controller, Vibration zurück als
  `screen.pad.rumble`. Verliert die Seite den Fokus, werden Sticks und Tasten losgelassen.
- **Was der Rechner kann**, steht im Hello (`microphone`, `gamepad` am Bildschirm-Plugin) und in der
  Antwort auf `screen.open` (`"ok"`, `"nodevice"`/`"nodriver"`, `"off"`).
- **Prüfen auf einem Rechner:** `matgate-agent screen-probe --microphone [Einstellung] [--tone]` und
  `screen-probe --gamepad [--plug]`.
- **Test:** `tests/agent-devices.js` (33 Prüfungen) im Testmodus des Agents: gefälschtes Mikrofon von
  Chromium, ein Ersatz für `navigator.getGamepads()`, der Agent meldet statt zu spielen/einzustecken; der
  Test-Controller „vibriert“, indem er seine Trigger als Motoren zurückgibt.
- **Nicht geprüft:** der echte ViGEmBus-Weg (auf dem Entwicklungsrechner ist kein Treiber; nur
  „nicht installiert“ wird erkannt) und ein echtes virtuelles Audiokabel (Wiedergabe in ein Gerät geprüft
  mit `screen-probe --microphone speakers`: öffnet und nimmt Ton). Safari/iPhone (AudioEncoder, Gamepad).
- **Offen / Entscheidung des Nutzers:** Soll das Setup die Treiber mitbringen oder herunterladen?
  Vorher Lizenz und Stand beider prüfen (ViGEmBus: frei verfügbar, wird meines Wissens nicht mehr weiterentwickelt; VB-CABLE: Spendenware des Herstellers, Mitliefern nicht ohne Weiteres erlaubt) - ungeprüft.
  RDP der eigenen Engine könnte Mikrofon nativ (MS-RDPEAI) - nicht gebaut. Mikrofon/Controller haben im
  Setup-Dialog kein Feld (nur Konfigurationsdatei).

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

## Aufgabe 3: Ein Agent für Windows, Linux und macOS (Auftrag vom 2026-11-18)

Der Nutzer, in seinen Worten: „Die Agents müssen auch für Linux, Mac und Windows gebuildet werden auf
GitHub?“ - „Wir wollten doch das Windows-Programm komplett auf Avalonia umbauen und schauen, dass wir
das cross-platform hinbekommen.“ - „Ich möchte auch den Bildschirm, und auch so, dass man nicht
unbedingt eine User-Session braucht. Auch möchte ich den Dienst natürlich installieren, dass er als
Systemkonto läuft bei Windows und Linux/Mac-Äquivalent.“

Ziel also: EIN Agent mit Avalonia-Oberfläche (MatDesktop-Baukasten aus MatDesign, Vorlage ThixClip:
`../ThixClip`, dort `CLAUDE.md`, `src/ThixClip/Setup`, `Tray`, `MatDesktop/`, `scripts/package-macos.sh`,
`.github/workflows/desktop.yml`), als Dienst des Systems installiert, mit Bildschirm auch am
Anmeldebildschirm. Etappen:

### Etappe 1 - erledigt (0.25.0 / Agent 0.11.0): Kern überall, Dienst auf Linux/macOS

- **Nähte im Kern** (`agent/Matgate.Agent/AgentServices.cs`): `IPty`/`IPtyStarter`, `IAgentScreen`
  (+ `NoScreen`), `IAgentUpdates`, `AgentPlatform.Id` (`win-x64`, `linux-x64`, `linux-arm64`,
  `osx-arm64`, `osx-x64`). `Connection`, `TerminalPlugin` kennen nur noch die Schnittstellen.
- **`agent/Matgate.Agent.Unix`** (net10.0, Konsole, `matgate-agent`): bindet die gemeinsamen Dateien des
  Windows-Projekts ein (kein Verschieben - das kommt mit Etappe 2). Eigenes: `UnixPty` (posix_openpt +
  posix_spawn mit eigener Sitzung; `ioctl` auf Apple-ARM mit Platzhaltern, weil variadisch),
  `UnixUser`/`as-user` (Benutzerwechsel im eigenen zweiten Prozess: initgroups, setgid, setuid, execve -
  `su -c` nimmt dem Programm das Terminal, Strg+C beendete alles), `UnixInstaller` (systemd-Unit als
  root bzw. LaunchDaemon `com.matgate.agent`; Einstellungen nur für root), `UnixUpdates` (Datei unter dem
  laufenden Programm tauschen, beenden, der Dienstverwalter startet neu; Vorgänger bleibt als `.previous`).
- **Matgate:** `AgentHello.Platform`, `AgentUpdates.LatestAsync(platform)` - je System ein Manifest und
  ein Programm im Release (`matgate-agent-<platform>[.json]`; Windows behält seine Namen). Formular nennt
  den Installationsbefehl für Linux/macOS.
- **CI** (`.github/workflows/agent.yml`): Windows wie bisher; Matrix linux-x64, linux-arm64 (ubuntu),
  osx-arm64, osx-x64 (macos, ad hoc signiert); ein eigener Job füllt das Release (Programme vor Manifesten).
- **Nebenbei gefunden:** Die Terminalseite löschte nach dem Wiederverbinden den eben empfangenen Verlauf
  (`term.reset()` beim `ready`, die Zeilen kommen aber davor) - unter Windows fiel das nicht auf, weil
  ConPTY neu zeichnet. Jetzt wird vor dem Verbinden geleert.
- **Test:** `tests/agent-linux.js` (36 Prüfungen) gegen den Container `agent-linux` des Teststapels
  (als root, Programme als `demo`): Status, Logo, Dateien, Terminal (Benutzer, Ordner, tty, Größe,
  Strg+C, Farben, Umlaute), Sitzung überlebt den Browser, Varianten, Beenden, Update von 0.10.0.1 auf
  0.99.0.7 über ein eigenes Release, Setup schreibt Unit und Einstellungen, Deinstallation.
- **Nicht geprüft:** macOS überhaupt (nur gebaut, und das erst durch die CI); ein echtes systemd (im
  Container gibt es keines - geprüft ist, WAS das Setup schreibt, nicht dass der Dienst startet);
  linux-arm64; BusyBox/Alpine (musl) - dafür bräuchte es ein `linux-musl`-Programm.

### Etappe 2 - erledigt (Agent 0.12.0): Oberfläche auf Avalonia, ein Projekt für alle

- **Ein Projekt** `agent/Matgate.Agent` für alle Systeme: `net10.0-windows` beim Bauen für Windows (wegen
  der Zwischenablage-Brücke des Bildschirms, die WinForms als Nachrichtenschleife nutzt - nicht mehr als
  Oberfläche), sonst `net10.0`. Windows-Dateien und `Unix/` werden je nach Ziel ausgeschlossen
  (`AgentForWindows`, Konstanten `AGENT_WINDOWS`/`AGENT_UNIX`). `Matgate.Agent.Unix` ist darin aufgegangen.
- **MatDesktop 0.2.0** vollständig kopiert (`MatDesktop/`, aus MatDesign Zweig `origin/dev` - der
  Arbeitsstand des MatDesign-Ordners hat ihn nicht; kopiert wurde aus einem `git archive`, ohne dort etwas
  zu ändern).
- **Fenster:** `Ui/AgentApp.cs` (Application, Akzent `#0a7d57`, das Logo wird gezeichnet statt geladen),
  `Ui/AgentSetup.cs` (`MatSetupWindow`: Willkommen → Verbindung und Ausführung → Was Matgate sehen darf →
  Betrieb und Aussehen → Fortschritt → Werte für Matgate; gleiche Version = Einstellungen ändern, ältere
  installiert = Update, das nichts fragt), `Ui/AgentTray.cs` (`MatTray`/`MatTrayMenu`, Info als
  `MatInfoWindow`, Verbindungsdaten), `Ui/Snapshot.cs` (`matgate-agent snapshot <Ordner>`: jedes Fenster
  ohne Bildschirm, hell und dunkel; ersetzt `setup-preview`). WinForms-Dateien (Brand, BrandWindow, Theme,
  SetupWizard, SetupPreview, TrayApplication) sind gelöscht.
- **`Setup/AgentInstall.cs`:** eine Tür zu beiden Installern (Pfade, FindExisting, Install, Uninstall,
  Start/Stop/Restart) und `RunElevated` (UAC / `pkexec` / `osascript … with administrator privileges`).
  Unter Windows hat das Setup-Fenster selbst Administratorrechte; unter Linux/macOS läuft es als die
  angemeldete Person und gibt die Arbeit an einen zweiten, erhöhten Lauf (`setup … --result-json`).
- **Tray unter Linux/macOS:** Autostart über `/etc/xdg/autostart/matgate-agent-tray.desktop` bzw. einen
  LaunchAgent; der Statuskanal ist dort die Socket-Datei `/tmp/matgate-agent-status` (für alle lesbar,
  ohne Geheimnis), weil Dienst (root) und Symbol (Benutzer) sonst verschiedene Temp-Ordner hätten.
- **CI:** dieselbe Projektdatei für alle fünf Ziele; jeder Lauf zeichnet die Fenster (`snapshot`) und
  lädt die Bilder als Artefakt `windows-<Plattform>` hoch - so sieht man auch die macOS-Fenster.
- **Geprüft:** alle Windows-Agent-Tests (50/33/10/95) und `agent-linux.js` (36) mit dem neuen Programm;
  Fenster als Bilder unter Windows und Linux angesehen; das Tray-Symbol startet unter Windows.
- **Nicht geprüft:** die Fenster *in Bedienung* (Klicks, Ordnerwahl, UAC-Lauf des Setups, Deinstallation
  über das Fenster) - nur ihr Aussehen und die Arbeit dahinter über die Kommandozeile; Tray und Setup
  unter einem echten Linux-Desktop (pkexec, StatusNotifier) und unter macOS überhaupt. macOS hat noch kein
  `.app`/dmg (Vorlage: `../ThixClip/scripts/package-macos.sh`).
- **Falle:** `AgentLog.AttachToParentConsole` rief unter Linux `kernel32` - nur in einem Terminal (dort
  ist die Ausgabe nicht umgeleitet), also genau beim Start eines Programms über `as-user`.

### Etappe 3 - erledigt (Agent 0.13.0): Bildschirm auf Linux (X11), auch ohne angemeldeten Benutzer

- **Der Bildschirm-Code ist jetzt für alle Systeme derselbe:** `Screen/ScreenPlugin.cs` (Sitzung, Takt,
  Einstellungen, ein Fenster als Bild, Zwischenablage-Protokoll) fragt `Screen/ScreenPlatform.cs` nach
  allem, was am System hängt (Quelle, Encoder, Eingabe, Zwischenablage, Auflösung, Fensterliste,
  Hilfsprozess). Die Windows-Klassen stehen in ihren Dateien hinter `#if AGENT_WINDOWS`; Schnittstellen
  und Test-Attrappen sind gemeinsam. `SessionHost.cs` (Hilfsprozess über eine Pipe) gilt für alle.
- **Linux** (`Unix/Screen/`): `X11Capture` (XShm, XDamage - ohne XDamage Vergleich mit dem Bild davor;
  Mauszeiger über XFixes als PNG), `X11Input` (XTest; Scancodes = Linux-Tastencodes + 8, Tabelle für die
  erweiterten Tasten; Text als Zeichen über die Belegung oder eine freie Taste, die kurz umbelegt wird),
  `X11Clipboard` (Besitzer der Auswahl beobachten, Text lesen; Text, PNG und Dateien als
  `text/uri-list`/`x-special/gnome-copied-files` ausgeben), `X11Monitors`/`X11DisplayControl` (XRandR;
  Auflösung nur bei einem Monitor), `X11WindowList` (EWMH), `OpenH264Encoder` (bindet
  `Matgate/Display/Video/OpenH264.cs` ein - für den Agent mit allen Sonamen und der Layoutprüfung statt
  einer Versionsliste; geprüft mit 2.3.1 aus Debian 12 und 2.6.0 aus Debian 13. **Falle:** ab 2.5 haben
  Parameter, Quellbild und jede Ausgabeschicht drei Felder mehr am Ende (PSNR, darum Soname 8) - die
  Bibliothek schrieb hinter die alte Struktur, der Prozess starb mit SIGABRT. Jetzt bekommt jede Struktur
  Platz dahinter, `OpenH264.ExtendedLayout` merkt sich, welches Layout die Bibliothek hat, und die
  Schichten werden in der passenden Größe durchlaufen).
- **Ohne Benutzersitzung:** `XServers.Find` sucht als root den X-Server, den der Monitor zeigt: Prozess
  (Xorg, Xvfb, Xvnc …), Display aus der Kommandozeile oder - bei `-displayfd` (GDM) - über den Socket,
  Schlüsseldatei aus `-auth` bzw. der Umgebung des Servers, virtuelles Terminal gegen
  `/sys/class/tty/tty0/active`. Der Hilfsprozess startet mit `DISPLAY`/`XAUTHORITY` dieses Servers. Endet
  der X-Server (Ab-/Anmelden) oder zeigt der Monitor ein anderes Terminal, endet der Hilfsprozess, und der
  Agent öffnet für die Zuschauenden den nächsten.
- **Falle:** Das geteilte Bild (SysV-Shared-Memory) gehört root, der X-Server läuft heute meist als der
  angemeldete Benutzer und dürfte es nicht einbinden - der Agent übergibt das Segment per `IPC_SET` an
  dessen Benutzer (statt es für alle lesbar zu machen).
- **Geprüft** (`tests/agent-linux.js`, 49 Prüfungen; der Container hat jetzt einen Desktop: Xvfb als
  Benutzer `demo` mit Schlüsseldatei, openbox, ein xterm): Bild kommt an und zeigt das Fenster, der Zeiger
  folgt, Tasten und Text (auch Zeichen ohne Taste: „Grüße für 5 €“) landen im Fenster, Zwischenablage in
  beide Richtungen, ein anderer X-Server unter anderem Display nach „Abmelden“ wird übernommen.
- **Nicht geprüft:** ein echter Rechner mit GDM/LightDM/SDDM (Anmeldebildschirm, Wechsel des Terminals,
  `-displayfd`), mehrere Monitore, Auflösungswechsel, Fensterliste in der Oberfläche, arm64.
- **Offen:** Wayland (der Agent meldet es beim Öffnen; Weg wäre KMS/DRM-Aufnahme + uinput als root oder
  das Portal je Sitzung), Ton (PulseAudio/PipeWire-Monitor der Sitzung), Mikrofon und Controller (uinput),
  Bilder/Dateien vom Rechner in die Zwischenablage des Browsers.

### Etappe 4 - erster Stand (Agent 0.14.0): Bildschirm auf macOS - auf keinem Mac bedient

- `Unix/Screen/Mac*.cs`: `MacCapture` (`CGDisplayCreateImage`, Vergleich mit dem Bild davor; Monitore in
  Pixeln mit dem Faktor zu macOS' Punkten; der Zeiger als gezeichneter Pfeil, weil seine echte Form nur über
  AppKit zu haben ist), `VideoToolboxEncoder` (VTCompressionSession, NV12 hinein, AVCC nach Annex B, vor
  Schlüsselbildern SPS/PPS aus der Formatbeschreibung), `MacInput` (CGEvent: Tabelle Scancode → virtuelle
  Taste, Ziehen statt Bewegen bei gehaltener Taste, Doppelklick über den Klickzähler, Text als
  Unicode-Zeichen), `MacClipboard` (NSPasteboard über die Objective-C-Laufzeit, nur Text),
  `MacScreenPlatform`.
- **Wie der Dienst an den Bildschirm kommt:** Der LaunchDaemon (root) startet den Hilfsprozess mit
  `launchctl asuser <Benutzer an der Konsole>`. `launchctl bsexec <loginwindow>` sieht unter macOS 26
  keinen Monitor mehr (ausprobiert). Wechselt der Besitzer von `/dev/console`, endet der Hilfsprozess.
- **Geprüft - nur auf GitHubs macOS-26-Runner** (der Build hält die Ausgabe von `screen-probe` als Notiz
  des Laufs fest, Schritt „Screen probe (macOS)“): als angemeldeter Benutzer Aufnahme (1024x768, echtes
  Bild), VideoToolbox (Schlüsselbild und Folgebilder), Eingabe ohne Fehler; aus einem echten LaunchDaemon
  über `asuser`: der Monitor der Sitzung wird gesehen, die Aufnahme scheitert dort erwartungsgemäß an der
  fehlenden Freigabe „Bildschirmaufnahme“ für dieses Programm.
- **Nicht geprüft:** ein Mac, der über Matgate bedient wird (Bild im Browser, ob Maus und Tasten ankommen,
  Zwischenablage); die Freigaben „Bildschirmaufnahme“ und „Bedienungshilfen“, wie ein Mensch sie erteilt -
  und ob sie ein Update überleben (das Programm ist ohne Identität signiert); Retina, mehrere Monitore,
  Intel (nur gebaut).
- **Offen:** Anmeldefenster (bräuchte einen LaunchAgent mit `LimitLoadToSessionType=LoginWindow`),
  Änderungen statt Vergleich (ScreenCaptureKit/CGDisplayStream brauchen Objective-C-Blöcke), echte
  Zeigerform, Fensterliste, Ton, Mikrofon, Controller, `.app`/dmg mit Signatur (dann blieben auch die
  Freigaben über Updates erhalten).

## Oberfläche aufräumen (0.29.0, Wunsch vom 2026-11-19)

Der Nutzer: „Die App grundsätzlich ist gut. Aber mich stören die Details“ - Knöpfe, die man zweimal drücken
muss („Ton aus“), unaufgeräumte Dialoge („Screen Resolution“), Einstellungen, Verbindung bearbeiten und
Browser „nicht hübsch“, das Handy-Menü ohne Logo, die Anmeldeseite; dazu ein Test mit Bildern und ein
Server-Manager links „wie RoyalTS“. MatUi (`../MatDesign/src/MatUi`) als Vorlage für einzelne Elemente,
„aber nicht grundlegend“: übernommen sind Aufbau und Aussehen, nicht das Toolkit.

- **Zweimal drücken - Ursache und Abhilfe:** `updateTabActions` baute die Aktionsleiste bei jeder Meldung
  einer Sitzung neu (`replaceChildren`). Einen Knopf zu drücken **ist** so eine Meldung - das Bild verliert
  den Cursor -, also wurde der Knopf zwischen „runter“ und „hoch“ ersetzt, und der Klick ging an niemanden.
  Solange ein Druck auf der Leiste (oder ihrem „...“-Panel) dauert, wartet der Neuaufbau jetzt
  (`tabActionsHeld`/`tabActionsStale`, höchstens 4 s). `tests/action-press.js` drückt wie eine Hand
  (runter, 120 ms, hoch) und schlug vorher in drei von vier Prüfungen fehl.
- **Ein Rahmen für alle Dialoge:** `.credential-dialog` hat jetzt überall `.dialog-head` (Titel, ✕ mit
  `data-dialog-x`), `.dialog-body` (scrollt für sich) und `.dialog-foot` (Knöpfe, positiv zuerst; bei der
  Löschfrage steht „Löschen“ rechts abgesetzt). Das ✕ drückt den Schließen-Knopf des Dialogs
  (`data-dialog-close`), tut also dasselbe wie er; Escape schließt Anzeige, Zwischenablage und
  Zugangsdaten. Das Umfeld wird abgedunkelt (zweiter Schatten des Dialogs - kein eigenes Element, klickt
  also nichts weg). Am Telefon bleiben es Vollbild-Blätter, mit sicheren Rändern.
- **Dialog „Anzeige“ (vorher „Auflösung“):** Abschnitte mit Überschrift statt einer gemischten Knopfliste -
  Größe (An Fenster anpassen | Wie am Rechner | Feste Auflösung, die Liste als Kacheln dahinter),
  Skalierung (Regler, sechs Stufen), Bildqualität und Monitor als Auswahl nebeneinander, Optionen als echte
  Schalter (`role="switch"`). Klassen und Datenattribute, an denen die Tests hängen, sind geblieben
  (`.resolution-option`, `.resolution-scale-preset`, `[data-fixed-sizes]`, `[data-perf]`,
  `[data-keyboard-resize]`); eine Wahl schließt den Dialog wie bisher, ein Schalter nicht.
- **Handy-Menü:** Kopf mit Logo und Namen, darunter wer angemeldet ist, die Einträge als Zeilen mit
  Symbolkachel, Abmelden abgesetzt am Ende, Version als Fußzeile. Im Dropdown der Kompaktansicht am Desktop
  unverändert.
- **Anmeldung, zweiter Schritt, Ersteinrichtung:** zwei Hälften - links die Marke in ihren Farben mit drei
  Zeilen, was Matgate ist (`LoginAside`), rechts das Formular. Am Telefon die Marke oben über die ganze
  Breite, das Formular als Blatt darunter. Keine Version auf der Seite (nicht angemeldet).
- **Browser:** Der leere Tab zeigt eine Startseite statt einer leeren Fläche - wo man tippt, die als
  Verbindung gespeicherten Websites und die zuletzt besuchten Adressen (nur in diesem Browser gemerkt,
  `matgate.browser.recent`, leerbar). Der Rahmen bleibt dabei `about:blank`.
  **Dabei gefunden:** Mit Hostnamen für Websites blieb ein Browser-Tab auf „This address belongs to a
  Matgate session“ stehen, wenn der Browser den Hostnamen nicht als zu Matgate gehörig behandelt (Matgate
  über eine IP geöffnet) - nur gespeicherte Verbindungen fielen auf den Pfad zurück. Der Browser-Tab tut es
  jetzt auch (`sitePathOnly`, die gewünschte Adresse unter dem Pfad); `tests/website.js`.
- **Einstellungen und Verwaltung:** Die Tab-Leiste scrollt auf schmalen Schirmen in einer Zeile, statt sich
  dreizeilig zu stapeln; der Knopf eines `form-grid` steht unter den Feldern. Die Seite einer Verbindung
  hat einen Kopf mit ihrem Symbol in ihrer Farbe.
- **Server-Manager (Desktop):** Knopf links in der Tab-Zeile blendet eine Leiste ein (`#server-manager`,
  280 px, neben den Sitzungen): Suche, Ordner zum Auf- und Zuklappen, Verbindungen mit Symbol und Ziel, ein
  Punkt an denen mit offener Sitzung (Zahl ab zwei). Klick verbindet oder springt zur offenen Sitzung,
  Strg+Klick und mittlere Taste öffnen eine weitere, Rechtsklick bringt das Menü der Karte von der
  Startseite. Zustand und zugeklappte Ordner stehen im Browser (`matgate.serverManager.*`). Unter 900 px
  und im Vollbild gibt es sie nicht. Die Liste kommt aus `availableServers` (neu: `folder`), ohne Ablagen
  und ohne Einmal-Verbindungen aus Quick-Connect (`ephemeral`). `tests/server-manager.js`.
- **Seitenleiste statt Startseite (0.30.0):** Die Leiste heißt jetzt Seitenleiste und ist am Desktop von
  Anfang an offen (`matgate.serverManager.open` fehlt = offen). Gruppen von oben: Favoriten, Zuletzt
  verwendet (5), die Ordner (ohne Ordner zuletzt; gibt es keine Ordner, heißt die Gruppe „Verbindungen“),
  Ablagen, Freigaben - alle zuklappbar; bei einer Suche nur die Ordner, Ablagen und Freigaben. Die Daten
  dafür kommen als `sidebarData` (Seite) bzw. `side` (`/api/connections/panel`). Neben der Leiste zeigt die
  Startseite nur Kopf („Neue Verbindung“), Quick-Connect und die Farm-Zeile; weggeklappt ist sie wieder die
  volle Liste. Ein- und Ausblenden auch im Menü (Kontomenü, Burger-Menü im Kompaktmodus,
  `[data-server-manager-menu]`). Unter 900 px ist sie ein Blatt über der Seite, nur über das Menü
  („Verbindungen“), schließt bei Auswahl, Escape oder Tipp daneben, und merkt sich nichts. Quick-Connect am
  Handy: Protokolle als Kacheln vier in einer Reihe, passt bis 320×568 ohne Rollen. Tests, die die Liste der
  Startseite brauchen, klappen die Leiste mit `tests/list-on-start-page.js` vorher weg.
- **0.31.0:** Der Ansichts-Knopf (`#view-mode-toggle`) ist eine Auswahl (eigenes Menü, `#view-mode-choices`):
  Normal / Normal mit Seitenleiste / Kompakt; unter 900 px nur Normal / Kompakt. Kompakt hat nie eine
  Seitenleiste daneben. Handy startet ohne gespeicherte Wahl kompakt. Tests schalten mit
  `window.MatgateChooseView('normal'|'sidebar'|'minimal')`. Der Seitenleisten-Eintrag im Menü gibt es nur
  noch unter 900 px (Blatt). Ordner in der Seitenleiste: Unterordner sind Namen mit „/“ („Labor/Linux“),
  als Baum gezeichnet; Verbindungen per Ziehen auf einen Ordner (oder „Ohne Ordner“) verschieben
  (`POST /api/servers/{id}/folder`, Feld `folder`, `ServerFolders.CleanPath`); Rechtsklick auf Ordner: Neue
  Verbindung hier (`/admin/servers/new?folder=`), Unterordner anlegen, Ordner bearbeiten, alle zu/auf; auf
  freier Fläche: Quick-Connect, Neue Verbindung, Neuer Ordner; auf Verbindungen zusätzlich „In Ordner
  verschieben“. `tests/side-panel-folders.js`. Quick-Connect: „Öffnen mit“ Matgate/Guacamole (RDP, VNC, SSH)
  bzw. Integriert/Browser-Farm (Browser, nur wenn die Farm eingerichtet ist), Feld `engine`. Kacheln der
  Startseite: Variante A „Wasserzeichen“ (`ConnectionMark`: Fenster für RDP, Prompt für SSH, sonst das Symbol).
- **Bilder:** `tests/gallery.js` ist eine Tour, kein Vergleich: Anmeldung, Start, Menü, Dialoge, echte
  RDP-/VNC-/SSH-Sitzungen, Browser, Server-Manager, Einstellungen - je am Desktop und am Handy, als PNG nach
  `tests/gallery/` mit `index.html`. So ist der Umbau beurteilt worden.

**0.29.1 - der Inhalt der Einstellungen und die Leiste des Browsers** (der Nutzer: „weiter“):

- **Schalter-Zeilen** (`.toggle-row`, überall): was es ist links, der Schalter rechts, im Aussehen des
  Dialogs „Anzeige“ (Spur in der Akzentfarbe, weißer Knopf); Zeilen, die aufeinander folgen, stehen in einem
  Kasten mit Linie dazwischen (`:has(+ .toggle-row)`). Vorher: Schalter links, jede Zeile lose.
- **Kontoseite** in einer Spalte in der Mitte (`.settings-page`, 880 px wie die Seite einer Verbindung)
  statt eines 640-px-Streifens links in einem fensterbreiten Kasten; Kopf mit Symbol, Name und Kontoname.
  Ein Kopf mit Symbol (`.conn-head`) bleibt auch am Telefon eine Zeile.
- **Browser-Leiste:** die Adresse als ein rundes Feld mit Zeichen davor - Globus, bei `https:` ein Schloss
  (`markWebsiteAddress`, Klasse `is-secure`) -, Zurück/Vor/Neu laden und „In neuem Tab öffnen“ als Zeichen
  ohne Rahmen; die Schrift der Adresse ist die der Oberfläche, nicht mehr Consolas.

**0.29.2 - die Verbindungsliste am Handy** (der Nutzer am 2026-11-20: „Mobile die Tabmenü soll auch so
aussehen wie das neue Hauptmenü vom Style her“). Verstanden als die Liste der offenen Verbindungen in der
kompakten Ansicht (`#mobile-tab-menu`, Blatt `.mobile-tab-sheet`):

- Derselbe Aufbau wie das Hauptmenü: Hintergrund der Seite, Kopf mit Titel, Zahl der Verbindungen und
  Schließen-Kachel; je Verbindung eine Karte mit Symbol in einer Kachel, Name und darunter, was sie ist
  (der Text aus `.session-tab-description`, beim Öffnen gelesen - er läuft nicht mit, solange das Blatt
  offen ist); Schließen als eigene Kachel rechts in der Karte; die aktive Karte hat den Akzentrand. „Neue
  Verbindung“ steht mit Abstand und gestricheltem Rand darunter (`.mobile-tab-item--add`).
- Das „…“-Menü der Sitzungsaktionen bleibt ein Menü am Knopf (es soll die Sitzung nicht verdecken), seine
  Zeilen haben aber dieselben Symbolkacheln, Schriftgröße und -stärke.
- Bildertour: neues Bild `phone-rdp-connections`; `tests/mobile-shell.js` prüft den Aufbau.

**Dabei gesehen, nicht geändert:** Eine **eigene dunkle Hintergrundfarbe im hellen Modus** lässt die
Eingabefelder weiß - `color-scheme` folgt dem Modus (hell/dunkel), nicht der gewählten Farbe, und die Felder
haben keine eigene Hintergrundfarbe. Der Teststapel stand nach `colours.js` so da. Abhilfe wäre,
`color-scheme` aus der Helligkeit der Palette zu bestimmen; nicht gebaut, weil es das Aussehen der Felder
im dunklen Modus überall mitändern kann.

**Nicht angefasst:** die Felder der Verbindungs-Seite und die Verwaltungsseiten. **Ungeprüft:** alles davon
auf einem echten iPhone; der Server-Manager mit vielen Ordnern und Verbindungen (im Teststapel gibt es
fünf Verbindungen und im Test einen Ordner). Das Schloss ist echt über den Dev-Stack geprüft
(`https://10.10.0.1/login`: Schloss; leer und `http`: Globus) - **es sagt nur „verschlüsselt“, nicht
„Zertifikat geprüft“**: Browser-Tabs ignorieren Zertifikatsfehler in der Voreinstellung (`IgnoreCertificate`).

## Backlog (Wünsche vom 2026-11-21, noch nicht begonnen)

Reihenfolge noch nicht abgesprochen - vor dem Anfangen fragen, welcher zuerst.

1. **Datenbanken - erster Stand in 0.31.0:** Protokolle `MsSql`, `MySql`, `Postgres` (Felder `DatabaseName`,
   „Zertifikat nicht prüfen“ = `IgnoreCertificate`), `Services/Database/DatabaseGateway.cs` (je Server ein
   `IDatabaseDialect`, Ausführen/Lesen gemeinsam), Seite `/db/<id>/browser` (`HtmlViews.Database.cs`), API
   `/api/db/<id>/databases|objects|rows|query`; Zugangsdaten ohne gespeichertes Passwort fragt die Seite und
   schickt sie als Header mit. Test `tests/database.js` gegen postgres/mariadb/mssql im Teststapel. Offen:
   Quick-Connect für Datenbanken, Bearbeiten von Zeilen, Export (CSV), Spalten-/Index-Ansicht.
   Ursprünglicher Wunsch: **Datenbanken als Verbindung + Datenbank-Browser.** Neue Protokolle MSSQL, MySQL/MariaDB, PostgreSQL
   (Host, Port, Benutzer, Passwort, Datenbank optional). Ein Browser wie der Dateimanager: links Datenbanken
   → Schemas → Tabellen/Sichten, rechts Tabellendaten (Seiten, Filter, Sortierung) und ein Abfragefenster.
   Wunsch: „wie das File-Thema wieder einheitlicher Code“ - also eine gemeinsame Schnittstelle
   (`IDatabaseGateway` analog `FileGatewayService`) mit je einem Treiber (Microsoft.Data.SqlClient, MySqlConnector,
   Npgsql) und einer Oberfläche für alle drei. Offen: nur lesen oder auch ändern; Grenzen für Ergebnismengen.
2. **Kopieren zwischen Datei-Tabs.** Strg+C / Strg+V zwischen zwei Dateimanager-Tabs (verschiedene
   Verbindungen) und Ziehen von Dateien auf einen anderen Tab (Tab wird beim Darüberhalten aktiv, Ablegen
   kopiert dorthin) - „wie im Windows Explorer“. Der Strom bleibt auf dem Gateway (`/api/files/{id}/copy-to`
   gibt es schon zwischen Ablagen).
3. **Matgate-Tunnel.** Alternative zum Agent: ein kleines Programm in einem fremden Netz, über das Matgate
   Verbindungen dorthin aufbaut. Je Verbindung wählbar: „Direkt“ oder „Über Tunnel X“. Arten: Reverse-Tunnel
   (das Programm verbindet sich zu Matgate, wie der Agent), offener Port, oder WireGuard / OpenVPN - dann baut
   der Container für diese Verbindung selbst ein VPN auf und verbindet sich darüber (RDP, SSH, Dateien ...).
   Offen: VPN im Container braucht NET_ADMIN / ein TUN-Gerät oder einen Userspace-Stack (z. B.
   wireguard-go mit netstack) - Sicherheit und Rechte vorher klären.
4. **Zugangsdaten-Verwaltung.** Gespeicherte Zugangsdaten als eigene Objekte (Name, Benutzer, Passwort/Schlüssel,
   Domäne), die mehrere Verbindungen benutzen; ändern an einer Stelle. Persönliche und geteilte; verschlüsselt
   wie heute die Passwörter der Verbindungen.
5. **Besser mehrbenutzerfähig (Firmen).** Admin verwaltet Server zentral; Admin legt Ordner an, die für alle
   (oder Gruppen) sichtbar sind; vermutlich Gruppen/Rollen statt Rechte je Benutzer. Heute ist ein Ordner nur
   der Name an den Verbindungen (`ServerFolders`) - für eigene Ordner (auch leere, mit Rechten) braucht es
   Ordner als eigene Objekte. Das hängt mit Punkt 4 und mit den Unterordnern der Seitenleiste zusammen.

Frage aus der Sitzung, beantwortet: **AnyDesk / TeamViewer** sind geschlossene Protokolle ohne offene
Beschreibung - nachbauen geht nicht. Möglich wäre (a) eine Verbindungsart, die nur die installierte App auf
dem Gerät öffnet (`anydesk:<ID>`, `teamviewer10://control?device=<ID>`), oder (b) der Linux-Client in einem
Browser-Farm-artigen Container, als VNC-Bild gestreamt (Lizenzbedingungen prüfen). Nicht beauftragt.

## Offene Prüfungen am echten Gerät

- iPhone: Diktat in RDP/VNC und im Terminal – kommt jeder Satz einmal an, auch nach Korrekturen?
- Android (Gboard): Tippen mit Wortvorschlägen, Wischen, Diktat.
- Windows-Server über die eigene Engine: Anmeldung (NLA), Bild, Ton, Dateien per Strg+V in den Explorer.
