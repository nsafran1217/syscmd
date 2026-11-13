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

    // Theme preferences ride in cookies rather than local storage. App.razor is rendered on the
    // server, so a cookie is readable before the first byte goes out and the first paint is already
    // in the right palette; local storage would give a flash of the wrong theme on every load.
    readCookie: function (name) {
        const match = document.cookie.match(new RegExp('(?:^|; )' + name.replace(/\./g, '\\.') + '=([^;]*)'));
        return match ? decodeURIComponent(match[1]) : null;
    },

    writeCookie: function (name, value) {
        const year = 60 * 60 * 24 * 365;
        document.cookie = name + '=' + encodeURIComponent(value) + ';path=/;max-age=' + year + ';SameSite=Lax';
    },

    // Repoints the generated stylesheet so a palette can be tried on without a reload. The link
    // carries an id precisely so this can find it.
    setThemeHref: function (href) {
        const link = document.getElementById('cde-theme');
        if (!link) return;
        // xterm holds its own copy of its colours, so open consoles have to be told separately
        // once the new sheet has actually applied.
        link.addEventListener('load', function once() {
            link.removeEventListener('load', once);
            if (window.syscmdConsole) window.syscmdConsole.retheme();
        });
        link.setAttribute('href', href);
    },

    // Scrolls a box to its end: the console log viewer opens on the latest output.
    scrollToBottom: function (id) {
        const el = document.getElementById(id);
        if (el) el.scrollTop = el.scrollHeight;
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

    // ----------------------------------------------------------- context menus

    // Keeps a context menu where it was opened relative to the element it was opened from. The
    // menu is fixed-positioned, so without this it stays put in the viewport while the page
    // scrolls the button away from under it. Each adjustment is measured against where the menu
    // actually landed rather than computed from the anchor alone, so it is right whichever box
    // the browser resolves the fixed position against.
    anchorMenu: function (menuId, anchorId) {
        const menu = document.getElementById(menuId);
        if (!menu) return;
        if (menu._ctxCleanup) menu._ctxCleanup();

        const shift = (dx, dy) => {
            if (!dx && !dy) return;
            menu.style.left = (parseFloat(menu.style.left) || 0) + dx + 'px';
            menu.style.top = (parseFloat(menu.style.top) || 0) + dy + 'px';
        };

        // A phone shows the menu as a bottom sheet, which is pinned by the stylesheet and has
        // nothing to follow or pull back on screen.
        const sheet = () => window.matchMedia('(max-width: 640px)').matches;

        // Pull it back on screen now it can be measured - the server placed it at the pointer
        // without knowing how big the viewport is.
        if (!sheet()) {
            const box = menu.getBoundingClientRect();
            const dx = Math.min(0, window.innerWidth - 4 - box.right) || Math.max(0, 4 - box.left);
            const dy = Math.min(0, window.innerHeight - 4 - box.bottom) || Math.max(0, 4 - box.top);
            shift(dx, dy);
        }

        const anchor = anchorId ? document.getElementById(anchorId) : null;
        if (!anchor) return;

        const a0 = anchor.getBoundingClientRect(), m0 = menu.getBoundingClientRect();
        const offX = m0.left - a0.left, offY = m0.top - a0.top;

        const follow = () => {
            if (!menu.isConnected || !anchor.isConnected) { cleanup(); return; }
            if (sheet()) return;
            const a = anchor.getBoundingClientRect(), m = menu.getBoundingClientRect();
            shift(a.left + offX - m.left, a.top + offY - m.top);
        };

        // Captured, so a scroll inside any scrolling box carries the menu too, not just the page.
        window.addEventListener('scroll', follow, { capture: true, passive: true });
        window.addEventListener('resize', follow);
        const cleanup = () => {
            window.removeEventListener('scroll', follow, { capture: true });
            window.removeEventListener('resize', follow);
            menu._ctxCleanup = null;
        };
        menu._ctxCleanup = cleanup;
    },

    releaseMenu: function (menuId) {
        const menu = document.getElementById(menuId);
        if (menu && menu._ctxCleanup) menu._ctxCleanup();
    },

    // ---------------------------------------------------------------- pop-outs

    // Opens a console in a window of its own, which the window manager can put anywhere -
    // including outside this browser window. The name is per console, so popping the same one
    // out twice reuses its window instead of stacking up copies. Returns false when a pop-up
    // blocker refused it, so the caller can leave the console where it is.
    popOut: function (url, name, width, height) {
        const left = Math.max(0, (window.screenX || 0) + 60);
        const top = Math.max(0, (window.screenY || 0) + 60);
        const features = 'popup=yes,width=' + width + ',height=' + height + ',left=' + left + ',top=' + top;
        const opened = window.open(url, name, features);
        if (!opened) return false;
        try { opened.focus(); } catch (e) { /* focus is a nicety */ }
        return true;
    },

    // Closes a pop-out from its own Close entry. A browser only lets script close a window that
    // script opened, so a pop-out reached by typing its address in stays open; the page says so.
    closeSelf: function () {
        window.close();
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
        const grips = el.querySelectorAll('[data-cw-resize]');

        // The frame says how small it may go: a console has a character grid it must not fall
        // below, an ordinary window does not. Falls back to something merely usable.
        const MIN_W = parseInt(el.dataset.cwMinWidth, 10) || 280;
        const MIN_H = parseInt(el.dataset.cwMinHeight, 10) || 140;
        let mode = null, edge = '', startX = 0, startY = 0, originX = 0, originY = 0, originW = 0, originH = 0;

        const onDown = (e, which, which_edge) => {
            // Only the bar itself drags - not its boxes, and not the window menu that hangs off
            // it. That menu matters more than it looks: it is a child of the title bar, so a
            // touch on one of its entries starts here, and the preventDefault below would cancel
            // the click the browser synthesises from the tap. On a mouse that is harmless -
            // preventing default on mousedown does not stop the click - which is why closing a
            // window from its window menu worked everywhere except under a finger.
            if (which === 'move' && e.target.closest('.cw-box, .menu-anchor')) return;
            if (el.classList.contains('maximised')) return;
            if (which === 'resize' && el.classList.contains('shaded')) return;
            if (e.button !== undefined && e.button !== 0) return;

            mode = which;
            edge = which_edge || 'se';
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
                // Which edges the gesture owns comes from the handle's name, so a CDE frame
                // resizes from any of its eight pieces rather than only the bottom-right corner.
                // Dragging a top or left edge moves the origin as well as the size, and the
                // movement is clamped so the far edge stays put once the minimum is reached.
                let x = originX, y = originY, w = originW, h = originH;

                if (edge.includes('e')) w = Math.max(MIN_W, originW + dx);
                if (edge.includes('s')) h = Math.max(MIN_H, originH + dy);
                if (edge.includes('w')) {
                    w = Math.max(MIN_W, originW - dx);
                    x = originX + (originW - w);
                }
                if (edge.includes('n')) {
                    h = Math.max(MIN_H, originH - dy);
                    y = originY + (originH - h);
                }

                el.style.left = x + 'px';
                el.style.top = y + 'px';
                el.style.width = w + 'px';
                el.style.height = h + 'px';
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
        grips.forEach((grip) => {
            const which = grip.getAttribute('data-cw-resize') || 'se';
            grip.addEventListener('mousedown', (e) => onDown(e, 'resize', which));
            grip.addEventListener('touchstart', (e) => onDown(e, 'resize', which), { passive: false });
        });

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
