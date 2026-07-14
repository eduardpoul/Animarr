// ====== Animarr TV — lightweight 10-foot UI ======
// Design intent: weak TV silicon hates decoding many images at once, so this
// build keeps EXACTLY ONE photographic image on screen at a time (the hero /
// detail backdrop), and a "Data saver" setting drops even that — everything
// else (rails, tiles, posters, episodes) is a pure CSS gradient tinted by the
// item's stored `hue`. Zero image decode, instant paint, cheap scroll.
// Navigation is remote-first: ↑ ↓ ← → move focus, Enter selects, Esc/⌫ go back.
// Mouse hover + click also work for desktop testing.

const { useState, useEffect, useRef, useMemo } = React;
const { LIBRARY, FOLDERS } = window;

// ── helpers ──────────────────────────────────────────────────────────────
const tileBg = (hue) =>
  `radial-gradient(120% 90% at 78% 8%, oklch(0.58 0.16 ${hue} / 0.9) 0%, transparent 55%),` +
  `linear-gradient(158deg, oklch(0.42 0.13 ${hue}) 0%, oklch(0.24 0.08 ${hue}) 58%, oklch(0.15 0.05 ${hue}) 100%)`;

const fmtTime = (sec) => {
  const m = Math.floor(sec / 60), s = Math.floor(sec % 60);
  return m + ":" + String(s).padStart(2, "0");
};
const runtimeToSec = (rt) => {
  let h = 0, m = 0;
  const hm = /(\d+)h/.exec(rt); if (hm) h = +hm[1];
  const mm = /(\d+)m/.exec(rt);  if (mm) m = +mm[1];
  return (h * 60 + m) * 60 || 24 * 60;
};

function itemsForCategory(folder) {
  const t = folder.title;
  return LIBRARY.filter(i => {
    if (t === "Anime") return i.type === "Anime";
    if (t === "Movies") return i.type === "Movie";
    if (t === "Serials") return i.type === "Series";
    if (t === "Multserials") return i.type === "Multserials";
    if (t === "Donghua") return (i.tags || []).includes("Donghua");
    return true;
  });
}
const CATS = FOLDERS
  .map(f => ({ ...f, items: itemsForCategory(f) }))
  .filter(f => f.items.length > 0);

// Next-up / continue-watching for the CURRENT user (window.WATCHING is swapped
// by applyUser on profile change).
const nextUpList = () => (window.WATCHING || []).map(w => {
  const it = LIBRARY.find(l => l.id === w.id);
  return it ? { ...it, ep: w.ep, progress: w.progress, kind: w.kind } : null;
}).filter(Boolean);

const FEATURED = [...LIBRARY].sort((a, b) => b.rating - a.rating)[0];

// Per-item watch progress (drives the episode checkmarks / resume).
const watchStateFor = (id) => (window.WATCHING || []).find(w => w.id === id) || null;

const ACCENTS = {
  crimson: { base: "oklch(0.66 0.20 25)",  hi: "oklch(0.76 0.21 25)",  soft: "oklch(0.66 0.20 25 / 0.18)",  line: "oklch(0.66 0.20 25 / 0.45)" },
  amber:   { base: "oklch(0.72 0.17 60)",  hi: "oklch(0.82 0.16 60)",  soft: "oklch(0.72 0.17 60 / 0.18)",  line: "oklch(0.72 0.17 60 / 0.45)" },
  green:   { base: "oklch(0.70 0.15 150)", hi: "oklch(0.80 0.15 150)", soft: "oklch(0.70 0.15 150 / 0.18)", line: "oklch(0.70 0.15 150 / 0.45)" },
  blue:    { base: "oklch(0.66 0.16 240)", hi: "oklch(0.76 0.15 240)", soft: "oklch(0.66 0.16 240 / 0.18)", line: "oklch(0.66 0.16 240 / 0.45)" },
  violet:  { base: "oklch(0.66 0.20 290)", hi: "oklch(0.76 0.20 290)", soft: "oklch(0.66 0.20 290 / 0.18)", line: "oklch(0.66 0.20 290 / 0.45)" },
};
const ACCENT_KEYS = Object.keys(ACCENTS);

