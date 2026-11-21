namespace Matgate.Agent;

// The terminal of an agent program (Claude Code, PowerShell, ...) in the browser - its own small page,
// opened as a tab of the shell. The session id is part of the address: reloading, a restored tab or a
// phone that lost the connection attach to the same running program.
public static class AgentTerminalPage
{
    public const string Html = """
        <!doctype html>
        <html lang="__LANG__">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover, interactive-widget=resizes-content">
        <title>__TITLE__</title>
        <link rel="stylesheet" href="/assets/xterm/xterm.css">
        <style>
            html, body { background: #0f1412; color: #d8e0dc; height: 100%; margin: 0; overflow: hidden; }
            body { box-sizing: border-box; display: flex; flex-direction: column; font: 13px system-ui, sans-serif; }
            .bar { align-items: center; border-bottom: 1px solid #263029; display: flex; gap: 8px; min-height: 34px; padding: 0 8px; }
            .bar strong { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .status { color: #8fa39a; flex: 1 1 auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .status.bad { color: #f0a92b; }
            button { background: #1b2420; border: 1px solid #33413a; border-radius: 6px; color: inherit; cursor: pointer; font: inherit; min-height: 28px; padding: 2px 10px; }
            button:hover { background: #24302a; }
            #screen { flex: 1 1 auto; min-height: 0; padding: 4px 0 0 6px; }
            /* The key bar wears the colours of the application - taken from the shell when the page is inside it
               (the user's theme, as the on-screen keyboard there), this page's dark ones otherwise. */
            .keys { background: var(--kb-panel, #101613); border-top: 1px solid var(--kb-line, #263029); display: none; flex: 0 0 auto; gap: 5px; overflow-x: auto; overscroll-behavior-x: contain; padding: 6px 6px calc(6px + env(safe-area-inset-bottom)); scrollbar-width: none; }
            .keys::-webkit-scrollbar { display: none; }
            .keys button { background: var(--kb-key, #1f2a25); border: 1px solid var(--kb-line, #33413a); border-radius: 8px; box-shadow: none; color: var(--kb-text, #d8e0dc); flex: 0 0 auto; font-size: 15px; font-weight: 500; min-height: 40px; min-width: 44px; padding: 2px 12px; touch-action: manipulation; }
            .keys button:hover { background: var(--kb-key, #1f2a25); }
            .keys button:active, .keys button.on { background: var(--kb-accent, #2f6b50); border-color: var(--kb-accent, #4f8a6c); color: var(--kb-on, #fff); }
            /* Below the bar nothing has to stay clear on top of the device's keyboard - nor inside the shell,
               which keeps the safe area of the screen clear itself (the bar had a gap taller than its buttons). */
            .keys.kb, body.in-shell .keys { padding-bottom: 6px; }
            .keys .keys-setup { align-items: center; background: transparent; border-color: transparent; color: var(--kb-text, #8fa39a); display: inline-flex; justify-content: center; opacity: .7; padding: 2px 8px; }
            @media (pointer: coarse) { .keys { display: flex; } }
            /* Someone who removed every button has no bar. */
            .keys.none { display: none !important; }
            .hidden { display: none !important; }
            button.icon { align-items: center; display: inline-flex; justify-content: center; min-width: 32px; padding: 2px 6px; }
            body.embedded .bar { display: none; }
            .note { background: #18211d; border: 1px solid #33413a; border-radius: 10px; bottom: 16px; box-shadow: 0 8px 24px rgba(0,0,0,.5); color: #d8e0dc; max-width: min(420px, calc(100% - 32px)); padding: 10px 14px; position: fixed; right: 16px; z-index: 5; }
            .note.bad { border-color: #f0a92b; color: #f0a92b; }
        </style>
        </head>
        <body data-server="__SERVER__" data-csrf="__CSRF__" data-text="__TEXT__" data-kind="__KIND__" data-base="__BASE__" data-font="__FONT__" data-keys="__KEYS__">
        <div class="bar">
            <button id="home" type="button" class="icon" title=""><svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></svg></button>
            <strong>__TITLE__</strong>
            <span id="status" class="status"></span>
            <button id="restart" type="button" class="hidden"></button>
            <button id="kill" type="button"></button>
        </div>
        <div id="screen"></div>
        <div class="keys" id="keys"></div>
        <div id="note" class="note hidden"></div>
        <input id="pick" type="file" multiple class="hidden">
        <script src="/assets/xterm/xterm.js"></script>
        <script src="/assets/xterm/addon-fit.js"></script>
        <script>
        (() => {
            const text = JSON.parse(document.body.dataset.text);
            const server = document.body.dataset.server;
            // "agent" (a program on an agent's machine) or "ssh"; where the socket is.
            const kind = document.body.dataset.kind || 'agent';
            const base = document.body.dataset.base || '/api/agent/' + server;
            // Inside Matgate's shell the session toolbar steers the page; the bar only shows on its own.
            const embedded = (() => {
                try {
                    return !!(window.parent && window.parent !== window && window.parent.MatgateShowAgent);
                }
                catch {
                    return false;
                }
            })();
            if (embedded && kind === 'ssh') {
                document.body.classList.add('embedded');
            }
            document.body.classList.toggle('in-shell', embedded);
            if (embedded) {
                // Inside the shell the key bar wears the application's colours, whatever theme the user chose.
                try {
                    const colours = getComputedStyle(window.parent.document.documentElement);
                    [['--kb-panel', '--panel'], ['--kb-key', '--surface-2'], ['--kb-line', '--line'], ['--kb-text', '--text'], ['--kb-accent', '--accent'], ['--kb-on', '--bg']].forEach(([own, from]) => {
                        const value = colours.getPropertyValue(from).trim();
                        if (value) {
                            document.documentElement.style.setProperty(own, value);
                        }
                    });
                }
                catch {
                    // The page's own colours stay.
                }
            }
            const params = new URLSearchParams(location.search);
            const plugin = params.get('plugin') || '';
            // The program's name first: the shell takes the tab's title from here.
            const programTitle = params.get('title');
            if (programTitle) {
                document.title = programTitle + ' - ' + document.title;
                document.querySelector('.bar strong').textContent = document.title;
            }
            let session = params.get('session') || '';
            if (!/^[A-Za-z0-9-]{8,64}$/.test(session)) {
                session = crypto.randomUUID();
                params.set('session', session);
                history.replaceState(null, '', location.pathname + '?' + params.toString());
            }

            const statusLine = document.getElementById('status');
            const restartButton = document.getElementById('restart');
            const killButton = document.getElementById('kill');
            restartButton.textContent = text.restart;
            // The agent's icon: back to its start screen in the shell.
            const homeButton = document.getElementById('home');
            homeButton.title = text.overview;
            homeButton.setAttribute('aria-label', text.overview);
            if (kind !== 'agent' || !(window.parent && window.parent !== window && window.parent.MatgateShowAgent)) {
                homeButton.classList.add('hidden');
            }
            homeButton.addEventListener('click', () => window.parent.MatgateShowAgent(server));
            killButton.textContent = text.kill;
            const setStatus = (value, bad) => {
                statusLine.textContent = value;
                statusLine.classList.toggle('bad', !!bad);
            };

            const TerminalClass = window.Terminal && (window.Terminal.Terminal || window.Terminal);
            const FitClass = window.FitAddon && (window.FitAddon.FitAddon || window.FitAddon);
            const term = new TerminalClass({
                cursorBlink: true,
                fontFamily: 'Cascadia Mono, Consolas, ui-monospace, monospace',
                fontSize: Number(document.body.dataset.font) || (matchMedia('(pointer: coarse)').matches ? 12 : 14),
                scrollback: 5000,
                theme: { background: '#0f1412', foreground: '#d8e0dc', cursor: '#7fd6a8' },
            });
            const fit = new FitClass();
            term.loadAddon(fit);
            term.open(document.getElementById('screen'));
            fit.fit();
            term.focus();

            const encoder = new TextEncoder();
            let socket = null;
            let ended = false;
            let retry = 0;
            let retryTimer = 0;

            const sendBytes = bytes => {
                if (socket && socket.readyState === WebSocket.OPEN) {
                    socket.send(bytes);
                }
            };
            const sendControl = value => {
                if (socket && socket.readyState === WebSocket.OPEN) {
                    socket.send(JSON.stringify(value));
                }
            };

            // ---- the key bar: the user's own buttons (Account -> Key bar) ------------------------------------
            const barButtons = JSON.parse(document.body.dataset.keys || '[]');
            const keyBar = document.getElementById('keys');
            keyBar.classList.toggle('none', !barButtons.length);
            // What the sticky buttons hold for the next key: id -> modifiers (Shift 1, Alt 2, Ctrl 4).
            const sticky = new Map();
            const stickyMask = () => [...sticky.values()].reduce((mask, mods) => mask | mods, 0);
            const dropSticky = () => {
                sticky.clear();
                keyBar.querySelectorAll('.on').forEach(item => item.classList.remove('on'));
            };
            // One typed character with the modifiers a sticky button holds: Ctrl makes the control character
            // of it, Alt puts an Escape in front. Anything longer is text and stays as it is.
            const withSticky = data => {
                const mask = stickyMask();
                if (!mask) {
                    return data;
                }
                dropSticky();
                if ([...data].length !== 1 || data.charCodeAt(0) < 0x20) {
                    return data;
                }
                let out = mask & 1 ? data.toUpperCase() : data;
                if (mask & 4 && out.length === 1) {
                    const code = out.toUpperCase().charCodeAt(0);
                    out = code >= 0x40 && code <= 0x5f ? String.fromCharCode(code & 0x1f) : code === 0x20 ? '\x00' : code === 0x3f ? '\x7f' : out;
                }
                return mask & 2 ? '\x1b' + out : out;
            };
            const fromHex = hex => Uint8Array.from(hex.match(/../g) || [], pair => parseInt(pair, 16));
            const pressBarButton = async (button, item) => {
                if (button.sticky) {
                    if (sticky.has(button.id)) {
                        sticky.delete(button.id);
                    }
                    else {
                        sticky.set(button.id, button.mods);
                    }
                    item.classList.toggle('on', sticky.has(button.id));
                    term.focus();
                    return;
                }
                const mask = stickyMask();
                dropSticky();
                if (button.action === 'paste' && !mask) {
                    // What is on this device's clipboard; without access to it the key goes as it is.
                    let clip = '';
                    try {
                        clip = await navigator.clipboard.readText();
                    }
                    catch {
                    }
                    if (clip) {
                        term.paste(clip);
                        term.focus();
                        return;
                    }
                }
                // A program that switched the cursor keys to application mode (an editor, a pager) expects them so.
                const application = !mask && button.app && term.modes && term.modes.applicationCursorKeysMode;
                sendBytes(fromHex(application ? button.app : button.send[mask]));
                term.focus();
            };
            // A tap on a button of the bar must not take the keyboard away. Preventing pointerdown is not enough on
            // phones: the tap's touchend still moves the focus off the hidden field, and the device keyboard closes.
            // So a short tap is taken on touchend (which then neither moves the focus nor clicks a second time), and
            // the field keeps the focus; a swipe along the bar stays a swipe.
            const tapKeepsKeyboard = (item, action) => {
                let start = null;
                item.addEventListener('touchstart', event => {
                    const touch = event.changedTouches[0];
                    start = touch ? { x: touch.clientX, y: touch.clientY, field: /^(TEXTAREA|INPUT)$/.test((document.activeElement || {}).tagName || '') ? document.activeElement : null } : null;
                }, { passive: true });
                item.addEventListener('touchend', event => {
                    const touch = event.changedTouches[0];
                    const was = start;
                    start = null;
                    if (!was || !touch || Math.abs(touch.clientX - was.x) > 10 || Math.abs(touch.clientY - was.y) > 10) {
                        return;
                    }

                    event.preventDefault();
                    action();
                    if (was.field && document.activeElement !== was.field) {
                        was.field.focus({ preventScroll: true });
                    }
                }, { passive: false });
                item.addEventListener('touchcancel', () => { start = null; });
                item.addEventListener('click', action);
            };
            barButtons.forEach(button => {
                const item = document.createElement('button');
                item.type = 'button';
                item.textContent = button.label;
                item.title = button.title;
                item.dataset.id = button.id;
                // The bar's buttons must not take the keyboard away from the terminal.
                item.addEventListener('mousedown', event => event.preventDefault());
                tapKeepsKeyboard(item, () => pressBarButton(button, item));
                keyBar.appendChild(item);
            });
            // The bar is the user's own (Account -> Key bar): its last button leads there. Only inside the
            // shell, which has a place to open that page in.
            if (barButtons.length) {
                try {
                    if (window.parent !== window && typeof window.parent.MatgateOpenShellTab === 'function') {
                        const setup = document.createElement('button');
                        setup.type = 'button';
                        setup.className = 'keys-setup';
                        setup.title = text.keyBarSetup;
                        setup.setAttribute('aria-label', text.keyBarSetup);
                        setup.innerHTML = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-2.9 1.2V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-2.9-1.2l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0-1.2-2.9H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.2-2.9l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 2.9-1.2V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 2.9 1.2l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0 1.2 2.9H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/></svg>';
                        setup.addEventListener('mousedown', event => event.preventDefault());
                        setup.addEventListener('click', () => window.parent.MatgateOpenShellTab('/account?tab=keys', text.keyBarSetup));
                        keyBar.appendChild(setup);
                    }
                }
                catch {
                    // Not inside the shell.
                }
            }

            term.onData(data => sendBytes(encoder.encode(withSticky(data))));

            // ---- the device's keyboard ---------------------------------------------------------------
            // It lies over the bottom of the page. The page ends above it: the key bar sits on top of the
            // keyboard and the terminal has the rows that are left - as a terminal does whose window shrinks.
            let keyboardInset = 0;
            const applyKeyboardInset = value => {
                const inset = Math.max(0, Math.round(Number(value) || 0));
                if (inset === keyboardInset) {
                    return;
                }
                keyboardInset = inset;
                document.body.style.paddingBottom = inset ? inset + 'px' : '';
                keyBar.classList.toggle('kb', inset > 0);
            };
            // Inside the shell, the shell says so: an embedded page cannot see the keyboard.
            window.MatgateKeyboardInset = applyKeyboardInset;
            if (window.parent === window) {
                try {
                    if ('virtualKeyboard' in navigator) {
                        navigator.virtualKeyboard.overlaysContent = true;
                        navigator.virtualKeyboard.addEventListener('geometrychange', () => measureKeyboard());
                    }
                }
                catch {
                }
                const measureKeyboard = () => {
                    const keyboard = navigator.virtualKeyboard;
                    const view = window.visualViewport;
                    let inset = 0;
                    if (keyboard && keyboard.boundingRect && keyboard.boundingRect.height > 0) {
                        inset = window.innerHeight - keyboard.boundingRect.top;
                    }
                    else if (view && window.innerHeight - (view.offsetTop + view.height) > 120) {
                        inset = window.innerHeight - (view.offsetTop + view.height);
                    }
                    applyKeyboardInset(inset);
                };
                if (window.visualViewport) {
                    window.visualViewport.addEventListener('resize', measureKeyboard);
                    window.visualViewport.addEventListener('scroll', measureKeyboard);
                }
            }
            term.onBinary(data => sendBytes(Uint8Array.from(data, c => c.charCodeAt(0))));
            term.onResize(size => sendControl({ type: 'resize', cols: size.cols, rows: size.rows }));
            new ResizeObserver(() => fit.fit()).observe(document.getElementById('screen'));

            let everConnected = false;
            const connect = () => {
                window.clearTimeout(retryTimer);
                setStatus(text.connecting);
                if (everConnected) {
                    // Connecting again: a session that kept running sends what it printed last once more - on a
                    // page that still shows it, it would stand there twice. Cleared here, before anything
                    // arrives (the session's lines come before its "ready").
                    term.reset();
                }
                const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
                const query = new URLSearchParams({ plugin, session, cols: term.cols, rows: term.rows, cwd: params.get('cwd') || '', variant: params.get('variant') || '' });
                socket = new WebSocket(`${scheme}//${location.host}${base}/terminal-socket?${query}`);
                socket.binaryType = 'arraybuffer';
                socket.onmessage = event => {
                    if (typeof event.data !== 'string') {
                        term.write(new Uint8Array(event.data));
                        return;
                    }
                    const message = JSON.parse(event.data);
                    if (message.type === 'ready') {
                        retry = 0;
                        setStatus(message.attached ? text.reattached : text.connected);
                        everConnected = true;
                        // A program that kept running redraws itself on a size change - but the console ignores a
                        // "change" to the size it already has, so it is nudged one column and back.
                        if (message.attached) {
                            sendControl({ type: 'resize', cols: Math.max(10, term.cols - 1), rows: term.rows });
                            window.setTimeout(() => sendControl({ type: 'resize', cols: term.cols, rows: term.rows }), 150);
                        }
                        else {
                            sendControl({ type: 'resize', cols: term.cols, rows: term.rows });
                        }
                    }
                    else if (message.type === 'exit') {
                        ended = true;
                        setStatus(text.ended + (message.code ? ` (${message.code})` : ''), true);
                        restartButton.classList.remove('hidden');
                        killButton.classList.add('hidden');
                    }
                    else if (message.type === 'error') {
                        ended = true;
                        setStatus(message.message, true);
                        restartButton.classList.remove('hidden');
                    }
                };
                socket.onclose = () => {
                    if (ended) {
                        return;
                    }
                    setStatus(text.lost, true);
                    retry = Math.min(retry + 1, 6);
                    retryTimer = window.setTimeout(connect, 500 * retry * retry);
                };
            };

            restartButton.addEventListener('click', () => {
                ended = false;
                session = crypto.randomUUID();
                params.set('session', session);
                history.replaceState(null, '', location.pathname + '?' + params.toString());
                try {
                    // The shell keeps the tab's address (and with it the session) - tell it the new one.
                    if (window.parent !== window && typeof window.parent.MatgateTabAddressChanged === 'function') {
                        window.parent.MatgateTabAddressChanged();
                    }
                }
                catch {
                }
                restartButton.classList.add('hidden');
                killButton.classList.remove('hidden');
                term.reset();
                connect();
            });
            killButton.addEventListener('click', async () => {
                const ask = window.parent && window.parent.MatgateAskToConfirm;
                const confirmed = ask
                    ? await ask({ title: text.kill, text: text.killConfirm, confirm: text.kill })
                    : true;
                if (confirmed) {
                    sendControl({ type: 'kill' });
                }
            });

            document.addEventListener('visibilitychange', () => {
                if (document.visibilityState === 'visible' && !ended && (!socket || socket.readyState > WebSocket.OPEN)) {
                    connect();
                }
            });

            // ---- files (SSH): pasted, dropped or picked, they go to the server through SFTP - into the home
            // folder of the login, which is where a new shell starts as well.
            const note = document.getElementById('note');
            let noteTimer = 0;
            const showNote = (value, bad) => {
                note.textContent = value;
                note.classList.toggle('bad', !!bad);
                note.classList.remove('hidden');
                window.clearTimeout(noteTimer);
                noteTimer = window.setTimeout(() => note.classList.add('hidden'), bad ? 9000 : 6000);
            };
            window.__uploads = [];
            const uploadFiles = async files => {
                if (kind !== 'ssh' || !files.length) {
                    return;
                }
                let folder = '';
                let done = 0;
                for (const file of files) {
                    showNote(text.uploading.replace('{name}', file.name || '').replace('{n}', String(done + 1)).replace('{count}', String(files.length)));
                    let answer = null;
                    try {
                        const response = await fetch(base + '/upload?' + new URLSearchParams({ session, name: file.name || 'Datei' }), {
                            method: 'POST',
                            credentials: 'same-origin',
                            headers: { 'X-Matgate-Csrf': document.body.dataset.csrf, 'Content-Type': 'application/octet-stream' },
                            body: file,
                        });
                        answer = await response.json().catch(() => null);
                        if (!response.ok) {
                            showNote((answer && answer.error) || text.uploadFailed, true);
                            return;
                        }
                    }
                    catch {
                        showNote(text.uploadFailed, true);
                        return;
                    }
                    folder = answer.folder;
                    window.__uploads.push(answer);
                    done++;
                }
                showNote(text.uploaded.replace('{count}', String(done)).replace('{folder}', folder));
                term.focus();
            };
            // What is on the clipboard are files when there is no page content beside them and any text only
            // names them (as a file manager copies): a copied table or a picture from a web page stays text.
            const filesOf = data => {
                const files = data ? [...data.files] : [];
                if (!files.length || data.getData('text/html')) {
                    return [];
                }
                const plain = data.getData('text/plain');
                return !plain || files.every(file => plain.includes(file.name)) ? files : [];
            };
            const screenElement = document.getElementById('screen');
            screenElement.addEventListener('paste', event => {
                const files = kind === 'ssh' ? filesOf(event.clipboardData) : [];
                if (files.length) {
                    event.preventDefault();
                    event.stopPropagation();
                    uploadFiles(files);
                }
            }, true);
            screenElement.addEventListener('dragover', event => {
                if (kind === 'ssh') {
                    event.preventDefault();
                }
            });
            screenElement.addEventListener('drop', event => {
                if (kind === 'ssh' && event.dataTransfer && event.dataTransfer.files.length) {
                    event.preventDefault();
                    uploadFiles([...event.dataTransfer.files]);
                }
            });
            const picker = document.getElementById('pick');
            picker.addEventListener('change', () => {
                uploadFiles([...picker.files]);
                picker.value = '';
            });

            // ---- a phone's keyboard --------------------------------------------------------------------------
            // xterm.js takes keys well and text badly: what a phone's keyboard changes afterwards - dictation
            // that rewrites what it said, a word corrected after the fact - never reaches the shell, or reaches
            // it twice. So on a phone the keyboard types into a field of this page, as on the screen page of
            // RDP and VNC: the field keeps the text exactly as the keyboard left it, and every change goes to
            // the shell as the difference - Backspace for what went, then the new text. Emptying the field
            // under the keyboard makes it deliver everything again, so that only happens after a quiet moment.
            if (matchMedia('(pointer: coarse)').matches) {
                // One character always stays in the field, so that Backspace has something to delete and is
                // reported at all.
                const MARK = ' ';
                const sink = document.createElement('textarea');
                sink.id = 'sink';
                ['autocapitalize', 'autocomplete', 'autocorrect'].forEach(name => sink.setAttribute(name, 'off'));
                sink.setAttribute('spellcheck', 'false');
                sink.setAttribute('aria-hidden', 'true');
                sink.style.cssText = 'border:0;caret-color:transparent;color:transparent;font-size:16px;height:1px;left:0;opacity:0;outline:0;padding:0;pointer-events:none;position:fixed;resize:none;top:0;width:1px;';
                sink.value = MARK;
                document.body.appendChild(sink);
                const own = term.textarea;
                // Whatever gives the terminal the keyboard gives it to the field.
                own.addEventListener('focus', () => sink.focus({ preventScroll: true }));
                try {
                    // The terminal's own field never has the focus here: its cursor should not look switched off.
                    term.options.cursorInactiveStyle = 'block';
                }
                catch {
                }

                let composing = false;
                let typed = MARK;
                // Up to here the field's text belongs to an earlier line or another place on the line.
                let floor = MARK.length;
                let lastTextAt = 0;
                const quiet = () => !composing && Date.now() - lastTextAt > 2000;
                const clearText = () => {
                    if (sink.value !== MARK) {
                        sink.value = MARK;
                    }
                    typed = MARK;
                    floor = MARK.length;
                };
                const boundary = () => {
                    if (quiet()) {
                        clearText();
                    }
                    else {
                        floor = typed.length;
                    }
                };
                const syncText = () => {
                    const now = sink.value;
                    const before = typed;
                    if (now === before) {
                        return;
                    }
                    lastTextAt = Date.now();
                    typed = now;
                    let same = 0;
                    const limit = Math.min(before.length, now.length);
                    while (same < limit && before.charCodeAt(same) === now.charCodeAt(same)) {
                        same++;
                    }
                    if (same > 0 && (before.charCodeAt(same - 1) & 0xfc00) === 0xd800) {
                        same--;
                    }
                    if (same < floor) {
                        if (now.length > same) {
                            // The keyboard rewrote text that is no longer where the cursor is.
                            floor = now.length;
                            return;
                        }
                        floor = same;
                    }
                    let gone = [...before.slice(Math.max(same, MARK.length))].length;
                    if (same < MARK.length && gone === 0 && now.length === 0) {
                        gone = 1;
                    }
                    const added = now.slice(same).replace(/\r\n?|\n/g, '\r');
                    // A modifier that a sticky button of the key bar holds counts for the one character typed next.
                    const bytes = encoder.encode('\x7f'.repeat(gone) + (gone ? added : withSticky(added)));
                    if (bytes.length) {
                        sendBytes(bytes);
                    }
                    if (added.includes('\r')) {
                        floor = typed.length;
                    }
                };
                const settleText = () => {
                    if (!composing && sink.value === '') {
                        clearText();
                    }
                };
                sink.addEventListener('compositionstart', () => composing = true);
                sink.addEventListener('compositionend', () => {
                    composing = false;
                    syncText();
                    settleText();
                });
                sink.addEventListener('input', () => {
                    syncText();
                    settleText();
                });
                sink.addEventListener('focus', () => {
                    if (quiet()) {
                        clearText();
                    }
                });
                sink.addEventListener('blur', () => {
                    composing = false;
                    syncText();
                });
                sink.addEventListener('beforeinput', event => {
                    if (/^(insertFromPaste|insertFromDrop|historyUndo|historyRedo)$/.test(event.inputType)) {
                        event.preventDefault();
                    }
                });
                sink.addEventListener('paste', event => {
                    event.preventDefault();
                    const files = kind === 'ssh' ? filesOf(event.clipboardData) : [];
                    if (files.length) {
                        uploadFiles(files);
                        return;
                    }
                    const plain = event.clipboardData ? event.clipboardData.getData('text/plain') : '';
                    if (plain) {
                        boundary();
                        term.paste(plain);
                    }
                });
                // Keys without text - an arrow, Tab, a shortcut from a keyboard that is plugged in - are the
                // terminal's: it knows what each of them sends.
                const forward = event => {
                    if (event.isComposing || event.key === 'Process' || event.keyCode === 229) {
                        return;
                    }
                    const types = !event.ctrlKey && !event.altKey && !event.metaKey
                        && ([...String(event.key || '')].length === 1 || event.key === 'Backspace' || event.key === 'Enter');
                    if (types) {
                        return;
                    }
                    event.preventDefault();
                    if (event.type === 'keydown') {
                        boundary();
                    }
                    own.dispatchEvent(new KeyboardEvent(event.type, event));
                };
                sink.addEventListener('keydown', forward);
                sink.addEventListener('keyup', forward);
                // The key bar moves the cursor or ends the line just the same.
                document.getElementById('keys').addEventListener('click', boundary);
                if (document.activeElement === own) {
                    sink.focus({ preventScroll: true });
                }
            }

            // The session toolbar's side (SSH): disconnect, connect again, paste.
            window.MatgateTerminal = {
                state: () => ({ status: statusLine.textContent, ended, rows: term.rows, cols: term.cols }),
                disconnect: () => sendControl({ type: 'kill' }),
                reconnect: () => restartButton.click(),
                paste: value => {
                    sendBytes(encoder.encode(value));
                    term.focus();
                },
                pickFiles: () => picker.click(),
                focus: () => term.focus(),
            };

            connect();
        })();
        </script>
        </body>
        </html>
        """;
}
