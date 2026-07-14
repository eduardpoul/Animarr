// ====== Animarr — shared feature UI primitives ======
// Loads AFTER components-v4.jsx, BEFORE app-v4.jsx. Exposes on window:
//   useWatchlist(), toggleWatchlist(id), WatchlistButton
//   EpisodeBadges  — unified badge hierarchy for episode cards
//   FeatureRail, SectionHead
//   date helpers: fmtTime, fmtDayLabel, relLabel (RU)
const { useState: fsS, useEffect: fsE, useRef: fsR } = React;

// ── one-time CSS for hover-reveal + rails ─────────────────────
(function injectFeatCSS() {
  if (document.getElementById("feat-css")) return;
  const el = document.createElement("style");
  el.id = "feat-css";
  el.textContent = `
    .wl-btn{ transition:opacity .18s ease, background .18s ease, color .18s ease, transform .1s ease; }
    .wl-btn:active{ transform:scale(.9); }
    @media (hover:hover){
      .poster-btn .wl-btn{ opacity:0; }
      .poster-btn:hover .wl-btn{ opacity:1; }
      .wl-btn.on{ opacity:1 !important; }
    }
    .feat-rail{ scrollbar-width:none; }
    .feat-rail::-webkit-scrollbar{ display:none; }
    @keyframes spin{ to{ transform:rotate(360deg); } }
    .feat-chip{ display:inline-flex; align-items:center; gap:4px; font-family:var(--font-mono); font-weight:700;
      letter-spacing:.4px; text-transform:uppercase; white-space:nowrap; }
  `;
  document.head.appendChild(el);
})();

// ── watchlist store (external, event-driven) ──────────────────
const wlListeners = new Set();
window.toggleWatchlist = (id) => {
  const s = window.WATCHLIST; if (!s) return;
  if (s.has(id)) s.delete(id); else s.add(id);
  wlListeners.forEach(fn => fn());
};
window.useWatchlist = () => {
  const [, force] = fsS(0);
  fsE(() => { const fn = () => force(x => x + 1); wlListeners.add(fn); return () => wlListeners.delete(fn); }, []);
  return {
    has: (id) => !!(window.WATCHLIST && window.WATCHLIST.has(id)),
    toggle: window.toggleWatchlist,
    list: () => [...(window.WATCHLIST || [])],
    count: () => (window.WATCHLIST ? window.WATCHLIST.size : 0),
  };
};

