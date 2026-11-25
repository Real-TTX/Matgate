namespace Matgate.Web;

// A swipe on a control that cannot scroll itself (the key bar above the keyboard, a toolbar, a button) must not
// move the page. iOS hands a scroll nobody takes on to the document - with the keyboard up even when nothing on it
// can scroll - and from a session's frame on to the shell around it: the whole app slid away under the finger.
// So a one-finger move is let through only where something under the finger can scroll in that direction (or into
// a field, where moving the finger moves the caret); anywhere else it is stopped. The session's picture is not
// touched by this: its gestures are pointer events of its own.
public static class TouchGuard
{
    public const string Script = """
        <script>
        (() => {
            if (window.__matgateTouchGuard) { return; }
            window.__matgateTouchGuard = true;
            let start = null;
            const scrolls = (node, vertical) => {
                const style = getComputedStyle(node);
                return vertical
                    ? /(auto|scroll)/.test(style.overflowY) && node.scrollHeight > node.clientHeight + 1
                    : /(auto|scroll)/.test(style.overflowX) && node.scrollWidth > node.clientWidth + 1;
            };
            document.addEventListener('touchstart', event => {
                const touch = event.touches[0];
                start = event.touches.length === 1 && touch ? { x: touch.clientX, y: touch.clientY } : null;
            }, { passive: true, capture: true });
            document.addEventListener('touchmove', event => {
                const touch = event.touches[0];
                if (!start || event.touches.length !== 1 || !touch || event.defaultPrevented) {
                    return;
                }
                const vertical = Math.abs(touch.clientY - start.y) >= Math.abs(touch.clientX - start.x);
                for (let node = event.target; node && node.nodeType === 1 && node !== document.documentElement; node = node.parentElement) {
                    if (node.matches('input, textarea, select, [contenteditable]:not([contenteditable="false"]), [data-touch-free], .xterm')) {
                        return;
                    }
                    if (scrolls(node, vertical)) {
                        return;
                    }
                }
                // A page that scrolls as a whole (not the shell: there html and body hide their overflow).
                const root = document.scrollingElement || document.documentElement;
                const hidden = style => vertical ? style.overflowY === 'hidden' : style.overflowX === 'hidden';
                if (!hidden(getComputedStyle(document.documentElement)) && !hidden(getComputedStyle(document.body))
                    && (vertical ? root.scrollHeight > root.clientHeight + 1 : root.scrollWidth > root.clientWidth + 1)) {
                    return;
                }
                event.preventDefault();
            }, { passive: false, capture: true });
        })();
        </script>
        """;
}
