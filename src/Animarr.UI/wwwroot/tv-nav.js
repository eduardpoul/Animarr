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

        // Backspace / Browser-back on TV remote → close modal / drawer.
        if (e.key === 'Escape') {
            // Let scoped components handle Esc themselves; we don't
            // pre-empt it. Their click-dismiss usually wires Esc too.
            return;
        }
    }

    document.addEventListener('keydown', onKeyDown, { capture: true });

    // Expose a small API for components that want to programmatically focus
    // an element or refresh after DOM changes (Blazor re-renders).
    window.animarrTvNav = {
        focusFirst() {
            const all = getFocusables();
            if (all.length > 0) focusElement(all[0]);
        },
        focusElement,
        findNeighbour,
    };
})();