// ── WatchlistButton ───────────────────────────────────────────
// variant: "poster" (compact circular, corner overlay) | "chip" (pill w/ label) | "wide" (full button)
const WatchlistButton = ({ id, variant = "chip", stop = true }) => {
  const wl = window.useWatchlist();
  const inList = wl.has(id);
  const onClick = (e) => { if (stop) { e.stopPropagation(); e.preventDefault(); } wl.toggle(id); };

  if (variant === "poster") {
    return (
      <div role="button" tabIndex={0} aria-label={inList ? window.RU.inList : window.RU.add}
        onClick={onClick} onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") onClick(e); }}
        className={"wl-btn tv-focus" + (inList ? " on" : "")}
        title={inList ? window.RU.inList : window.RU.wantToWatch}
        style={{
          position: "absolute", top: 9, right: 9, zIndex: 6,
          width: 30, height: 30, borderRadius: 9, display: "grid", placeItems: "center",
          background: inList ? "var(--accent)" : "rgba(10,8,7,0.55)",
          border: "1px solid " + (inList ? "var(--accent)" : "rgba(255,255,255,0.22)"),
          color: inList ? "#fff" : "var(--text)", backdropFilter: "blur(8px)", cursor: "pointer",
        }}>
        {inList ? <window.Icon name="check" size={15} stroke={2.6} /> : <BookmarkPlus size={15} />}
      </div>
    );
  }
  if (variant === "wide") {
    return (
      <button onClick={onClick} className="tv-focus" style={{
        all: "unset", cursor: "pointer", boxSizing: "border-box",
        display: "inline-flex", alignItems: "center", justifyContent: "center", gap: 8,
        padding: "11px 16px", borderRadius: 10, fontWeight: 700, fontSize: 13,
        background: inList ? "var(--accent-soft)" : "var(--surface-2)",
        border: "1px solid " + (inList ? "var(--accent-line)" : "var(--border-strong)"),
        color: inList ? "var(--accent-hi)" : "var(--text)",
      }}>
        {inList ? <window.Icon name="check" size={16} stroke={2.4} /> : <BookmarkPlus size={16} />}
        {inList ? window.RU.inList : window.RU.wantToWatch}
      </button>
    );
  }
  // chip
  return (
    <div role="button" tabIndex={0} onClick={onClick}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") onClick(e); }}
      className="wl-btn tv-focus" title={inList ? window.RU.inList : window.RU.wantToWatch}
      style={{
        display: "inline-flex", alignItems: "center", gap: 6, cursor: "pointer",
        padding: "6px 11px", borderRadius: 999, fontWeight: 700, fontSize: 12,
        background: inList ? "var(--accent-soft)" : "rgba(255,255,255,0.05)",
        border: "1px solid " + (inList ? "var(--accent-line)" : "var(--border-strong)"),
        color: inList ? "var(--accent-hi)" : "var(--text-dim)",
      }}>
      {inList ? <window.Icon name="check" size={13} stroke={2.6} /> : <BookmarkPlus size={13} />}
      {inList ? window.RU.inList : window.RU.add}
    </div>
  );
};

const BookmarkPlus = ({ size = 14 }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
    strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round">
    <path d="M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h6" />
    <path d="M17 3v6M20 6h-6" />
  </svg>
);

// ── EpisodeBadges — unified hierarchy ─────────────────────────
// The badge grid on an episode card is getting crowded (watched · progress ·
// rating · filler/recap). This centralizes it. Two directions via
// window.__feat.badgeStyle:  "chips" (loud pills) | "meta" (calm meta text)
// layout: "thumb" (over the artwork) | "row" (inline in a list row)
const KIND_TONE = {
  filler: { c: "var(--warn)", bg: "oklch(0.80 0.17 75 / 0.16)", b: "oklch(0.60 0.16 75 / 0.5)", label: () => window.RU.filler },
  recap:  { c: "var(--text-dim)", bg: "rgba(255,255,255,0.07)", b: "var(--border-strong)", label: () => window.RU.recap },
};
const EpisodeBadges = ({ item, ep, layout = "thumb", showRating = true }) => {
  const meta = (ep && ep.meta) || (window.epMeta ? window.epMeta(item, ep.n) : null);
  if (!meta) return null;
  const style = (window.__feat && window.__feat.badgeStyle) || "chips";
  const tone = KIND_TONE[meta.kind];

  if (layout === "row") {
    // inline chips next to the title
    return (
      <span style={{ display: "inline-flex", alignItems: "center", gap: 7 }}>
        {tone && style === "chips" && (
          <span className="feat-chip" style={{ fontSize: 9.5, padding: "2px 7px", borderRadius: 5, color: tone.c, background: tone.bg, border: "1px solid " + tone.b }}>{tone.label()}</span>
        )}
        {tone && style === "meta" && (
          <span className="feat-chip" style={{ fontSize: 10, color: tone.c, background: "none", border: "none", padding: 0 }}>
            <span style={{ width: 5, height: 5, borderRadius: 5, background: tone.c, display: "inline-block" }} /> {tone.label()}
          </span>
        )}
      </span>
    );
  }

  // thumb layout — a single compact chip, top-left under the number is handled
  // by the card; here we return a corner chip for filler/recap only.
  if (!tone) return null;
  if (style === "meta") {
    return (
      <span className="feat-chip" style={{ fontSize: 9, padding: "2px 6px", borderRadius: 4, color: tone.c, background: "rgba(10,8,7,0.6)", border: "1px solid " + tone.b, backdropFilter: "blur(4px)" }}>{tone.label()}</span>
    );
  }
  return (
    <span className="feat-chip" style={{ fontSize: 9, padding: "3px 7px", borderRadius: 5, color: tone.c, background: tone.bg, border: "1px solid " + tone.b, backdropFilter: "blur(4px)" }}>{tone.label()}</span>
  );
};

