// ====== Animarr v4 — screens (welcome, login, topbar, profile, server, llm) ======
const { useState: s4S, useEffect: s4E, useMemo: s4M, useRef: s4R } = React;

// ─────────────────────────────────────────────────────────────
// WELCOME
// ─────────────────────────────────────────────────────────────
const WelcomeScreen = ({ onStart }) => {
  const bd = window.BD?.storm || "";
  return (
    <div style={{ position:"fixed", inset: 0, overflow:"hidden", background:"var(--bg-0)" }}>
      <div style={{
        position:"absolute", inset: 0,
        backgroundImage:`url("${bd}")`, backgroundSize:"cover", backgroundPosition:"center",
        filter:"blur(8px) brightness(35%) saturate(0.9)",
        transform:"scale(1.06)",
      }} />
      <div style={{
        position:"absolute", inset: 0,
        background:"radial-gradient(80% 80% at 50% 50%, transparent 0%, rgba(8,6,5,0.85) 100%)",
      }} />
      <div style={{
        position:"absolute", right:"4%", top:"-2%",
        fontFamily:"var(--font-cjk)", fontSize: 460, lineHeight: 0.85,
        color:"oklch(0.95 0.10 25 / 0.07)",
        writingMode:"vertical-rl", textOrientation:"upright", letterSpacing: 8,
        userSelect:"none", pointerEvents:"none",
      }}>动画</div>

      {/* Github link */}
      <a href={window.GITHUB_URL || "#"} target="_blank" rel="noreferrer" style={{
        position:"absolute", top: 22, right: 30, zIndex: 5,
        display:"inline-flex", alignItems:"center", gap: 8,
        padding:"9px 14px", borderRadius: 8,
        background:"rgba(10,8,7,0.55)", backdropFilter:"blur(10px)",
        border:"1px solid rgba(255,255,255,0.12)",
        color:"var(--text-dim)", fontSize: 12.5, fontWeight: 600,
        fontFamily:"var(--font-mono)", letterSpacing: 0.6, textDecoration:"none",
      }}>
        <window.Icon name="github" size={14} /> GITHUB
      </a>

      <div style={{
        position:"relative", height:"100vh",
        display:"flex", flexDirection:"column", alignItems:"center", justifyContent:"center",
        padding: 40, maxWidth: 1000, margin:"0 auto",
      }}>
        {/* brand */}
        <div style={{ display:"flex", alignItems:"center", gap: 16, marginBottom: 28 }}>
          <div style={{
            width: 64, height: 64, borderRadius: 16,
            background:"linear-gradient(135deg, var(--accent), oklch(0.42 0.18 25))",
            display:"grid", placeItems:"center",
            fontFamily:"var(--font-display)", color:"#fff", fontSize: 38, lineHeight: 1,
            boxShadow:"0 14px 36px -8px var(--accent-soft), 0 0 60px -10px oklch(0.66 0.20 25 / 0.45)",
          }}>A</div>
          <div>
            <div style={{ fontFamily:"var(--font-display)", fontSize: 32, letterSpacing:-0.8 }}>ANIMARR</div>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-faint)", letterSpacing: 1.2, marginTop: 2 }}>v2.0 · LOCAL · LLM-DRIVEN</div>
          </div>
        </div>

        <h1 style={{
          margin:0, fontFamily:"var(--font-display)", fontSize:84, lineHeight:0.9,
          letterSpacing:-2.6, color:"#fff", textAlign:"center",
          textShadow:"0 4px 30px rgba(0,0,0,0.6)", maxWidth: 920,
        }}>YOUR LIBRARY,<br/>UNDERSTOOD.</h1>

        <p style={{
          marginTop: 22, maxWidth: 720, textAlign:"center",
          color:"#e7dfd7", fontSize: 16.5, lineHeight: 1.6,
          textWrap:"pretty", textShadow:"0 2px 10px rgba(0,0,0,0.55)",
        }}>
          Self-hosted media manager for anime, donghua, films and series.
          A local LLM cleans up messy folder names before TMDB / MAL / IMDb
          take over. SQLite stores everything — the files on disk are never
          renamed or moved.
        </p>

        <div style={{ display:"flex", gap: 12, marginTop: 38 }}>
          <button onClick={onStart} className="tv-focus" style={{
            all:"unset", cursor:"pointer",
            background:"var(--accent)", color:"#fff",
            padding:"14px 28px", borderRadius: 10,
            fontSize: 15, fontWeight: 700, letterSpacing: 0.4,
            display:"inline-flex", alignItems:"center", gap: 10,
            boxShadow:"0 10px 30px -10px var(--accent-soft)",
          }}>
            <window.Icon name="arrow-r" size={16} stroke={2.2} />
            START
          </button>
          <a href={window.GITHUB_URL || "#"} target="_blank" rel="noreferrer" className="tv-focus" style={{
            all:"unset", cursor:"pointer",
            background:"rgba(255,255,255,0.06)", color:"var(--text)",
            border:"1px solid rgba(255,255,255,0.10)",
            padding:"14px 22px", borderRadius: 10,
            fontSize: 14, fontWeight: 600, letterSpacing: 0.3,
            display:"inline-flex", alignItems:"center", gap: 10, textDecoration:"none",
          }}>
            <window.Icon name="github" size={15} /> View source
          </a>
        </div>

        <div style={{
          display:"grid", gridTemplateColumns:"repeat(3, 1fr)", gap: 18,
          marginTop: 80, maxWidth: 820, width:"100%",
        }}>
          {[
            ["LLM-FIRST",         "Messy folder names get normalised before any source query."],
            ["SAFE BY DEFAULT",   "Files on disk never moved or renamed without your action."],
            ["MULTI-USER",        "Roles, per-folder access, personal watch state, favorites."],
          ].map(([h, b]) => (
            <div key={h} style={{
              background:"rgba(20,15,12,0.55)", backdropFilter:"blur(8px)",
              border:"1px solid var(--border)", borderRadius: 12, padding: 16,
            }}>
              <div style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--accent-hi)", letterSpacing: 1.4 }}>{h}</div>
              <div style={{ fontSize: 13, color:"var(--text-dim)", marginTop: 6, lineHeight: 1.5 }}>{b}</div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
};

// ─────────────────────────────────────────────────────────────
// LOGIN
// ─────────────────────────────────────────────────────────────
const LoginScreen = ({ onLogin, onBack }) => {
  const [username, setUsername] = s4S("yuri");
  const [password, setPassword] = s4S("");
  const [err, setErr] = s4S(null);
  const bd = window.BD?.mist || "";
  const submit = (e) => {
    e?.preventDefault?.();
    const u = window.USERS.find(x => x.username === username);
    if (!u) { setErr("Unknown user"); return; }
    setErr(null);
    onLogin(username);
  };
  return (
    <div style={{ position:"fixed", inset: 0, overflow:"hidden", background:"var(--bg-0)" }}>
      <div style={{
        position:"absolute", inset: 0,
        backgroundImage:`url("${bd}")`, backgroundSize:"cover", backgroundPosition:"center",
        filter:"blur(14px) brightness(28%) saturate(0.9)", transform:"scale(1.06)",
      }} />
      <div style={{
        position:"absolute", inset: 0,
        background:"radial-gradient(80% 80% at 50% 50%, transparent 0%, rgba(8,6,5,0.85) 100%)",
      }} />

      {/* back */}
      <button onClick={onBack} className="tv-focus" style={{
        all:"unset", cursor:"pointer", position:"absolute",
        top: 22, left: 30, zIndex: 5,
        display:"inline-flex", alignItems:"center", gap: 8,
        padding:"9px 14px", borderRadius: 8,
        background:"rgba(10,8,7,0.55)", backdropFilter:"blur(10px)",
        border:"1px solid rgba(255,255,255,0.12)",
        color:"var(--text-dim)", fontSize: 12, fontFamily:"var(--font-mono)", letterSpacing: 0.6,
      }}>
        <window.Icon name="chev-l" size={13} /> BACK
      </button>

      <div style={{
        position:"relative", height:"100vh",
        display:"flex", alignItems:"center", justifyContent:"center", padding: 32,
      }}>
        <form onSubmit={submit} style={{
          width: 420, padding: 32,
          background:"linear-gradient(180deg, rgba(21,17,14,0.85), rgba(15,12,10,0.85))",
          backdropFilter:"blur(20px)",
          border:"1px solid var(--border-strong)", borderRadius: 18,
          boxShadow:"0 40px 80px -20px rgba(0,0,0,0.7)",
        }}>
          <div style={{ display:"flex", alignItems:"center", gap: 12, marginBottom: 22 }}>
            <div style={{
              width: 38, height: 38, borderRadius: 8,
              background:"linear-gradient(135deg, var(--accent), oklch(0.42 0.18 25))",
              display:"grid", placeItems:"center",
              fontFamily:"var(--font-display)", color:"#fff", fontSize: 22,
            }}>A</div>
            <div>
              <div style={{ fontFamily:"var(--font-display)", fontSize: 22, letterSpacing:-0.4 }}>SIGN IN</div>
              <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-faint)", letterSpacing: 0.8 }}>ANIMARR · LOCAL SERVER</div>
            </div>
          </div>

          <div style={{ display:"flex", flexDirection:"column", gap: 16 }}>
            <window.Field label="Username">
              <window.Input value={username} onChange={e => setUsername(e.target.value)} autoFocus />
            </window.Field>
            <window.Field label="Password">
              <window.Input type="password" value={password} onChange={e => setPassword(e.target.value)} placeholder="••••••••" />
            </window.Field>
            {err && (
              <div style={{ color:"var(--warn)", fontSize: 12, fontFamily:"var(--font-mono)", letterSpacing: 0.4 }}>
                ⚠ {err}
              </div>
            )}
            <button type="submit" className="tv-focus" style={{
              all:"unset", cursor:"pointer",
              background:"var(--accent)", color:"#fff",
              padding:"12px 0", borderRadius: 10, textAlign:"center",
              fontSize: 14, fontWeight: 700, letterSpacing: 0.3,
              marginTop: 6,
            }}>SIGN IN</button>
          </div>

          <div style={{
            marginTop: 18, padding:"10px 12px",
            background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 8,
            fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-faint)",
            letterSpacing: 0.4, lineHeight: 1.55,
          }}>
            Demo logins: <span style={{ color:"var(--accent-hi)" }}>yuri</span> · <span style={{ color:"var(--accent-hi)" }}>anna</span> · <span style={{ color:"var(--accent-hi)" }}>pavel</span><br/>
            Password is ignored in the prototype.
          </div>
        </form>
      </div>

      <a href={window.GITHUB_URL || "#"} target="_blank" rel="noreferrer" style={{
        position:"absolute", bottom: 22, right: 30,
        color:"var(--text-faint)", fontFamily:"var(--font-mono)", fontSize: 11, letterSpacing: 0.6,
        textDecoration:"none", display:"inline-flex", alignItems:"center", gap: 7,
      }}>
        <window.Icon name="github" size={13} /> github.com / animarr
      </a>
    </div>
  );
};

