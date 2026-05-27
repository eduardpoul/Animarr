// ====== shared components ======
const { useState, useEffect, useRef, useMemo, useCallback } = React;

// ---- Icons (tiny inline SVG, stroke-based) ----
const Icon = ({ name, size = 16, stroke = 1.6, ...rest }) => {
  const props = {
    width: size, height: size, viewBox: "0 0 24 24",
    fill: "none", stroke: "currentColor", strokeWidth: stroke,
    strokeLinecap: "round", strokeLinejoin: "round",
    ...rest,
  };
  switch (name) {
    case "catalog":   return <svg {...props}><rect x="3" y="3" width="7" height="9"/><rect x="14" y="3" width="7" height="5"/><rect x="14" y="12" width="7" height="9"/><rect x="3" y="16" width="7" height="5"/></svg>;
    case "torrent":   return <svg {...props}><path d="M12 4v12"/><path d="m6 10 6 6 6-6"/><path d="M5 20h14"/></svg>;
    case "folder":    return <svg {...props}><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>;
    case "clock":     return <svg {...props}><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg>;
    case "settings":  return <svg {...props}><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/></svg>;
    case "search":    return <svg {...props}><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>;
    case "play":      return <svg {...props}><path d="M6 4l14 8-14 8z"/></svg>;
    case "plus":      return <svg {...props}><path d="M12 5v14M5 12h14"/></svg>;
    case "x":         return <svg {...props}><path d="M6 6l12 12M18 6l-12 12"/></svg>;
    case "check":     return <svg {...props}><path d="M5 12l5 5 9-12"/></svg>;
    case "warn":      return <svg {...props}><path d="M12 9v4"/><circle cx="12" cy="17" r=".5" fill="currentColor"/><path d="M10.6 3.7 1.7 18.4A2 2 0 0 0 3.4 21h17.2a2 2 0 0 0 1.7-2.6L13.4 3.7a2 2 0 0 0-2.8 0z"/></svg>;
    case "info":      return <svg {...props}><circle cx="12" cy="12" r="9"/><path d="M12 8h.01M11 12h1v5h1"/></svg>;
    case "magic":     return <svg {...props}><path d="m13 4 1 3 3 1-3 1-1 3-1-3-3-1 3-1z"/><path d="m4 14 .8 2.2L7 17l-2.2.8L4 20l-.8-2.2L1 17l2.2-.8z"/><path d="m17 14 .8 2.2L20 17l-2.2.8L17 20l-.8-2.2L14 17l2.2-.8z"/></svg>;
    case "chev-r":    return <svg {...props}><path d="m9 6 6 6-6 6"/></svg>;
    case "chev-d":    return <svg {...props}><path d="m6 9 6 6 6-6"/></svg>;
    case "chev-l":    return <svg {...props}><path d="m15 6-6 6 6 6"/></svg>;
    case "undo":      return <svg {...props}><path d="M9 14 4 9l5-5"/><path d="M4 9h11a5 5 0 0 1 0 10h-4"/></svg>;
    case "pencil":    return <svg {...props}><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/></svg>;
    case "trash":     return <svg {...props}><path d="M3 6h18"/><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><path d="M19 6 18 20a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg>;
    case "refresh":   return <svg {...props}><path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 4v5h-5"/></svg>;
    case "pause":     return <svg {...props}><rect x="6" y="4" width="4" height="16"/><rect x="14" y="4" width="4" height="16"/></svg>;
    case "download":  return <svg {...props}><path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M5 21h14"/></svg>;
    case "upload":    return <svg {...props}><path d="M12 21V9"/><path d="m7 14 5-5 5 5"/><path d="M5 3h14"/></svg>;
    case "video":     return <svg {...props}><rect x="3" y="6" width="13" height="12" rx="2"/><path d="m22 8-6 4 6 4z"/></svg>;
    case "image":     return <svg {...props}><rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="9" cy="9" r="2"/><path d="m21 15-5-5L5 21"/></svg>;
    case "file":      return <svg {...props}><path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z"/><path d="M14 3v6h6"/></svg>;
    case "link":      return <svg {...props}><path d="M10 13a5 5 0 0 0 7 0l3-3a5 5 0 0 0-7-7l-1 1"/><path d="M14 11a5 5 0 0 0-7 0l-3 3a5 5 0 0 0 7 7l1-1"/></svg>;
    case "external":  return <svg {...props}><path d="M15 3h6v6"/><path d="M10 14 21 3"/><path d="M21 14v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5"/></svg>;
    case "star":      return <svg {...props}><polygon points="12 2 15.1 8.3 22 9.3 17 14.1 18.2 21 12 17.8 5.8 21 7 14.1 2 9.3 8.9 8.3 12 2"/></svg>;
    case "star-fill": return <svg {...props} fill="currentColor"><polygon points="12 2 15.1 8.3 22 9.3 17 14.1 18.2 21 12 17.8 5.8 21 7 14.1 2 9.3 8.9 8.3 12 2"/></svg>;
    case "filter":    return <svg {...props}><path d="M3 5h18l-7 9v6l-4-2v-4z"/></svg>;
    case "sparkle":   return <svg {...props}><path d="M12 3v6m0 6v6m9-9h-6m-6 0H3"/><path d="m18 6-3 3M6 18l3-3m9 3-3-3M6 6l3 3"/></svg>;
    default:          return null;
  }
};