// Keydown helper — normalises remote / keyboard keys and blocks page scroll.
const NAV_KEYS = ["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Enter", " ", "Backspace", "Escape"];
// A modal (Settings) grabs all keys; underlying screens stop listening so an
// Enter doesn't open something behind the overlay.
let MODAL_COUNT = 0;
function useKeys(handler, modal = false) {
  const ref = useRef(handler); ref.current = handler;
  useEffect(() => {
    const onKey = (e) => {
      if (!modal && MODAL_COUNT > 0) return;
      if (NAV_KEYS.includes(e.key)) e.preventDefault();
      ref.current(e.key, e);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [modal]);
}

const SAFE = 90; // TV title-safe side padding

// ── shared UI ────────────────────────────────────────────────────────────
function Avatar({ user, size = 38, focused = false }) {
  const hue = { master: 150, user: 240, uploader: 60 }[user.role] || 150;
  return (
    <div className={focused ? "tvf" : ""} data-f={focused ? "1" : "0"} style={{
      width: size, height: size, borderRadius: "50%", display: "grid", placeItems: "center",
      background: `oklch(0.62 0.13 ${hue})`, color: "#06140c",
      fontWeight: 800, fontSize: size * 0.42, flex: "0 0 auto",
    }}>{user.name[0]}</div>
  );
}

function TopBar({ user, settingsFocused, onSettings }) {
  return (
    <div style={{
      position: "absolute", top: 0, left: 0, right: 0, height: 84, zIndex: 30,
      display: "flex", alignItems: "center", justifyContent: "space-between", padding: `0 ${SAFE}px`,
    }}>
      <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
        <div style={{
          width: 34, height: 34, borderRadius: 9, display: "grid", placeItems: "center",
          background: "linear-gradient(135deg, var(--accent) 0%, oklch(0.42 0.18 25) 100%)",
          color: "#fff", fontWeight: 800, fontSize: 19, boxShadow: "0 6px 18px -6px var(--accent-soft)",
        }}>A</div>
        <div style={{ fontWeight: 800, fontSize: 21, letterSpacing: 1 }}>ANIMARR</div>
        <div style={{
          marginLeft: 4, padding: "3px 9px", borderRadius: 6, fontSize: 12, fontWeight: 700,
          letterSpacing: 1.4, background: "var(--accent-soft)", color: "var(--accent-hi)",
        }}>TV</div>
      </div>
      <div style={{ display: "flex", alignItems: "center", gap: 22, color: "var(--text-dim)" }}>
        <div style={{ fontSize: 18, fontWeight: 600, fontVariantNumeric: "tabular-nums" }}>21:30</div>
        {onSettings ? (
          <button className="tvf" data-f={settingsFocused ? "1" : "0"} onClick={onSettings}
            style={{ display: "flex", alignItems: "center", gap: 10, padding: "5px 8px 5px 14px", borderRadius: 999,
              background: settingsFocused ? "var(--surface-3)" : "transparent" }}>
            <span style={{ fontSize: 16, fontWeight: 600, color: "var(--text)" }}>{user.name}</span>
            <Avatar user={user} size={38} />
          </button>
        ) : <Avatar user={user} size={38} />}
      </div>
    </div>
  );
}

function HintBar({ items }) {
  return (
    <div style={{
      position: "absolute", bottom: 22, left: 0, right: 0, zIndex: 40,
      display: "flex", justifyContent: "center", gap: 26, color: "var(--text-faint)", fontSize: 15, fontWeight: 500,
    }}>
      {items.map(([k, l]) => (
        <span key={l} style={{ display: "inline-flex", alignItems: "center", gap: 8 }}>
          <kbd style={{
            display: "inline-grid", placeItems: "center", minWidth: 26, height: 26, padding: "0 7px",
            borderRadius: 6, background: "var(--surface-2)", border: "1px solid var(--border-strong)",
            color: "var(--text-dim)", fontFamily: "inherit", fontSize: 13, fontWeight: 700,
          }}>{k}</kbd>{l}
        </span>
      ))}
    </div>
  );
}

// CSS-only poster — no image decode.
function GradientPoster({ item, w, h, focused, kicker, flat }) {
  return (
    <div className={flat ? "tvf-flat" : "tvf"} data-f={focused ? "1" : "0"} style={{
      width: w, height: h, borderRadius: 14, overflow: "hidden", position: "relative",
      background: tileBg(item.hue), flex: "0 0 auto",
      boxShadow: focused ? undefined : "inset 0 0 0 1px rgba(255,255,255,0.05)",
    }}>
      <div style={{
        position: "absolute", right: -6, top: -22, fontFamily: "var(--font-cjk)",
        fontSize: h * 0.62, lineHeight: 1, color: "rgba(255,255,255,0.10)",
        fontWeight: 900, whiteSpace: "nowrap", pointerEvents: "none",
      }}>{item.cjk}</div>
      <div style={{ position: "absolute", inset: 0, background: "linear-gradient(0deg, rgba(0,0,0,0.55) 0%, transparent 55%)" }} />
      <div style={{ position: "absolute", left: 14, right: 14, bottom: 12 }}>
        {kicker && <div style={{ fontSize: 12, fontWeight: 700, letterSpacing: 1, color: "var(--accent-hi)", marginBottom: 5 }}>{kicker}</div>}
        <div style={{ fontSize: w > 220 ? 20 : 17, fontWeight: 700, lineHeight: 1.1, textShadow: "0 2px 8px rgba(0,0,0,0.6)" }}>{item.title}</div>
        <div style={{ display: "flex", gap: 10, marginTop: 6, fontSize: 13, color: "rgba(255,255,255,0.82)", fontWeight: 500 }}>
          <span style={{ color: "var(--accent-hi)", fontWeight: 700 }}>★ {item.rating.toFixed(1)}</span>
          <span>{item.year}</span>
        </div>
      </div>
      {flat && focused && <FocusRing r={14} />}
    </div>
  );
}

// Inset focus ring drawn ABOVE a card's content (gradient overlays would hide a
// box-shadow ring). Inset → never clipped by a parent's overflow:hidden.
function FocusRing({ r }) {
  return <div style={{ position: "absolute", inset: 0, borderRadius: r, boxShadow: "inset 0 0 0 3px var(--accent-hi)", pointerEvents: "none", zIndex: 4 }} />;
}

// One photographic backdrop — or a hue gradient when Data saver is on.
function Backdrop({ item, dataSaver, mode }) {
  if (dataSaver) {
    return <div style={{ position: "absolute", inset: 0, background: tileBg(item.hue), opacity: mode === "blur" ? 0.5 : 0.85 }} />;
  }
  const common = { position: "absolute", animation: "tvfade .5s ease" };
  if (mode === "blur") {
    return <img src={item.bd} alt="" style={{ ...common, inset: 0, width: "100%", height: "100%", objectFit: "cover", filter: "blur(28px) brightness(0.32)", transform: "scale(1.1)" }} />;
  }
  return <img key={item.id} src={item.bd} alt="" style={{ ...common, top: 0, right: 0, width: mode === "detail" ? "68%" : "74%", height: mode === "detail" ? "100%" : 720, objectFit: "cover", objectPosition: "center 26%", filter: "brightness(1.1) saturate(1.12)" }} />;
}

// ── LOGIN — TV "who's watching" profile picker ─────────────────────────────
function Login({ onPick, dataSaver }) {
  const users = window.USERS;
  const [i, setI] = useState(0);
  useKeys((k) => {
    if (k === "ArrowRight") setI(x => Math.min(users.length - 1, x + 1));
    else if (k === "ArrowLeft") setI(x => Math.max(0, x - 1));
    else if (k === "Enter" || k === " ") onPick(users[i]);
  });
  return (
    <div style={{ position: "absolute", inset: 0, background: "var(--bg-0)", overflow: "hidden" }}>
      {dataSaver
        ? <div style={{ position: "absolute", inset: 0, background: tileBg(25), opacity: 0.4 }} />
        : <img src={window.BD.mist} alt="" style={{ position: "absolute", inset: 0, width: "100%", height: "100%", objectFit: "cover", filter: "blur(16px) brightness(0.3) saturate(0.9)", transform: "scale(1.08)" }} />}
      <div style={{ position: "absolute", inset: 0, background: "radial-gradient(75% 75% at 50% 45%, transparent, rgba(8,6,5,0.8))" }} />

      <div style={{ position: "absolute", inset: 0, display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 14, marginBottom: 14 }}>
          <div style={{ width: 46, height: 46, borderRadius: 12, display: "grid", placeItems: "center",
            background: "linear-gradient(135deg, var(--accent), oklch(0.42 0.18 25))", color: "#fff", fontWeight: 800, fontSize: 26 }}>A</div>
          <div style={{ fontWeight: 800, fontSize: 30, letterSpacing: 1 }}>ANIMARR <span style={{ fontSize: 16, color: "var(--accent-hi)", letterSpacing: 1.4 }}>TV</span></div>
        </div>
        <div style={{ fontSize: 34, fontWeight: 700, color: "var(--text)", marginBottom: 50 }}>Who’s watching?</div>

        <div style={{ display: "flex", gap: 40 }}>
          {users.map((u, idx) => {
            const focused = idx === i;
            const hue = { master: 150, user: 240, uploader: 60 }[u.role] || 150;
            return (
              <button key={u.id} className="tvf" data-f={focused ? "1" : "0"} onMouseEnter={() => setI(idx)} onClick={() => onPick(u)}
                style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 16, padding: 8, borderRadius: 18 }}>
                <div style={{ width: 150, height: 150, borderRadius: 20, display: "grid", placeItems: "center",
                  background: `linear-gradient(155deg, oklch(0.6 0.13 ${hue}), oklch(0.34 0.09 ${hue}))`,
                  color: "#fff", fontWeight: 800, fontSize: 64 }}>{u.name[0]}</div>
                <div style={{ textAlign: "center" }}>
                  <div style={{ fontSize: 22, fontWeight: 700, color: focused ? "var(--text)" : "var(--text-dim)" }}>{u.name}</div>
                  <div style={{ fontSize: 14, color: "var(--text-faint)", textTransform: "capitalize", marginTop: 2 }}>{u.role}</div>
                </div>
              </button>
            );
          })}
        </div>
      </div>
      <HintBar items={[["↔", "Choose"], ["OK", "Sign in"]]} />
    </div>
  );
}