// ── FeatureRail — horizontal scroll section (Home rails) ──────
const FeatureRail = ({ overline, title, sub, right, children, pad }) => (
  <div style={{ padding: pad || "0 var(--side, 48px)", marginBottom: 40 }}>
    <div style={{ display: "flex", alignItems: "flex-end", gap: 14, marginBottom: 16 }}>
      <div style={{ minWidth: 0 }}>
        {overline && <div style={{ fontFamily: "var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color: "var(--accent-hi)", textTransform: "uppercase", marginBottom: 6 }}>{overline}</div>}
        <div style={{ display: "flex", alignItems: "baseline", gap: 12 }}>
          <h2 style={{ margin: 0, fontFamily: "var(--font-display)", fontSize: 26, letterSpacing: -0.6, fontWeight: 700 }}>{title}</h2>
          {sub && <span style={{ fontSize: 12.5, color: "var(--text-faint)" }}>{sub}</span>}
        </div>
      </div>
      <div style={{ marginLeft: "auto", flexShrink: 0 }}>{right}</div>
    </div>
    <div className="feat-rail" style={{ display: "flex", gap: 14, overflowX: "auto", paddingBottom: 4, scrollSnapType: "x proximity" }}>
      {children}
    </div>
  </div>
);

const SectionHead = ({ overline, title, sub, right }) => (
  <div style={{ display: "flex", alignItems: "flex-end", gap: 14, marginBottom: 20, paddingBottom: 14, borderBottom: "1px solid var(--border)" }}>
    <div style={{ minWidth: 0 }}>
      {overline && <div style={{ fontFamily: "var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color: "var(--accent-hi)", textTransform: "uppercase", marginBottom: 7 }}>{overline}</div>}
      <h1 style={{ margin: 0, fontFamily: "var(--font-display)", fontSize: 40, letterSpacing: -1.2, fontWeight: 700, lineHeight: 1 }}>{title}</h1>
      {sub && <div style={{ fontSize: 13.5, color: "var(--text-dim)", marginTop: 10, maxWidth: 620, lineHeight: 1.55 }}>{sub}</div>}
    </div>
    <div style={{ marginLeft: "auto", flexShrink: 0 }}>{right}</div>
  </div>
);

// ── RU date helpers ───────────────────────────────────────────
const fmtTime = (d) => `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
const fmtDayLabel = (d) => {
  const now = new Date();
  if (sameDay(d, now)) return window.RU.today;
  const tm = new Date(now.getTime() + 864e5);
  if (sameDay(d, tm)) return window.RU.tomorrow;
  return `${window.RU.weekdaysShort[d.getDay()]}, ${d.getDate()} ${window.RU.months[d.getMonth()]}`;
};
const relLabel = (d) => {
  const ms = d.getTime() - Date.now();
  if (ms <= 0) return "вышла";
  const hrs = ms / 36e5;
  if (hrs < 1) return `через ${Math.round(ms / 6e4)} мин`;
  if (hrs < 24) return `через ${Math.round(hrs)} ч`;
  const days = Math.round(hrs / 24);
  const w = days % 10 === 1 && days % 100 !== 11 ? "день" : (days % 10 >= 2 && days % 10 <= 4 && (days % 100 < 10 || days % 100 >= 20) ? "дня" : "дней");
  return `через ${days} ${w}`;
};

Object.assign(window, {
  WatchlistButton, EpisodeBadges, FeatureRail, SectionHead, BookmarkPlus,
  fmtTime, fmtDayLabel, relLabel, sameDay,
});