// ---- Logo mark (original A glyph) ----
const Logo = ({ size = 22 }) => (
  <div style={{
    width: size, height: size, display:"grid", placeItems:"center",
    borderRadius: 6,
    background: "linear-gradient(135deg, var(--accent) 0%, oklch(0.42 0.18 25) 100%)",
    color: "#fff", fontFamily: "var(--font-display)", fontSize: size * 0.6,
    boxShadow: "0 4px 16px -4px var(--accent-soft), inset 0 1px 0 rgba(255,255,255,0.18)",
    lineHeight: 1,
  }}>A</div>
);

// ---- Sidebar ----
const SIDEBAR_W = 230;
const Sidebar = ({ route, onRoute }) => {
  const items = [
    { id: "catalog",  label: "Catalog",        icon: "catalog",  count: window.LIBRARY.length },
    { id: "torrents", label: "Downloads",      icon: "torrent",  count: window.TORRENTS.filter(t=>t.state!=="seeding").length, badge: "live" },
    { id: "settings", label: "Settings",       icon: "settings" },
  ];
  return (
    <aside style={{
      width: SIDEBAR_W, height: "100%", flexShrink: 0,
      background: "linear-gradient(180deg, rgba(20,15,12,0.92), rgba(10,8,7,0.88))",
      backdropFilter: "blur(20px)",
      borderRight: "1px solid var(--border)",
      display: "flex", flexDirection: "column",
      position: "relative", zIndex: 5,
    }}>
      {/* brand */}
      <div style={{ padding: "20px 18px 22px", display: "flex", alignItems: "center", gap: 12, borderBottom: "1px solid var(--border)" }}>
        <Logo size={28} />
        <div>
          <div style={{ fontFamily: "var(--font-display)", fontSize: 17, letterSpacing: -0.4, lineHeight: 1 }}>ANIMARR</div>
          <div style={{ fontFamily: "var(--font-mono)", fontSize: 9.5, color: "var(--text-faint)", marginTop: 4, letterSpacing: 0.6 }}>v2.0 · LOCAL</div>
        </div>
      </div>

      {/* nav */}
      <nav style={{ padding: "16px 10px", display: "flex", flexDirection: "column", gap: 2 }}>
        <div style={{ fontFamily:"var(--font-mono)", fontSize: 9.5, color:"var(--text-faint)", padding: "8px 10px 6px", letterSpacing: 1 }}>LIBRARY</div>
        {items.map(it => {
          const active = route === it.id;
          return (
            <button
              key={it.id}
              onClick={() => onRoute(it.id)}
              style={{
                all: "unset",
                cursor: "pointer",
                display: "flex", alignItems: "center", gap: 12,
                padding: "10px 12px",
                borderRadius: 8,
                position: "relative",
                color: active ? "var(--text)" : "var(--text-dim)",
                background: active ? "linear-gradient(90deg, var(--accent-soft), transparent 80%)" : "transparent",
                fontSize: 13.5, fontWeight: active ? 600 : 500,
              }}
              onMouseEnter={e => { if (!active) e.currentTarget.style.background = "rgba(255,255,255,0.03)"; }}
              onMouseLeave={e => { if (!active) e.currentTarget.style.background = "transparent"; }}
            >
              {active && <div style={{ position:"absolute", left: 0, top: 8, bottom: 8, width: 2, background: "var(--accent)", borderRadius: 2 }} />}
              <Icon name={it.icon} size={17} />
              <span style={{ flex: 1 }}>{it.label}</span>
              {typeof it.count === "number" && (
                <span style={{
                  fontFamily: "var(--font-mono)", fontSize: 10,
                  color: active ? "var(--accent-hi)" : "var(--text-faint)",
                  background: active ? "rgba(0,0,0,0.25)" : "transparent",
                  padding: "1px 6px", borderRadius: 4,
                }}>{it.count}</span>
              )}
              {it.badge && (
                <span style={{ display:"inline-flex", alignItems:"center", gap:4, fontFamily:"var(--font-mono)", fontSize: 9, color:"var(--success)", textTransform:"uppercase" }}>
                  <span style={{ width:6, height:6, borderRadius:6, background:"var(--success)", boxShadow:`0 0 8px var(--success)` }} />
                  {it.badge}
                </span>
              )}
            </button>
          );
        })}
      </nav>

      <div style={{ flex: 1 }} />

      {/* status footer */}
      <div style={{ padding: 14, borderTop: "1px solid var(--border)" }}>
        <div style={{ background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 10, padding: 12 }}>
          <div style={{ display:"flex", alignItems:"center", gap:8, marginBottom: 8 }}>
            <span style={{ width:7, height:7, borderRadius:6, background:"var(--success)", boxShadow:`0 0 10px var(--success)` }} />
            <span style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-dim)", letterSpacing:0.6 }}>LLM · ONLINE</span>
          </div>
          <div style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)", lineHeight: 1.55 }}>
            ollama · qwen2.5:1.5b<br/>
            <span style={{ color:"var(--text-dim)" }}>localhost:11434</span>
          </div>
          <div style={{ height: 4, background:"rgba(255,255,255,0.04)", borderRadius: 4, marginTop: 10, overflow:"hidden" }}>
            <div style={{ width:"68%", height:"100%", background:"linear-gradient(90deg, var(--accent), var(--accent-hi))", boxShadow:"0 0 8px var(--accent-soft)" }} />
          </div>
          <div style={{ display:"flex", justifyContent:"space-between", fontFamily:"var(--font-mono)", fontSize: 9.5, color:"var(--text-faint)", marginTop: 6 }}>
            <span>queue</span><span>17 / 25</span>
          </div>
        </div>
      </div>
    </aside>
  );
};

