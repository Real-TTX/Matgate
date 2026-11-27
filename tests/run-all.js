// Everything, one after another. Signing in is rate limited to 10 attempts per 5 minutes per IP,
// so the scripts run in groups with a pause in between. Without the pause a script fails on the
// rate limit instead of on the thing it tests, which is the most useless failure of all.
const { spawn } = require("child_process");

// At most six scripts per group: some of them sign in more than once, and the limit is ten
// attempts per five minutes. Seven in one group was enough to make the last one fail on the rate
// limit instead of on the thing it tests.
const GROUPS = [
  {
    name: "Group 1 - files, places, administration",
    scripts: ["workspace-places.js", "workspace-page-pointer.js", "sftp.js", "admin-users.js",
      "action-bar.js", "download-keeps-tunnel.js"],
  },
  {
    name: "Group 2 - sessions under load, mobile shell",
    scripts: ["favorite-keeps-session.js", "download-large.js", "mobile-shell.js",
      "burger-and-tabstrip.js", "mobile-neighbours.js", "mobile-dialogs.js"],
  },
  {
    name: "Group 3 - menus, tabs, file manager",
    scripts: ["menus.js", "file-manager-tab.js", "file-manager-actions.js", "own-dialogs.js",
      "tab-scrollbar.js", "add-tab-plus.js"],
  },
  {
    name: "Group 4 - appearance, home page, names",
    scripts: ["colours.js", "colours-follow-theme.js", "colour-picker.js", "home-page.js",
      "username-case.js", "offline-page.js"],
  },
  {
    name: "Group 5 - second factor, sharing, closing tabs, agent",
    scripts: ["totp.js", "place-settings-dialog.js", "sharing.js", "workspace-dissolved.js", "tab-middle-click.js",
      "agent.js"],
  },
  {
    name: "Group 6 - the phone, creating places, stuck keys",
    scripts: ["file-manager-phone.js", "file-area-dialog-phone.js", "file-manager-menus.js",
      "public-page-phone.js", "create-place.js", "stuck-keys.js"],
  },
  {
    name: "Group 7 - remote sessions: pointer, scale, panning, pasting files, keyboards",
    scripts: ["touch-pointer.js", "scale-slider.js", "pan-and-zoom.js", "mouse-follows-view.js", "paste-files.js",
      "keyboard-and-dialogs.js"],
  },
  {
    name: "Group 8 - an agent (updating it, its screen) and Matgate's own engine for RDP, VNC and SSH",
    scripts: ["agent-update.js", "agent-screen.js", "agent-devices.js", "display.js", "text-input.js"],
  },
  {
    name: "Group 9 - the key bar, the scale and the keyboard of a session, the connection form, fields on a phone",
    scripts: ["key-bar.js", "screen-scale.js", "connection-form.js", "phone-field-zoom.js", "connection-order.js"],
  },
  {
    name: "Group 10 - sessions that outlive the browser, right click, the order of the quick-connect chips, the browser under a path",
    scripts: ["kept-sessions.js", "context-menu.js", "quick-connect-order.js", "browser-under-path.js"],
  },
  {
    name: "Group 11 - websites: an address of their own, the path under Matgate's, a browser tab; the settings page and what saving it keeps",
    scripts: ["website.js", "settings-page.js", "settings-safety.js", "sso-groups.js", "db-edit-plan.js"],
  },
  {
    name: "Group 12 - the agent on Linux",
    scripts: ["agent-linux.js", "clipboard-button.js", "perf-position.js", "admin-workspaces.js"],
  },
  {
    name: "Group 13 - one press on a toolbar button, the frame of the dialogs, the server manager, the account menu",
    scripts: ["action-press.js", "server-manager.js", "account-menu.js", "phone-list.js", "admin-browser-farm.js", "no-stray-selection.js"],
  },
  {
    name: "Group 14 - folders in the side panel, sessions in windows of their own, the database browser",
    scripts: ["side-panel-folders.js", "session-windows.js", "database.js", "file-tabs-copy.js", "access.js", "admin-people.js"],
  },
  {
    name: "Group 15 - stored credentials, the order of things and the way back to it",
    scripts: ["credentials.js", "order-defaults.js", "groups.js", "keybar-keeps-keyboard.js", "tunnel.js",
      "ssh-key.js"],
  },
  {
    name: "Group 16 - the status bar, cards of one size, the right-click ring, the phone's tab cards",
    scripts: ["status-bar.js", "equal-cards.js", "hold-ring-setting.js", "tab-previews.js"],
  },
];