// ── SETTINGS — only what matters on a TV ───────────────────────────────────
function Settings({ user, settings, setSetting, onSwitchProfile, onSignOut, onClose }) {
  const cycle = (key, arr, dir) => {
    const at = arr.indexOf(settings[key]);
    setSetting(key, arr[(at + dir + arr.length) % arr.length]);
  };
  const rows = [
    { label: "Profile", value: user.name, sub: user.role, enter: onSwitchProfile, action: "Switch ›" },
    { kind: "section", label: "Playback" },
    { label: "Audio language", value: settings.audio,
      prev: () => cycle("audio", ["Japanese", "Mandarin", "English", "Russian"], -1),
      next: () => cycle("audio", ["Japanese", "Mandarin", "English", "Russian"], 1), choice: true },
    { label: "Subtitles", value: settings.subs,
      prev: () => cycle("subs", ["Russian", "English", "Off"], -1),
      next: () => cycle("subs", ["Russian", "English", "Off"], 1), choice: true },
    { label: "Subtitle size", value: settings.subSize + " px",
      prev: () => setSetting("subSize", Math.max(14, settings.subSize - 2)),
      next: () => setSetting("subSize", Math.min(30, settings.subSize + 2)), choice: true },
    { kind: "section", label: "Display" },
    { label: "Accent color", value: settings.accent, swatch: ACCENTS[settings.accent].base,
      prev: () => cycle("accent", ACCENT_KEYS, -1), next: () => cycle("accent", ACCENT_KEYS, 1), choice: true },
    { label: "Data saver", sub: "Hide backdrops — fewer images for slow TVs", value: settings.dataSaver ? "On" : "Off",
      prev: () => setSetting("dataSaver", !settings.dataSaver), next: () => setSetting("dataSaver", !settings.dataSaver),
      enter: () => setSetting("dataSaver", !settings.dataSaver), choice: true, toggle: true },
    { kind: "section", label: "Account" },
    { label: "Sign out", value: "", enter: onSignOut, action: "›", danger: true },
  ];
  const focusable = rows.map((r, i) => (r.kind === "section" ? -1 : i)).filter(i => i >= 0);
  const [fi, setFi] = useState(0);
  const idx = focusable[fi];

  useEffect(() => { MODAL_COUNT++; return () => { MODAL_COUNT--; }; }, []);
  useKeys((k) => {
    if (k === "Escape" || k === "Backspace") return onClose();
    if (k === "ArrowDown") setFi(x => Math.min(focusable.length - 1, x + 1));
    else if (k === "ArrowUp") setFi(x => Math.max(0, x - 1));
    else if (k === "ArrowRight") rows[idx].next?.();
    else if (k === "ArrowLeft") rows[idx].prev?.();
    else if (k === "Enter" || k === " ") rows[idx].enter?.();
  }, true);

  return (
    <div style={{ position: "absolute", inset: 0, background: "rgba(8,6,5,0.94)", backdropFilter: "blur(8px)", zIndex: 60 }}>
      <div style={{ position: "absolute", left: "50%", top: 90, transform: "translateX(-50%)", width: 760 }}>
        <div style={{ display: "flex", alignItems: "center", gap: 16, marginBottom: 28 }}>
          <Avatar user={user} size={52} />
          <div>
            <div style={{ fontSize: 34, fontWeight: 800, letterSpacing: -0.6 }}>Settings</div>
            <div style={{ fontSize: 16, color: "var(--text-faint)" }}>Signed in as {user.name}</div>
          </div>
        </div>

        <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          {rows.map((r, i) => {
            if (r.kind === "section") return (
              <div key={"s" + i} style={{ fontSize: 14, fontWeight: 700, letterSpacing: 2, color: "var(--text-faint)", margin: "20px 0 6px" }}>{r.label.toUpperCase()}</div>
            );
            const focused = i === idx;
            return (
              <div key={i} className="tvf" data-f={focused ? "1" : "0"} onMouseEnter={() => setFi(focusable.indexOf(i))}
                onClick={() => (r.enter ? r.enter() : r.next?.())}
                style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 20,
                  padding: "18px 22px", borderRadius: 12, cursor: "pointer",
                  background: focused ? "var(--surface-2)" : "var(--surface)", border: "1px solid var(--border)" }}>
                <div>
                  <div style={{ fontSize: 20, fontWeight: 600, color: r.danger ? "var(--accent-hi)" : "var(--text)" }}>{r.label}</div>
                  {r.sub && <div style={{ fontSize: 14, color: "var(--text-faint)", marginTop: 3 }}>{r.sub}</div>}
                </div>
                <div style={{ display: "flex", alignItems: "center", gap: 14, color: "var(--text-dim)", fontSize: 19, fontWeight: 600 }}>
                  {r.swatch && <span style={{ width: 18, height: 18, borderRadius: "50%", background: r.swatch }} />}
                  {r.choice && focused && <span style={{ color: "var(--accent-hi)" }}>‹</span>}
                  {r.value && <span style={{ textTransform: r.label === "Accent color" ? "capitalize" : "none" }}>{r.value}</span>}
                  {r.choice && focused && <span style={{ color: "var(--accent-hi)" }}>›</span>}
                  {r.action && <span style={{ color: r.danger ? "var(--accent-hi)" : "var(--text-faint)" }}>{r.action}</span>}
                </div>
              </div>
            );
          })}
        </div>
      </div>
      <HintBar items={[["↕", "Move"], ["↔", "Change"], ["OK", "Select"], ["⌫", "Close"]]} />
    </div>
  );
}

