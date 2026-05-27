// =============================================================
// tv-nav.js — D-pad spatial navigation for TV browsers / remotes.
//
// Activates when document.documentElement has class "tv-mode" (toggled by
// ProfilePanel → Appearance → TV mode). Listens for arrow keys globally,
// computes the nearest focusable element in the pressed direction relative
// to the currently focused one, moves focus + smooth-scrolls it into view.
//
// Algorithm:
//   1. Get all visible focusable elements (tabindex ≥ 0, not disabled,
//      offsetParent != null, computed display != none).
//   2. For each candidate, compute its bounding rect centre relative to the
//      current focus rect.
//   3. Reject candidates that aren't strictly in the pressed direction
//      (e.g. for "right", candidate's left edge must be >= current's right
//      edge minus a small overlap tolerance).
//   4. Score each remaining candidate as:
//        primary = orthogonal distance in pressed direction
//        secondary = perpendicular distance (penalised 2× so the cone
//                     toward straight-ahead wins over far-off-axis hits)
//   5. Focus the lowest-scoring candidate. .focus() + scrollIntoView
//      ({block:'center', behavior:'smooth'}).
//
// Player override: when document.body has class "player-open" (set by
// MediaDetail when Artplayer mounts), suspend spatial nav so Artplayer's
// own arrow handlers (seek / volume) take over. Pressing Esc inside
// player closes it (handled by player JS), which removes the class and
// re-enables spatial nav.
//
// Number-key episode jump: pressing 0-9 while focus is in an episode grid
// (.md-episode-grid) jumps focus to the Nth visible EpisodeCard so users
// can pick an episode without arrowing through 24 cards.
// =============================================================