const PAUSE = 310000;

// A fresh stack has neither the test connection nor the big file - seeding first costs a few
// seconds and saves six scripts from failing on something that is not their subject.
function seed() {
  return new Promise(done => {
    const child = spawn("node", ["seed.js"], { cwd: __dirname, stdio: "inherit" });
    child.on("close", () => done());
  });
}

function run(file) {
  return new Promise(done => {
    const started = Date.now();
    const child = spawn("node", [file], { cwd: __dirname });
    let out = "";
    child.stdout.on("data", d => { out += d; });
    child.stderr.on("data", d => { out += d; });
    child.on("close", code => {
      const lines = out.split(/\r?\n/);
      done({
        file,
        code,
        ok: lines.filter(l => l.startsWith("ok   ")).length,
        failed: lines.filter(l => l.startsWith("FAIL ")).length,
        aborted: lines.find(l => l.startsWith("ABORTED")),
        seconds: Math.round((Date.now() - started) / 1000),
        failures: lines.filter(l => l.startsWith("FAIL ")),
      });
    });
  });
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

// Never the installed Edge or Chrome: each of their starts with a fresh profile signs in to Windows with an
// empty password (to see whether the account has one), Windows counts that as a wrong password, and a run
// of all scripts locked the account of whoever ran it. See README.
function refuseInstalledBrowsers() {
  const fs = require("fs");
  const path = require("path");
  const offenders = GROUPS.flatMap(group => group.scripts)
    .filter(file => /channel:\s*["'](msedge|chrome)/.test(fs.readFileSync(path.join(__dirname, file), "utf8")));
  if (offenders.length) {
    console.log("NOT RUN: these start Edge or Chrome (locks the Windows account, see README): " + offenders.join(", "));
    process.exit(1);
  }
}

(async () => {
  refuseInstalledBrowsers();
  await seed();
  const results = [];
  for (let g = 0; g < GROUPS.length; g++) {
    console.log("\n===== " + GROUPS[g].name);
    for (const file of GROUPS[g].scripts) {
      const r = await run(file);
      results.push(r);
      console.log((r.code === 0 ? "  PASS  " : "  FAIL  ") + file.padEnd(32) +
        r.ok + " ok, " + r.failed + " failed, " + r.seconds + "s" + (r.aborted ? "  " + r.aborted : ""));
      r.failures.forEach(l => console.log("          " + l.trim()));
      // A script that dies within seconds having checked almost nothing did not get in: the login
      // rate limit answers with 429 and the page it expected never loads. That looks exactly like
      // a real failure in the log, and it is not one - say so instead of leaving it to be guessed.
      if (r.code !== 0 && r.seconds < 12 && r.ok <= 1) {
        console.log("          ^ this smells of the login rate limit, not of the thing it tests");
        console.log("            (10 attempts per 5 minutes per IP - see README)");
      }
    }
    if (g < GROUPS.length - 1) {
      console.log("  ... pausing " + Math.round(PAUSE / 1000) + "s for the login rate limit");
      await sleep(PAUSE);
    }
  }
  const ok = results.reduce((n, r) => n + r.ok, 0);
  const failed = results.reduce((n, r) => n + r.failed, 0);
  const broken = results.filter(r => r.code !== 0);
  console.log("\n===== " + results.length + " scripts, " + ok + " checks ok, " + failed + " failed");
  console.log(broken.length ? "NOT GREEN: " + broken.map(r => r.file).join(", ") : "ALL GREEN");
  process.exit(broken.length ? 1 : 0);
})();
