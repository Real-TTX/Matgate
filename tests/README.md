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
30 minutes.

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

A script that aborts before its cleanup can also leave a share behind. Tests that look a share up
by name take the first match, so one leftover does not fail the next run - but to start clean:

```bash
docker exec matgate-tests-matgate-1 sh -c 'echo "[]" > /data/workspaces.json'
docker restart matgate-tests-matgate-1
```

## Writing another one

Copy the shortest script that is close to what you need (`tab-scrollbar.js` is a good start) and
keep the shape: sign in, do the thing, **measure**, print one line per check. Measuring is the
point - "the button looks right" is not a test, "the button's centre is 69.6 and the tab's centre
is 69.6" is.