(function () {
    'use strict';

    // Bail in non-browser environments (server-prerender doesn't have window).
    if (typeof window === 'undefined' || typeof document === 'undefined') return;

    const FOCUSABLE_SELECTOR = [
        'a[href]',
        'button:not([disabled])',
        'input:not([disabled]):not([type="hidden"])',
        'select:not([disabled])',
        'textarea:not([disabled])',
        '[tabindex]:not([tabindex="-1"])',
        '[contenteditable="true"]',
    ].join(',');

    function isVisible(el) {
        if (!el || !el.getBoundingClientRect) return false;
        const r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0) return false;
        if (el.offsetParent === null && getComputedStyle(el).position !== 'fixed') return false;
        if (el.getAttribute('aria-hidden') === 'true') return false;
        return true;
    }

    function getFocusables() {
        return Array.from(document.querySelectorAll(FOCUSABLE_SELECTOR)).filter(isVisible);
    }

    /**
     * Direction one of "up" | "down" | "left" | "right".
     * Returns the best candidate element to move focus to, or null.
     */
    function findNeighbour(direction, from) {
        const fromRect = from.getBoundingClientRect();
        const fromCx = fromRect.left + fromRect.width  / 2;
        const fromCy = fromRect.top  + fromRect.height / 2;

        const cands = getFocusables().filter(el => el !== from);
        let best = null;
        let bestScore = Infinity;

        for (const el of cands) {
            const r = el.getBoundingClientRect();
            const cx = r.left + r.width  / 2;
            const cy = r.top  + r.height / 2;
            const dx = cx - fromCx;
            const dy = cy - fromCy;

            let primary, perpendicular;
            switch (direction) {
                case 'right':
                    // Candidate must be to the right of the current element's
                    // right edge (allow tiny overlap so adjacent cells count).
                    if (r.left < fromRect.right - 4) continue;
                    primary       = r.left - fromRect.right;
                    perpendicular = Math.abs(dy);
                    break;
                case 'left':
                    if (r.right > fromRect.left + 4) continue;
                    primary       = fromRect.left - r.right;
                    perpendicular = Math.abs(dy);
                    break;
                case 'down':
                    if (r.top < fromRect.bottom - 4) continue;
                    primary       = r.top - fromRect.bottom;
                    perpendicular = Math.abs(dx);
                    break;
                case 'up':
                    if (r.bottom > fromRect.top + 4) continue;
                    primary       = fromRect.top - r.bottom;
                    perpendicular = Math.abs(dx);
                    break;
                default: continue;
            }
            // Score: orthogonal distance plus a penalty for being off-axis.
            // The 2× weight on perpendicular biases toward straight-ahead
            // candidates over far-out diagonal hits.
            const score = primary + perpendicular * 2;
            if (score < bestScore) {
                bestScore = score;
                best = el;
            }
        }
        return best;
    }

    function focusElement(el) {
        if (!el) return;
        try { el.focus({ preventScroll: true }); } catch (_) { try { el.focus(); } catch (__) {} }
        // After focus, smoothly scroll into the centre of the viewport. If the
        // element is already fully visible this is a no-op.
        try {
            el.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' });
        } catch (_) {
            // Older browsers — fall back to non-smooth.
            el.scrollIntoView();
        }
    }

    function isTypingInInput(target) {
        if (!target) return false;
        const tag = target.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') {
            const type = (target.getAttribute('type') || '').toLowerCase();
            // Text-like inputs eat arrow keys for cursor nav; only intercept
            // when the input is button-y (checkbox/radio/etc.).
            if (tag === 'INPUT' && (type === 'checkbox' || type === 'radio' || type === 'button' || type === 'submit')) {
                return false;
            }
            return true;
        }
        return target.isContentEditable === true;
    }

    function onKeyDown(e) {
        // ── Back / Esc handling ──────────────────────────────────────
        // Runs BEFORE the tv-mode gate because Esc-to-close-drawer is
        // valuable on phone + desktop too: a mouse user opening
        // ProfilePanel and hitting Esc expects the drawer to close, same
        // as a TV remote user pressing Back. The dismissibility convention
        // is the same — animarrHandleBack() walks the overlays + clicks
        // the topmost one.
        const isBackKey =
            e.key === 'Escape' ||
            e.key === 'GoBack' ||
            e.key === 'BrowserBack' ||
            (e.key === 'Backspace' && !isTypingInInput(e.target));
        if (isBackKey) {
            // Player owns Back when its overlay is mounted (animarr-player.js
            // has its own keydown handler that maps Esc/GoBack to "close
            // player"). We sit out so we don't double-fire.
            if (document.body.classList.contains('player-open')) return;
            if (window.animarrHandleBack && window.animarrHandleBack()) {
                e.preventDefault();
                e.stopPropagation();
            }
            return;
        }

        // Only intercept in TV mode. Outside TV mode, browser default
        // (tab/shift+tab) behaviour stays untouched.
        if (!document.documentElement.classList.contains('tv-mode')) return;

        // Suspend during fullscreen player so Artplayer's hotkeys (arrow=seek,
        // space=play/pause) take precedence.
        if (document.body.classList.contains('player-open')) return;

        // Don't fight text inputs.
        if (isTypingInInput(e.target)) return;

        // Arrow-key spatial nav.
        let direction = null;
        switch (e.key) {
            case 'ArrowLeft':  direction = 'left';  break;
            case 'ArrowRight': direction = 'right'; break;
            case 'ArrowUp':    direction = 'up';    break;
            case 'ArrowDown':  direction = 'down';  break;
            default: break;
        }
        if (direction) {
            const current = (document.activeElement && document.activeElement !== document.body)
                ? document.activeElement
                : null;
            if (!current) {
                // No focus yet → focus the first focusable, regardless of dir.
                const all = getFocusables();
                if (all.length > 0) {
                    e.preventDefault();
                    focusElement(all[0]);
                }
                return;
            }
            const next = findNeighbour(direction, current);
            if (next) {
                e.preventDefault();
                focusElement(next);
            }
            return;
        }

        // Number-key episode jump — only when current focus is inside an
        // episode grid. 1-9 → that index; 0 → 10th episode.
        if (/^[0-9]$/.test(e.key)) {
            const focusEl = document.activeElement;
            const grid = focusEl ? focusEl.closest('.md-episode-grid') : null;
            if (grid) {
                const idx = e.key === '0' ? 9 : parseInt(e.key, 10) - 1;
                const cards = Array.from(grid.querySelectorAll('.md-episode-card'));
                const target = cards[idx];
                if (target) {
                    const focusable = target.querySelector(FOCUSABLE_SELECTOR) || target;
                    e.preventDefault();
                    focusElement(focusable);
                }
            }
            return;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Back-button handler — shared by:
    //   • the keydown listener above (Esc / GoBack / BrowserBack /
    //     Backspace-outside-input on web + WASM hosts),
    //   • Animarr.App's MainActivity.OnBackPressed on Android (the
    //     hardware BACK key + edge-swipe gesture are intercepted by the
    //     Activity before they reach JS, so the bridge has to call back
    //     into here explicitly via EvaluateJavascript).
    //
    // Returns true when the press is consumed (a drawer / overlay
    // closed). The Android side then suppresses its history.back() /
    // Finish() fallback so the user stays on the same route.
    //
    // Convention this leans on: every dismissible modal in Animarr.UI
    // renders a sibling <div class="*-backdrop|*-overlay"
    // aria-hidden="true" @onclick="Close"> — clicking the backdrop is
    // the existing mouse / touch dismiss path, so we just synthesize
    // that click. No per-component refactor needed; new modals get this
    // behaviour for free as long as they follow the existing pattern.
    //
    // The player overlay is intentionally excluded — animarr-player.js
    // has its own back-key handler that knows how to flush playback
    // state, persist progress, detach Artplayer, and call ClosePlayer-
    // FromJs on the host. Routing player-Back through this generic
    // handler would skip that teardown.
    window.animarrHandleBack = function () {
        if (document.body.classList.contains('player-open')) {
            // Defer to animarr-player.js by replaying an Escape keydown
            // on document — its own onKey listener (capture phase) maps
            // Escape to callbacks.back(), which calls ClosePlayerFromJs.
            try {
                document.dispatchEvent(new KeyboardEvent('keydown', {
                    key: 'Escape', code: 'Escape',
                    bubbles: true, cancelable: true,
                }));
            } catch (_) {}
            return true;
        }
        const overlays = Array.from(document.querySelectorAll(
            '[aria-hidden="true"][class*="backdrop"], ' +
            '[aria-hidden="true"][class*="overlay"]'
        )).filter(function (el) {
            const r = el.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) return false;
            const s = getComputedStyle(el);
            if (s.display === 'none') return false;
            if (s.visibility === 'hidden') return false;
            // pointer-events: none on a backdrop usually means the design
            // wants it visual-only (no click-to-dismiss). Skip — the user
            // must use a close button instead.
            if (s.pointerEvents === 'none') return false;
            return true;
        });
        if (overlays.length > 0) {
            // Topmost overlay wins: prefer the highest z-index; same z-index
            // breaks to DOM order (later = above).
            overlays.sort(function (a, b) {
                const za = parseInt(getComputedStyle(a).zIndex, 10) || 0;
                const zb = parseInt(getComputedStyle(b).zIndex, 10) || 0;
                return za - zb;
            });
            try { overlays[overlays.length - 1].click(); }
            catch (_) { return false; }
            return true;
        }

        // Fallback for dialogs that don't render a separate sibling
        // backdrop element (Login's QR-scan modal, PairConfirm dialogs,
        // role=dialog cards that are themselves the full-bleed
        // overlay). Find any visible [role=dialog] and click the close
        // button inside it. Close buttons follow one of two recognisable
        // patterns: aria-label="Close" or a class containing "close" /
        // "dismiss" / "cancel". When more than one dialog is on screen
        // (PIN keypad on top of ProfilePanel, etc.) the LAST one in DOM
        // order is the topmost, so we walk dialogs in reverse.
        const dialogs = Array.from(document.querySelectorAll('[role="dialog"]'))
            .filter(function (el) {
                const r = el.getBoundingClientRect();
                if (r.width === 0 || r.height === 0) return false;
                const s = getComputedStyle(el);
                return s.display !== 'none' && s.visibility !== 'hidden';
            });
        for (let i = dialogs.length - 1; i >= 0; i--) {
            const dlg = dialogs[i];
            const closeBtn =
                dlg.querySelector('button[aria-label="Close" i]') ||
                dlg.querySelector('button[class*="close" i]') ||
                dlg.querySelector('button[class*="dismiss" i]') ||
                dlg.querySelector('button[class*="cancel" i]');
            if (closeBtn) {
                try { closeBtn.click(); return true; } catch (_) {}
            }
        }
        return false;
    };

    document.addEventListener('keydown', onKeyDown, { capture: true });

    // ─────────────────────────────────────────────────────────────────
    // Default-focus on route change.
    //
    // The Samsung + Android TV UX guidelines both require every screen to
    // have a clearly defined starting focus — otherwise the first arrow
    // press has nothing to compute "nearest in direction" from and the
    // remote feels stuck. Pages can override by setting their own focus
    // in OnAfterRenderAsync; we just provide a sane default for screens
    // that don't bother.
    //
    // Blazor's NavigationManager dispatches popstate-like events through
    // its own pipeline rather than the browser's history API directly, so
    // observing the URL via the `:navigation:` MutationObserver pattern
    // would be flaky. Instead we listen for `Animarr:navigated` — a
    // synthetic event that MainLayout fires on every LocationChanged —
    // plus the regular popstate / hashchange so the back button works in
    // the WASM browser host too. Pick the first focusable inside <main>
    // so we never accidentally focus the topbar / sidebar on landing.
    /// Pick a sensible default-focus target inside the visible page.
    ///   1. An element marked `[data-tv-autofocus]` — the page's own
    ///      explicit "land here" pin (Welcome → Get started, Discovery →
    ///      first server card, MediaDetail → Continue / Play). If
    ///      multiple are present we take the first one in DOM order.
    ///   2. Otherwise the first focusable in <main> (or in <body> when
    ///      no <main> exists — AuthLayout uses a plain <div> shell).
    /// `force=true` skips the "already focused inside main" early-out,
    /// so a page can rebind focus after a state change (Discovery
    /// Scanning→Found, MediaDetail loading→loaded) even if the user is
    /// already poking around the previous controls.
    function focusFirstInMain(force) {
        if (!document.documentElement.classList.contains('tv-mode')) return;
        if (document.body.classList.contains('player-open')) return;
        // Defer until the next frame so Blazor finishes committing the
        // new route's DOM before we read it.
        requestAnimationFrame(function () {
            const main = document.querySelector('main.animarr-main')
                || document.querySelector('main')
                || document.body;
            // Priority 1: explicit autofocus marker.
            const pinned = Array.from(document.querySelectorAll('[data-tv-autofocus]'))
                .filter(isVisible)[0];
            if (pinned) {
                if (!force) {
                    const active = document.activeElement;
                    if (active && active !== document.body && active === pinned) return;
                }
                focusElement(pinned);
                return;
            }
            // Priority 2: first focusable in main.
            const cands = Array.from(main.querySelectorAll(FOCUSABLE_SELECTOR))
                .filter(isVisible);
            if (cands.length === 0) return;
            if (!force) {
                const active = document.activeElement;
                if (active && active !== document.body && main.contains(active)) return;
            }
            focusElement(cands[0]);
        });
    }
    window.addEventListener('Animarr:navigated', focusFirstInMain);
    window.addEventListener('popstate',          focusFirstInMain);
    window.addEventListener('hashchange',        focusFirstInMain);

    // ─────────────────────────────────────────────────────────────────
    // History-API hook for SPA navigation.
    //
    // Blazor's NavigationManager.NavigateTo eventually calls
    // history.pushState / replaceState. Those don't fire popstate (popstate
    // is only for back/forward), so without this hook we'd miss every
    // forward-link click — Welcome → Discovery, MediaDetail back to /, etc.
    //
    // MainLayout also listens to NavigationManager.LocationChanged and fires
    // an `Animarr:navigated` event, but AuthLayout-hosted pages (Welcome,
    // Login, Setup, Pair, Discovery — the entire first-time TV flow) never
    // mount MainLayout, so that path doesn't catch them. Patching history
    // here means the focus reset works on every route, regardless of which
    // layout owns the screen.
    const _origPush    = history.pushState;
    const _origReplace = history.replaceState;
    history.pushState = function () {
        const r = _origPush.apply(this, arguments);
        window.dispatchEvent(new Event('Animarr:navigated'));
        return r;
    };
    history.replaceState = function () {
        const r = _origReplace.apply(this, arguments);
        window.dispatchEvent(new Event('Animarr:navigated'));
        return r;
    };

    // Expose a small API for components that want to programmatically focus
    // an element or refresh after DOM changes (Blazor re-renders).
    window.animarrTvNav = {
        focusFirst() {
            const all = getFocusables();
            if (all.length > 0) focusElement(all[0]);
        },
        focusFirstInMain,
        /// Force the default-focus pick even if the user already has
        /// something focused. Used by pages that finished an async load
        /// (Discovery: Scanning→Found, MediaDetail: loading→loaded) and
        /// want focus to jump to the headline new content.
        refocusAutofocus() { focusFirstInMain(true); },
        focusElement,
        findNeighbour,
    };
})();