// TopBarV4 — top of every signed-in page. Now shows FOLDER nav (All + each
// SectionFolder) in place of a single Catalog tab. Clicking a folder routes
// to /catalog with that folder pre-selected (passed via window.__folderJump).
const TopBarV4 = ({ user, route, onRoute, onProfile, onLLM }) => {
  const isAdmin = window.can(user, "systemSettings");
  const canDownload = window.can(user, "uploadContent");
  const wl = window.useWatchlist ? window.useWatchlist() : null;
  const activeBtn = (key) => route === key;
  const [folder, setFolder] = s4S("All");
  const folders = ["All", ...window.FOLDERS.map(f => f.title)];

  const onPickFolder = (name) => {
    setFolder(name);
    window.__folderJump = name; // CatalogV3 picks this up on mount
    onRoute("catalog");
  };

  return (
    <header style={{
      position:"fixed", top: 0, left: 0, right: 0, height: 60, zIndex: 50,
      background:"linear-gradient(180deg, rgba(10,8,7,0.78), rgba(10,8,7,0.55))",
      backdropFilter:"blur(20px)", WebkitBackdropFilter:"blur(20px)",
      borderBottom:"1px solid var(--border)",
      display:"flex", alignItems:"center", gap: 16, padding:"0 24px",
    }}>
      {/* brand → home (catalog) */}
      <button onClick={() => { setFolder("All"); window.__folderJump = "All"; onRoute("catalog"); }} className="tv-focus" style={{
        all:"unset", cursor:"pointer",
        display:"flex", alignItems:"center", gap: 11, flexShrink: 0,
      }}>
        <div style={{
          width: 30, height: 30, borderRadius: 7,
          background:"linear-gradient(135deg, var(--accent), oklch(0.42 0.18 25))",
          display:"grid", placeItems:"center",
          fontFamily:"var(--font-display)", color:"#fff", fontSize: 17,
          boxShadow:"0 4px 16px -4px var(--accent-soft)",
        }}>A</div>
        <div style={{ fontFamily:"var(--font-display)", fontSize: 14, letterSpacing:-0.3 }}>ANIMARR</div>
      </button>

      {/* folder nav — replaces single Catalog tab. Horizontal scroll on small screens. */}
      <nav style={{
        display:"flex", gap: 4, marginLeft: 16, flex: 1, minWidth: 0,
        overflowX:"auto", scrollbarWidth:"none",
      }}>
        {folders.map(name => {
          const active = route === "catalog" && folder === name || (route === "media" && folder === name);
          return (
            <button key={name} onClick={() => onPickFolder(name)} className="tv-focus" style={{
              all:"unset", cursor:"pointer", flexShrink: 0,
              padding:"7px 14px", borderRadius: 8, height: 36, boxSizing:"border-box",
              color: active ? "var(--text)" : "var(--text-dim)",
              background: active ? "var(--surface-3)" : "transparent",
              boxShadow: active ? "0 1px 0 var(--accent) inset" : "none",
              fontSize: 13, fontWeight: 600,
              display:"inline-flex", alignItems:"center",
            }}>{name}</button>
          );
        })}
      </nav>

      {/* LLM status pill */}
      <button onClick={onLLM} className="tv-focus" title="LLM status" style={{
        all:"unset", cursor:"pointer", flexShrink: 0,
        display:"inline-flex", alignItems:"center", gap: 8,
        padding:"6px 12px", borderRadius: 999,
        background:"var(--surface)", border:"1px solid var(--border)",
        height: 36, boxSizing:"border-box",
      }}>
        <span style={{
          width: 7, height: 7, borderRadius: 7,
          background:"var(--success)", boxShadow:"0 0 10px var(--success)",
          animation:"llm-pulse 1.8s ease-in-out infinite",
        }} />
        <window.Icon name="magic" size={13} style={{ color:"var(--accent-hi)" }} />
        <span style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-dim)", letterSpacing: 0.6 }}>17/25</span>
      </button>

      <button className="tv-focus" title="Поиск" style={{
        all:"unset", cursor:"pointer", flexShrink: 0, width: 36, height: 36, borderRadius: 8,
        background:"var(--surface)", border:"1px solid var(--border)", color:"var(--text-dim)",
        display:"grid", placeItems:"center",
      }}>
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><circle cx="11" cy="11" r="7"/><line x1="20" y1="20" x2="16.5" y2="16.5"/></svg>
      </button>
      {window.CalendarPage && (
        <button onClick={() => onRoute("calendar")} className="tv-focus" title={window.RU ? window.RU.calendar : "Calendar"} style={{
          all:"unset", cursor:"pointer", flexShrink: 0,
          width: 36, height: 36, borderRadius: 8,
          background: activeBtn("calendar") ? "var(--accent-soft)" : "var(--surface)",
          border:`1px solid ${activeBtn("calendar") ? "var(--accent-line)" : "var(--border)"}`,
          color: activeBtn("calendar") ? "var(--accent-hi)" : "var(--text-dim)",
          display:"grid", placeItems:"center",
        }}>
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><rect x="3" y="4.5" width="18" height="16.5" rx="2.5"/><line x1="3" y1="9.5" x2="21" y2="9.5"/><line x1="8" y1="2.5" x2="8" y2="6.5"/><line x1="16" y1="2.5" x2="16" y2="6.5"/></svg>
        </button>
      )}

      {window.WatchlistPage && (
        <button onClick={() => onRoute("watchlist")} className="tv-focus" title={window.RU ? window.RU.watchlist : "Watchlist"} style={{
          all:"unset", cursor:"pointer", position:"relative", flexShrink: 0,
          width: 36, height: 36, borderRadius: 8,
          background: activeBtn("watchlist") ? "var(--accent-soft)" : "var(--surface)",
          border:`1px solid ${activeBtn("watchlist") ? "var(--accent-line)" : "var(--border)"}`,
          color: activeBtn("watchlist") ? "var(--accent-hi)" : "var(--text-dim)",
          display:"grid", placeItems:"center",
        }}>
          {window.BookmarkPlus ? <window.BookmarkPlus size={15} /> : null}
          {wl && wl.count() > 0 && (
            <span style={{ position:"absolute", top:-3, right:-3, background:"var(--accent)", color:"#fff", fontSize:9, fontWeight:700, fontFamily:"var(--font-mono)", minWidth:14, height:14, padding:"0 3px", borderRadius:14, display:"grid", placeItems:"center", border:"1.5px solid var(--bg-0)", lineHeight:1 }}>{wl.count()}</span>
          )}
        </button>
      )}

      {window.StatsPage && (
        <button onClick={() => onRoute("stats")} className="tv-focus" title={window.RU ? window.RU.stats : "Stats"} style={{
          all:"unset", cursor:"pointer", flexShrink: 0, width: 36, height: 36, borderRadius: 8,
          background: activeBtn("stats") ? "var(--accent-soft)" : "var(--surface)",
          border:`1px solid ${activeBtn("stats") ? "var(--accent-line)" : "var(--border)"}`,
          color: activeBtn("stats") ? "var(--accent-hi)" : "var(--text-dim)",
          display:"grid", placeItems:"center",
        }}>
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><line x1="6" y1="20" x2="6" y2="13"/><line x1="12" y1="20" x2="12" y2="8"/><line x1="18" y1="20" x2="18" y2="4"/></svg>
        </button>
      )}
      {/* Downloads */}
      {canDownload && (
        <button onClick={() => onRoute("downloads")} className="tv-focus" title="Downloads" style={{
          all:"unset", cursor:"pointer", position:"relative", flexShrink: 0,
          width: 36, height: 36, borderRadius: 8,
          background: activeBtn("downloads") ? "var(--accent-soft)" : "var(--surface)",
          border:`1px solid ${activeBtn("downloads") ? "var(--accent-line)" : "var(--border)"}`,
          color: activeBtn("downloads") ? "var(--accent-hi)" : "var(--text-dim)",
          display:"grid", placeItems:"center",
        }}>
          <window.Icon name="torrent" size={15} />
          <span style={{
            position:"absolute", top: -3, right: -3,
            background:"var(--accent)", color:"#fff",
            fontSize: 9, fontWeight: 700, fontFamily:"var(--font-mono)",
            minWidth: 14, height: 14, padding:"0 3px",
            borderRadius: 14, display:"grid", placeItems:"center",
            border:"1.5px solid var(--bg-0)", lineHeight: 1,
          }}>2</span>
        </button>
      )}

      {/* Admin server settings */}
      {isAdmin && (
        <button onClick={() => onRoute("server")} className="tv-focus" title="Server settings (admin)" style={{
          all:"unset", cursor:"pointer", flexShrink: 0,
          width: 36, height: 36, borderRadius: 8,
          background: activeBtn("server") ? "var(--accent-soft)" : "var(--surface)",
          border:`1px solid ${activeBtn("server") ? "var(--accent-line)" : "var(--border)"}`,
          color: activeBtn("server") ? "var(--accent-hi)" : "var(--text-dim)",
          display:"grid", placeItems:"center",
        }}>
          <window.Icon name="server" size={15} />
        </button>
      )}

      {/* Profile */}
      <button onClick={onProfile} className="tv-focus" title={user?.name} style={{
        all:"unset", cursor:"pointer", flexShrink: 0, borderRadius: 999,
      }}>
        <window.Avatar user={user} size={34} />
      </button>
      <style>{`@keyframes llm-pulse { 0%,100%{opacity:1;} 50%{opacity:0.35;} }`}</style>
    </header>
  );
};