// ── HOME ───────────────────────────────────────────────────────────────────
function Home({ user, dataSaver, onOpenItem, onOpenCategory, onSettings }) {
  const next = useMemo(nextUpList, [user.id]);
  const hasNext = next.length > 0;
  const heroBase = hasNext ? next[0] : FEATURED;

  // zones: -1 top(settings) · 0 hero · 1 next-up · 2 categories
  const zonesList = hasNext ? [-1, 0, 1, 2] : [-1, 0, 2];
  const [zi, setZi] = useState(zonesList.indexOf(0));
  const zone = zonesList[Math.min(zi, zonesList.length - 1)];
  const [ni, setNi] = useState(0);
  const [ci, setCi] = useState(0);

  const heroItem = zone === 1 ? next[ni] : heroBase;

  useKeys((k) => {
    if (k === "ArrowDown") setZi(z => Math.min(zonesList.length - 1, z + 1));
    else if (k === "ArrowUp") setZi(z => Math.max(0, z - 1));
    else if (k === "ArrowRight") {
      if (zone === 1) setNi(i => Math.min(next.length - 1, i + 1));
      else if (zone === 2) setCi(i => Math.min(CATS.length - 1, i + 1));
    } else if (k === "ArrowLeft") {
      if (zone === 1) setNi(i => Math.max(0, i - 1));
      else if (zone === 2) setCi(i => Math.max(0, i - 1));
    } else if (k === "Enter" || k === " ") {
      if (zone === -1) onSettings();
      else if (zone === 0) hasNext ? onOpenItem(heroBase.id, true) : onOpenItem(heroBase.id);
      else if (zone === 1) onOpenItem(next[ni].id);
      else if (zone === 2) onOpenCategory(CATS[ci]);
    }
  });

  return (
    <div style={{ position: "absolute", inset: 0 }}>
      <Backdrop item={heroItem} dataSaver={dataSaver} mode="hero" />
      <div style={{ position: "absolute", inset: 0,
        background: "linear-gradient(90deg, var(--bg-0) 20%, rgba(10,8,7,.5) 44%, rgba(10,8,7,0) 72%)," +
          "linear-gradient(0deg, var(--bg-0) 28%, rgba(10,8,7,.55) 46%, rgba(10,8,7,0) 64%)" }} />

      <TopBar user={user} settingsFocused={zone === -1} onSettings={onSettings} />

      {/* hero text */}
      <div style={{ position: "absolute", left: SAFE, top: 188, maxWidth: 820, zIndex: 20 }}>
        <div style={{ fontSize: 15, fontWeight: 700, letterSpacing: 2.4, color: "var(--accent-hi)", marginBottom: 14 }}>
          {hasNext ? "CONTINUE WATCHING" : "FEATURED"}
        </div>
        <div style={{ fontSize: 76, fontWeight: 800, lineHeight: 0.98, letterSpacing: -1.5, textShadow: "0 4px 30px rgba(0,0,0,0.6)" }}>{heroBase.title}</div>
        <div style={{ display: "flex", alignItems: "center", gap: 18, marginTop: 18, fontSize: 18, color: "var(--text-dim)", fontWeight: 500 }}>
          <span style={{ color: "var(--accent-hi)", fontWeight: 700 }}>★ {heroBase.rating.toFixed(1)}</span>
          <span>{heroBase.year}</span>
          {hasNext && <span>S1 · E{heroBase.ep}</span>}
          {hasNext && <span style={{ color: "var(--text-faint)" }}>{Math.round(heroBase.progress * 100)}% watched</span>}
        </div>
        <div style={{ marginTop: 16, fontSize: 19, lineHeight: 1.55, color: "var(--text-dim)", maxWidth: 700,
          display: "-webkit-box", WebkitLineClamp: 2, WebkitBoxOrient: "vertical", overflow: "hidden" }}>{heroBase.overview}</div>
        <button className="tvf" data-f={zone === 0 ? "1" : "0"} onMouseEnter={() => setZi(zonesList.indexOf(0))}
          onClick={() => hasNext ? onOpenItem(heroBase.id, true) : onOpenItem(heroBase.id)}
          style={{ marginTop: 26, display: "inline-flex", alignItems: "center", gap: 12, padding: "16px 34px", borderRadius: 12,
            fontSize: 20, fontWeight: 700, background: zone === 0 ? "var(--accent-hi)" : "var(--accent)", color: "#fff" }}>
          <span style={{ fontSize: 16 }}>▶</span> {hasNext ? `Resume Episode ${heroBase.ep}` : "View title"}
        </button>
      </div>

      {/* rails */}
      <div style={{ position: "absolute", left: SAFE, right: SAFE, top: 600, zIndex: 20 }}>
        {hasNext && <>
          <RailLabel>NEXT UP</RailLabel>
          <div style={{ display: "flex", gap: 22, marginTop: 14 }}>
            {next.map((it, i) => (
              <div key={it.id} onMouseEnter={() => { setZi(zonesList.indexOf(1)); setNi(i); }} onClick={() => onOpenItem(it.id)} style={{ position: "relative" }}>
                <GradientPoster item={it} w={258} h={150} focused={zone === 1 && ni === i} />
                <div style={{ position: "absolute", left: 0, right: 0, bottom: 0, height: 5, background: "rgba(0,0,0,0.4)", borderRadius: "0 0 14px 14px", overflow: "hidden" }}>
                  <div style={{ height: "100%", width: `${Math.max(it.progress, 0.04) * 100}%`, background: "var(--accent)" }} />
                </div>
              </div>
            ))}
          </div>
        </>}

        <div style={{ marginTop: hasNext ? 30 : 0 }}>
          <RailLabel>BROWSE CATEGORIES</RailLabel>
          <div style={{ display: "flex", gap: 22, marginTop: 14 }}>
            {CATS.map((c, i) => (
              <CategoryTile key={c.id} cat={c} focused={zone === 2 && ci === i}
                onMouseEnter={() => { setZi(zonesList.indexOf(2)); setCi(i); }} onClick={() => onOpenCategory(c)} />
            ))}
          </div>
        </div>
      </div>

      <HintBar items={[["↕", "Move"], ["↔", "Browse"], ["OK", "Open"]]} />
    </div>
  );
}

