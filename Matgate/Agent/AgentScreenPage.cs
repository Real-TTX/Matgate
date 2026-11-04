namespace Matgate.Agent;

// The screen of an agent's machine in the browser: H.264 from the agent, decoded by the browser itself
// (WebCodecs) onto a canvas; mouse, keyboard and touch go back as input; sound as Opus (or PCM); the
// clipboard works both ways - text, pictures and files. Its own small page, opened as a shell tab.
public static class AgentScreenPage
{
    public const string Html = """
        <!doctype html>
        <html lang="__LANG__">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover, interactive-widget=resizes-content">
        <title>__TITLE__</title>
        <style>
            html, body { background: #0b0f0d; color: #d8e0dc; height: 100%; margin: 0; overflow: hidden; }
            body { display: flex; flex-direction: column; font: 13px system-ui, sans-serif; }
            .bar { align-items: center; border-bottom: 1px solid #263029; display: flex; flex: 0 0 auto; gap: 6px; min-height: 36px; padding: 0 8px; }
            .bar strong { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .status { color: #8fa39a; flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .status.bad { color: #f0a92b; }
            button, select { background: #1b2420; border: 1px solid #33413a; border-radius: 6px; color: inherit; cursor: pointer; font: inherit; min-height: 28px; padding: 2px 8px; }
            button:hover, select:hover { background: #24302a; }
            button.icon { align-items: center; display: inline-flex; justify-content: center; min-width: 32px; padding: 2px 6px; }
            button.on { border-color: #4f8a6c; color: #9fe0bd; }
            button svg { height: 18px; width: 18px; }
            #stage { flex: 1 1 auto; min-height: 0; overflow: hidden; position: relative; touch-action: none; user-select: none; -webkit-user-select: none; }
            #frame { left: 0; position: absolute; top: 0; transform-origin: 0 0; }
            #screen { background: #000; display: block; height: 100%; image-rendering: auto; width: 100%; }
            #pointer { left: 0; pointer-events: none; position: absolute; top: 0; }
            #sink { border: 0; caret-color: transparent; color: transparent; font-size: 16px; height: 1px; left: 0; opacity: 0; outline: 0; padding: 0; pointer-events: none; position: fixed; resize: none; top: 0; width: 1px; }
            .notice { align-items: center; background: rgba(11, 15, 13, .82); display: flex; flex-direction: column; gap: 12px; inset: 0; justify-content: center; padding: 24px; position: absolute; text-align: center; }
            .notice p { font-size: 15px; margin: 0; max-width: 460px; }
            .toast { background: #18211d; border: 1px solid #33413a; border-radius: 10px; bottom: 16px; box-shadow: 0 8px 24px rgba(0,0,0,.5); max-width: min(420px, calc(100% - 32px)); padding: 12px 14px; position: absolute; right: 16px; }
            .toast ul { list-style: none; margin: 8px 0; max-height: 40vh; overflow: auto; padding: 0; }
            .toast li { align-items: center; display: flex; gap: 8px; justify-content: space-between; padding: 3px 0; }
            .toast li span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .toast a { color: #9fe0bd; white-space: nowrap; }
            .toast .muted { color: #8fa39a; }
            .keys { border-top: 1px solid #263029; display: none; flex: 0 0 auto; gap: 4px; overflow-x: auto; padding: 4px 6px calc(4px + env(safe-area-inset-bottom)); }
            .keys button { flex: 0 0 auto; min-height: 36px; min-width: 44px; }
            @media (pointer: coarse) { .keys { display: flex; } .wide { display: none; } }
            .hidden { display: none !important; }
        </style>
        </head>
        <body data-server="__SERVER__" data-text="__TEXT__">
        <div class="bar">
            <button id="home" type="button" class="icon"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></svg></button>
            <strong>__TITLE__</strong>
            <span id="status" class="status"></span>
            <select id="monitor" class="hidden"></select>
            <select id="quality" class="wide"></select>
            <button id="sound" type="button" class="icon"></button>
            <button id="keyboard" type="button" class="icon"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="6" width="20" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/></svg></button>
            <button id="fullscreen" type="button" class="icon"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/></svg></button>
        </div>
        <div id="stage">
            <div id="frame"><canvas id="screen" width="16" height="9"></canvas><img id="pointer" class="hidden" alt=""></div>
            <div id="notice" class="notice"><p id="notice-text"></p><button id="notice-action" type="button" class="hidden"></button></div>
            <div id="toast" class="toast hidden"></div>
        </div>
        <div class="keys" id="keys">
            <button type="button" data-key="01">Esc</button>
            <button type="button" data-key="0f">Tab</button>
            <button type="button" data-key="5be">&#8862;</button>
            <button type="button" data-sticky="1d">Strg</button>
            <button type="button" data-sticky="38">Alt</button>
            <button type="button" data-key="53e">Entf</button>
            <button type="button" data-key="4be">&#8592;</button>
            <button type="button" data-key="48e">&#8593;</button>
            <button type="button" data-key="50e">&#8595;</button>
            <button type="button" data-key="4de">&#8594;</button>
        </div>
        <textarea id="sink" autocapitalize="off" autocomplete="off" autocorrect="off" spellcheck="false" aria-hidden="true"></textarea>
        <script>
        (() => {
            const text = JSON.parse(document.body.dataset.text);
            const server = document.body.dataset.server;
            const params = new URLSearchParams(location.search);
            const $ = id => document.getElementById(id);
            const stage = $('stage');
            const frameBox = $('frame');
            const canvas = $('screen');
            const context = canvas.getContext('2d', { alpha: false, desynchronized: true });
            const pointerImage = $('pointer');
            const sink = $('sink');
            const statusLine = $('status');
            const notice = $('notice');
            const noticeText = $('notice-text');
            const noticeAction = $('notice-action');
            const toast = $('toast');
            const coarse = matchMedia('(pointer: coarse)').matches;
            const isMac = /Mac|iPhone|iPad/.test(navigator.platform || '');
            window.__screen = { debug: [], frames: 0, audio: 0, clipboard: [], downloads: [] };

            const setStatus = (value, bad) => {
                statusLine.textContent = value || '';
                statusLine.classList.toggle('bad', !!bad);
            };
            const showNotice = (message, action, onAction) => {
                noticeText.textContent = message;
                notice.classList.remove('hidden');
                noticeAction.classList.toggle('hidden', !action);
                noticeAction.textContent = action || '';
                noticeAction.onclick = onAction || null;
            };
            const hideNotice = () => notice.classList.add('hidden');

            // ---- the agent's own icon: back to its overview in the shell -----------------------------------
            const homeButton = $('home');
            homeButton.title = text.overview;
            homeButton.setAttribute('aria-label', text.overview);
            if (!(window.parent && window.parent !== window && window.parent.MatgateShowAgent)) {
                homeButton.classList.add('hidden');
            }
            homeButton.addEventListener('click', () => window.parent.MatgateShowAgent(server));

            if (!('VideoDecoder' in window)) {
                showNotice(text.noDecoder);
                return;
            }

            // ---- the wire: [4 bytes header length][JSON header][payload], as between Matgate and the agent --
            const encoder = new TextEncoder();
            const decoder = new TextDecoder();
            const encodeFrame = (op, data, payload) => {
                const header = encoder.encode(JSON.stringify({ kind: 'evt', op, data }));
                const body = payload ? new Uint8Array(payload) : new Uint8Array(0);
                const message = new Uint8Array(4 + header.length + body.length);
                new DataView(message.buffer).setInt32(0, header.length);
                message.set(header, 4);
                message.set(body, 4 + header.length);
                return message;
            };
            const decodeFrame = buffer => {
                const length = new DataView(buffer).getInt32(0);
                const header = JSON.parse(decoder.decode(new Uint8Array(buffer, 4, length)));
                header.payload = new Uint8Array(buffer, 4 + length);
                header.data = header.data || {};
                return header;
            };

            let socket = null;
            let session = '';
            let ended = false;
            let retry = 0;
            let retryTimer = 0;
            const send = (op, data, payload) => {
                if (socket && socket.readyState === WebSocket.OPEN) {
                    socket.send(encodeFrame(op, data || {}, payload));
                }
            };

            // ---- what is shown: the monitor's size (for input) and the video's size (for the canvas) ------
            const remote = { width: 0, height: 0 };
            let zoom = 1;
            let panX = 0;
            let panY = 0;
            const layout = () => {
                const width = canvas.width;
                const height = canvas.height;
                const box = stage.getBoundingClientRect();
                const fit = Math.min(box.width / width, box.height / height) || 1;
                const shownWidth = width * fit;
                const shownHeight = height * fit;
                frameBox.style.width = shownWidth + 'px';
                frameBox.style.height = shownHeight + 'px';
                const scaledWidth = shownWidth * zoom;
                const scaledHeight = shownHeight * zoom;
                // Centred while it fits, otherwise moved by the pan within its edges.
                const left = scaledWidth <= box.width ? (box.width - scaledWidth) / 2 : Math.min(0, Math.max(box.width - scaledWidth, panX));
                const top = scaledHeight <= box.height ? (box.height - scaledHeight) / 2 : Math.min(0, Math.max(box.height - scaledHeight, panY));
                panX = left;
                panY = top;
                frameBox.style.transform = `translate(${left}px, ${top}px) scale(${zoom})`;
                placePointer();
            };
            new ResizeObserver(layout).observe(stage);

            // ---- video --------------------------------------------------------------------------------------
            let video = null;
            let configured = '';
            let pending = [];
            let lastKeyRequest = 0;
            let shown = false;
            const requestKey = () => {
                const now = performance.now();
                if (now - lastKeyRequest > 400) {
                    lastKeyRequest = now;
                    send('screen.key', {});
                }
            };
            // The codec string from the stream's own SPS (profile, constraints, level).
            const codecOf = bytes => {
                for (let i = 0; i + 7 < bytes.length; i++) {
                    if (bytes[i] === 0 && bytes[i + 1] === 0 && bytes[i + 2] === 1 && (bytes[i + 3] & 0x1f) === 7) {
                        const hex = value => value.toString(16).padStart(2, '0');
                        return 'avc1.' + hex(bytes[i + 4]) + hex(bytes[i + 5]) + hex(bytes[i + 6]);
                    }
                }
                return 'avc1.42e01f';
            };
            const resetVideo = () => {
                try {
                    video && video.state !== 'closed' && video.close();
                }
                catch {
                }
                video = null;
                configured = '';
                if (pending.length) {
                    send('screen.ack', { seq: pending[pending.length - 1] });
                }
                pending = [];
                requestKey();
            };
            const createVideo = () => new VideoDecoder({
                output: picture => {
                    if (canvas.width !== picture.displayWidth || canvas.height !== picture.displayHeight) {
                        canvas.width = picture.displayWidth;
                        canvas.height = picture.displayHeight;
                        layout();
                    }
                    context.drawImage(picture, 0, 0);
                    picture.close();
                    const sequence = pending.shift();
                    if (sequence) {
                        send('screen.ack', { seq: sequence });
                    }
                    window.__screen.frames++;
                    if (!shown) {
                        shown = true;
                        hideNotice();
                    }
                },
                error: () => resetVideo(),
            });
            const onVideo = (data, bytes) => {
                if (!video || video.state === 'closed') {
                    video = createVideo();
                    configured = '';
                }
                if (data.key) {
                    const codec = codecOf(bytes);
                    const wanted = codec + ':' + data.width + 'x' + data.height;
                    if (wanted !== configured) {
                        video.configure({ codec, codedWidth: data.width, codedHeight: data.height, optimizeForLatency: true });
                        configured = wanted;
                    }
                }
                else if (!configured) {
                    send('screen.ack', { seq: data.seq });
                    requestKey();
                    return;
                }
                pending.push(data.seq);
                try {
                    video.decode(new EncodedVideoChunk({ type: data.key ? 'key' : 'delta', timestamp: data.seq * 33333, data: bytes }));
                }
                catch {
                    resetVideo();
                }
            };

            // ---- the mouse pointer: the machine's own cursor shape, shown by the browser ------------------
            let pointerUrl = '';
            let pointerHot = { x: 0, y: 0 };
            let pointerAt = { x: 0, y: 0, visible: false };
            let touchMode = coarse;
            const placePointer = () => {
                const show = touchMode && pointerAt.visible && pointerUrl && remote.width;
                pointerImage.classList.toggle('hidden', !show);
                if (show) {
                    const scale = parseFloat(frameBox.style.width) / remote.width;
                    pointerImage.style.transform = `translate(${pointerAt.x * scale - pointerHot.x}px, ${pointerAt.y * scale - pointerHot.y}px)`;
                }
            };
            const onCursor = (data, bytes) => {
                if (pointerUrl) {
                    URL.revokeObjectURL(pointerUrl);
                }
                pointerUrl = URL.createObjectURL(new Blob([bytes], { type: 'image/png' }));
                pointerHot = { x: data.x || 0, y: data.y || 0 };
                pointerImage.src = pointerUrl;
                updateCursor();
            };
            const updateCursor = () => {
                canvas.style.cursor = !pointerAt.visible ? 'none' : pointerUrl ? `url(${pointerUrl}) ${pointerHot.x} ${pointerHot.y}, default` : 'default';
                placePointer();
            };

            // ---- connecting -----------------------------------------------------------------------------
            const monitorSelect = $('monitor');
            const qualitySelect = $('quality');
            const quality = localStorage.getItem('matgate.screen.quality') || 'auto';
            [['auto', text.qualityHigh], ['medium', text.qualityMedium], ['low', text.qualityLow]].forEach(([value, label]) => qualitySelect.add(new Option(label, value, false, value === quality)));
            qualitySelect.title = text.quality;
            qualitySelect.addEventListener('change', () => {
                localStorage.setItem('matgate.screen.quality', qualitySelect.value);
                send('screen.config', { quality: qualitySelect.value });
            });
            monitorSelect.title = text.monitor;
            monitorSelect.addEventListener('change', () => send('screen.config', { monitor: Number(monitorSelect.value) }));

            const connect = () => {
                window.clearTimeout(retryTimer);
                ended = false;
                shown = false;
                resetVideo();
                pending = [];
                showNotice(text.connecting);
                setStatus(text.connecting);
                session = crypto.randomUUID();
                const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
                const maxWidth = Math.max(1280, Math.round(Math.max(screen.width, screen.height) * (window.devicePixelRatio || 1)));
                const query = new URLSearchParams({
                    session,
                    monitor: localStorage.getItem('matgate.screen.monitor.' + server) || '0',
                    quality: qualitySelect.value,
                    maxWidth: String(Math.min(2560, maxWidth)),
                });
                socket = new WebSocket(`${scheme}//${location.host}/api/agent/${server}/screen-socket?${query}`);
                socket.binaryType = 'arraybuffer';
                socket.onmessage = event => {
                    if (typeof event.data === 'string') {
                        return;
                    }
                    const message = decodeFrame(event.data);
                    handle(message.op, message.data, message.payload);
                };
                socket.onclose = () => {
                    releaseAll();
                    stopSound(false);
                    if (ended) {
                        return;
                    }
                    setStatus(text.lost, true);
                    showNotice(text.lost);
                    retry = Math.min(retry + 1, 6);
                    retryTimer = window.setTimeout(connect, 400 * retry * retry);
                };
            };

            const handle = (op, data, payload) => {
                switch (op) {
                    case 'screen.ready':
                        retry = 0;
                        setStatus(text.connected);
                        remote.width = data.width;
                        remote.height = data.height;
                        monitorSelect.replaceChildren(...(data.monitors || []).map(item => new Option(`${item.index + 1}: ${item.width}×${item.height}`, item.index, false, item.index === data.monitor)));
                        monitorSelect.classList.toggle('hidden', (data.monitors || []).length < 2);
                        if (soundWanted && soundUnlocked) {
                            startSound();
                        }
                        break;
                    case 'screen.monitor':
                        remote.width = data.width;
                        remote.height = data.height;
                        monitorSelect.value = String(data.monitor);
                        localStorage.setItem('matgate.screen.monitor.' + server, String(data.monitor));
                        break;
                    case 'screen.video':
                        onVideo(data, payload);
                        break;
                    case 'screen.cursor':
                        onCursor(data, payload);
                        break;
                    case 'screen.pointer':
                        pointerAt = { x: data.x, y: data.y, visible: !!data.visible };
                        updateCursor();
                        break;
                    case 'screen.audio':
                        onAudio(data, payload);
                        break;
                    case 'screen.clip':
                        onRemoteClipboard(data, payload);
                        break;
                    case 'screen.clip.done':
                        setStatus(text.connected);
                        break;
                    case 'screen.status':
                        if (data.state === 'unavailable') {
                            showNotice(text.unavailable);
                        }
                        else if (data.state === 'ok') {
                            hideNotice();
                        }
                        else if (data.state === 'nosound') {
                            stopSound(false);
                            setStatus(text.noSound, true);
                        }
                        else if (data.state === 'clipfailed') {
                            setStatus(text.clipFailed, true);
                        }
                        else if (data.state === 'error') {
                            setStatus(data.message || text.error, true);
                        }
                        break;
                    case 'screen.ended':
                        ended = true;
                        releaseAll();
                        stopSound(false);
                        if (data.reason === 'taken') {
                            setStatus(text.taken, true);
                            showNotice(text.taken, text.takeBack, connect);
                        }
                        else {
                            setStatus(text.ended, true);
                            showNotice(text.ended, text.reconnect, connect);
                        }
                        socket && socket.close();
                        break;
                    case 'screen.error':
                        ended = true;
                        setStatus(data.message || text.error, true);
                        showNotice(data.message || text.error, text.reconnect, connect);
                        break;
                    case 'screen.debug':
                        window.__screen.debug.push(data.text);
                        break;
                }
            };

            // ---- mouse ----------------------------------------------------------------------------------
            // Browser coordinates to the machine's: the canvas shows the whole monitor, scaled and maybe zoomed.
            const toRemote = (clientX, clientY) => {
                const box = canvas.getBoundingClientRect();
                return {
                    x: Math.round((clientX - box.left) / box.width * remote.width),
                    y: Math.round((clientY - box.top) / box.height * remote.height),
                };
            };
            let moveQueued = null;
            const queueMove = (clientX, clientY) => {
                const first = !moveQueued;
                moveQueued = toRemote(clientX, clientY);
                if (first) {
                    requestAnimationFrame(() => {
                        if (moveQueued) {
                            send('screen.input', { t: 'm', x: moveQueued.x, y: moveQueued.y });
                            if (touchMode) {
                                pointerAt = { ...pointerAt, x: moveQueued.x, y: moveQueued.y, visible: true };
                                placePointer();
                            }
                            moveQueued = null;
                        }
                    });
                }
            };
            const flushMove = () => {
                if (moveQueued) {
                    send('screen.input', { t: 'm', x: moveQueued.x, y: moveQueued.y });
                    moveQueued = null;
                }
            };
            const buttonsDown = new Set();
            const button = (number, down) => {
                if (down ? buttonsDown.has(number) : !buttonsDown.has(number)) {
                    return;
                }
                down ? buttonsDown.add(number) : buttonsDown.delete(number);
                flushMove();
                send('screen.input', { t: 'b', b: number, d: down });
            };
            const focusKeyboard = () => {
                if (!coarse && document.activeElement !== sink) {
                    sink.focus({ preventScroll: true });
                }
            };

            canvas.addEventListener('pointerdown', event => {
                unlockSound();
                if (event.pointerType === 'touch') {
                    return;
                }
                touchMode = false;
                placePointer();
                event.preventDefault();
                focusKeyboard();
                canvas.setPointerCapture(event.pointerId);
                queueMove(event.clientX, event.clientY);
                button(event.button, true);
            });
            canvas.addEventListener('pointermove', event => {
                if (event.pointerType !== 'touch') {
                    queueMove(event.clientX, event.clientY);
                }
            });
            canvas.addEventListener('pointerup', event => {
                if (event.pointerType !== 'touch') {
                    queueMove(event.clientX, event.clientY);
                    button(event.button, false);
                }
            });
            canvas.addEventListener('contextmenu', event => event.preventDefault());
            let wheelX = 0;
            let wheelY = 0;
            stage.addEventListener('wheel', event => {
                event.preventDefault();
                const factor = event.deltaMode === 1 ? 40 : event.deltaMode === 2 ? 400 : 1.2;
                wheelY += -event.deltaY * factor;
                wheelX += event.deltaX * factor;
                const dy = Math.trunc(wheelY);
                const dx = Math.trunc(wheelX);
                if (dy || dx) {
                    wheelY -= dy;
                    wheelX -= dx;
                    queueMove(event.clientX, event.clientY);
                    flushMove();
                    send('screen.input', { t: 'w', dy, dx });
                }
            }, { passive: false });

            // ---- touch: one finger is the mouse, two fingers zoom, move the picture or scroll ------------
            const touches = new Map();
            let gesture = null;
            let longPress = 0;
            const tap = (clientX, clientY, number) => {
                queueMove(clientX, clientY);
                flushMove();
                button(number, true);
                button(number, false);
            };
            stage.addEventListener('pointerdown', event => {
                if (event.pointerType !== 'touch') {
                    return;
                }
                unlockSound();
                touchMode = true;
                event.preventDefault();
                touches.set(event.pointerId, { x: event.clientX, y: event.clientY, startX: event.clientX, startY: event.clientY });
                try {
                    stage.setPointerCapture(event.pointerId);
                }
                catch {
                    // A pointer the browser no longer knows: the gesture works without the capture.
                }
                window.clearTimeout(longPress);
                if (touches.size === 1) {
                    gesture = { kind: 'one', moved: false, dragging: false, start: performance.now() };
                    longPress = window.setTimeout(() => {
                        if (gesture && gesture.kind === 'one' && !gesture.moved) {
                            gesture.kind = 'done';
                            tap(event.clientX, event.clientY, 2);
                        }
                    }, 550);
                }
                else if (touches.size === 2) {
                    if (gesture && gesture.dragging) {
                        button(0, false);
                    }
                    const [a, b] = [...touches.values()];
                    gesture = {
                        kind: 'two', moved: false, start: performance.now(),
                        distance: Math.hypot(a.x - b.x, a.y - b.y), zoom,
                        centerX: (a.x + b.x) / 2, centerY: (a.y + b.y) / 2, panX, panY,
                    };
                }
            });
            stage.addEventListener('pointermove', event => {
                const touch = touches.get(event.pointerId);
                if (!touch) {
                    return;
                }
                event.preventDefault();
                touch.x = event.clientX;
                touch.y = event.clientY;
                if (!gesture) {
                    return;
                }
                if (gesture.kind === 'one') {
                    if (!gesture.moved && Math.hypot(touch.x - touch.startX, touch.y - touch.startY) > 8) {
                        gesture.moved = true;
                        window.clearTimeout(longPress);
                        // Finger down and moving: a drag with the left button, like a mouse.
                        queueMove(touch.startX, touch.startY);
                        flushMove();
                        button(0, true);
                        gesture.dragging = true;
                    }
                    if (gesture.dragging) {
                        queueMove(touch.x, touch.y);
                    }
                }
                else if (gesture.kind === 'two' && touches.size === 2) {
                    const [a, b] = [...touches.values()];
                    const distance = Math.hypot(a.x - b.x, a.y - b.y);
                    const centerX = (a.x + b.x) / 2;
                    const centerY = (a.y + b.y) / 2;
                    if (!gesture.moved && (Math.abs(distance - gesture.distance) > 12 || Math.hypot(centerX - gesture.centerX, centerY - gesture.centerY) > 8)) {
                        gesture.moved = true;
                    }
                    const pinching = Math.abs(distance - gesture.distance) > 30 || gesture.zoom > 1 || gesture.pinched;
                    if (pinching) {
                        gesture.pinched = true;
                        const box = stage.getBoundingClientRect();
                        const next = Math.min(5, Math.max(1, gesture.zoom * distance / gesture.distance));
                        // Keep the point between the fingers under the fingers.
                        const originX = (gesture.centerX - box.left - gesture.panX) / gesture.zoom;
                        const originY = (gesture.centerY - box.top - gesture.panY) / gesture.zoom;
                        zoom = next;
                        panX = centerX - box.left - originX * next;
                        panY = centerY - box.top - originY * next;
                        layout();
                    }
                    else if (gesture.moved) {
                        const step = (centerY - (gesture.lastY ?? gesture.centerY)) * 4;
                        gesture.lastY = centerY;
                        if (Math.abs(step) >= 1) {
                            queueMove(centerX, centerY);
                            flushMove();
                            send('screen.input', { t: 'w', dy: Math.round(step), dx: 0 });
                        }
                    }
                }
            });
            const endTouch = event => {
                if (!touches.has(event.pointerId)) {
                    return;
                }
                touches.delete(event.pointerId);
                window.clearTimeout(longPress);
                if (!gesture) {
                    return;
                }
                if (gesture.kind === 'one') {
                    if (gesture.dragging) {
                        queueMove(event.clientX, event.clientY);
                        button(0, false);
                    }
                    else if (!gesture.moved) {
                        tap(event.clientX, event.clientY, 0);
                    }
                    gesture = null;
                }
                else if (gesture.kind === 'two' && touches.size === 0) {
                    // Two fingers, tapped: the right mouse button.
                    if (!gesture.moved && performance.now() - gesture.start < 400) {
                        const box = stage.getBoundingClientRect();
                        tap(gesture.centerX, gesture.centerY, 2);
                    }
                    gesture = null;
                }
            };
            stage.addEventListener('pointerup', endTouch);
            stage.addEventListener('pointercancel', endTouch);

            // ---- keyboard: scan codes, so the machine's own layout applies ------------------------------
            const SCAN = {
                Escape: 0x01, Digit1: 0x02, Digit2: 0x03, Digit3: 0x04, Digit4: 0x05, Digit5: 0x06, Digit6: 0x07, Digit7: 0x08, Digit8: 0x09, Digit9: 0x0a, Digit0: 0x0b,
                Minus: 0x0c, Equal: 0x0d, Backspace: 0x0e, Tab: 0x0f, KeyQ: 0x10, KeyW: 0x11, KeyE: 0x12, KeyR: 0x13, KeyT: 0x14, KeyY: 0x15, KeyU: 0x16, KeyI: 0x17,
                KeyO: 0x18, KeyP: 0x19, BracketLeft: 0x1a, BracketRight: 0x1b, Enter: 0x1c, ControlLeft: 0x1d, KeyA: 0x1e, KeyS: 0x1f, KeyD: 0x20, KeyF: 0x21,
                KeyG: 0x22, KeyH: 0x23, KeyJ: 0x24, KeyK: 0x25, KeyL: 0x26, Semicolon: 0x27, Quote: 0x28, Backquote: 0x29, ShiftLeft: 0x2a, Backslash: 0x2b,
                KeyZ: 0x2c, KeyX: 0x2d, KeyC: 0x2e, KeyV: 0x2f, KeyB: 0x30, KeyN: 0x31, KeyM: 0x32, Comma: 0x33, Period: 0x34, Slash: 0x35, ShiftRight: 0x36,
                NumpadMultiply: 0x37, AltLeft: 0x38, Space: 0x39, CapsLock: 0x3a, F1: 0x3b, F2: 0x3c, F3: 0x3d, F4: 0x3e, F5: 0x3f, F6: 0x40, F7: 0x41,
                F8: 0x42, F9: 0x43, F10: 0x44, ScrollLock: 0x46, Numpad7: 0x47, Numpad8: 0x48, Numpad9: 0x49, NumpadSubtract: 0x4a, Numpad4: 0x4b,
                Numpad5: 0x4c, Numpad6: 0x4d, NumpadAdd: 0x4e, Numpad1: 0x4f, Numpad2: 0x50, Numpad3: 0x51, Numpad0: 0x52, NumpadDecimal: 0x53,
                IntlBackslash: 0x56, F11: 0x57, F12: 0x58, IntlRo: 0x73, F13: 0x64, F14: 0x65, F15: 0x66, F16: 0x67, F17: 0x68, F18: 0x69, F19: 0x6a,
            };
            const EXTENDED = {
                NumpadEnter: 0x1c, ControlRight: 0x1d, NumpadDivide: 0x35, PrintScreen: 0x37, AltRight: 0x38, NumLock: 0x45, Home: 0x47, ArrowUp: 0x48,
                PageUp: 0x49, ArrowLeft: 0x4b, ArrowRight: 0x4d, End: 0x4f, ArrowDown: 0x50, PageDown: 0x51, Insert: 0x52, Delete: 0x53,
                MetaLeft: 0x5b, MetaRight: 0x5c, ContextMenu: 0x5d, OSLeft: 0x5b, OSRight: 0x5c,
            };
            // A phone's keyboard often names no key position, only the key.
            const BY_KEY = { Backspace: 'Backspace', Enter: 'Enter', Tab: 'Tab', Escape: 'Escape', ArrowLeft: 'ArrowLeft', ArrowRight: 'ArrowRight', ArrowUp: 'ArrowUp', ArrowDown: 'ArrowDown', Delete: 'Delete', Home: 'Home', End: 'End' };
            const scanOf = event => {
                let code = event.code || BY_KEY[event.key] || '';
                // On a Mac, Cmd does what Ctrl does on Windows.
                if (isMac && (code === 'MetaLeft' || code === 'MetaRight')) {
                    code = 'ControlLeft';
                }
                if (code in SCAN) {
                    return { s: SCAN[code], e: false, code };
                }
                if (code in EXTENDED) {
                    return { s: EXTENDED[code], e: true, code };
                }
                return null;
            };
            const keysDown = new Map();
            const key = (scan, down) => {
                const id = scan.s + (scan.e ? 'e' : '');
                if (down) {
                    keysDown.set(id, scan);
                }
                else if (!keysDown.delete(id)) {
                    return;
                }
                send('screen.input', { t: 'k', s: scan.s, e: scan.e, d: down });
            };
            const releaseAll = () => {
                if (keysDown.size || buttonsDown.size) {
                    send('screen.input', { t: 'r' });
                }
                keysDown.clear();
                buttonsDown.clear();
                sticky.clear();
                document.querySelectorAll('[data-sticky]').forEach(item => item.classList.remove('on'));
                heldPaste && window.clearTimeout(heldPaste.timer);
                heldPaste = null;
            };

            // Ctrl+V / Shift+Insert: the key press waits a moment for the browser's paste event, the only way
            // to see what is on this device's clipboard. With something there, it goes to the machine's
            // clipboard and is pasted there; otherwise the key press goes on as it is.
            let heldPaste = null;
            const swallowed = new Set();
            const isPasteChord = event => !event.altKey
                && ((((isMac ? event.metaKey : event.ctrlKey) && !event.shiftKey) && (event.code === 'KeyV' || String(event.key).toLowerCase() === 'v'))
                    || (event.shiftKey && !event.ctrlKey && event.code === 'Insert'));
            const passPasteOn = () => {
                if (!heldPaste) {
                    return;
                }
                window.clearTimeout(heldPaste.timer);
                const scan = heldPaste.scan;
                heldPaste = null;
                key(scan, true);
                key(scan, false);
            };

            window.addEventListener('keydown', event => {
                unlockSound();
                writePendingClipboard();
                // The bar's own controls keep their keys (choosing a monitor with the arrow keys).
                if (event.target instanceof HTMLSelectElement) {
                    return;
                }
                if (event.isComposing || event.key === 'Process' || event.keyCode === 229) {
                    return;
                }
                const scan = scanOf(event);
                if (!scan) {
                    return;
                }
                if (isPasteChord(event)) {
                    // Not cancelled: the browser fires "paste" only for a key press that was not.
                    if (document.activeElement !== sink) {
                        sink.focus({ preventScroll: true });
                    }
                    if (!heldPaste) {
                        heldPaste = { scan, timer: window.setTimeout(passPasteOn, 300) };
                    }
                    swallowed.add(scan.code);
                    return;
                }
                event.preventDefault();
                key(scan, true);
            });
            window.addEventListener('keyup', event => {
                const scan = scanOf(event);
                if (!scan) {
                    return;
                }
                event.preventDefault();
                if (swallowed.delete(scan.code)) {
                    return;
                }
                key(scan, false);
            });

            // Text from a phone's keyboard or an input method arrives as text, not as keys.
            let composing = false;
            sink.addEventListener('compositionstart', () => composing = true);
            sink.addEventListener('compositionend', event => {
                composing = false;
                if (event.data) {
                    send('screen.input', { t: 'u', text: event.data });
                }
                sink.value = '';
            });
            sink.addEventListener('beforeinput', event => {
                if (composing || event.isComposing) {
                    return;
                }
                const tapKey = scan => {
                    key(scan, true);
                    key(scan, false);
                };
                if (event.inputType === 'insertText' && event.data) {
                    send('screen.input', { t: 'u', text: event.data });
                }
                else if (event.inputType === 'deleteContentBackward') {
                    tapKey({ s: 0x0e, e: false });
                }
                else if (event.inputType === 'insertLineBreak' || event.inputType === 'insertParagraph') {
                    tapKey({ s: 0x1c, e: false });
                }
                else if (event.inputType === 'insertFromPaste') {
                    return;
                }
                event.preventDefault();
            });
            sink.addEventListener('input', () => {
                if (!composing) {
                    sink.value = '';
                }
            });

            // Nothing stays pressed on the machine when this page loses the keyboard.
            window.addEventListener('blur', releaseAll);
            document.addEventListener('visibilitychange', () => {
                if (document.visibilityState !== 'visible') {
                    releaseAll();
                }
                else if (!ended && (!socket || socket.readyState > WebSocket.OPEN)) {
                    connect();
                }
            });

            // The key bar of a phone: single keys, and Ctrl/Alt that stay down for the next key.
            const sticky = new Set();
            const keyBar = $('keys');
            keyBar.querySelectorAll('button').forEach(item => item.addEventListener('pointerdown', event => event.preventDefault()));
            keyBar.addEventListener('click', event => {
                const item = event.target.closest('button');
                if (!item) {
                    return;
                }
                const parse = value => ({ s: parseInt(value.replace('e', ''), 16), e: value.endsWith('e') });
                if (item.dataset.sticky) {
                    const scan = parse(item.dataset.sticky);
                    if (sticky.has(item.dataset.sticky)) {
                        sticky.delete(item.dataset.sticky);
                        key(scan, false);
                        item.classList.remove('on');
                    }
                    else {
                        sticky.add(item.dataset.sticky);
                        key(scan, true);
                        item.classList.add('on');
                    }
                    return;
                }
                const scan = parse(item.dataset.key);
                key(scan, true);
                key(scan, false);
                sticky.forEach(value => key(parse(value), false));
                sticky.clear();
                keyBar.querySelectorAll('[data-sticky]').forEach(button => button.classList.remove('on'));
            });

            const keyboardButton = $('keyboard');
            keyboardButton.title = text.keyboard;
            keyboardButton.classList.toggle('hidden', !coarse);
            keyboardButton.addEventListener('click', () => {
                if (document.activeElement === sink) {
                    sink.blur();
                }
                else {
                    sink.focus({ preventScroll: true });
                }
            });

            const fullscreenButton = $('fullscreen');
            fullscreenButton.title = text.fullscreen;
            fullscreenButton.classList.toggle('hidden', !document.fullscreenEnabled);
            fullscreenButton.addEventListener('click', async () => {
                if (document.fullscreenElement) {
                    await document.exitFullscreen();
                    return;
                }
                await document.documentElement.requestFullscreen();
                // In full screen the browser hands over keys it otherwise keeps (Esc, Ctrl+W, ...).
                try {
                    await navigator.keyboard?.lock?.();
                }
                catch {
                }
            });

            // ---- clipboard: this device -> the machine ---------------------------------------------------
            const CHUNK = 192 * 1024;
            const uploadFiles = async (files, kind) => {
                const id = crypto.randomUUID();
                setStatus(text.pasting);
                send('screen.clip.begin', { id, kind, paste: true, files: files.map(file => ({ name: file.name || 'Bild.png', size: file.size })) });
                for (let index = 0; index < files.length; index++) {
                    const file = files[index];
                    for (let offset = 0; offset < file.size; offset += CHUNK) {
                        const piece = new Uint8Array(await file.slice(offset, offset + CHUNK).arrayBuffer());
                        // Do not pile up more than a few megabytes in the socket: the picture shares it.
                        while (socket && socket.bufferedAmount > 4 * 1024 * 1024) {
                            await new Promise(resolve => setTimeout(resolve, 30));
                        }
                        send('screen.clip.chunk', { id, index, offset }, piece);
                    }
                }
                send('screen.clip.end', { id });
            };
            document.addEventListener('paste', event => {
                const held = heldPaste;
                if (!held && document.activeElement !== sink) {
                    return;
                }
                event.preventDefault();
                if (held) {
                    window.clearTimeout(held.timer);
                    heldPaste = null;
                }
                const data = event.clipboardData;
                const files = data ? [...data.files] : [];
                if (files.length) {
                    // One picture without a name of its own is a copied picture, not a copied file.
                    const picture = files.length === 1 && /^image\//.test(files[0].type) && /^image\.\w+$/i.test(files[0].name);
                    uploadFiles(files, picture ? 'image' : 'files');
                    return;
                }
                const plain = data ? data.getData('text/plain') : '';
                if (plain) {
                    setStatus(text.pasting);
                    send('screen.clip.set', { kind: 'text', paste: true }, encoder.encode(plain));
                    return;
                }
                if (held) {
                    heldPaste = held;
                    passPasteOn();
                }
            });

            // ---- clipboard: the machine -> this device ---------------------------------------------------
            let pendingClipboard = null;
            const writePendingClipboard = () => {
                const item = pendingClipboard;
                if (!item || !document.hasFocus() || !navigator.clipboard) {
                    return;
                }
                const write = item.kind === 'text'
                    ? navigator.clipboard.writeText(item.text)
                    : navigator.clipboard.write([new ClipboardItem({ 'image/png': item.blob })]);
                write.then(() => {
                    if (pendingClipboard === item) {
                        pendingClipboard = null;
                    }
                    window.__screen.clipboard.push(item.kind === 'text' ? item.text : 'image');
                }).catch(() => {
                    // Needs a click or a key press here first (Safari), or focus: tried again then.
                });
            };
            window.addEventListener('focus', writePendingClipboard);
            stage.addEventListener('pointerdown', writePendingClipboard);
            const onRemoteClipboard = (data, payload) => {
                if (data.kind === 'text') {
                    pendingClipboard = { kind: 'text', text: decoder.decode(payload) };
                    writePendingClipboard();
                }
                else if (data.kind === 'image') {
                    pendingClipboard = { kind: 'image', blob: new Blob([payload], { type: 'image/png' }) };
                    writePendingClipboard();
                }
                else if (data.kind === 'files') {
                    showFiles(data);
                }
            };
            const escapeHtml = value => String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
            const size = bytes => bytes < 1024 ? bytes + ' B' : bytes < 1048576 ? (bytes / 1024).toFixed(0) + ' KB' : (bytes / 1048576).toFixed(1) + ' MB';
            // Files copied on the machine: a browser cannot put files on a clipboard, so they are offered for download.
            const showFiles = data => {
                const rows = (data.files || []).map((file, index) => {
                    const link = `/api/agent/${server}/screen-clip?` + new URLSearchParams({ session, token: data.token, index: String(index) });
                    return `<li><span>${escapeHtml(file.name)}</span>${file.folder
                        ? `<span class="muted">${escapeHtml(text.folder)}</span>`
                        : `<a href="${link}" download="${escapeHtml(file.name)}" data-download>${escapeHtml(text.download)} (${size(file.size)})</a>`}</li>`;
                }).join('');
                toast.innerHTML = `<strong>${escapeHtml(text.clipFiles)}</strong><ul>${rows}</ul><button type="button" data-close>${escapeHtml(text.close)}</button>`;
                toast.classList.remove('hidden');
                toast.querySelector('[data-close]').addEventListener('click', () => toast.classList.add('hidden'));
                toast.querySelectorAll('[data-download]').forEach(link => link.addEventListener('click', () => window.__screen.downloads.push(link.getAttribute('download'))));
            };

            // ---- sound ----------------------------------------------------------------------------------
            const soundButton = $('sound');
            let soundWanted = localStorage.getItem('matgate.screen.sound') !== 'off';
            let soundUnlocked = false;
            let audioContext = null;
            let audioDecoder = null;
            let nextTime = 0;
            let audioStamp = 0;
            const SPEAKER_ON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 5 6 9H3v6h3l5 4z"/><path d="M15.5 8.5a5 5 0 0 1 0 7M18.5 5.5a9 9 0 0 1 0 13"/></svg>';
            const SPEAKER_OFF = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 5 6 9H3v6h3l5 4z"/><path d="m22 9-6 6M16 9l6 6"/></svg>';
            const paintSound = () => {
                const playing = !!audioContext;
                soundButton.innerHTML = playing ? SPEAKER_ON : SPEAKER_OFF;
                soundButton.classList.toggle('on', playing);
                soundButton.title = playing ? text.soundOff : soundWanted ? text.soundBlocked : text.soundOn;
            };
            const play = (left, right) => {
                const buffer = audioContext.createBuffer(2, left.length, 48000);
                buffer.copyToChannel(left, 0);
                buffer.copyToChannel(right, 1);
                const source = audioContext.createBufferSource();
                source.buffer = buffer;
                source.connect(audioContext.destination);
                const now = audioContext.currentTime;
                // A small cushion against jitter; a growing lag is cut back.
                if (nextTime < now + 0.01 || nextTime > now + 0.25) {
                    nextTime = now + 0.06;
                }
                source.start(nextTime);
                nextTime += buffer.duration;
                window.__screen.audio++;
            };
            const startSound = async () => {
                if (audioContext || !socket || socket.readyState !== WebSocket.OPEN) {
                    return;
                }
                audioContext = new AudioContext({ sampleRate: 48000, latencyHint: 'interactive' });
                let opus = false;
                try {
                    opus = 'AudioDecoder' in window && (await AudioDecoder.isConfigSupported({ codec: 'opus', sampleRate: 48000, numberOfChannels: 2 })).supported;
                }
                catch {
                }
                if (opus) {
                    audioDecoder = new AudioDecoder({
                        output: data => {
                            if (!audioContext) {
                                data.close();
                                return;
                            }
                            const frames = data.numberOfFrames;
                            const left = new Float32Array(frames);
                            const right = new Float32Array(frames);
                            data.copyTo(left, { planeIndex: 0, format: 'f32-planar' });
                            data.copyTo(right, { planeIndex: data.numberOfChannels > 1 ? 1 : 0, format: 'f32-planar' });
                            data.close();
                            play(left, right);
                        },
                        error: () => {
                        },
                    });
                    audioDecoder.configure({ codec: 'opus', sampleRate: 48000, numberOfChannels: 2 });
                }
                send('screen.audio', { on: true, codec: opus ? 'opus' : 'pcm' });
                paintSound();
            };
            const stopSound = tell => {
                if (tell) {
                    send('screen.audio', { on: false });
                }
                try {
                    audioDecoder && audioDecoder.state !== 'closed' && audioDecoder.close();
                }
                catch {
                }
                audioDecoder = null;
                audioContext && audioContext.close();
                audioContext = null;
                paintSound();
            };
            const onAudio = (data, payload) => {
                if (!audioContext) {
                    return;
                }
                if (data.codec === 'opus') {
                    if (audioDecoder && audioDecoder.state === 'configured') {
                        audioStamp += 20000;
                        audioDecoder.decode(new EncodedAudioChunk({ type: 'key', timestamp: audioStamp, data: payload }));
                    }
                    return;
                }
                const samples = new Int16Array(payload.buffer.slice(payload.byteOffset, payload.byteOffset + payload.byteLength));
                const left = new Float32Array(samples.length / 2);
                const right = new Float32Array(samples.length / 2);
                for (let i = 0; i < left.length; i++) {
                    left[i] = samples[i * 2] / 32768;
                    right[i] = samples[i * 2 + 1] / 32768;
                }
                play(left, right);
            };
            // A browser plays sound only after a click or key press on the page.
            const unlockSound = () => {
                if (soundUnlocked) {
                    return;
                }
                soundUnlocked = true;
                if (soundWanted) {
                    startSound();
                }
            };
            soundButton.addEventListener('click', () => {
                soundUnlocked = true;
                if (audioContext) {
                    soundWanted = false;
                    stopSound(true);
                }
                else {
                    soundWanted = true;
                    startSound();
                }
                localStorage.setItem('matgate.screen.sound', soundWanted ? 'on' : 'off');
                paintSound();
            });
            paintSound();

            connect();
        })();
        </script>
        </body>
        </html>
        """;
}