const TopBarNavBtn = ({ label, icon, active, onClick }) => (
  <button onClick={onClick} className="tv-focus" style={{
    all:"unset", cursor:"pointer",
    display:"inline-flex", alignItems:"center", gap: 8,
    padding:"7px 14px", borderRadius: 8, height: 36, boxSizing:"border-box",
    color: active ? "var(--text)" : "var(--text-dim)",
    background: active ? "var(--surface-3)" : "transparent",
    boxShadow: active ? "0 1px 0 var(--accent) inset" : "none",
    fontSize: 13, fontWeight: 600,
  }}>
    <window.Icon name={icon} size={14} />
    {label}
  </button>
);

// ─────────────────────────────────────────────────────────────
// LLM STATUS POPUP
// ─────────────────────────────────────────────────────────────
const LLMStatusPopup = ({ onClose }) => (
  <>
    <div onClick={onClose} style={{ position:"fixed", inset: 0, zIndex: 60 }} />
    <div style={{
      position:"fixed", top: 70, right: 24, zIndex: 61,
      width: 360, padding: 18,
      background:"linear-gradient(180deg, var(--surface) 0%, var(--surface-2) 100%)",
      border:"1px solid var(--border-strong)", borderRadius: 14,
      boxShadow:"0 30px 60px -20px rgba(0,0,0,0.7)",
    }}>
      <div style={{ display:"flex", alignItems:"center", gap: 12, marginBottom: 14 }}>
        <div style={{
          width: 36, height: 36, borderRadius: 8, background:"var(--accent)",
          display:"grid", placeItems:"center", color:"#fff",
        }}><window.Icon name="magic" size={16} /></div>
        <div style={{ flex: 1 }}>
          <div style={{ display:"flex", alignItems:"center", gap: 6 }}>
            <span style={{ width: 7, height: 7, borderRadius: 7, background:"var(--success)", boxShadow:"0 0 10px var(--success)" }} />
            <span style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-dim)", letterSpacing: 0.8 }}>ONLINE</span>
          </div>
          <div style={{ fontFamily:"var(--font-display)", fontSize: 14, letterSpacing:-0.2, marginTop: 3 }}>ollama · qwen2.5:1.5b</div>
        </div>
        <button onClick={onClose} style={{ all:"unset", cursor:"pointer", color:"var(--text-faint)", padding: 4 }}>
          <window.Icon name="x" size={14} />
        </button>
      </div>

      <div style={{ marginTop: 4 }}>
        <div style={{ display:"flex", justifyContent:"space-between", fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)", letterSpacing: 0.6, marginBottom: 5 }}>
          <span>QUEUE</span><span>17 / 25</span>
        </div>
        <div style={{ height: 4, background:"rgba(255,255,255,0.05)", borderRadius: 4, overflow:"hidden" }}>
          <div style={{ width:"68%", height:"100%", background:"linear-gradient(90deg, var(--accent), var(--accent-hi))" }} />
        </div>
      </div>

      <div style={{ display:"grid", gridTemplateColumns:"1fr 1fr", gap: 10, marginTop: 14, fontFamily:"var(--font-mono)", fontSize: 11 }}>
        <div style={{ padding:"8px 10px", background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 8 }}>
          <div style={{ color:"var(--text-faint)", fontSize: 9.5, letterSpacing: 0.6 }}>AVG</div>
          <div style={{ color:"var(--text)", fontSize: 14, marginTop: 3 }}>480 <span style={{ fontSize: 10, color:"var(--text-faint)" }}>ms/item</span></div>
        </div>
        <div style={{ padding:"8px 10px", background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 8 }}>
          <div style={{ color:"var(--text-faint)", fontSize: 9.5, letterSpacing: 0.6 }}>HIT RATE</div>
          <div style={{ color:"var(--success)", fontSize: 14, marginTop: 3 }}>99.2%</div>
        </div>
      </div>

      <div style={{ fontFamily:"var(--font-mono)", fontSize: 9.5, color:"var(--text-faint)", letterSpacing: 1, marginTop: 16, marginBottom: 8 }}>RECENT</div>
      <div style={{ display:"flex", flexDirection:"column", gap: 6 }}>
        {[
          ["Tian Bao Fu Yao Lu", "78%"],
          ["薛先生的猛主日记 (2026)", "66%"],
          ["[Anistar.org] Perfect World", "98%"],
        ].map(([t, c]) => (
          <div key={t} style={{ display:"flex", alignItems:"center", gap: 10, padding:"6px 10px", background:"var(--bg-1)", borderRadius: 6, border:"1px solid var(--border)" }}>
            <span style={{ flex: 1, fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-dim)", overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap" }}>{t}</span>
            <span style={{ fontFamily:"var(--font-mono)", fontSize: 10, color: parseFloat(c) > 85 ? "var(--success)" : "var(--warn)", letterSpacing: 0.4 }}>{c}</span>
          </div>
        ))}
      </div>
    </div>
  </>
);