function RailLabel({ children }) {
  return <div style={{ fontSize: 15, fontWeight: 700, letterSpacing: 2, color: "var(--text-dim)" }}>{children}</div>;
}

function CategoryTile({ cat, focused, onMouseEnter, onClick }) {
  return (
    <button className="tvf" data-f={focused ? "1" : "0"} onMouseEnter={onMouseEnter} onClick={onClick}
      style={{ width: 312, height: 150, borderRadius: 14, overflow: "hidden", position: "relative", textAlign: "left", padding: 0,
        background: tileBg(cat.hue), boxShadow: focused ? undefined : "inset 0 0 0 1px rgba(255,255,255,0.05)" }}>
      <div style={{ position: "absolute", inset: 0, background: "linear-gradient(120deg, rgba(0,0,0,0.45), transparent 70%)" }} />
      <div style={{ position: "absolute", left: 20, bottom: 18 }}>
        <div style={{ fontSize: 26, fontWeight: 800, letterSpacing: -0.4 }}>{cat.title}</div>
        <div style={{ fontSize: 15, color: "rgba(255,255,255,0.78)", fontWeight: 600, marginTop: 4 }}>{cat.items.length} titles</div>
      </div>
      <div style={{ position: "absolute", right: 18, top: 16, fontSize: 26, opacity: 0.5 }}>›</div>
    </button>
  );
}

// ── CATEGORY ───────────────────────────────────────────────────────────────
function Category({ cat, user, onOpenItem, onBack }) {
  const items = cat.items;
  const COLS = 6, CW = 250, CH = 350, GAP = 24, ROWH = CH + GAP;
  const [idx, setIdx] = useState(0);
  const row = Math.floor(idx / COLS);
  const rows = Math.ceil(items.length / COLS);
  const offset = -Math.max(0, Math.min(rows - 2, row - 1)) * ROWH;

  useKeys((k) => {
    if (k === "Backspace" || k === "Escape") return onBack();
    if (k === "ArrowRight") setIdx(i => Math.min(items.length - 1, i + 1));
    else if (k === "ArrowLeft") setIdx(i => Math.max(0, i - 1));
    else if (k === "ArrowDown") setIdx(i => Math.min(items.length - 1, i + COLS));
    else if (k === "ArrowUp") setIdx(i => (i < COLS ? i : i - COLS));
    else if (k === "Enter" || k === " ") onOpenItem(items[idx].id);
  });

  return (
    <div style={{ position: "absolute", inset: 0, background: "var(--bg-0)" }}>
      <div style={{ position: "absolute", inset: 0, background: `radial-gradient(80% 60% at 12% 0%, oklch(0.5 0.13 ${cat.hue} / 0.22), transparent 60%)` }} />
      <TopBar user={user} />
      <div style={{ position: "absolute", left: SAFE, top: 116, display: "flex", alignItems: "baseline", gap: 18 }}>
        <button className="tvf" onClick={onBack} style={{ fontSize: 17, color: "var(--text-dim)", fontWeight: 600, display: "inline-flex", alignItems: "center", gap: 8 }}>‹ Back</button>
        <div style={{ fontSize: 48, fontWeight: 800, letterSpacing: -1 }}>{cat.title}</div>
        <div style={{ fontSize: 18, color: "var(--text-faint)", fontWeight: 600 }}>{items.length} titles</div>
      </div>

      <div style={{ position: "absolute", left: SAFE, right: SAFE, top: 210, bottom: 0, overflow: "hidden" }}>
        <div style={{ display: "grid", gridTemplateColumns: `repeat(${COLS}, ${CW}px)`, gap: GAP,
          transform: `translateY(${offset}px)`, transition: "transform .22s cubic-bezier(.3,.7,.4,1)" }}>
          {items.map((it, i) => (
            <div key={it.id} onMouseEnter={() => setIdx(i)} onClick={() => onOpenItem(it.id)}>
              <GradientPoster item={it} w={CW} h={CH} focused={idx === i} flat />
            </div>
          ))}
        </div>
      </div>
      <HintBar items={[["↕↔", "Move"], ["OK", "Open"], ["⌫", "Back"]]} />
    </div>
  );
}