// ---- Backdrop (global ambient fanart, cross-fades between images) ----
const Backdrop = ({ image, blur = 14, brightness = 38, hue = 0 }) => {
  const [layers, setLayers] = useState([{ url: image, key: 0, on: true }]);
  const counter = useRef(0);
  useEffect(() => {
    setLayers(prev => {
      if (prev[prev.length - 1]?.url === image) return prev;
      counter.current += 1;
      const next = [...prev.slice(-2), { url: image, key: counter.current, on: false }];
      requestAnimationFrame(() => {
        setLayers(p => p.map(l => l.key === counter.current ? { ...l, on: true } : l));
      });
      return next;
    });
  }, [image]);

  return (
    <div aria-hidden style={{
      position: "fixed", inset: 0, zIndex: 0, pointerEvents: "none",
      overflow: "hidden", background: "var(--bg-0)",
    }}>
      {layers.map(l => (
        <div key={l.key} style={{
          position:"absolute", inset: "-4%",
          backgroundImage: `url("${l.url}")`,
          backgroundSize: "cover", backgroundPosition: "center",
          filter: `blur(${blur}px) brightness(${brightness}%) saturate(0.95)`,
          opacity: l.on ? 1 : 0,
          transition: "opacity 1.6s ease",
          animation: "bd-pan 30s ease-in-out infinite alternate",
        }} />
      ))}
      {/* vignette */}
      <div style={{
        position:"absolute", inset: 0,
        background: "radial-gradient(120% 80% at 50% 30%, transparent 0%, rgba(10,8,7,0.5) 60%, rgba(10,8,7,0.95) 100%)",
      }} />
      {/* color tint follows the current hue */}
      <div style={{
        position:"absolute", inset: 0,
        background: `linear-gradient(180deg, oklch(0.20 0.10 ${hue} / 0.30), transparent 30%, rgba(10,8,7,0.6) 100%)`,
        mixBlendMode: "multiply",
        transition: "background 1.6s ease",
      }} />
      <style>{`@keyframes bd-pan { 0% { transform: scale(1.02) translate(-1%, -1%); } 100% { transform: scale(1.08) translate(2%, 2%); } }`}</style>
    </div>
  );
};