// ─────────────────────────────────────────────────────────────
// PROFILE PANEL — personal settings
// ─────────────────────────────────────────────────────────────
const ProfilePanel = ({ user, onClose, onLogout, accent, onAccent, showBackdrop, onShowBackdrop, tvMode, onTvMode, heroPager, onHeroPager }) => {
  const [tab, setTab] = s4S("identity");
  return (
    <>
      <div onClick={onClose} style={{ position:"fixed", inset: 0, top: 60, background:"rgba(0,0,0,0.55)", backdropFilter:"blur(3px)", zIndex: 30 }} />
      <div style={{
        position:"fixed", top: 60, right: 0, bottom: 0, width: 480, zIndex: 31,
        background:"linear-gradient(180deg, var(--surface) 0%, var(--surface-2) 100%)",
        borderLeft:"1px solid var(--border-strong)",
        borderTop:"1px solid var(--border)",
        display:"flex", flexDirection:"column",
        boxShadow:"-30px 0 60px -20px rgba(0,0,0,0.6)",
      }}>
        {/* header */}
        <div style={{ padding:"22px 24px 16px", borderBottom:"1px solid var(--border)", display:"flex", alignItems:"center", gap: 14 }}>
          <window.Avatar user={user} size={48} />
          <div style={{ flex: 1 }}>
            <div style={{ fontFamily:"var(--font-display)", fontSize: 20, letterSpacing:-0.4 }}>{user.name}</div>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-faint)", letterSpacing: 0.8, marginTop: 2 }}>
              {user.username}{user.email ? ` · ${user.email}` : ""}
            </div>
          </div>
          <button onClick={onClose} style={{ all:"unset", cursor:"pointer", color:"var(--text-dim)", padding: 4 }}>
            <window.Icon name="x" size={18} />
          </button>
        </div>

        {/* tabs */}
        <div style={{ display:"flex", padding:"10px 24px 0", gap: 2, borderBottom:"1px solid var(--border)" }}>
          {[
            ["identity","Identity","user"],
            ["appearance","Appearance","sparkle"],
            ["audio","Audio","audio"],
            ["language","Language","globe"],
          ].map(([k,l,ic]) => (
            <button key={k} onClick={() => setTab(k)} className="tv-focus" style={{
              all:"unset", cursor:"pointer",
              padding:"10px 13px", fontSize: 12, fontWeight: 600,
              color: tab === k ? "var(--text)" : "var(--text-dim)",
              borderBottom: tab === k ? "2px solid var(--accent)" : "2px solid transparent",
              marginBottom: -1,
              display:"inline-flex", alignItems:"center", gap: 7,
            }}>
              <window.Icon name={ic} size={12} />
              {l}
            </button>
          ))}
        </div>

        {/* body */}
        <div style={{ flex: 1, overflow:"auto", padding: 22 }}>
          {tab === "identity" && <ProfileIdentity user={user} onLogout={onLogout} />}
          {tab === "appearance" && <ProfileAppearance accent={accent} onAccent={onAccent} showBackdrop={showBackdrop} onShowBackdrop={onShowBackdrop} tvMode={tvMode} onTvMode={onTvMode} heroPager={heroPager} onHeroPager={onHeroPager} />}
          {tab === "audio" && <ProfileAudio />}
          {tab === "language" && <ProfileLanguage />}
        </div>
      </div>
    </>
  );
};