// ── DETAIL — synopsis + episode list (or the film itself) ───────────────────
function Detail({ item, user, dataSaver, onPlay, onBack }) {
  const isMovie = item.type === "Movie";
  const ws = watchStateFor(item.id);
  const resumeEp = ws ? ws.ep : 1;

  // Episodes are paged into chunks of 50 and laid out as a GRID (not one giant
  // row) — scrolling past 200 episodes in a single line is miserable on a remote.
  const CHUNK = 50, COLS = 8, EW = 188, EH = 92, EGAP = 14, VIS_ROWS = 3;
  const totalEps = isMovie ? 1 : item.episodes;
  const chunks = isMovie ? 1 : Math.ceil(totalEps / CHUNK);
  const hasChunks = chunks > 1;
  const GRID = hasChunks ? 2 : 1; // zone id of the episode grid

  const [chunk, setChunk] = useState(isMovie ? 0 : Math.floor((resumeEp - 1) / CHUNK));
  const chunkStart = chunk * CHUNK;
  const epNums = useMemo(() => isMovie ? [1]
    : Array.from({ length: Math.min(CHUNK, totalEps - chunkStart) }, (_, i) => chunkStart + i + 1), [item.id, chunk]);

  const [zone, setZone] = useState(0);   // 0 buttons · 1 chunks · GRID grid
  const [bi, setBi] = useState(0);       // 0 play · 1 back
  const [gi, setGi] = useState(Math.max(0, (resumeEp - 1) - chunkStart)); // grid focus
  const gRow = Math.floor(gi / COLS);
  const gridRows = Math.ceil(epNums.length / COLS);
  const gridOffset = -Math.max(0, Math.min(gridRows - VIS_ROWS, gRow - 1)) * (EH + EGAP);

  const pickChunk = (c) => { const n = Math.max(0, Math.min(chunks - 1, c)); setChunk(n); setGi(0); };

  useKeys((k) => {
    if (k === "Backspace" || k === "Escape") return onBack();
    if (zone === 0) {
      if (k === "ArrowLeft") setBi(0);
      else if (k === "ArrowRight") setBi(1);
      else if (k === "ArrowDown") setZone(hasChunks ? 1 : GRID);
      else if (k === "Enter" || k === " ") (bi === 0 ? onPlay(item.id, resumeEp) : onBack());
    } else if (zone === 1) {
      if (k === "ArrowLeft") pickChunk(chunk - 1);
      else if (k === "ArrowRight") pickChunk(chunk + 1);
      else if (k === "ArrowUp") setZone(0);
      else if (k === "ArrowDown" || k === "Enter" || k === " ") setZone(GRID);
    } else {
      if (k === "ArrowRight") setGi(i => Math.min(epNums.length - 1, i + 1));
      else if (k === "ArrowLeft") setGi(i => (i > 0 ? i - 1 : i));
      else if (k === "ArrowDown") setGi(i => Math.min(epNums.length - 1, i + COLS));
      else if (k === "ArrowUp") { if (gRow === 0) setZone(hasChunks ? 1 : 0); else setGi(i => i - COLS); }
      else if (k === "Enter" || k === " ") onPlay(item.id, epNums[gi]);
    }
  });

  return (
    <div style={{ position: "absolute", inset: 0, background: "var(--bg-0)" }}>
      <Backdrop item={item} dataSaver={dataSaver} mode="detail" />
      <div style={{ position: "absolute", inset: 0,
        background: "linear-gradient(90deg, var(--bg-0) 26%, rgba(10,8,7,.5) 52%, rgba(10,8,7,0) 78%)," +
          "linear-gradient(0deg, var(--bg-0) 16%, rgba(10,8,7,.5) 40%, transparent 64%)" }} />
      <TopBar user={user} />

      <div style={{ position: "absolute", left: SAFE, top: 150, maxWidth: 880, zIndex: 20 }}>
        <div style={{ display: "flex", gap: 10, marginBottom: 14 }}>
          {(item.tags || []).slice(0, 2).map(t => (
            <span key={t} style={{ padding: "5px 13px", borderRadius: 7, fontSize: 14, fontWeight: 600,
              background: "var(--surface-2)", border: "1px solid var(--border-strong)", color: "var(--text-dim)" }}>{t}</span>
          ))}
        </div>
        <div style={{ fontSize: 76, fontWeight: 800, lineHeight: 0.96, letterSpacing: -1.8, textShadow: "0 4px 30px rgba(0,0,0,0.6)" }}>{item.title}</div>
        <div style={{ display: "flex", alignItems: "center", gap: 20, marginTop: 16, fontSize: 19, color: "var(--text-dim)", fontWeight: 500 }}>
          <span style={{ color: "var(--accent-hi)", fontWeight: 700 }}>★ {item.rating.toFixed(1)}</span>
          <span>{item.year}</span>
          <span>{isMovie ? item.runtime : `${item.episodes} episodes`}</span>
          <span>{item.studio}</span>
        </div>
        <div style={{ marginTop: 16, fontSize: 19, lineHeight: 1.55, color: "var(--text)", maxWidth: 760,
          display: "-webkit-box", WebkitLineClamp: 2, WebkitBoxOrient: "vertical", overflow: "hidden" }}>{item.overview}</div>
        <div style={{ display: "flex", gap: 16, marginTop: 24 }}>
          <button className="tvf" data-f={zone === 0 && bi === 0 ? "1" : "0"} onMouseEnter={() => { setZone(0); setBi(0); }} onClick={() => onPlay(item.id, resumeEp)}
            style={{ display: "inline-flex", alignItems: "center", gap: 12, padding: "15px 34px", borderRadius: 12, fontSize: 20, fontWeight: 700,
              background: zone === 0 && bi === 0 ? "var(--accent-hi)" : "var(--accent)", color: "#fff" }}>
            <span style={{ fontSize: 16 }}>▶</span> {isMovie ? "Play movie" : (ws ? `Resume Episode ${resumeEp}` : "Play Episode 1")}
          </button>
          <button className="tvf" data-f={zone === 0 && bi === 1 ? "1" : "0"} onMouseEnter={() => { setZone(0); setBi(1); }} onClick={onBack}
            style={{ padding: "15px 30px", borderRadius: 12, fontSize: 20, fontWeight: 700,
              background: zone === 0 && bi === 1 ? "var(--surface-3)" : "var(--surface)", border: "1px solid var(--border-strong)", color: "var(--text)" }}>Back</button>
        </div>
      </div>

      {/* episodes (paged grid) / film */}
      <div style={{ position: "absolute", left: SAFE, right: SAFE, top: 560, bottom: 64, zIndex: 20, display: "flex", flexDirection: "column" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 16 }}>
          <RailLabel>{isMovie ? "FILM" : "EPISODES"}</RailLabel>
          {!isMovie && <span style={{ fontSize: 14, color: "var(--text-faint)", fontWeight: 600 }}>{item.season} · {totalEps} total</span>}
        </div>

        {hasChunks && (
          <div style={{ display: "flex", gap: 10, marginTop: 14, flexWrap: "wrap" }}>
            {Array.from({ length: chunks }, (_, c) => {
              const a = c * CHUNK + 1, b = Math.min(totalEps, (c + 1) * CHUNK);
              const focused = zone === 1 && chunk === c, active = chunk === c;
              return (
                <button key={c} className="tvf" data-f={focused ? "1" : "0"} onMouseEnter={() => { setZone(1); pickChunk(c); }} onClick={() => { pickChunk(c); setZone(GRID); }}
                  style={{ padding: "9px 16px", borderRadius: 9, fontSize: 15, fontWeight: 700,
                    background: active ? "var(--accent-soft)" : "var(--surface)", border: "1px solid " + (active ? "var(--accent-line)" : "var(--border)"),
                    color: active ? "var(--accent-hi)" : "var(--text-dim)" }}>{a}–{b}</button>
              );
            })}
          </div>
        )}

        <div style={{ flex: 1, overflow: "hidden", marginTop: 14 }}>
          <div style={{ display: "grid", gridTemplateColumns: isMovie ? "380px" : `repeat(${COLS}, ${EW}px)`, gap: EGAP,
            transform: `translateY(${gridOffset}px)`, transition: "transform .2s cubic-bezier(.3,.7,.4,1)" }}>
            {epNums.map((n, i) => {
              const focused = zone === GRID && gi === i;
              const watched = ws && n < ws.ep;
              const inProg = ws && n === ws.ep && ws.progress > 0;
              if (isMovie) return (
                <button key={n} className="tvf-flat" data-f={focused ? "1" : "0"} onMouseEnter={() => { setZone(GRID); setGi(i); }} onClick={() => onPlay(item.id, n)}
                  style={{ width: 380, height: 104, borderRadius: 12, overflow: "hidden", position: "relative", textAlign: "left", padding: 0, background: tileBg(item.hue) }}>
                  <div style={{ position: "absolute", inset: 0, background: "linear-gradient(0deg, rgba(0,0,0,0.6), transparent 60%)" }} />
                  <div style={{ position: "absolute", left: 18, top: 0, bottom: 0, display: "flex", flexDirection: "column", justifyContent: "center" }}>
                    <div style={{ fontSize: 20, fontWeight: 700 }}>Feature film</div>
                    <div style={{ fontSize: 14, color: "rgba(255,255,255,0.8)", marginTop: 5 }}>{item.runtime} · 1080p · {item.lang}</div>
                  </div>
                  {focused && <FocusRing r={12} />}
                </button>
              );
              return (
                <button key={n} className="tvf-flat" data-f={focused ? "1" : "0"} onMouseEnter={() => { setZone(GRID); setGi(i); }} onClick={() => onPlay(item.id, n)}
                  style={{ width: EW, height: EH, borderRadius: 11, overflow: "hidden", position: "relative", textAlign: "left", padding: 0, background: tileBg(item.hue),
                    boxShadow: focused ? undefined : "inset 0 0 0 1px rgba(255,255,255,0.06)", opacity: watched ? 0.55 : 1 }}>
                  <div style={{ position: "absolute", inset: 0, background: "linear-gradient(0deg, rgba(0,0,0,0.55), transparent 60%)" }} />
                  <div style={{ position: "absolute", left: 12, top: 8, fontSize: 26, fontWeight: 800, lineHeight: 1, textShadow: "0 2px 8px rgba(0,0,0,0.7)" }}>{String(n).padStart(2, "0")}</div>
                  <div style={{ position: "absolute", left: 12, bottom: 8, fontSize: 13, color: "rgba(255,255,255,0.82)", fontWeight: 600 }}>Episode {n}</div>
                  {watched && <div style={{ position: "absolute", right: 8, top: 8, width: 20, height: 20, borderRadius: "50%", background: "var(--accent)", color: "#fff", display: "grid", placeItems: "center", fontSize: 12 }}>✓</div>}
                  {inProg && <div style={{ position: "absolute", left: 0, right: 0, bottom: 0, height: 4, background: "rgba(0,0,0,0.4)" }}><div style={{ height: "100%", width: `${ws.progress * 100}%`, background: "var(--accent)" }} /></div>}
                  {focused && <FocusRing r={11} />}
                </button>
              );
            })}
          </div>
        </div>
      </div>
      <HintBar items={[["↕↔", "Move"], ["OK", "Play"], ["⌫", "Back"]]} />
    </div>
  );
}

