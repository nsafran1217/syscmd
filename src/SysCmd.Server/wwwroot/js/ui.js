// Small helpers the layout needs from the browser: remembering whether the navigation
// panel is showing, and what size screen we are on for its first-run default.

window.syscmdUi = {
    // "open", "closed", or null when the user has never chosen.
    readNavState: function () {
        try {
            return window.localStorage.getItem('syscmd.nav');
        } catch (e) {
            // Private browsing and blocked site data both throw here; fall back to auto.
            return null;
        }
    },

    writeNavState: function (state) {
        try {
            window.localStorage.setItem('syscmd.nav', state);
        } catch (e) {
            // Not being able to remember the choice is not worth surfacing.
        }
    },

    // Generic preference storage. Any of these can throw in a private window or with site
    // data blocked, so every caller has to cope with a null coming back.
    readSetting: function (key) {
        try { return window.localStorage.getItem(key); } catch (e) { return null; }
    },

    writeSetting: function (key, value) {
        try { window.localStorage.setItem(key, value); } catch (e) { /* not worth surfacing */ }
    },

    isWideViewport: function () {
        return window.matchMedia('(min-width: 900px)').matches;
    },

    // Reports the current width and keeps reporting changes. Without this the layout would
    // hold whatever it measured on first render, so resizing a window across the breakpoint
    // left the drawer without its close control.
    watchViewport: function (dotnetRef) {
        const mq = window.matchMedia('(min-width: 900px)');
        if (window._syscmdViewportCleanup) window._syscmdViewportCleanup();
        const handler = (e) => {
            dotnetRef.invokeMethodAsync('OnViewportChanged', e.matches)
                .catch(() => { /* the circuit may have gone */ });
        };
        mq.addEventListener('change', handler);
        window._syscmdViewportCleanup = () => mq.removeEventListener('change', handler);
        return mq.matches;
    },

    unwatchViewport: function () {
        if (window._syscmdViewportCleanup) {
            window._syscmdViewportCleanup();
            window._syscmdViewportCleanup = null;
        }
    },

    // ---------------------------------------------------------------- windows

    // Dragging and resizing run entirely in the browser. Doing it through Blazor would
    // put a server round trip on every pointer move; instead the element is moved here
    // and the final geometry is handed back once, when the gesture ends.
    initWindow: function (windowId, dotnetRef) {
        const el = document.getElementById(windowId);
        if (!el || el.dataset.cwInit === '1') return;
        el.dataset.cwInit = '1';

        const handle = el.querySelector('[data-cw-drag]');
        const grip = el.querySelector('[data-cw-resize]');

        let mode = null, startX = 0, startY = 0, originX = 0, originY = 0, originW = 0, originH = 0;

        const onDown = (e, which) => {
            // Ignore the title-bar boxes; only the bar itself drags.
            if (which === 'move' && e.target.closest('.cw-box')) return;
            if (el.classList.contains('maximised')) return;
            if (which === 'resize' && el.classList.contains('shaded')) return;
            if (e.button !== undefined && e.button !== 0) return;

            mode = which;
            const p = e.touches ? e.touches[0] : e;
            startX = p.clientX;
            startY = p.clientY;
            const box = el.getBoundingClientRect();
            originX = box.left; originY = box.top; originW = box.width; originH = box.height;

            document.body.classList.add(which === 'move' ? 'cw-dragging' : 'cw-resizing');
            e.preventDefault();
        };

        const onMove = (e) => {
            if (!mode) return;
            const p = e.touches ? e.touches[0] : e;
            const dx = p.clientX - startX, dy = p.clientY - startY;

            if (mode === 'move') {
                // Keep at least a sliver of the title bar reachable on screen.
                const x = Math.min(Math.max(originX + dx, -originW + 80), window.innerWidth - 80);
                const y = Math.min(Math.max(originY + dy, 0), window.innerHeight - 30);
                el.style.left = x + 'px';
                el.style.top = y + 'px';
            } else {
                el.style.width = Math.max(280, originW + dx) + 'px';
                el.style.height = Math.max(140, originH + dy) + 'px';
            }
            e.preventDefault();
        };

        const onUp = () => {
            if (!mode) return;
            mode = null;
            document.body.classList.remove('cw-dragging', 'cw-resizing');

            const box = el.getBoundingClientRect();
            if (dotnetRef) {
                // A rolled-up or maximised frame is not the window's real size. Recording it
                // would shrink the window to its title bar the next time it was restored.
                const realSize = !el.classList.contains('shaded') && !el.classList.contains('maximised');
                dotnetRef.invokeMethodAsync('OnGeometryChanged',
                    Math.round(box.left), Math.round(box.top),
                    realSize ? Math.round(box.width) : 0,
                    realSize ? Math.round(box.height) : 0)
                    .catch(() => { /* the circuit may have gone */ });
            }
        };

        if (handle) {
            handle.addEventListener('mousedown', (e) => onDown(e, 'move'));
            handle.addEventListener('touchstart', (e) => onDown(e, 'move'), { passive: false });
        }
        if (grip) {
            grip.addEventListener('mousedown', (e) => onDown(e, 'resize'));
            grip.addEventListener('touchstart', (e) => onDown(e, 'resize'), { passive: false });
        }

        window.addEventListener('mousemove', onMove);
        window.addEventListener('touchmove', onMove, { passive: false });
        window.addEventListener('mouseup', onUp);
        window.addEventListener('touchend', onUp);

        el._cwCleanup = () => {
            window.removeEventListener('mousemove', onMove);
            window.removeEventListener('touchmove', onMove);
            window.removeEventListener('mouseup', onUp);
            window.removeEventListener('touchend', onUp);
        };
    },

    disposeWindow: function (windowId) {
        const el = document.getElementById(windowId);
        if (el && el._cwCleanup) el._cwCleanup();
    }
};
