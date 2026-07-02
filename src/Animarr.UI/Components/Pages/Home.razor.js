// Collocated JS for Home — library-block fill logic.
// CSS alone can't know how many 210px columns auto-fill produced, so it
// can't both FILL the two rows and keep the "view all" tile right after the
// last poster. This measures the resolved track count, shows exactly
// (columns × rows − 1) posters and lets the tile flow into the next cell —
// or straight after the last poster when the library runs out.

export function initLibGrid(grid) {
    if (!grid || grid.__libRo) return;

    const apply = () => {
        const style = getComputedStyle(grid);
        // Resolved template lists the actual tracks ("210px 210px …") for
        // both auto-fill and the phone's repeat(2, 1fr).
        const cols = style.gridTemplateColumns.split(' ').filter(Boolean).length;
        const rows = window.matchMedia('(max-width: 639px)').matches ? 4 : 2;
        const slots = Math.max(2, cols * rows);
        const tile = grid.querySelector('.home-lib__more');
        const posters = [...grid.children].filter(k => k !== tile);
        const maxPosters = Math.min(posters.length, slots - 1);
        posters.forEach((p, i) => { p.style.display = i < maxPosters ? '' : 'none'; });
    };

    const ro = new ResizeObserver(apply);
    ro.observe(grid);
    // Belt-and-braces: some embedded webviews are stingy with RO callbacks
    // on viewport resizes — the window event catches those.
    window.addEventListener('resize', apply);
    grid.__libRo = ro;
    grid.__libResize = apply;
    apply();
}

export function destroyLibGrid(grid) {
    if (!grid) return;
    if (grid.__libRo) { grid.__libRo.disconnect(); delete grid.__libRo; }
    if (grid.__libResize) { window.removeEventListener('resize', grid.__libResize); delete grid.__libResize; }
}