// ── PLAYER ───────────────────────────────────────────────────────────────
function Player({ item, ep, settings, dataSaver, onBack }) {
  const dur = runtimeToSec(item.runtime);
  const key = "tv-pos-" + item.id + "-" + ep;
  const [pos, setPos] = useState(() => { const v = parseFloat(localStorage.getItem(key)); return Number.isFinite(v) ? v : 0; });
  const [playing, setPlaying] = useState(true);

  useEffect(() => {
    if (!playing) return;
    const t = setInterval(() => setPos(p => { const n = Math.min(1, p + 1 / dur); localStorage.setItem(key, String(n)); return n; }), 1000);
    return () => clearInterval(t);
  }, [playing, dur, key]);

  const seek = (d) => setPos(p => { const n = Math.max(0, Math.min(1, p + d)); localStorage.setItem(key, String(n)); return n; });
  useKeys((k) => {
    if (k === "Backspace" || k === "Escape") return onBack();
    if (k === "Enter" || k === " ") setPlaying(p => !p);
    else if (k === "ArrowRight") seek(0.03);
    else if (k === "ArrowLeft") seek(-0.03);
  });

  const cur = pos * dur;
  const isMovie = item.type === "Movie";
  return (
    <div style={{ position: "absolute", inset: 0, background: "#000" }}>
      <Backdrop item={item} dataSaver={dataSaver} mode="blur" />
      <div style={{ position: "absolute", inset: 0, background: "linear-gradient(0deg, rgba(0,0,0,0.85) 0%, transparent 45%)" }} />

      {!playing && (
        <div style={{ position: "absolute", inset: 0, display: "grid", placeItems: "center" }}>
          <div style={{ width: 110, height: 110, borderRadius: "50%", background: "rgba(0,0,0,0.45)", border: "2px solid rgba(255,255,255,0.5)", display: "grid", placeItems: "center", fontSize: 38 }}>❚❚</div>
        </div>
      )}

      <div style={{ position: "absolute", left: SAFE, right: SAFE, bottom: 90, zIndex: 10 }}>
        <div style={{ display: "flex", alignItems: "center", gap: 14, marginBottom: 10 }}>
          <span style={{ fontSize: 15, fontWeight: 700, letterSpacing: 2, color: "var(--accent-hi)" }}>{isMovie ? "NOW PLAYING" : `S1 · EPISODE ${ep}`}</span>
          <span style={{ fontSize: 14, color: "var(--text-faint)", fontWeight: 600 }}>· {settings.audio} audio · {settings.subs === "Off" ? "no subs" : settings.subs + " subs"}</span>
        </div>
        <div style={{ fontSize: 54, fontWeight: 800, letterSpacing: -1, marginBottom: 24 }}>{item.title}</div>
        <div style={{ display: "flex", alignItems: "center", gap: 20 }}>
          <span style={{ fontSize: 18, fontVariantNumeric: "tabular-nums", color: "var(--text-dim)", width: 64 }}>{fmtTime(cur)}</span>
          <div style={{ flex: 1, height: 8, borderRadius: 999, background: "rgba(255,255,255,0.18)", overflow: "hidden" }}>
            <div style={{ height: "100%", width: `${pos * 100}%`, background: "var(--accent)", borderRadius: 999 }} />
          </div>
          <span style={{ fontSize: 18, fontVariantNumeric: "tabular-nums", color: "var(--text-dim)", width: 64, textAlign: "right" }}>{fmtTime(dur)}</span>
        </div>
      </div>
      <HintBar items={[["OK", playing ? "Pause" : "Play"], ["↔", "Seek"], ["⌫", "Exit"]]} />
    </div>
  );
}