// ---- Poster (procedural / image-backed) ----
// Each poster: image background via picsum (deterministic per id) + heavy design treatment:
// dark gradient bottom, large CJK watermark top-right, title set in bold, year tag.
const Poster = ({ item, w = 188, h = 280, ribbon = true, onClick }) => {
  // Numeric refs for font/CJK sizing — gracefully handle "100%" etc.
  const wRef = typeof w === "number" ? w : 220;
  const hRef = typeof h === "number" ? h : 320;
  // procedural background image (deterministic) - cinematic photos from picsum
  const seed = item.id;
  const bg = item.bd || `https://picsum.photos/seed/${encodeURIComponent(seed)}/400/600`;
  const hue = item.hue ?? 12;
  return (
    <button
      onClick={onClick}
      style={{
        all: "unset",
        cursor: "pointer",
        position:"relative",
        width: w, height: h, flexShrink: 0,
        borderRadius: 10,
        overflow:"hidden",
        background: `linear-gradient(180deg, oklch(0.30 0.08 ${hue}), oklch(0.18 0.06 ${hue}))`,
        boxShadow: "0 10px 30px -16px rgba(0,0,0,0.7), 0 0 0 1px rgba(255,255,255,0.04) inset",
        transition: "transform .25s ease, box-shadow .25s ease",
      }}
      onMouseEnter={e => {
        e.currentTarget.style.transform = "translateY(-3px) scale(1.015)";
        e.currentTarget.style.boxShadow = `0 18px 40px -14px oklch(0.4 0.2 ${hue} / 0.45), 0 0 0 1px var(--accent-line) inset`;
      }}
      onMouseLeave={e => {
        e.currentTarget.style.transform = "";
        e.currentTarget.style.boxShadow = "0 10px 30px -16px rgba(0,0,0,0.7), 0 0 0 1px rgba(255,255,255,0.04) inset";
      }}
    >
      {/* image */}
      <div style={{
        position:"absolute", inset: 0,
        backgroundImage: `url("${bg}")`,
        backgroundSize: "cover", backgroundPosition: "center",
        filter: `saturate(0.85) brightness(0.85) hue-rotate(${(hue - 30) * 0.3}deg)`,
      }} />
      {/* color wash to unify by hue */}
      <div style={{
        position:"absolute", inset: 0,
        background: `linear-gradient(150deg, oklch(0.35 0.18 ${hue} / 0.55), oklch(0.15 0.08 ${hue} / 0.25) 60%)`,
        mixBlendMode: "color",
      }} />
      {/* darken bottom for title */}
      <div style={{
        position:"absolute", inset: 0,
        background: "linear-gradient(180deg, transparent 35%, rgba(0,0,0,0.85) 100%)",
      }} />

      {/* CJK watermark (decorative) */}
      <div style={{
        position:"absolute", top: 8, right: 10, opacity: 0.55,
        fontFamily:"var(--font-cjk)", fontSize: Math.min(hRef * 0.18, 50),
        color:"#fff", lineHeight: 0.85, letterSpacing: 2,
        writingMode:"vertical-rl", textOrientation:"upright",
        textShadow:"0 1px 0 rgba(0,0,0,0.5)",
        maxHeight: hRef * 0.5, overflow:"hidden",
      }}>{item.cjk}</div>

      {/* type ribbon */}
      {ribbon && (
        <div style={{
          position:"absolute", top: 10, left: 10,
          fontFamily:"var(--font-mono)", fontSize: 9, letterSpacing: 1.4,
          padding: "3px 7px", borderRadius: 3,
          background: "rgba(0,0,0,0.55)",
          backdropFilter:"blur(4px)",
          border:"1px solid rgba(255,255,255,0.12)",
          textTransform:"uppercase",
          color:"#fff",
        }}>{item.type}</div>
      )}

      {/* confidence indicator */}
      {item.conf !== undefined && item.conf < 0.85 && (
        <div style={{
          position:"absolute", top: 10, right: 10,
          display:"flex", alignItems:"center", gap: 4,
          fontFamily:"var(--font-mono)", fontSize: 9,
          padding:"3px 6px", borderRadius: 3,
          background:"oklch(0.55 0.16 60 / 0.85)",
          color:"#000", fontWeight: 700,
        }}>
          <Icon name="warn" size={9} stroke={2.4} /> {Math.round(item.conf*100)}%
        </div>
      )}

      {/* title block */}
      <div style={{ position:"absolute", left: 0, right: 0, bottom: 0, padding: "14px 12px 12px" }}>
        <div style={{
          fontFamily:"var(--font-display)",
          fontSize: wRef > 160 ? 17 : 14,
          lineHeight: 1.05,
          letterSpacing: -0.3,
          color:"#fff",
          textShadow:"0 2px 12px rgba(0,0,0,0.7)",
          display:"-webkit-box", WebkitLineClamp: 2, WebkitBoxOrient:"vertical", overflow:"hidden",
        }}>{item.title}</div>
        <div style={{
          fontFamily:"var(--font-mono)", fontSize: 10.5,
          color:"var(--text-dim)", marginTop: 6, display:"flex", alignItems:"center", gap: 8,
        }}>
          <span>{item.year}</span>
          {item.episodes > 1 && <span style={{ color:"var(--text-faint)" }}>· {item.episodes} EP</span>}
          {item.rating && (
            <span style={{ marginLeft:"auto", color:"var(--accent-hi)" }}>★ {item.rating.toFixed(1)}</span>
          )}
        </div>
      </div>
    </button>
  );
};