const ProfileIdentity = ({ user, onLogout }) => (
  <div style={{ display:"flex", flexDirection:"column", gap: 16 }}>
    <div style={{
      background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10, padding: 14,
      display:"grid", gridTemplateColumns:"auto 1fr", gap:"10px 16px", fontFamily:"var(--font-mono)", fontSize: 12,
    }}>
      <span style={{ color:"var(--text-faint)" }}>USERNAME</span><span style={{ color:"var(--text)" }}>{user.username}</span>
      <span style={{ color:"var(--text-faint)" }}>ROLE</span><span style={{ color:"var(--accent-hi)" }}>{user.role.toUpperCase()}</span>
      <span style={{ color:"var(--text-faint)" }}>EMAIL</span><span style={{ color:"var(--text)" }}>{user.email || "—"}</span>
      <span style={{ color:"var(--text-faint)" }}>CREATED</span><span style={{ color:"var(--text-dim)" }}>{user.created}</span>
    </div>
    <window.Btn kind="solid" icon="key">Change password</window.Btn>
    <window.Btn kind="solid" icon="pencil">Edit profile</window.Btn>
    <div style={{ flex: 1, height: 12 }} />
    <button onClick={onLogout} className="tv-focus" style={{
      all:"unset", cursor:"pointer",
      background:"transparent", color:"oklch(0.74 0.20 25)",
      border:"1px solid oklch(0.55 0.15 25 / 0.5)", borderRadius: 8,
      padding:"10px 14px", textAlign:"center",
      fontSize: 13, fontWeight: 600,
      display:"inline-flex", alignItems:"center", justifyContent:"center", gap: 8,
    }}>
      <window.Icon name="logout" size={14} /> SIGN OUT
    </button>
  </div>
);

const ProfileAppearance = ({ accent, onAccent, showBackdrop, onShowBackdrop, tvMode, onTvMode, heroPager, onHeroPager }) => {
  const accents = [
    ["crimson", "oklch(0.66 0.20 25)"],
    ["amber",   "oklch(0.72 0.17 60)"],
    ["green",   "oklch(0.74 0.15 150)"],
    ["blue",    "oklch(0.66 0.16 240)"],
    ["violet",  "oklch(0.66 0.20 290)"],
  ];
  const pagers = [
    ["F", "Transparent named pager"],
    ["G", "Hover chevrons + dash pager"],
    ["H", "Numbered pills + chevrons"],
  ];
  return (
    <div style={{ display:"flex", flexDirection:"column", gap: 22 }}>
      <window.Field label="Accent color">
        <div style={{ display:"flex", gap: 12, marginTop: 6 }}>
          {accents.map(([k, c]) => (
            <button key={k} onClick={() => onAccent(k)} className="tv-focus" style={{
              all:"unset", cursor:"pointer",
              width: 36, height: 36, borderRadius: 36, background: c,
              boxShadow: accent === k ? `0 0 0 2px var(--bg-0), 0 0 0 4px ${c}` : "none",
            }} />
          ))}
        </div>
      </window.Field>

      <window.Field label="Hero pager style" hint="Controls how the catalog's 5-slot hero shows its slot picker.">
        <div style={{ display:"flex", flexDirection:"column", gap: 6, marginTop: 4 }}>
          {pagers.map(([k, label]) => (
            <button key={k} onClick={() => onHeroPager?.(k)} className="tv-focus" style={{
              all:"unset", cursor:"pointer",
              display:"flex", alignItems:"center", gap: 12, padding:"10px 12px",
              background: heroPager === k ? "var(--accent-soft)" : "var(--bg-1)",
              border: `1px solid ${heroPager === k ? "var(--accent-line)" : "var(--border)"}`,
              borderRadius: 9,
            }}>
              <span style={{
                width: 24, height: 24, borderRadius: 6,
                background: heroPager === k ? "var(--accent)" : "rgba(255,255,255,0.06)",
                color: heroPager === k ? "#fff" : "var(--text-dim)",
                display:"grid", placeItems:"center",
                fontFamily:"var(--font-mono)", fontSize: 11, fontWeight: 700,
              }}>{k}</span>
              <span style={{ fontSize: 13, color: heroPager === k ? "var(--text)" : "var(--text-dim)", fontWeight: 600 }}>
                {label}
              </span>
            </button>
          ))}
        </div>
      </window.Field>

      <div style={{ display:"flex", alignItems:"center", justifyContent:"space-between", padding:"4px 0" }}>
        <div>
          <div style={{ fontSize: 13.5, color:"var(--text)" }}>Animated backdrop</div>
          <div style={{ fontSize: 11, color:"var(--text-faint)", marginTop: 2 }}>Fanart slideshow behind every page.</div>
        </div>
        <window.Toggle on={showBackdrop} onChange={onShowBackdrop} />
      </div>
      <div style={{ display:"flex", alignItems:"center", justifyContent:"space-between", padding:"4px 0" }}>
        <div>
          <div style={{ fontSize: 13.5, color:"var(--text)" }}>TV mode</div>
          <div style={{ fontSize: 11, color:"var(--text-faint)", marginTop: 2 }}>Larger focus rings + bigger hit targets for remote-control navigation.</div>
        </div>
        <window.Toggle on={tvMode} onChange={onTvMode} />
      </div>
    </div>
  );
};

