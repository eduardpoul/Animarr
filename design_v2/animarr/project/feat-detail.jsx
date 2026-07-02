// ====== Animarr — MediaDetail extras: Franchise + Similar ======
// Loads after feat-shared.jsx. Exposes window.MediaExtras (rendered at the
// bottom of MediaDetailV3): the franchise watch-order rail + a "похожее" rail.
const { useState: fdtS } = React;

const FORMAT_RU = { TV: "ТВ", Movie: "Фильм", OVA: "OVA" };

// External (not-in-library) "+ хочу" button — local visual state so it doesn't
// pollute the real per-user watchlist Set with synthetic ids.
const ExtWantButton = ({ node }) => {
  const [want, setWant] = fdtS(false);
  return (
    <div role="button" tabIndex={0} onClick={(e) => { e.stopPropagation(); setWant(w => !w); }}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); setWant(w => !w); } }}
      className="wl-btn tv-focus" style={{
        display: "inline-flex", alignItems: "center", gap: 6, cursor: "pointer",
        padding: "6px 10px", borderRadius: 999, fontSize: 11.5, fontWeight: 700,
        background: want ? "var(--accent-soft)" : "rgba(255,255,255,0.06)",
        border: "1px solid " + (want ? "var(--accent-line)" : "var(--border-strong)"),
        color: want ? "var(--accent-hi)" : "var(--text-dim)",
      }}>
      {want ? <window.Icon name="check" size={13} stroke={2.6} /> : (window.BookmarkPlus ? <window.BookmarkPlus size={13} /> : "+")}
      {want ? window.RU.inList : window.RU.add}
    </div>
  );
};

