// Terminal instances, one per open console window. Bytes pass through untouched in both
// directions - the server handles telnet negotiation, so this stays a dumb pipe.

window.syscmdConsole = (function () {
    const instances = new Map();

    // The console is a text area, which is colour set 4's job in CDE, so its colours come from
    // the same custom properties every other surface reads. Reading them here rather than
    // hardcoding a palette is what lets a theme switch reach inside the terminals too.
    function themeColors() {
        const css = getComputedStyle(document.documentElement);
        const value = (name, fallback) => (css.getPropertyValue(name) || '').trim() || fallback;
        return {
            background: value('--cs4-bg', '#12141a'),
            foreground: value('--cs4-fg', '#e6e6e6'),
            cursor: value('--cs1-bg', '#eda870'),
            selectionBackground: value('--cs4-sel', '#4e5566')
        };
    }

    // Repaints every open console when the palette changes. xterm keeps its own copy of the
    // theme, so nothing short of handing it a new one will do.
    function retheme() {
        const theme = themeColors();
        instances.forEach((entry) => { entry.term.options.theme = theme; });
    }

    function make(elementId, wsUrl) {
        const element = document.getElementById(elementId);
        if (!element) return null;

        const term = new Terminal({
            convertEol: false,
            cursorBlink: true,
            fontFamily: "'DejaVu Sans Mono', 'Liberation Mono', Menlo, Consolas, monospace",
            fontSize: 13,
            scrollback: 5000,
            theme: themeColors()
        });

        const fit = new FitAddon.FitAddon();
        term.loadAddon(fit);
        term.open(element);
        try { fit.fit(); } catch (e) { /* zero-sized while the window animates in */ }

        const socket = new WebSocket(wsUrl);
        socket.binaryType = 'arraybuffer';

        socket.onopen = () => term.focus();
        socket.onmessage = (event) => term.write(new Uint8Array(event.data));
        socket.onclose = () => term.write('\r\n\x1b[33m*** Session closed ***\x1b[0m\r\n');
        socket.onerror = () => term.write('\r\n\x1b[31m*** Connection error ***\x1b[0m\r\n');

        term.onData((data) => {
            if (socket.readyState === WebSocket.OPEN) {
                socket.send(new TextEncoder().encode(data));
            }
        });

        // The window can be dragged, resized, maximised or rolled up; refit whenever its
        // box changes rather than only on window resize.
        const observer = new ResizeObserver(() => {
            try { fit.fit(); } catch (e) { /* hidden while rolled up */ }
        });
        observer.observe(element);

        return { term, fit, socket, observer, element, fontSize: 13 };
    }

    return {
        open: function (elementId, wsUrl) {
            this.close(elementId);
            const instance = make(elementId, wsUrl);
            if (instance) instances.set(elementId, instance);
        },

        // Send a literal string, for the toolbar's control-key buttons.
        send: function (elementId, text) {
            const i = instances.get(elementId);
            if (i && i.socket.readyState === WebSocket.OPEN) {
                i.socket.send(new TextEncoder().encode(text));
                i.term.focus();
            }
        },

        // Control messages go as text frames; keystrokes are always binary, so the two
        // never collide in the byte stream.
        command: function (elementId, name) {
            const i = instances.get(elementId);
            if (i && i.socket.readyState === WebSocket.OPEN) {
                i.socket.send(name);
                i.term.focus();
            }
        },

        clear: function (elementId) {
            const i = instances.get(elementId);
            if (i) { i.term.clear(); i.term.focus(); }
        },

        focus: function (elementId) {
            const i = instances.get(elementId);
            if (i) i.term.focus();
        },

        selectAll: function (elementId) {
            const i = instances.get(elementId);
            if (i) i.term.selectAll();
        },

        // Copies the current selection, or the whole buffer when nothing is selected.
        copy: async function (elementId) {
            const i = instances.get(elementId);
            if (!i) return false;
            let text = i.term.getSelection();
            if (!text) {
                const lines = [];
                for (let n = 0; n < i.term.buffer.active.length; n++) {
                    lines.push(i.term.buffer.active.getLine(n).translateToString(true));
                }
                text = lines.join('\n').replace(/\n+$/, '');
            }
            try {
                await navigator.clipboard.writeText(text);
                return true;
            } catch (e) {
                // Clipboard access needs a secure context; report it rather than failing silently.
                return false;
            }
        },

        paste: async function (elementId) {
            const i = instances.get(elementId);
            if (!i) return false;
            try {
                const text = await navigator.clipboard.readText();
                if (text && i.socket.readyState === WebSocket.OPEN) {
                    i.socket.send(new TextEncoder().encode(text));
                }
                i.term.focus();
                return true;
            } catch (e) {
                return false;
            }
        },

        setFontSize: function (elementId, delta) {
            const i = instances.get(elementId);
            if (!i) return 0;
            i.fontSize = Math.min(24, Math.max(8, i.fontSize + delta));
            i.term.options.fontSize = i.fontSize;
            try { i.fit.fit(); } catch (e) { /* ignore */ }
            return i.fontSize;
        },

        refit: function (elementId) {
            const i = instances.get(elementId);
            if (i) { try { i.fit.fit(); } catch (e) { /* ignore */ } }
        },

        isConnected: function (elementId) {
            const i = instances.get(elementId);
            return !!i && i.socket.readyState === WebSocket.OPEN;
        },

        close: function (elementId) {
            const i = instances.get(elementId);
            if (!i) return;
            try { i.observer.disconnect(); } catch (e) { }
            try { i.socket.close(); } catch (e) { }
            try { i.term.dispose(); } catch (e) { }
            instances.delete(elementId);
        },

        closeAll: function () {
            for (const id of [...instances.keys()]) this.close(id);
        },

        // Called after the theme stylesheet is swapped, so open consoles follow the palette.
        retheme: retheme
    };
})();