const ProfileAudio = () => {
  const [audio, setAudio] = s4S(window.AUDIO_DEFAULTS);
  const set = (k, v) => setAudio({ ...audio, [k]: v });
  return (
    <div style={{ display:"grid", gridTemplateColumns:"1fr 1fr", gap: 16 }}>
      <window.Field label="Preferred audio">
        <window.Select value={audio.preferredLanguage} onChange={e => set("preferredLanguage", e.target.value)}>
          <option>Japanese</option><option>Mandarin</option><option>English</option><option>Russian</option><option>Korean</option>
        </window.Select>
      </window.Field>
      <window.Field label="Subtitle language">
        <window.Select value={audio.subtitleLanguage} onChange={e => set("subtitleLanguage", e.target.value)}>
          <option>Russian</option><option>English</option><option>Off</option><option>Japanese</option>
        </window.Select>
      </window.Field>
      <div style={{ gridColumn: "1 / -1" }}>
        <window.Field label={`Subtitle size · ${audio.subtitleSize}px`}>
          <input type="range" min="12" max="32" value={audio.subtitleSize} onChange={e => set("subtitleSize", +e.target.value)} style={{ accentColor:"var(--accent)" }} />
        </window.Field>
      </div>
      <div style={{ gridColumn: "1 / -1" }}>
        <window.Field label={`Default volume · ${audio.defaultVolume}%`}>
          <input type="range" min="0" max="100" value={audio.defaultVolume} onChange={e => set("defaultVolume", +e.target.value)} style={{ accentColor:"var(--accent)" }} />
        </window.Field>
      </div>
      <div style={{ gridColumn: "1 / -1", display:"flex", alignItems:"center", justifyContent:"space-between" }}>
        <div>
          <div style={{ fontSize: 13.5 }}>Audio passthrough</div>
          <div style={{ fontSize: 11, color:"var(--text-faint)", marginTop: 2 }}>For AV receivers — sends raw bitstream.</div>
        </div>
        <window.Toggle on={audio.audioPassthrough} onChange={v => set("audioPassthrough", v)} />
      </div>
      <div style={{ gridColumn: "1 / -1", display:"flex", alignItems:"center", justifyContent:"space-between" }}>
        <div>
          <div style={{ fontSize: 13.5 }}>Normalize volume between titles</div>
          <div style={{ fontSize: 11, color:"var(--text-faint)", marginTop: 2 }}>EBU R128 loudness normalization.</div>
        </div>
        <window.Toggle on={audio.normalizeVolume} onChange={v => set("normalizeVolume", v)} />
      </div>
    </div>
  );
};

const ProfileLanguage = () => (
  <div>
    <window.Field label="Interface language">
      <window.Select defaultValue="en">
        <option value="en">English</option>
        <option value="ru">Русский</option>
        <option value="zh">中文</option>
        <option value="ja">日本語</option>
      </window.Select>
    </window.Field>
    <div style={{ marginTop: 22, padding: 14, background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10, fontSize: 12.5, color:"var(--text-dim)", lineHeight: 1.6 }}>
      Changes apply instantly. Your preferred audio + subtitle languages are configured in the <strong style={{ color:"var(--text)" }}>Audio</strong> tab.
    </div>
  </div>
);

// ─────────────────────────────────────────────────────────────
// DOWNLOADS ROUTE — wraps existing TorrentsScreen
// ─────────────────────────────────────────────────────────────
const DownloadsRoute = () => (
  <div style={{ padding:"38px 48px 0", maxWidth: 1480, margin:"0 auto" }}>
    <window.TorrentsScreen />
  </div>
);

// ─────────────────────────────────────────────────────────────
// SERVER SETTINGS — admin only
// ─────────────────────────────────────────────────────────────
const ServerSettingsScreen = () => {
  const init = window.__init || {};
  const [tab, setTab] = s4S(init.serverTab || "users");
  const tabs = [
    ["users",    "Users & Roles", "users"],
    ["folders",  "Root folders",  "folder"],
    ["history",  "Rename history","clock"],
    ["llm",      "AI / LLM",      "magic"],
    ["patterns", "Patterns",      "pencil"],
    ["ignore",   "Ignore rules",  "filter"],
    ["downloads","Downloads",     "download"],
    ["meta",     "Metadata",      "external"],
    ["about",    "About",         "info"],
  ];
  return (
    <div style={{ padding:"38px 48px 80px", maxWidth: 1480, margin:"0 auto" }}>
      <window.PageHeader overline="ADMIN" title="SERVER SETTINGS" sub="Visible only to users with the systemSettings permission. Changes apply live." />
      <div style={{ display:"grid", gridTemplateColumns:"240px 1fr", gap: 32 }}>
        <div style={{
          position:"sticky", top: 80, alignSelf:"start",
          background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 12,
          padding: 8, display:"flex", flexDirection:"column", gap: 2,
        }}>
          {tabs.map(([k, l, ic]) => (
            <button key={k} onClick={() => setTab(k)} className="tv-focus" style={{
              all:"unset", cursor:"pointer",
              padding:"9px 14px", borderRadius: 7,
              fontSize: 12.5, fontWeight: 600,
              color: tab === k ? "var(--text)" : "var(--text-dim)",
              background: tab === k ? "var(--surface-3)" : "transparent",
              borderLeft: tab === k ? "2px solid var(--accent)" : "2px solid transparent",
              paddingLeft: 12,
              display:"flex", alignItems:"center", gap: 9,
            }}>
              <window.Icon name={ic} size={13} />
              {l}
            </button>
          ))}
        </div>
        <div>
          {tab === "users"     && <AdminUsersRoles />}
          {tab === "folders"   && <window.SettingsFolders />}
          {tab === "history"   && <window.SettingsHistory />}
          {tab === "llm"       && <window.SettingsLLM />}
          {tab === "patterns"  && <window.SettingsPatterns />}
          {tab === "ignore"    && <window.SettingsIgnore />}
          {tab === "downloads" && <AdminDownloadsConfig />}
          {tab === "meta"      && <window.SettingsMeta />}
          {tab === "about"     && <AdminAbout />}
        </div>
      </div>
    </div>
  );
};