// ── APP ────────────────────────────────────────────────────────────────────
const SETTINGS_KEY = "tv-settings-v1";
const loadSettings = () => {
  let s = {};
  try { s = JSON.parse(localStorage.getItem(SETTINGS_KEY)) || {}; } catch (e) {}
  const ad = window.AUDIO_DEFAULTS || {};
  return {
    accent: s.accent || "crimson",
    dataSaver: s.dataSaver ?? false,
    audio: s.audio || ad.preferredLanguage || "Japanese",
    subs: s.subs || ad.subtitleLanguage || "Russian",
    subSize: s.subSize || ad.subtitleSize || 18,
  };
};

function App() {
  const [view, setView] = useState("login"); // login | app
  const [user, setUser] = useState(window.USERS[0]);
  const [settings, setSettings] = useState(loadSettings);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [stack, setStack] = useState([{ name: "home" }]);
  const cur = stack[stack.length - 1];

  const setSetting = (k, v) => setSettings(s => { const n = { ...s, [k]: v }; localStorage.setItem(SETTINGS_KEY, JSON.stringify(n)); return n; });

  // live accent
  useEffect(() => {
    const a = ACCENTS[settings.accent] || ACCENTS.crimson;
    const r = document.documentElement.style;
    r.setProperty("--accent", a.base); r.setProperty("--accent-hi", a.hi); r.setProperty("--accent-soft", a.soft); r.setProperty("--accent-line", a.line);
  }, [settings.accent]);

  const byId = (id) => LIBRARY.find(l => l.id === id);
  const push = (s) => setStack(st => [...st, s]);
  const back = () => setStack(st => st.length > 1 ? st.slice(0, -1) : st);

  const pickUser = (u) => { window.applyUser(u); setUser(u); setStack([{ name: "home" }]); setSettingsOpen(false); setView("app"); };

  if (view === "login") return <Login onPick={pickUser} dataSaver={settings.dataSaver} />;

  let screen;
  if (cur.name === "home") {
    screen = <Home user={user} dataSaver={settings.dataSaver}
      onOpenItem={(id, resume) => push(resume ? { name: "player", id, ep: (watchStateFor(id)?.ep || 1) } : { name: "detail", id })}
      onOpenCategory={(cat) => push({ name: "category", cat })}
      onSettings={() => setSettingsOpen(true)} />;
  } else if (cur.name === "category") {
    screen = <Category cat={cur.cat} user={user} onBack={back} onOpenItem={(id) => push({ name: "detail", id })} />;
  } else if (cur.name === "detail") {
    screen = <Detail item={byId(cur.id)} user={user} dataSaver={settings.dataSaver} onBack={back} onPlay={(id, ep) => push({ name: "player", id, ep })} />;
  } else if (cur.name === "player") {
    screen = <Player item={byId(cur.id)} ep={cur.ep} settings={settings} dataSaver={settings.dataSaver} onBack={back} />;
  }

  return (
    <>
      {screen}
      {settingsOpen && (
        <Settings user={user} settings={settings} setSetting={setSetting}
          onSwitchProfile={() => { setSettingsOpen(false); setView("login"); }}
          onSignOut={() => { setSettingsOpen(false); setView("login"); }}
          onClose={() => setSettingsOpen(false)} />
      )}
    </>
  );
}

const __style = document.createElement("style");
__style.textContent = "@keyframes tvfade{from{opacity:0}to{opacity:1}}";
document.head.appendChild(__style);

ReactDOM.createRoot(document.getElementById("root")).render(<App />);
