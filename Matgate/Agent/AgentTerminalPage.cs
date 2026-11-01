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
            body { display: flex; flex-direction: column; font: 13px system-ui, sans-serif; }
            .bar { align-items: center; border-bottom: 1px solid #263029; display: flex; gap: 8px; min-height: 34px; padding: 0 8px; }
            .bar strong { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .status { color: #8fa39a; flex: 1 1 auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .status.bad { color: #f0a92b; }
            button { background: #1b2420; border: 1px solid #33413a; border-radius: 6px; color: inherit; cursor: pointer; font: inherit; min-height: 28px; padding: 2px 10px; }
            button:hover { background: #24302a; }
            #screen { flex: 1 1 auto; min-height: 0; padding: 4px 0 0 6px; }
            .keys { border-top: 1px solid #263029; display: none; gap: 4px; overflow-x: auto; padding: 4px 6px calc(4px + env(safe-area-inset-bottom)); }
            .keys button { flex: 0 0 auto; min-height: 36px; min-width: 44px; }
            @media (pointer: coarse) { .keys { display: flex; } }
            .hidden { display: none !important; }
            button.icon { align-items: center; display: inline-flex; justify-content: center; min-width: 32px; padding: 2px 6px; }
        </style>
        </head>
        <body data-server="__SERVER__" data-csrf="__CSRF__" data-text="__TEXT__">
        <div class="bar">
            <button id="home" type="button" class="icon" title=""><svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></svg></button>
            <strong>__TITLE__</strong>
            <span id="status" class="status"></span>
            <button id="restart" type="button" class="hidden"></button>
            <button id="kill" type="button"></button>
        </div>
        <div id="screen"></div>
        <div class="keys" id="keys">
            <button type="button" data-send="1b">Esc</button>
            <button type="button" data-send="09">Tab</button>
            <button type="button" data-send="1b5b5a">&#8679;Tab</button>
            <button type="button" data-send="03">Strg+C</button>
            <button type="button" data-send="1b5b41">&#8593;</button>
            <button type="button" data-send="1b5b42">&#8595;</button>
            <button type="button" data-send="1b5b44">&#8592;</button>
            <button type="button" data-send="1b5b43">&#8594;</button>
            <button type="button" data-send="0d">&#9166;</button>
        </div>
        <script src="/assets/xterm/xterm.js"></script>
        <script src="/assets/xterm/addon-fit.js"></script>
        <script>
        (() => {
            const text = JSON.parse(document.body.dataset.text);
            const server = document.body.dataset.server;
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
            if (!(window.parent && window.parent !== window && window.parent.MatgateShowAgent)) {
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
                fontSize: matchMedia('(pointer: coarse)').matches ? 12 : 14,
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

            term.onData(data => sendBytes(encoder.encode(data)));
            term.onBinary(data => sendBytes(Uint8Array.from(data, c => c.charCodeAt(0))));
            term.onResize(size => sendControl({ type: 'resize', cols: size.cols, rows: size.rows }));
            new ResizeObserver(() => fit.fit()).observe(document.getElementById('screen'));

            document.getElementById('keys').addEventListener('click', event => {
                const button = event.target.closest('[data-send]');
                if (!button) {
                    return;
                }
                const hex = button.dataset.send;
                sendBytes(Uint8Array.from(hex.match(/../g), pair => parseInt(pair, 16)));
                term.focus();
            });
            // The bar's buttons must not take the keyboard away from the terminal.
            document.querySelectorAll('.keys button').forEach(button => button.addEventListener('mousedown', event => event.preventDefault()));

            const connect = () => {
                window.clearTimeout(retryTimer);
                setStatus(text.connecting);
                const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
                const query = new URLSearchParams({ plugin, session, cols: term.cols, rows: term.rows, cwd: params.get('cwd') || '' });
                socket = new WebSocket(`${scheme}//${location.host}/api/agent/${server}/terminal-socket?${query}`);
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
                        // A program that kept running redraws itself on a size change.
                        sendControl({ type: 'resize', cols: term.cols, rows: term.rows });
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

            connect();
        })();
        </script>
        </body>
        </html>
        """;
}
