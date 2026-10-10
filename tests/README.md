# UI tests

End-to-end tests that drive a real browser against a real Matgate. They exist because almost
everything in Matgate only breaks in a browser: a tab that takes every open session down with it,
a select that closes before you can pick anything, a toolbar that is 4.5 points out of line. None
of that shows up in a unit test.

## Running them

```bash
cd tests && npm install
docker compose -f docker-compose.yml up -d --build
node run-all.js
```

The runner seeds the stack first (`seed.js`): one SSH connection to the stack's own ssh container
and one 180 MB file in the global place, both of which several scripts expect to find. Seeding is
idempotent and can also be run on its own with `node seed.js`.

The stack serves <http://127.0.0.1:18091>. It is separate from the development stack on purpose:
the tests create and delete users, places and shares.

A single test runs on its own:

```bash
node add-tab-plus.js
```

Requirements: Node, Docker, and a Chromium-based browser Playwright can drive - `msedge` or
`chrome`, whichever is installed. `npm install` in this folder pulls `playwright-core`; the browser
itself is the one already on the machine, so nothing downloads a second Chromium.

## What the output means

Every check prints one line, `ok` or `FAIL`, with what it got and what it wanted. A script exits
non-zero when anything failed. `run-all.js` collects the scripts, prints a line per script and ends
with the total.

## The login rate limit

Signing in is limited to **10 attempts per 5 minutes per IP** (`Program.cs`, policy "login").
Several scripts sign in more than once, so `run-all.js` runs them in groups of at most six with a
five-minute pause in between. Without that the last script of a group fails on the rate limit
instead of on the thing it tests - the most useless failure there is. A full run takes about
35 minutes.

Symptom when it happens anyway: a script aborts after a few seconds because the shell never
loaded, usually with "Cannot read properties of null".

## After an aborted run

`totp.js` may have left the second factor switched ON for the test account. Its code and recovery
codes are gone with the process, so every later sign-in stops at the second step (POST /login
answers 200 and the URL stays /login). To get out:

```bash
docker exec matgate-tests-matgate-1 sed -i 's/"totpEnabled": true/"totpEnabled": false/' /data/users.json
docker restart matgate-tests-matgate-1
```

A script that aborts before its cleanup can also leave a user or a share behind. `seed.js` removes
those before every run - by **exact name**, only the ones the tests themselves create (`UmbauTester`,
`Frisch angelegt`, ...; the list is at the top of the file). Anything else in the stack is left alone.
Without that, the next run fails on "name already taken" instead of on the thing it tests.

To start completely clean:

```bash
docker exec matgate-tests-matgate-1 sh -c 'echo "[]" > /data/workspaces.json'
docker restart matgate-tests-matgate-1
```

## Writing another one

Copy the shortest script that is close to what you need (`tab-scrollbar.js` is a good start) and
keep the shape: sign in, do the thing, **measure**, print one line per check. Measuring is the
point - "the button looks right" is not a test, "the button's centre is 69.6 and the tab's centre
is 69.6" is.

## A remote desktop to test against

The stack has an SSH host but no RDP or VNC machine, and a pointer, a scale or a pan can only be checked
against a desktop. `toy-vnc.js` is a minimal VNC server for that: a 1600x900 screen of coloured tiles, a
white marker that follows the pointer, and a log of every pointer and key event it received
(`GET http://127.0.0.1:5902/log`). guacd reaches it as `host.docker.internal:5901`, which needs Docker
Desktop. `toy-vnc-rig.js` starts it, makes the connection `Toy-VNC` through the admin form and removes both
again; `seed.js` clears a connection left behind by an aborted run. The tests that use it
(`touch-pointer.js`, `scale-slider.js`, `pan-and-zoom.js`, `mouse-follows-view.js`) read what the
"remote" received instead of guessing from the picture. It speaks VNC, so RDP's own habits - the reconnect
on a new size, the pointer a fresh connection starts with - are not part of it.

## What the phone tests can and cannot show

`file-manager-phone.js`, `file-area-dialog-phone.js`, `mobile-*.js` and `burger-and-tabstrip.js` run
Chromium at 390 pixels with touch, an iPhone user agent and a faked "installed to the home screen"
(`display-mode: standalone` and `navigator.standalone`). That shows what the LAYOUT does on a phone -
what is on the screen, how big it is, whether a tap reaches it and what the tap opens. It is not an
iPhone: WebKit's own habits (a `<details>` that does not open, fixed elements clipped inside a
scrolling container, safe areas, the soft keyboard) can only be checked on the device. When a phone
bug is reported, the first question is which build the phone is running (the version under the
info button).

`keyboard-and-dialogs.js` is the one place where the soft keyboard is part of a test - and there it is
faked: Chromium has none. iOS is imitated by shrinking (and panning) `window.visualViewport`, Android
by an own `navigator.virtualKeyboard` that reports a rectangle. What the script proves is what the
application does with those two reports (a dialog ends where the keyboard begins), not what a real
phone reports; and it proves that the application's own keyboard goes down when a dialog opens and
never lies on top of one.
