// ====== Animarr v4 — additional icons + small primitives ======
// Layers on top of components.jsx. Just adds icons the v4 UI needs:
// github, server, audio, lock, user, log-out, eye (toggle), tv, key, shield.

(function () {
  const oldIcon = window.Icon;
  window.Icon = ({ name, size = 16, stroke = 1.6, ...rest }) => {
    const p = { width: size, height: size, viewBox: "0 0 24 24", fill: "none",
      stroke: "currentColor", strokeWidth: stroke, strokeLinecap: "round",
      strokeLinejoin: "round", ...rest };
    switch (name) {
      case "github":  return <svg {...p}><path d="M9 19c-5 1.5-5-2.5-7-3m14 6v-3.87a3.37 3.37 0 0 0-.94-2.61c3.14-.35 6.44-1.54 6.44-7A5.44 5.44 0 0 0 20 4.77 5.07 5.07 0 0 0 19.91 1S18.73.65 16 2.48a13.38 13.38 0 0 0-7 0C6.27.65 5.09 1 5.09 1A5.07 5.07 0 0 0 5 4.77a5.44 5.44 0 0 0-1.5 3.78c0 5.42 3.3 6.61 6.44 7A3.37 3.37 0 0 0 9 18.13V22"/></svg>;
      case "server":  return <svg {...p}><rect x="2" y="2" width="20" height="8" rx="2"/><rect x="2" y="14" width="20" height="8" rx="2"/><line x1="6" y1="6" x2="6.01" y2="6"/><line x1="6" y1="18" x2="6.01" y2="18"/></svg>;
      case "audio":   return <svg {...p}><path d="M11 5L6 9H2v6h4l5 4z"/><path d="M19.07 4.93a10 10 0 0 1 0 14.14M15.54 8.46a5 5 0 0 1 0 7.07"/></svg>;
      case "lock":    return <svg {...p}><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>;
      case "user":    return <svg {...p}><path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></svg>;
      case "users":   return <svg {...p}><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75"/></svg>;
      case "logout":  return <svg {...p}><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/><polyline points="16 17 21 12 16 7"/><line x1="21" y1="12" x2="9" y2="12"/></svg>;
      case "tv":      return <svg {...p}><rect x="2" y="7" width="20" height="15" rx="2"/><polyline points="17 2 12 7 7 2"/></svg>;
      case "key":     return <svg {...p}><path d="M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.78 7.78 5.5 5.5 0 0 1 7.78-7.78zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3"/></svg>;
      case "shield":  return <svg {...p}><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/></svg>;
      case "globe":   return <svg {...p}><circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/></svg>;
      case "arrow-r": return <svg {...p}><line x1="5" y1="12" x2="19" y2="12"/><polyline points="12 5 19 12 12 19"/></svg>;
      default: return oldIcon ? oldIcon({ name, size, stroke, ...rest }) : null;
    }
  };

  // user avatar — initials disc, deterministic hue
  window.Avatar = ({ user, size = 32 }) => {
    if (!user) return null;
    const initials = (user.name || "?").split(" ").map(w => w[0]).join("").slice(0, 2).toUpperCase();
    const hue = ((user.name || "x").charCodeAt(0) * 41) % 360;
    return (
      <div style={{
        width: size, height: size, borderRadius: size,
        background: `linear-gradient(135deg, oklch(0.58 0.18 ${hue}), oklch(0.40 0.16 ${hue}))`,
        color:"#fff", display:"grid", placeItems:"center",
        fontFamily:"var(--font-display)", fontSize: size * 0.40, letterSpacing:-0.5,
        boxShadow:"inset 0 1px 0 rgba(255,255,255,0.18), 0 2px 6px rgba(0,0,0,0.4)",
        flexShrink: 0,
      }}>{initials}</div>
    );
  };
})();