// ─────────────────────────────────────────────────────────────
// USERS & ROLES (admin tab)
// ─────────────────────────────────────────────────────────────
const AdminUsersRoles = () => {
  const [view, setView] = s4S("users"); // "users" | "roles" | "newRole" | "newUser"
  return (
    <div>
      <div style={{
        display:"flex", gap: 4, padding: 4,
        background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 10,
        marginBottom: 20, width:"fit-content",
      }}>
        {[["users","Users"],["roles","Roles"]].map(([k, l]) => (
          <button key={k} onClick={() => setView(k)} style={{
            all:"unset", cursor:"pointer",
            padding:"7px 16px", borderRadius: 7,
            fontSize: 12.5, fontWeight: 600,
            color: view === k ? "var(--text)" : "var(--text-dim)",
            background: view === k ? "var(--surface-3)" : "transparent",
            boxShadow: view === k ? "0 1px 0 var(--accent) inset" : "none",
          }}>{l}</button>
        ))}
      </div>
      {view === "users" && <UsersList onNew={() => setView("newUser")} />}
      {view === "roles" && <RolesList onNew={() => setView("newRole")} />}
      {view === "newRole" && <RoleBuilder onClose={() => setView("roles")} />}
      {view === "newUser" && <UserBuilder onClose={() => setView("users")} />}
    </div>
  );
};

const UsersList = ({ onNew }) => (
  <window.SettingsCard title="Users" sub="Accounts that can sign in. Roles define what each can do.">
    <div style={{ display:"flex", flexDirection:"column", gap: 10, marginBottom: 16 }}>
      {window.USERS.map(u => (
        <div key={u.id} style={{
          display:"grid", gridTemplateColumns:"44px 1fr 110px 90px auto",
          gap: 14, alignItems:"center",
          background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10,
          padding:"10px 14px",
        }}>
          <window.Avatar user={u} size={36} />
          <div style={{ minWidth: 0 }}>
            <div style={{ fontSize: 13.5, fontWeight: 600 }}>{u.name}</div>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-faint)", marginTop: 2 }}>{u.username}{u.email ? ` · ${u.email}` : ""}</div>
          </div>
          <window.Pill tone={u.role === "master" ? "accent" : "neutral"}>{u.role.toUpperCase()}</window.Pill>
          <span style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-faint)" }}>{u.lastSeen}</span>
          <div style={{ display:"flex", gap: 4 }}>
            <button style={{ all:"unset", cursor:"pointer", padding: 6, color:"var(--text-faint)" }}><window.Icon name="pencil" size={14} /></button>
            {u.role !== "master" && <button style={{ all:"unset", cursor:"pointer", padding: 6, color:"var(--text-faint)" }}><window.Icon name="trash" size={14} /></button>}
          </div>
        </div>
      ))}
    </div>
    <window.Btn kind="primary" icon="plus" onClick={onNew}>New user</window.Btn>
  </window.SettingsCard>
);

const RolesList = ({ onNew }) => (
  <window.SettingsCard title="Roles" sub="Roles bundle permissions + folder access. Master is built-in and cannot be edited.">
    <div style={{ display:"flex", flexDirection:"column", gap: 10, marginBottom: 16 }}>
      {window.ROLES.map(r => (
        <div key={r.id} style={{
          background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10,
          padding: 14,
        }}>
          <div style={{ display:"flex", alignItems:"center", gap: 10, marginBottom: 8 }}>
            <div style={{
              width: 28, height: 28, borderRadius: 7, display:"grid", placeItems:"center",
              background: r.builtIn ? "var(--accent-soft)" : "rgba(255,255,255,0.05)",
              color: r.builtIn ? "var(--accent-hi)" : "var(--text-dim)",
              border: r.builtIn ? "1px solid var(--accent-line)" : "1px solid var(--border)",
            }}>
              <window.Icon name="shield" size={14} />
            </div>
            <div style={{ flex: 1 }}>
              <div style={{ fontSize: 13.5, fontWeight: 600 }}>
                {r.name}
                {r.builtIn && <span style={{ marginLeft: 8, fontFamily:"var(--font-mono)", fontSize: 9.5, color:"var(--text-faint)", letterSpacing: 0.6 }}>· BUILT-IN</span>}
              </div>
              <div style={{ fontSize: 11.5, color:"var(--text-dim)", marginTop: 2 }}>{r.description}</div>
            </div>
            <span style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-faint)" }}>
              {window.USERS.filter(u => `r-${u.role}` === r.id || u.role === r.name.toLowerCase()).length} users
            </span>
            {!r.builtIn && <button style={{ all:"unset", cursor:"pointer", padding: 6, color:"var(--text-faint)" }}><window.Icon name="pencil" size={14} /></button>}
          </div>
          <div style={{ display:"flex", flexWrap:"wrap", gap: 6 }}>
            {r.perms.viewContent     && <window.Pill tone="success">View content</window.Pill>}
            {r.perms.uploadContent   && <window.Pill tone="accent">Upload content</window.Pill>}
            {r.perms.systemSettings  && <window.Pill tone="warn">System settings</window.Pill>}
            {r.perms.manageUsers     && <window.Pill tone="warn">Manage users</window.Pill>}
            <span style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)", marginLeft: "auto", letterSpacing: 0.4 }}>
              {r.folders === "all" ? "ALL FOLDERS" : `${r.folders.length} FOLDER${r.folders.length === 1 ? "" : "S"}`}
            </span>
          </div>
        </div>
      ))}
    </div>
    <window.Btn kind="primary" icon="plus" onClick={onNew}>New role</window.Btn>
  </window.SettingsCard>
);

