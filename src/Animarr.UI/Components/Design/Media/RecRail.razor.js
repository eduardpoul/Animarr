// Collocated JS for RecRail — mouse drag-to-scroll + paging-arrow state.
// Touch scrolls natively; this only wires desktop mice: hold-and-drag pans
// the rail, and the component shows ‹ › overlays while more content exists
// in that direction (state pushed via the .NET callback on every scroll).

export function init(row, dotnet) {
    if (!row) return;
    let dragging = false, moved = false, startX = 0, startLeft = 0;

    // Edge-triggered: only cross the JS→.NET bridge when the ‹/› visibility
    // actually flips — scroll fires per frame during a drag and per-event
    // interop calls read as jank.
    let lastL = null, lastR = null;
    const notify = () => {
        const canLeft  = row.scrollLeft > 2;
        const canRight = row.scrollLeft + row.clientWidth < row.scrollWidth - 2;
        if (canLeft === lastL && canRight === lastR) return;
        lastL = canLeft; lastR = canRight;
        dotnet.invokeMethodAsync('OnRailScrollState', canLeft, canRight).catch(() => {});
    };

    row.addEventListener('scroll', notify, { passive: true });
    const ro = new ResizeObserver(notify);
    ro.observe(row);
    row.__recRailRo = ro;

    // Posters are <a><img> — both natively draggable, and the browser's
    // drag-and-drop ghost swallows pointermove, so grabbing a poster
    // wouldn't pan the rail (only the gaps between cards would).
    row.addEventListener('dragstart', (e) => e.preventDefault());

    row.addEventListener('pointerdown', (e) => {
        if (e.pointerType !== 'mouse' || e.button !== 0) return;
        dragging = true; moved = false;
        startX = e.clientX; startLeft = row.scrollLeft;
    });
    row.addEventListener('pointermove', (e) => {
        if (!dragging) return;
        const dx = e.clientX - startX;
        if (!moved && Math.abs(dx) > 5) {
            moved = true;
            try { row.setPointerCapture(e.pointerId); } catch { }
            row.classList.add('is-dragging');
            // Inline, not class-only: with snap active every scrollLeft write
            // re-snaps to the nearest card, so a drag jumps card-by-card
            // instead of panning. Restored on release so proximity snap
            // settles the row afterwards.
            row.__snapPrev = row.style.scrollSnapType;
            row.style.scrollSnapType = 'none';
            row.style.scrollBehavior = 'auto';
        }
        if (moved) row.scrollLeft = startLeft - dx;
    });
    const end = (e) => {
        if (!dragging) return;
        dragging = false;
        try { row.releasePointerCapture(e.pointerId); } catch { }
        row.classList.remove('is-dragging');
        row.style.scrollSnapType = row.__snapPrev || '';
        row.style.scrollBehavior = '';
    };
    row.addEventListener('pointerup', end);
    row.addEventListener('pointercancel', end);
    // A drag must not count as a click on the card under the cursor.
    row.addEventListener('click', (e) => {
        if (moved) { e.preventDefault(); e.stopPropagation(); moved = false; }
    }, true);

    notify();
}

export function scrollRail(row, dir) {
    if (!row) return;
    row.scrollBy({ left: dir * row.clientWidth * 0.8, behavior: 'smooth' });
}

export function destroy(row) {
    if (row && row.__recRailRo) { row.__recRailRo.disconnect(); delete row.__recRailRo; }
}