// ---- Small UI primitives ----
const Btn = ({ children, kind = "ghost", icon, ...rest }) => {
  const styles = {
    primary: { background: "var(--accent)", color:"#fff", border:"1px solid var(--accent)" },
    solid:   { background:"var(--surface-2)", color:"var(--text)", border:"1px solid var(--border-strong)" },
    ghost:   { background:"transparent", color:"var(--text-dim)", border:"1px solid var(--border-strong)" },
    danger:  { background:"transparent", color:"oklch(0.74 0.20 25)", border:"1px solid oklch(0.55 0.15 25 / 0.5)" },
    flat:    { background:"transparent", color:"var(--text-dim)", border:"1px solid transparent" },
  };
  return (
    <button
      {...rest}
      style={{
        display:"inline-flex", alignItems:"center", gap: 7,
        padding:"7px 12px",
        fontSize: 12.5, fontWeight: 600, letterSpacing: 0.1,
        borderRadius: 6,
        cursor: "pointer",
        transition: "transform .1s ease, background .15s ease, border-color .15s ease, color .15s ease",
        ...(styles[kind] || styles.ghost),
        ...(rest.style || {}),
      }}
      onMouseEnter={e => {
        if (kind === "primary") e.currentTarget.style.background = "var(--accent-hi)";
        else if (kind === "ghost" || kind === "flat") e.currentTarget.style.background = "rgba(255,255,255,0.04)";
      }}
      onMouseLeave={e => {
        if (kind === "primary") e.currentTarget.style.background = "var(--accent)";
        else if (kind === "ghost" || kind === "flat") e.currentTarget.style.background = "transparent";
      }}
    >
      {icon && <Icon name={icon} size={13} />}
      {children}
    </button>
  );
};

const Pill = ({ children, tone = "neutral" }) => {
  const tones = {
    neutral: { bg:"rgba(255,255,255,0.05)", c:"var(--text-dim)", b:"var(--border)" },
    accent:  { bg:"var(--accent-soft)",      c:"var(--accent-hi)", b:"var(--accent-line)" },
    success: { bg:"oklch(0.74 0.15 150 / 0.15)", c:"var(--success)", b:"oklch(0.55 0.15 150 / 0.35)" },
    warn:    { bg:"oklch(0.80 0.17 75 / 0.15)",  c:"var(--warn)",    b:"oklch(0.60 0.16 75 / 0.4)" },
    danger:  { bg:"oklch(0.66 0.20 25 / 0.15)",  c:"var(--accent-hi)", b:"var(--accent-line)" },
  };
  const t = tones[tone] || tones.neutral;
  return (
    <span style={{
      display:"inline-flex", alignItems:"center", gap: 5,
      fontFamily:"var(--font-mono)", fontSize: 10, letterSpacing: 0.6, textTransform:"uppercase",
      padding:"3px 7px", borderRadius: 4,
      background: t.bg, color: t.c, border:`1px solid ${t.b}`,
    }}>{children}</span>
  );
};