const FranchiseNode = ({ node, index, onOpen }) => {
  const libItem = node.inLib ? window.LIBRARY.find(x => x.id === node.id) : null;
  const hue = libItem ? (libItem.hue ?? 12) : ((node.title.length * 47) % 360);
  return (
    <div style={{ width: 158, flexShrink: 0, scrollSnapAlign: "start" }}>
      <div style={{ position: "relative", borderRadius: 12, ...(node.current ? { outline: "2px solid var(--accent)", outlineOffset: 2 } : {}) }}>
        <div style={{
          position: "absolute", top: -8, left: -8, zIndex: 5, width: 26, height: 26, borderRadius: 8,
          background: node.current ? "var(--accent)" : "var(--surface-3)", color: node.current ? "#fff" : "var(--text-dim)",
          border: "1px solid " + (node.current ? "var(--accent)" : "var(--border-strong)"),
          display: "grid", placeItems: "center", fontFamily: "var(--font-mono)", fontSize: 12, fontWeight: 700,
          boxShadow: "0 4px 10px rgba(0,0,0,0.45)",
        }}>{index + 1}</div>

        {libItem ? (
          <div style={{ borderRadius: 12, overflow: "hidden" }}>
            <window.Poster item={libItem} w={158} h={222} ribbon={false} onClick={() => onOpen && onOpen(libItem.id)} />
          </div>
        ) : (
          <div style={{
            position: "relative", width: 158, height: 222, borderRadius: 12, overflow: "hidden",
            border: "1px dashed var(--border-strong)",
            background: `linear-gradient(160deg, oklch(0.30 0.09 ${hue}), oklch(0.15 0.05 ${hue}))`,
            display: "flex", flexDirection: "column", justifyContent: "flex-end", padding: 12,
          }}>
            <div style={{ position: "absolute", inset: 0, background: "linear-gradient(180deg, rgba(0,0,0,0.12), rgba(0,0,0,0.62))" }} />
            <div style={{ position: "absolute", top: 10, right: 10, fontFamily: "var(--font-mono)", fontSize: 8.5, letterSpacing: 0.8, color: "rgba(255,255,255,.82)", background: "rgba(0,0,0,.5)", border: "1px solid rgba(255,255,255,.14)", padding: "2px 6px", borderRadius: 4, textTransform: "uppercase" }}>нет в библиотеке</div>
            <div style={{ position: "relative", fontFamily: "var(--font-display)", fontSize: 15, fontWeight: 700, color: "#fff", lineHeight: 1.12, textShadow: "0 2px 8px rgba(0,0,0,.75)" }}>{node.title}</div>
            <div style={{ position: "relative", marginTop: 11 }}><ExtWantButton node={node} /></div>
          </div>
        )}

        {node.current && <div style={{ position: "absolute", bottom: 8, left: 8, zIndex: 4, fontFamily: "var(--font-mono)", fontSize: 9, fontWeight: 700, letterSpacing: 0.6, textTransform: "uppercase", color: "#fff", background: "var(--accent)", padding: "3px 8px", borderRadius: 5 }}>вы здесь</div>}
      </div>

      <div style={{ marginTop: 10 }}>
        <div style={{ fontSize: 12.5, fontWeight: 600, color: libItem ? "var(--text)" : "var(--text-dim)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{node.title}</div>
        <div style={{ display: "flex", alignItems: "center", gap: 7, marginTop: 5, fontFamily: "var(--font-mono)", fontSize: 10, color: "var(--text-faint)" }}>
          <span>{node.year}</span>
          <span style={{ color: "var(--border-strong)" }}>·</span>
          <span>{FORMAT_RU[node.format] || node.format}{node.seasons ? ` ${node.seasons}` : ""}</span>
        </div>
        <div style={{ marginTop: 6 }}>
          <span style={{ fontSize: 9.5, fontWeight: 700, letterSpacing: 0.4, textTransform: "uppercase", color: "var(--accent-hi)", background: "var(--accent-soft)", border: "1px solid var(--accent-line)", padding: "2px 7px", borderRadius: 5 }}>{node.relation}</span>
        </div>
      </div>
    </div>
  );
};

const FranchiseRail = ({ item, onOpen }) => {
  const fr = window.franchiseFor && window.franchiseFor(item);
  if (!fr) return null;
  return (
    <div style={{ marginBottom: 48 }}>
      <div style={{ display: "flex", alignItems: "flex-end", gap: 14, marginBottom: 22 }}>
        <div>
          <div style={{ fontFamily: "var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color: "var(--accent-hi)" }}>{window.RU.franchise.toUpperCase()}</div>
          <h2 style={{ margin: "6px 0 0", fontFamily: "var(--font-display)", fontSize: 30, letterSpacing: -0.6, fontWeight: 700 }}>{fr.title}</h2>
        </div>
        <div style={{ marginLeft: "auto", display: "flex", alignItems: "center", gap: 10 }}>
          <div style={{ fontFamily: "var(--font-mono)", fontSize: 12, color: "var(--text-faint)" }}>просмотрено {fr.watched} из {fr.total}</div>
        </div>
      </div>
      <div className="feat-rail" style={{ display: "flex", gap: 24, overflowX: "auto", paddingTop: 8, paddingBottom: 8, scrollSnapType: "x proximity" }}>
        {fr.nodes.map((n, i) => <FranchiseNode key={i} node={n} index={i} onOpen={onOpen} />)}
      </div>
    </div>
  );
};

const SimilarRail = ({ item, onOpen }) => {
  const sims = window.similarFor && window.similarFor(item);
  if (!sims || !sims.length) return null;
  return (
    <div style={{ marginBottom: 40 }}>
      <div style={{ display: "flex", alignItems: "flex-end", gap: 14, marginBottom: 18 }}>
        <div>
          <div style={{ fontFamily: "var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color: "var(--accent-hi)" }}>ПОХОЖЕЕ</div>
          <h2 style={{ margin: "6px 0 0", fontFamily: "var(--font-display)", fontSize: 30, letterSpacing: -0.6, fontWeight: 700 }}>С этим смотрят</h2>
        </div>
      </div>
      <div className="feat-rail" style={{ display: "flex", gap: 16, overflowX: "auto", paddingBottom: 6, scrollSnapType: "x proximity" }}>
        {sims.map(({ item: it, reason }) => (
          <div key={it.id} style={{ width: 170, flexShrink: 0, scrollSnapAlign: "start" }}>
            <window.Poster item={it} w={170} h={240} onClick={() => onOpen && onOpen(it.id)} />
            <div style={{ marginTop: 9, fontSize: 11.5, color: "var(--text-dim)", lineHeight: 1.42, display: "-webkit-box", WebkitLineClamp: 2, WebkitBoxOrient: "vertical", overflow: "hidden" }}>
              <span style={{ color: "var(--accent-hi)" }}>●</span> {reason}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
};

const MediaExtras = ({ item, onOpen }) => (
  <div style={{ padding: `10px ${window.SIDE_PAD || 48}px 40px` }}>
    <FranchiseRail item={item} onOpen={onOpen} />
    <SimilarRail item={item} onOpen={onOpen} />
  </div>
);

Object.assign(window, { MediaExtras, FranchiseRail, SimilarRail });