// Role builder — source folder + permission bag.
const RoleBuilder = ({ onClose }) => {
  const [name, setName] = s4S("");
  const [perms, setPerms] = s4S({ viewContent: true, uploadContent: false, systemSettings: false, manageUsers: false });
  const [folderMode, setFolderMode] = s4S("all"); // "all" | "selected"
  const [folders, setFolders] = s4S(new Set());
  const toggleFolder = (id) => {
    const next = new Set(folders);
    next.has(id) ? next.delete(id) : next.add(id);
    setFolders(next);
  };
  return (
    <window.SettingsCard title="Create role" sub="Bundle permissions + folder access. Folder access is enforced server-side at the API layer.">
      <div style={{ display:"flex", flexDirection:"column", gap: 22 }}>
        <window.Field label="Role name">
          <window.Input value={name} onChange={e => setName(e.target.value)} placeholder="e.g. Donghua uploader" />
        </window.Field>

        <div>
          <div style={{ fontFamily:"var(--font-mono)", fontSize: 10, letterSpacing: 1, color:"var(--text-faint)", marginBottom: 10 }}>PERMISSIONS</div>
          <div style={{ display:"flex", flexDirection:"column", gap: 8 }}>
            {[
              ["viewContent",    "View content",     "Playback library, mark watched, manage favorites."],
              ["uploadContent",  "Upload content",   "Add downloads (torrents, magnets, file uploads)."],
              ["systemSettings", "System settings",  "Access Server Settings: LLM, patterns, ignore rules, downloads config."],
              ["manageUsers",    "Manage users",     "Create / edit / delete users and roles."],
            ].map(([k, l, h]) => (
              <label key={k} style={{ display:"flex", alignItems:"center", gap: 12, padding:"10px 12px", background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 8, cursor:"pointer" }}>
                <window.Toggle on={perms[k]} onChange={v => setPerms({ ...perms, [k]: v })} />
                <div>
                  <div style={{ fontSize: 13, color:"var(--text)" }}>{l}</div>
                  <div style={{ fontSize: 11, color:"var(--text-faint)", marginTop: 2 }}>{h}</div>
                </div>
              </label>
            ))}
          </div>
        </div>

        <div>
          <div style={{ fontFamily:"var(--font-mono)", fontSize: 10, letterSpacing: 1, color:"var(--text-faint)", marginBottom: 10 }}>SOURCE FOLDERS</div>
          <div style={{ display:"flex", gap: 4, padding: 4, background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 9, width:"fit-content", marginBottom: 12 }}>
            {[["all","All folders"],["selected","Selected only"]].map(([k, l]) => (
              <button key={k} onClick={() => setFolderMode(k)} style={{
                all:"unset", cursor:"pointer",
                padding:"6px 14px", borderRadius: 6,
                fontSize: 12, fontWeight: 600,
                color: folderMode === k ? "var(--text)" : "var(--text-dim)",
                background: folderMode === k ? "var(--surface-3)" : "transparent",
                boxShadow: folderMode === k ? "0 1px 0 var(--accent) inset" : "none",
              }}>{l}</button>
            ))}
          </div>
          {folderMode === "selected" && (
            <div style={{ display:"grid", gridTemplateColumns:"repeat(auto-fill, minmax(220px, 1fr))", gap: 8 }}>
              {window.FOLDERS.map(f => (
                <label key={f.id} style={{
                  display:"flex", alignItems:"center", gap: 10, padding:"8px 12px",
                  background:"var(--bg-1)", border:`1px solid ${folders.has(f.id) ? "var(--accent-line)" : "var(--border)"}`,
                  borderRadius: 8, cursor:"pointer",
                }}>
                  <input type="checkbox" checked={folders.has(f.id)} onChange={() => toggleFolder(f.id)} style={{ accentColor:"var(--accent)" }} />
                  <window.Icon name="folder" size={13} style={{ color:"var(--text-dim)" }} />
                  <span style={{ fontSize: 12.5 }}>{f.title}</span>
                </label>
              ))}
            </div>
          )}
        </div>

        <div style={{ display:"flex", gap: 10 }}>
          <window.Btn kind="primary" icon="check" onClick={onClose}>Create role</window.Btn>
          <window.Btn kind="ghost" onClick={onClose}>Cancel</window.Btn>
        </div>
      </div>
    </window.SettingsCard>
  );
};

const UserBuilder = ({ onClose }) => (
  <window.SettingsCard title="Create user" sub="Add a new account that can sign in.">
    <div style={{ display:"grid", gridTemplateColumns:"1fr 1fr", gap: 16 }}>
      <window.Field label="Full name"><window.Input placeholder="Anna Petrova" /></window.Field>
      <window.Field label="Username"><window.Input mono placeholder="anna" /></window.Field>
      <window.Field label="Email (optional)"><window.Input placeholder="anna@example.com" /></window.Field>
      <window.Field label="Role">
        <window.Select defaultValue="user">
          {window.ROLES.map(r => <option key={r.id} value={r.name.toLowerCase()}>{r.name}</option>)}
        </window.Select>
      </window.Field>
      <window.Field label="Initial password"><window.Input type="password" placeholder="••••••••" /></window.Field>
      <window.Field label="Repeat password"><window.Input type="password" placeholder="••••••••" /></window.Field>
    </div>
    <div style={{ display:"flex", gap: 10, marginTop: 18 }}>
      <window.Btn kind="primary" icon="check" onClick={onClose}>Create user</window.Btn>
      <window.Btn kind="ghost" onClick={onClose}>Cancel</window.Btn>
    </div>
  </window.SettingsCard>
);

const AdminDownloadsConfig = () => <window.SettingsTorrent />;

const AdminAbout = () => (
  <window.SettingsCard title="About Animarr" sub="Single-binary self-hosted media server with an LLM-driven identification pipeline.">
    <div style={{
      display:"grid", gridTemplateColumns:"160px 1fr", gap:"10px 24px",
      fontFamily:"var(--font-mono)", fontSize: 12.5,
    }}>
      <span style={{ color:"var(--text-faint)" }}>Version</span><span style={{ color:"var(--text)" }}>2.0.0 (local)</span>
      <span style={{ color:"var(--text-faint)" }}>Build</span><span style={{ color:"var(--text-dim)" }}>2026.05.26-r1</span>
      <span style={{ color:"var(--text-faint)" }}>Runtime</span><span style={{ color:"var(--text-dim)" }}>.NET 10 · Blazor Server</span>
      <span style={{ color:"var(--text-faint)" }}>LLM</span><span style={{ color:"var(--text-dim)" }}>ollama · qwen2.5:1.5b</span>
      <span style={{ color:"var(--text-faint)" }}>License</span><span style={{ color:"var(--text-dim)" }}>MIT</span>
    </div>
    <div style={{ display:"flex", gap: 10, marginTop: 22 }}>
      <a href={window.GITHUB_URL || "#"} target="_blank" rel="noreferrer" style={{ textDecoration:"none" }}>
        <window.Btn kind="solid" icon="github">View on GitHub</window.Btn>
      </a>
      <window.Btn kind="ghost" icon="external">Documentation</window.Btn>
      <window.Btn kind="ghost" icon="info">Report issue</window.Btn>
    </div>
    <div style={{ marginTop: 22, padding: 14, background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10, fontSize: 12.5, color:"var(--text-dim)", lineHeight: 1.65 }}>
      Animarr is built and maintained by the open-source community. Contributions, issue reports and pull requests are welcome — see the GitHub repo.
    </div>
  </window.SettingsCard>
);

Object.assign(window, {
  WelcomeScreen, LoginScreen, TopBarV4, TopBarNavBtn, LLMStatusPopup,
  ProfilePanel, ProfileIdentity, ProfileAppearance, ProfileAudio, ProfileLanguage,
  DownloadsRoute, ServerSettingsScreen, AdminUsersRoles, UsersList, RolesList,
  RoleBuilder, UserBuilder, AdminDownloadsConfig, AdminAbout,
});