const Toggle = ({ on, onChange }) => (
  <button onClick={() => onChange(!on)} style={{
    all:"unset", cursor:"pointer",
    width: 34, height: 18, borderRadius: 18,
    background: on ? "var(--accent)" : "rgba(255,255,255,0.12)",
    position:"relative", transition:"background .15s ease",
    boxShadow: on ? "0 0 12px var(--accent-soft)" : "inset 0 1px 2px rgba(0,0,0,0.4)",
  }}>
    <span style={{
      position:"absolute", top: 2, left: on ? 18 : 2,
      width: 14, height: 14, borderRadius: 14, background:"#fff",
      transition: "left .15s ease",
      boxShadow: "0 1px 3px rgba(0,0,0,0.4)",
    }} />
  </button>
);

const Field = ({ label, hint, children }) => (
  <label style={{ display:"flex", flexDirection:"column", gap: 6 }}>
    <span style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)", letterSpacing: 1, textTransform:"uppercase" }}>{label}</span>
    {children}
    {hint && <span style={{ fontSize: 11.5, color:"var(--text-faint)" }}>{hint}</span>}
  </label>
);

const Input = (props) => (
  <input {...props} style={{
    background:"var(--surface)", color:"var(--text)",
    border:"1px solid var(--border-strong)", borderRadius: 6,
    padding:"8px 10px", fontSize: 13, outline:"none",
    fontFamily: props.mono ? "var(--font-mono)" : "var(--font-ui)",
    ...(props.style || {}),
  }} />
);

const Select = ({ children, ...rest }) => (
  <select {...rest} style={{
    background:"var(--surface)", color:"var(--text)",
    border:"1px solid var(--border-strong)", borderRadius: 6,
    padding:"8px 10px", fontSize: 13, outline:"none",
    appearance:"none",
    backgroundImage: `url("data:image/svg+xml,%3Csvg width='10' height='6' xmlns='http://www.w3.org/2000/svg'%3E%3Cpath d='M1 1l4 4 4-4' stroke='%23a8a097' fill='none' stroke-width='1.5'/%3E%3C/svg%3E")`,
    backgroundRepeat:"no-repeat", backgroundPosition:"right 10px center",
    paddingRight: 28,
    ...(rest.style || {}),
  }}>{children}</select>
);

// ---- Page chrome ----
const PageHeader = ({ overline, title, sub, right, big = false }) => (
  <div style={{ display:"flex", alignItems:"flex-end", gap: 18, padding: "0 0 18px", borderBottom:"1px solid var(--border)", marginBottom: 22 }}>
    <div style={{ flex: 1 }}>
      {overline && (
        <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color:"var(--accent-hi)", textTransform:"uppercase", marginBottom: 8 }}>
          {overline}
        </div>
      )}
      <h1 style={{
        margin: 0,
        fontFamily:"var(--font-display)",
        fontSize: big ? 64 : 38,
        lineHeight: 0.95,
        letterSpacing: big ? -1.6 : -0.8,
        color:"var(--text)",
      }}>{title}</h1>
      {sub && (
        <div style={{ color:"var(--text-dim)", marginTop: 10, fontSize: 13.5, maxWidth: 720 }}>{sub}</div>
      )}
    </div>
    {right && <div style={{ display:"flex", alignItems:"center", gap: 8 }}>{right}</div>}
  </div>
);

// ---- Container (constrained content width) ----
const Container = ({ density = "comfortable", top = false, children, style }) => (
  <div style={{
    maxWidth: 1480,
    margin: "0 auto",
    padding: density === "compact"
      ? (top ? "26px 36px 0" : "0 36px")
      : (top ? "38px 48px 0" : "0 48px"),
    ...(style || {}),
  }}>{children}</div>
);

Object.assign(window, {
  Icon, Logo, Sidebar, Backdrop, Poster, Btn, Pill, Toggle, Field, Input, Select, PageHeader, SIDEBAR_W, Container,
});
