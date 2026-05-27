// ====== Mobile app — iPhone-frame version of Animarr ======
const { useState: useS, useEffect: useE, useMemo: useM, useRef: useR } = React;

const M = {
  bg: "var(--bg-0)",
  surface: "var(--surface)",
  surface2: "var(--surface-2)",
  border: "var(--border)",
  borderStrong: "var(--border-strong)",
  text: "var(--text)",
  dim: "var(--text-dim)",
  faint: "var(--text-faint)",
  accent: "var(--accent)",
  accentHi: "var(--accent-hi)",
  accentSoft: "var(--accent-soft)",
  success: "var(--success)",
  warn: "var(--warn)",
  ui: "var(--font-ui)",
  display: "var(--font-display)",
  mono: "var(--font-mono)",
  cjk: "var(--font-cjk)",
};

// ─────────────────────────────────────────────────────────────
// Tab bar (bottom)
// ─────────────────────────────────────────────────────────────
const TAB_H = 78;
const STATUS_H = 62; // status bar height inside IOSDevice

const TabBar = ({ route, onRoute }) => {
  const tabs = [
    { id: "catalog",  label: "Catalog",  icon: "catalog"  },
    { id: "torrents", label: "Downloads", icon: "torrent", badge: 2 },
    { id: "more",     label: "Settings", icon: "settings" },
  ];
  return (
    <div style={{
      position:"absolute", left: 0, right: 0, bottom: 0, height: TAB_H,
      background:"linear-gradient(180deg, rgba(20,15,12,0.65), rgba(15,12,10,0.92))",
      backdropFilter:"blur(20px)", WebkitBackdropFilter:"blur(20px)",
      borderTop:"1px solid var(--border)",
      display:"grid", gridTemplateColumns:"repeat(3, 1fr)",
      paddingBottom: 18,
      zIndex: 20,
    }}>
      {tabs.map(t => {
        const active = route === t.id;
        return (
          <button key={t.id} onClick={() => onRoute(t.id)} style={{
            all:"unset", cursor:"pointer",
            display:"flex", flexDirection:"column", alignItems:"center", justifyContent:"center",
            gap: 4, position:"relative",
            color: active ? M.accentHi : M.dim,
          }}>
            <div style={{ position:"relative" }}>
              <window.Icon name={t.icon} size={22} stroke={active ? 2 : 1.6} />
              {t.badge && (
                <span style={{
                  position:"absolute", top: -4, right: -8,
                  background: M.accent, color:"#fff",
                  fontSize: 9, fontWeight: 700, fontFamily: M.mono,
                  padding:"1px 5px", borderRadius: 8,
                  border: "1.5px solid var(--bg-0)",
                  lineHeight: 1.3,
                }}>{t.badge}</span>
              )}
            </div>
            <span style={{ fontSize: 10.5, fontWeight: active ? 600 : 500, letterSpacing: 0.2 }}>{t.label}</span>
            {active && (
              <span style={{
                position:"absolute", top: 4, width: 32, height: 3,
                background: M.accent, borderRadius: 3,
                boxShadow:`0 0 8px var(--accent-soft)`,
              }} />
            )}
          </button>
        );
      })}
    </div>
  );
};

// ─────────────────────────────────────────────────────────────
// Page wrapper — scroll container, accounts for safe areas
// ─────────────────────────────────────────────────────────────
const Scroll = ({ children, padTop = STATUS_H, padBottom = TAB_H + 16, bg }) => (
  <div style={{
    position:"absolute", inset: 0, overflowY:"auto", overflowX:"hidden",
    paddingTop: padTop, paddingBottom: padBottom,
    background: bg || "transparent",
  }}>{children}</div>
);

// ─────────────────────────────────────────────────────────────
// CATALOG (mobile)
// ─────────────────────────────────────────────────────────────
const CatalogMobile = ({ onOpen, setBdImage }) => {
  const init = window.__init || {};
  const [filter, setFilter] = useS("All");
  const [folderFilter, setFolderFilter] = useS("All");
  const [query, setQuery] = useS("");
  const [heroIdx, setHeroIdx] = useS(0);
  const [nrOpen, setNrOpen] = useS(!!init.openNRModal);

  const featured = useM(() => window.LIBRARY.filter(i => i.rating >= 8.0).slice(0, 5), []);
  useE(() => {
    const t = setInterval(() => setHeroIdx(i => (i + 1) % featured.length), 7000);
    return () => clearInterval(t);
  }, [featured.length]);
  const hero = featured[heroIdx] || window.LIBRARY[0];
  useE(() => { setBdImage?.(hero.bd, hero.hue); }, [hero.bd, hero.hue, setBdImage]);

  const items = window.LIBRARY.filter(i => {
    if (filter !== "All" && i.type !== filter && !(filter === "Donghua" && i.tags?.includes("Donghua"))) return false;
    if (folderFilter !== "All") {
      if (folderFilter === "Donghua" && !i.tags?.includes("Donghua")) return false;
      else if (folderFilter !== "Donghua" && i.type !== folderFilter) return false;
    }
    if (query && !i.title.toLowerCase().includes(query.toLowerCase())) return false;
    return true;
  });

  return (
    <Scroll padTop={0}>
      {/* HERO — fills the top, includes the status bar area */}
      <div style={{
        position:"relative", height: 460, overflow:"hidden",
      }}>
        <div key={hero.id} style={{
          position:"absolute", inset: 0,
          backgroundImage:`url("${hero.bd}")`, backgroundSize:"cover", backgroundPosition:"center",
          animation:"m-pan 24s ease-in-out infinite alternate",
        }} />
        <div style={{
          position:"absolute", inset: 0,
          background:`linear-gradient(180deg, rgba(10,8,7,0.55) 0%, transparent 18%, transparent 45%, rgba(10,8,7,0.92) 95%, var(--bg-0) 100%), linear-gradient(90deg, oklch(0.10 0.04 ${hero.hue} / 0.55) 0%, transparent 60%)`,
        }} />
        <div style={{
          position:"absolute", inset: 0,
          background:`radial-gradient(80% 100% at 0% 100%, oklch(0.4 0.18 ${hero.hue} / 0.28), transparent 70%)`,
          mixBlendMode:"screen",
        }} />

        {/* CJK */}
        <div style={{
          position:"absolute", right: -10, top: 30,
          fontFamily: M.cjk, fontSize: 200, lineHeight: 0.85,
          color: `oklch(0.95 0.1 ${hero.hue} / 0.08)`,
          writingMode:"vertical-rl", textOrientation:"upright",
          letterSpacing: 8, pointerEvents:"none", userSelect:"none",
        }}>{hero.cjk}</div>

        {/* status bar zone — keep header transparent on top of hero */}
        <div style={{
          position:"absolute", top: STATUS_H, left: 16, right: 16,
          display:"flex", alignItems:"center", justifyContent:"space-between",
        }}>
          <div style={{ display:"flex", alignItems:"center", gap: 8 }}>
            <window.Logo size={24} />
            <span style={{ fontFamily: M.display, fontSize: 14, letterSpacing:-0.3 }}>ANIMARR</span>
          </div>
          <div style={{ display:"flex", alignItems:"center", gap: 8 }}>
            {window.NEEDS_REVIEW.length > 0 && (
              <button onClick={() => setNrOpen(true)} title="Needs review" style={{
                all:"unset", cursor:"pointer", position:"relative",
                width: 36, height: 36, borderRadius: 12,
                background:"rgba(10,8,7,0.45)", backdropFilter:"blur(8px)",
                border:"1px solid oklch(0.60 0.16 75 / 0.4)",
                display:"grid", placeItems:"center", color: M.warn,
              }}>
                <window.Icon name="warn" size={16} />
                <span style={{
                  position:"absolute", top: -3, right: -3,
                  background: M.warn, color:"#000",
                  fontFamily: M.mono, fontSize: 9, fontWeight: 800,
                  minWidth: 16, height: 16, padding:"0 4px",
                  borderRadius: 8, display:"grid", placeItems:"center",
                  border: "1.5px solid var(--bg-0)",
                  lineHeight: 1,
                }}>{window.NEEDS_REVIEW.length}</span>
              </button>
            )}
            <button style={{
              all:"unset", cursor:"pointer",
              width: 36, height: 36, borderRadius: 12,
              background:"rgba(10,8,7,0.45)", backdropFilter:"blur(8px)",
              border:"1px solid rgba(255,255,255,0.10)",
              display:"grid", placeItems:"center", color: M.text,
            }}>
              <window.Icon name="search" size={16} />
            </button>
          </div>
        </div>

        {/* hero text — bottom */}
        <div style={{ position:"absolute", left: 18, right: 18, bottom: 30 }}>
          <div style={{ display:"flex", alignItems:"center", gap: 8, marginBottom: 10 }}>
            <window.Pill tone="accent">FEATURED</window.Pill>
            <span style={{ fontFamily: M.mono, fontSize: 9.5, color: M.faint, letterSpacing: 0.6 }}>
              {String(heroIdx + 1).padStart(2, "0")} / {String(featured.length).padStart(2, "0")}
            </span>
          </div>
          <h2 style={{
            margin: 0, fontFamily: M.display,
            fontSize: 38, lineHeight: 0.92, letterSpacing: -1.2, color: "#fff",
            textShadow:"0 4px 18px rgba(0,0,0,0.6)",
          }}>{hero.title.toUpperCase()}</h2>
          <div style={{
            display:"flex", alignItems:"center", gap: 12, marginTop: 8,
            fontFamily: M.mono, fontSize: 10.5, letterSpacing: 0.8, color: M.dim,
          }}>
            <span style={{ color: M.accentHi, fontWeight: 700 }}>★ {hero.rating.toFixed(1)}</span>
            <span>{hero.year}</span>
            <span>{hero.episodes} EP</span>
            <span>{hero.studio}</span>
          </div>
          <div style={{ display:"flex", gap: 8, marginTop: 14 }}>
            <button onClick={() => onOpen(hero.id)} style={{
              all:"unset", cursor:"pointer",
              background: M.accent, color:"#fff",
              borderRadius: 10, padding: "10px 18px",
              fontSize: 13, fontWeight: 700, letterSpacing: 0.3,
              display:"inline-flex", alignItems:"center", gap: 7,
            }}>
              <window.Icon name="play" size={14} stroke={2.2} /> Open detail
            </button>
          </div>
          {/* hero pager dots */}
          <div style={{ display:"flex", gap: 5, marginTop: 18, justifyContent:"center" }}>
            {featured.map((_, i) => (
              <span key={i} onClick={() => setHeroIdx(i)} style={{
                cursor:"pointer",
                width: i === heroIdx ? 20 : 5, height: 5,
                background: i === heroIdx ? M.accent : "rgba(255,255,255,0.25)",
                borderRadius: 5, transition:"width .25s ease",
              }} />
            ))}
          </div>
        </div>
      </div>

      {/* FILTER */}
      <div style={{ padding:"22px 16px 10px", display:"flex", flexDirection:"column", gap: 12 }}>
        <div style={{
          display:"flex", alignItems:"center", gap: 8,
          background: M.surface, border:`1px solid ${M.border}`,
          borderRadius: 12, padding:"0 12px", height: 40,
        }}>
          <window.Icon name="search" size={15} />
          <input value={query} onChange={e => setQuery(e.target.value)}
            placeholder="Search your library"
            style={{ all:"unset", flex: 1, color: M.text, fontSize: 14 }} />
          {query && (
            <button onClick={() => setQuery("")} style={{ all:"unset", cursor:"pointer", color: M.faint }}>
              <window.Icon name="x" size={13} />
            </button>
          )}
        </div>

        <div style={{
          display:"flex", gap: 8, overflowX:"auto", scrollbarWidth:"none",
          paddingBottom: 2,
        }}>
          {["All","Anime","Movie","Series","Donghua","Multserials"].map(t => {
            const active = filter === t;
            return (
              <button key={t} onClick={() => setFilter(t)} style={{
                all:"unset", cursor:"pointer", flexShrink: 0,
                padding: "8px 14px",
                fontSize: 12.5, fontWeight: 600,
                color: active ? "#fff" : M.dim,
                background: active ? M.accent : M.surface,
                border: `1px solid ${active ? M.accent : M.border}`,
                borderRadius: 999,
              }}>{t}</button>
            );
          })}
        </div>

        {/* folder filter row */}
        <div style={{
          display:"flex", gap: 8, overflowX:"auto", scrollbarWidth:"none",
          paddingBottom: 2, alignItems:"center",
        }}>
          <span style={{ fontFamily: M.mono, fontSize: 9.5, color: M.faint, letterSpacing: 1, marginRight: 2, flexShrink: 0 }}>FOLDER</span>
          {["All", ...window.FOLDERS.map(f => f.title)].map(name => {
            const active = folderFilter === name;
            return (
              <button key={name} onClick={() => setFolderFilter(name)} style={{
                all:"unset", cursor:"pointer", flexShrink: 0,
                padding: "5px 11px",
                fontSize: 11, fontWeight: 600,
                color: active ? "#fff" : M.dim,
                background: active ? M.accent : "transparent",
                border: `1px solid ${active ? M.accent : M.border}`,
                borderRadius: 999,
              }}>{name}</button>
            );
          })}
        </div>

        <div style={{
          display:"flex", justifyContent:"space-between", alignItems:"center",
          fontFamily: M.mono, fontSize: 10.5, color: M.faint, letterSpacing: 0.6,
          paddingTop: 4,
        }}>
          <span>{items.length} / {window.LIBRARY.length} TITLES</span>
          <button style={{ all:"unset", cursor:"pointer", color: M.dim, display:"inline-flex", alignItems:"center", gap: 4 }}>
            <window.Icon name="filter" size={11} /> SORT
          </button>
        </div>
      </div>

      {/* GRID 2-col */}
      <div style={{
        padding: "8px 16px 20px",
        display:"grid", gridTemplateColumns:"1fr 1fr", gap: 12,
      }}>
        {items.map(i => (
          <window.Poster key={i.id} item={i} w={"100%"} h={245} onClick={() => onOpen(i.id)} />
        ))}
      </div>

      {nrOpen && <NRSheet onClose={() => setNrOpen(false)} />}
    </Scroll>
  );
};

// ── Mobile NeedsReview bottom sheet ──────────────────────────────────────
const NRSheet = ({ onClose }) => (
  <>
    <div onClick={onClose} style={{
      position:"absolute", inset: 0, background:"rgba(0,0,0,0.6)",
      backdropFilter:"blur(4px)", zIndex: 50,
      animation:"sheet-bg .25s ease",
    }} />
    <div style={{
      position:"absolute", left: 0, right: 0, bottom: 0,
      height: "82%",
      background:"linear-gradient(180deg, var(--surface) 0%, var(--surface-2) 100%)",
      borderTopLeftRadius: 22, borderTopRightRadius: 22,
      borderTop:"1px solid var(--border-strong)",
      zIndex: 51,
      display:"flex", flexDirection:"column",
      animation:"sheet-in .3s cubic-bezier(.16,1,.3,1)",
      overflow:"hidden",
    }}>
      <div style={{ padding: "10px 0 6px", display:"flex", justifyContent:"center" }}>
        <span style={{ width: 36, height: 4, borderRadius: 4, background:"rgba(255,255,255,0.18)" }} />
      </div>
      <div style={{
        padding: "10px 18px 14px",
        borderBottom: `1px solid ${M.border}`,
        display:"flex", alignItems:"center", gap: 12,
      }}>
        <div style={{
          width: 36, height: 36, borderRadius: 8,
          background:"oklch(0.80 0.17 75 / 0.18)", color: M.warn,
          border:"1px solid oklch(0.60 0.16 75 / 0.4)",
          display:"grid", placeItems:"center",
        }}><window.Icon name="warn" size={15} /></div>
        <div style={{ flex: 1 }}>
          <div style={{ fontFamily: M.mono, fontSize: 10, letterSpacing: 1.2, color: M.warn }}>NEEDS REVIEW</div>
          <div style={{ fontFamily: M.display, fontSize: 17, letterSpacing:-0.3, marginTop: 2 }}>
            {window.NEEDS_REVIEW.length} titles below confidence
          </div>
        </div>
        <button onClick={onClose} style={{ all:"unset", cursor:"pointer", color: M.dim, padding: 4 }}>
          <window.Icon name="x" size={18} />
        </button>
      </div>

      <div style={{ flex: 1, overflowY:"auto", padding: "16px 16px 40px" }}>
        {window.NEEDS_REVIEW.map(nr => (
          <div key={nr.id} style={{
            background: M.bg, border:`1px solid ${M.border}`, borderRadius: 12,
            padding: 12, marginBottom: 12,
          }}>
            <div style={{ fontFamily: M.mono, fontSize: 11, color: M.dim, marginBottom: 10, wordBreak:"break-word" }}>{nr.folder}</div>
            <div style={{ display:"flex", flexDirection:"column", gap: 8 }}>
              {nr.candidates.map(c => (
                <div key={c.title} style={{
                  display:"flex", gap: 10,
                  background: M.surface2, border:`1px solid ${M.border}`, borderRadius: 8,
                  padding: 8,
                }}>
                  <window.Poster item={{ id: c.title, title: c.title, cjk: c.cjk, year: c.year, type:"?", hue: c.hue }} w={54} h={78} ribbon={false} />
                  <div style={{ flex: 1, display:"flex", flexDirection:"column", justifyContent:"space-between", minWidth: 0 }}>
                    <div>
                      <div style={{ fontFamily: M.display, fontSize: 13, letterSpacing:-0.2 }}>{c.title}</div>
                      <div style={{ fontFamily: M.mono, fontSize: 10, color: M.faint, marginTop: 2 }}>{c.year} · {c.source}</div>
                    </div>
                    <div style={{ display:"flex", justifyContent:"space-between", alignItems:"center" }}>
                      <span style={{
                        fontFamily: M.mono, fontSize: 10, letterSpacing: 0.4,
                        color: c.conf > 0.7 ? M.success : M.warn,
                      }}>{Math.round(c.conf*100)}%</span>
                      <button style={{
                        all:"unset", cursor:"pointer",
                        background: M.accent, color:"#fff", borderRadius: 7,
                        padding: "5px 12px", fontSize: 11.5, fontWeight: 700,
                      }}>Use</button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          </div>
        ))}
      </div>
    </div>
  </>
);

// ─────────────────────────────────────────────────────────────
// MEDIA DETAIL (mobile)
// ─────────────────────────────────────────────────────────────
const MediaDetailMobile = ({ id, onBack, setBdImage }) => {
  const init = window.__init || {};
  const item = window.LIBRARY.find(x => x.id === id) || window.LIBRARY[0];
  const [season, setSeason] = useS(1);
  const [editing, setEditing] = useS(!!init.openEditDrawer);
  const seasons = item.type === "Anime" || item.type === "Series" ? [1, 2] : [];
  const totalEps = item.episodes || 12;
  const isMovie = item.type === "Movie";

  // Episodes: have / watched / progress — same shape as desktop v3.
  const initEps = useM(() => Array.from({ length: Math.min(totalEps, 16) }, (_, i) => {
    const n = i + 1;
    const have = n < 13 || ((n * 7) % 11) > 2;
    let watched = false, progress = 0;
    if (n <= 4) { watched = true; progress = 1; }
    else if (n === 5) { progress = 0.38; }
    return { n, title: `Episode ${n}`, have, watched, progress, runtime: item.runtime };
  }), [item.id, totalEps]);

  const [eps, setEps] = useS(initEps);
  useE(() => { setEps(initEps); }, [initEps]);
  const toggleWatched = (n) => setEps(prev => prev.map(e =>
    e.n === n ? { ...e, watched: !e.watched, progress: e.watched ? 0 : 1 } : e
  ));

  // Single movie file.
  const initMovieFile = useM(() => ({
    n: 1, title: `${item.title} (${item.year})`,
    have: true, watched: false, progress: 0.62,
    runtime: item.runtime,
    filename: `${item.title} (${item.year}) - 1080p.mkv`,
    size: "6.1 GB",
  }), [item.id, item.title, item.year, item.runtime]);
  const [mFile, setMFile] = useS(initMovieFile);
  useE(() => { setMFile(initMovieFile); }, [initMovieFile]);
  const toggleMovieWatched = () => setMFile(p => ({ ...p, watched: !p.watched, progress: p.watched ? 0 : 1 }));

  // Continue action.
  const cont = useM(() => {
    if (isMovie) {
      if (mFile.watched) return { label: "Rewatch" };
      if (mFile.progress > 0) return { label: `Continue · ${Math.round(mFile.progress*100)}%` };
      return { label: "Play movie" };
    }
    const ip = eps.find(e => e.have && !e.watched && e.progress > 0);
    if (ip) return { label: `Continue · EP ${String(ip.n).padStart(2,"0")}` };
    const nextUp = eps.find(e => e.have && !e.watched);
    if (nextUp && eps.some(e => e.watched)) return { label: `Continue · EP ${String(nextUp.n).padStart(2,"0")}` };
    if (nextUp) return { label: "Play first episode" };
    return { label: "Replay" };
  }, [eps, mFile, isMovie]);

  useE(() => { setBdImage?.(item.bd, item.hue); }, [item.bd, item.hue, setBdImage]);

  return (
    <Scroll padTop={0}>
      {/* HERO */}
      <div style={{ position:"relative", height: 540, overflow:"hidden" }}>
        <div key={item.bd} style={{
          position:"absolute", inset: 0,
          backgroundImage:`url("${item.bd}")`, backgroundSize:"cover", backgroundPosition:"center",
          animation:"m-pan 28s ease-in-out infinite alternate",
        }} />
        <div style={{
          position:"absolute", inset: 0,
          background:`linear-gradient(180deg, rgba(10,8,7,0.45) 0%, transparent 18%, transparent 38%, rgba(10,8,7,0.92) 92%, var(--bg-0) 100%), linear-gradient(90deg, rgba(10,8,7,0.4) 0%, transparent 50%)`,
        }} />
        <div style={{
          position:"absolute", inset: 0,
          background:`radial-gradient(70% 100% at 0% 100%, oklch(0.4 0.18 ${item.hue} / 0.28), transparent 70%)`,
          mixBlendMode:"screen",
        }} />

        {/* CJK */}
        <div style={{
          position:"absolute", right: -6, top: 60,
          fontFamily: M.cjk, fontSize: 200, lineHeight: 0.85,
          color: `oklch(0.95 0.05 ${item.hue} / 0.09)`,
          writingMode:"vertical-rl", textOrientation:"upright", letterSpacing: 6,
          pointerEvents:"none", userSelect:"none",
        }}>{item.cjk}</div>

        {/* top bar — back + edit */}
        <div style={{
          position:"absolute", top: STATUS_H, left: 16, right: 16,
          display:"flex", alignItems:"center", justifyContent:"space-between", zIndex: 5,
        }}>
          <button onClick={onBack} style={{
            all:"unset", cursor:"pointer",
            width: 36, height: 36, borderRadius: 12,
            background:"rgba(10,8,7,0.5)", backdropFilter:"blur(10px)",
            border:"1px solid rgba(255,255,255,0.12)",
            display:"grid", placeItems:"center", color: M.text,
          }}>
            <window.Icon name="chev-l" size={16} stroke={2} />
          </button>
          <button onClick={() => setEditing(true)} style={{
            all:"unset", cursor:"pointer",
            height: 36, borderRadius: 12, padding:"0 12px",
            background:"rgba(10,8,7,0.5)", backdropFilter:"blur(10px)",
            border:"1px solid rgba(255,255,255,0.12)",
            display:"inline-flex", alignItems:"center", gap: 6,
            color: M.text, fontFamily: M.mono, fontSize: 11, letterSpacing: 0.6,
          }}>
            <window.Icon name="pencil" size={13} /> EDIT
          </button>
        </div>

        {/* bottom row: poster + title */}
        <div style={{
          position:"absolute", left: 16, right: 16, bottom: 22,
          display:"flex", alignItems:"flex-end", gap: 14,
        }}>
          <div style={{ width: 110, flexShrink: 0 }}>
            <window.Poster item={item} w={110} h={160} ribbon={false} />
          </div>
          <div style={{ flex: 1, paddingBottom: 4, minWidth: 0 }}>
            <div style={{ display:"flex", gap: 5, marginBottom: 8, flexWrap:"wrap" }}>
              <window.Pill tone="accent">{item.type.toUpperCase()}</window.Pill>
              {item.tags?.slice(0,1).map(t => <window.Pill key={t}>{t}</window.Pill>)}
            </div>
            <h1 style={{
              margin: 0, fontFamily: M.display,
              fontSize: 26, lineHeight: 0.95, letterSpacing: -0.6, color:"#fff",
              textShadow:"0 4px 18px rgba(0,0,0,0.6)",
              wordBreak:"break-word",
            }}>{item.title.toUpperCase()}</h1>
            <div style={{
              display:"flex", flexWrap:"wrap", gap: 10, marginTop: 8,
              fontFamily: M.mono, fontSize: 10, color: M.dim, letterSpacing: 0.4,
            }}>
              <span style={{ color: M.accentHi, fontWeight: 700 }}>★ {item.rating.toFixed(1)}</span>
              <span>{item.year}</span>
              <span>{item.episodes} EP</span>
            </div>
          </div>
        </div>
      </div>

      {/* primary actions */}
      <div style={{ padding: "18px 16px 6px", display:"flex", gap: 8 }}>
        <button style={{
          all:"unset", cursor:"pointer", flex: 1,
          background: M.accent, color:"#fff",
          borderRadius: 12, padding:"12px 0",
          fontSize: 14, fontWeight: 700, letterSpacing: 0.3,
          display:"flex", alignItems:"center", justifyContent:"center", gap: 8,
        }}>
          <window.Icon name="play" size={15} stroke={2.2} /> {cont.label}
        </button>
        <button style={{
          all:"unset", cursor:"pointer",
          background: M.surface, color: M.text,
          border: `1px solid ${M.borderStrong}`,
          borderRadius: 12, padding:"0 14px", height: 44,
          display:"inline-flex", alignItems:"center", justifyContent:"center", gap: 6,
        }}>
          <window.Icon name="magic" size={15} />
        </button>
      </div>

      {/* meta strip */}
      <div style={{
        padding: "10px 16px 0",
        fontFamily: M.mono, fontSize: 10, color: M.dim, letterSpacing: 0.5,
        display:"flex", flexWrap:"wrap", gap: "8px 16px",
      }}>
        <span><span style={{ color: M.faint }}>STUDIO</span> {item.studio}</span>
        <span><span style={{ color: M.faint }}>LANG</span> {item.lang}</span>
        <span><span style={{ color: M.faint }}>RUNTIME</span> {item.runtime}</span>
        <span><span style={{ color: M.success }}>● </span><span style={{ color: M.faint }}>MATCH</span> {Math.round(item.conf*100)}%</span>
      </div>

      {/* SYNOPSIS */}
      <div style={{ padding: "18px 16px 10px" }}>
        <div style={{ fontFamily: M.mono, fontSize: 10, letterSpacing: 1.2, color: M.accentHi, marginBottom: 8 }}>SYNOPSIS</div>
        <p style={{ margin: 0, fontSize: 14, lineHeight: 1.55, color: M.text, textWrap:"pretty" }}>{item.overview}</p>
      </div>

      {/* source row */}
      <div style={{ padding: "8px 16px 6px", display:"flex", gap: 8, overflowX:"auto", scrollbarWidth:"none" }}>
        {[
          ["TMDB", 0.99],
          ["MAL",  0.95],
          ["IMDb", item.conf],
        ].map(([s, c]) => (
          <div key={s} style={{
            flexShrink: 0,
            background: M.surface, border:`1px solid ${M.border}`,
            borderRadius: 10, padding:"8px 12px",
            display:"flex", alignItems:"center", gap: 8,
          }}>
            <span style={{ fontFamily: M.display, fontSize: 11, letterSpacing: -0.2 }}>{s}</span>
            <span style={{
              fontFamily: M.mono, fontSize: 10,
              color: c > 0.9 ? M.success : c > 0.7 ? M.warn : M.faint,
            }}>{Math.round(c*100)}%</span>
            <window.Icon name="external" size={11} stroke={1.8} style={{ color: M.faint }} />
          </div>
        ))}
      </div>

      {/* MOVIE FILE — same status language as episode row */}
      {isMovie && (
        <div style={{ padding: "18px 16px 8px" }}>
          <div style={{ display:"flex", alignItems:"center", justifyContent:"space-between", marginBottom: 12 }}>
            <div>
              <div style={{ fontFamily: M.mono, fontSize: 10, letterSpacing: 1.2, color: M.accentHi }}>FILE</div>
              <div style={{ fontFamily: M.display, fontSize: 20, letterSpacing: -0.3, marginTop: 2 }}>
                ON DISK
                {mFile.watched && <span style={{ fontSize: 11, color: M.success, marginLeft: 8, letterSpacing: 0.6, fontFamily: M.mono }}>· WATCHED</span>}
                {!mFile.watched && mFile.progress > 0 && (
                  <span style={{ fontSize: 11, color: M.accentHi, marginLeft: 8, letterSpacing: 0.6, fontFamily: M.mono }}>
                    · {Math.round(mFile.progress*100)}%
                  </span>
                )}
              </div>
            </div>
          </div>
          <MovieFileRowM file={mFile} item={item} onToggleWatched={toggleMovieWatched} />
        </div>
      )}

      {/* EPISODES */}
      {seasons.length > 0 && (
        <div style={{ padding: "18px 16px 8px" }}>
          <div style={{ display:"flex", alignItems:"center", justifyContent:"space-between", marginBottom: 12 }}>
            <div>
              <div style={{ fontFamily: M.mono, fontSize: 10, letterSpacing: 1.2, color: M.accentHi }}>EPISODES</div>
              <div style={{ fontFamily: M.display, fontSize: 20, letterSpacing: -0.3, marginTop: 2 }}>
                SEASON {String(season).padStart(2,"0")}
                <span style={{ fontSize: 12, color: M.faint, marginLeft: 8, letterSpacing: 0 }}>
                  {eps.filter(e=>e.have).length}/{eps.length}
                </span>
              </div>
            </div>
            <select value={season} onChange={e => setSeason(+e.target.value)} style={{
              background: M.surface, color: M.text,
              border:`1px solid ${M.borderStrong}`, borderRadius: 8,
              padding:"6px 26px 6px 10px", fontSize: 11, fontFamily: M.mono,
              appearance:"none",
              backgroundImage:`url("data:image/svg+xml,%3Csvg width='8' height='5' xmlns='http://www.w3.org/2000/svg'%3E%3Cpath d='M1 1l3 3 3-3' stroke='%23a8a097' fill='none' stroke-width='1.5'/%3E%3C/svg%3E")`,
              backgroundRepeat:"no-repeat", backgroundPosition:"right 10px center",
            }}>
              {seasons.map(s => <option key={s} value={s}>SEASON {String(s).padStart(2,"0")}</option>)}
            </select>
          </div>

          <div style={{ display:"flex", flexDirection:"column", gap: 8 }}>
            {eps.map(ep => <EpisodeRowM key={ep.n} ep={ep} item={item} onToggleWatched={() => toggleWatched(ep.n)} />)}
          </div>
        </div>
      )}

      <style>{`@keyframes m-pan { 0%{transform:scale(1.04) translate(-1%,-1%);} 100%{transform:scale(1.1) translate(1.5%,1.5%);} }`}</style>

      {editing && <EditSheet item={item} onClose={() => setEditing(false)} />}
    </Scroll>
  );
};

const EpisodeRowM = ({ ep, item, onToggleWatched }) => (
  <div style={{
    position:"relative",
    display:"flex", gap: 12,
    background: ep.have ? (ep.watched ? "rgba(21,17,14,0.7)" : M.surface) : "rgba(21,17,14,0.55)",
    border: ep.have ? `1px solid ${M.border}` : "1px dashed rgba(255,240,220,0.12)",
    borderRadius: 12, overflow:"hidden",
    opacity: ep.have ? (ep.watched ? 0.7 : 1) : 0.45,
  }}>
    <div style={{
      width: 4, alignSelf:"stretch",
      background: ep.have ? (ep.watched ? M.faint : M.success) : M.warn,
      boxShadow: (ep.have && !ep.watched) ? "0 0 8px var(--success)" : "none",
    }} />
    <div style={{
      width: 90, height: 64, flexShrink: 0,
      backgroundImage:`url("${item.bd}")`, backgroundSize:"cover",
      backgroundPosition: `${(ep.n * 19) % 100}% center`,
      position:"relative", margin: 8,
      borderRadius: 8, overflow:"hidden",
      filter: ep.have ? (ep.watched ? "saturate(0.6) brightness(0.7)" : "none") : "grayscale(1) brightness(0.55)",
    }}>
      <div style={{ position:"absolute", inset: 0, background:"linear-gradient(180deg, transparent 30%, rgba(0,0,0,0.7))" }} />
      <div style={{
        position:"absolute", top: 4, left: 6,
        fontFamily: M.display, fontSize: 16, color: ep.have ? "#fff" : "rgba(255,255,255,0.55)",
        textShadow:"0 1px 4px rgba(0,0,0,0.6)",
      }}>{String(ep.n).padStart(2,"0")}</div>
      {!ep.have && (
        <div style={{
          position:"absolute", inset: 0, display:"grid", placeItems:"center",
          color: M.warn, opacity: 0.7,
        }}><window.Icon name="download" size={14} stroke={1.8} /></div>
      )}
      {/* progress bar */}
      {ep.have && (ep.watched || ep.progress > 0) && (
        <div style={{ position:"absolute", left: 0, right: 0, bottom: 0, height: 3, background:"rgba(255,255,255,0.1)" }}>
          <div style={{
            width: `${(ep.watched ? 1 : ep.progress) * 100}%`, height: "100%",
            background: ep.watched ? M.faint : `linear-gradient(90deg, ${M.accent}, ${M.accentHi})`,
          }} />
        </div>
      )}
    </div>
    <div style={{ flex: 1, minWidth: 0, padding: "10px 4px 10px 0", display:"flex", flexDirection:"column", justifyContent:"center" }}>
      <div style={{ fontSize: 13, fontWeight: 600, color: ep.have ? (ep.watched ? M.dim : M.text) : M.dim }}>{ep.title}</div>
      <div style={{ fontFamily: M.mono, fontSize: 9.5, color: M.faint, marginTop: 3, display:"flex", gap: 6 }}>
        <span>{ep.runtime}</span>
        {ep.have ? (
          <><span>·</span><span>1080p</span><span>·</span><span>H.265</span></>
        ) : (
          <><span>·</span><span style={{ color: M.warn }}>Missing</span></>
        )}
      </div>
    </div>
    {/* Disk-status icon — immutable */}
    <div title={ep.have ? "On disk" : "Missing"} style={{
      width: 24, height: 24, borderRadius: 6, margin:"auto 6px auto 0", flexShrink: 0,
      background: ep.have ? "oklch(0.74 0.15 150 / 0.18)" : "oklch(0.80 0.17 75 / 0.20)",
      border: `1px solid ${ep.have ? M.success : M.warn}`,
      display:"grid", placeItems:"center",
      color: ep.have ? M.success : M.warn,
    }}>
      <window.Icon name={ep.have ? "check" : "warn"} size={11} stroke={2.4} />
    </div>
    {/* Watched-eye toggle — user-controlled */}
    {ep.have && (
      <button onClick={(e) => { e.stopPropagation(); onToggleWatched?.(); }}
        title={ep.watched ? "Mark as unwatched" : "Mark as watched"} style={{
        all:"unset", cursor:"pointer", margin: "auto 10px auto 0", flexShrink: 0,
        width: 24, height: 24, borderRadius: 6,
        background: ep.watched ? "oklch(0.74 0.15 150 / 0.20)" : "transparent",
        border: `1px solid ${ep.watched ? M.success : M.border}`,
        display:"grid", placeItems:"center",
        color: ep.watched ? M.success : M.dim,
      }}>
        <EyeIconM closed={!ep.watched} size={11} />
      </button>
    )}
  </div>
);

const EyeIconM = ({ closed, size = 15 }) => closed ? (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
       strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
    <path d="M9.88 4.24A10.1 10.1 0 0 1 12 4c7 0 10 8 10 8a17.7 17.7 0 0 1-2.16 3.19" />
    <path d="M6.61 6.61A17.6 17.6 0 0 0 2 12s3 8 10 8a9.7 9.7 0 0 0 5.39-1.61" />
    <path d="M9.9 9.9a3 3 0 0 0 4.2 4.2" />
    <path d="M1 1l22 22" />
  </svg>
) : (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
       strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
    <path d="M2 12s3-8 10-8 10 8 10 8-3 8-10 8S2 12 2 12z" />
    <circle cx="12" cy="12" r="3" />
  </svg>
);

// Movie file row — same status language as episode row, vertical layout
// suited for narrow mobile width.
const MovieFileRowM = ({ file, onToggleWatched, item }) => {
  const watched = file.watched;
  const inProg = !watched && file.progress > 0;
  return (
    <div style={{
      position:"relative", borderRadius: 14, overflow:"hidden",
      background: M.surface, border:`1px solid ${M.border}`,
      opacity: watched ? 0.8 : 1,
    }}>
      <div style={{
        position:"absolute", left: 0, top: 0, bottom: 0, width: 3,
        background: watched ? M.faint : M.success,
        boxShadow: !watched ? "0 0 12px var(--success)" : "none",
        zIndex: 2,
      }} />
      <div style={{
        position:"relative", aspectRatio: "16 / 9",
        backgroundImage:`url("${item.bd}")`, backgroundSize:"cover", backgroundPosition:"center",
        filter: watched ? "saturate(0.6) brightness(0.7)" : "none",
      }}>
        <div style={{ position:"absolute", inset: 0, background:"linear-gradient(180deg, transparent 30%, rgba(0,0,0,0.78) 100%)" }} />
        <div style={{
          position:"absolute", top: 14, left: 16,
          fontFamily: M.display, fontSize: 22, color:"#fff", textShadow:"0 2px 8px rgba(0,0,0,0.7)",
        }}>MOVIE</div>
        <button onClick={(e) => { e.stopPropagation(); onToggleWatched?.(); }} style={{
          all:"unset", cursor:"pointer", position:"absolute", top: 12, right: 12,
          width: 30, height: 30, borderRadius: 8,
          background: watched ? "oklch(0.74 0.15 150 / 0.22)" : "rgba(0,0,0,0.45)",
          border: `1px solid ${watched ? M.success : "rgba(255,255,255,0.18)"}`,
          color: watched ? M.success : "#fff",
          display:"grid", placeItems:"center", backdropFilter:"blur(6px)",
        }}>
          <EyeIconM closed={!watched} size={14} />
        </button>
        {(watched || inProg) && (
          <div style={{ position:"absolute", left: 0, right: 0, bottom: 0, height: 4, background:"rgba(255,255,255,0.08)" }}>
            <div style={{
              width: `${(watched ? 1 : file.progress) * 100}%`, height:"100%",
              background: watched ? M.faint : `linear-gradient(90deg, ${M.accent}, ${M.accentHi})`,
            }} />
          </div>
        )}
      </div>
      <div style={{ padding:"14px 14px 16px", paddingLeft: 18, display:"flex", flexDirection:"column", gap: 10 }}>
        <div style={{ fontSize: 14, fontWeight: 600, color: watched ? M.dim : M.text }}>{file.title}</div>
        <div style={{ fontFamily: M.mono, fontSize: 10.5, color: M.faint, wordBreak:"break-all" }}>{file.filename}</div>
        <div style={{ display:"flex", flexWrap:"wrap", gap: "4px 14px", fontFamily: M.mono, fontSize: 10.5, color: M.dim }}>
          <span><span style={{ color: M.faint }}>RUNTIME</span> {file.runtime}</span>
          <span><span style={{ color: M.faint }}>RES</span> 1080p</span>
          <span><span style={{ color: M.faint }}>CODEC</span> H.265</span>
          <span><span style={{ color: M.faint }}>SIZE</span> {file.size}</span>
        </div>
      </div>
    </div>
  );
};
  <div style={{
    position:"relative",
    display:"flex", gap: 12,
    background: ep.have ? M.surface : "rgba(21,17,14,0.55)",
    border: ep.have ? `1px solid ${M.border}` : "1px dashed rgba(255,240,220,0.12)",
    borderRadius: 12, overflow:"hidden",
    opacity: ep.have ? 1 : 0.45,
  }}>
    <div style={{
      width: 4, alignSelf:"stretch",
      background: ep.have ? M.success : M.warn,
      boxShadow: ep.have ? "0 0 8px var(--success)" : "none",
    }} />
    <div style={{
      width: 90, height: 64, flexShrink: 0,
      backgroundImage:`url("${item.bd}")`, backgroundSize:"cover",
      backgroundPosition: `${(ep.n * 19) % 100}% center`,
      position:"relative", margin: 8,
      borderRadius: 8, overflow:"hidden",
      filter: ep.have ? "none" : "grayscale(1) brightness(0.55)",
    }}>
      <div style={{ position:"absolute", inset: 0, background:"linear-gradient(180deg, transparent 30%, rgba(0,0,0,0.7))" }} />
      <div style={{
        position:"absolute", top: 4, left: 6,
        fontFamily: M.display, fontSize: 16, color: ep.have ? "#fff" : "rgba(255,255,255,0.55)",
        textShadow:"0 1px 4px rgba(0,0,0,0.6)",
      }}>{String(ep.n).padStart(2,"0")}</div>
      {!ep.have && (
        <div style={{
          position:"absolute", inset: 0,
          display:"grid", placeItems:"center",
          color: M.warn, opacity: 0.7,
        }}><window.Icon name="download" size={14} stroke={1.8} /></div>
      )}
    </div>
    <div style={{ flex: 1, minWidth: 0, padding: "10px 12px 10px 0", display:"flex", flexDirection:"column", justifyContent:"center" }}>
      <div style={{ fontSize: 13, fontWeight: 600, color: ep.have ? M.text : M.dim }}>{ep.title}</div>
      <div style={{ fontFamily: M.mono, fontSize: 9.5, color: M.faint, marginTop: 3, display:"flex", gap: 6 }}>
        <span>{ep.runtime}</span>
        {ep.have ? (
          <>
            <span>·</span><span>1080p</span><span>·</span><span>H.265</span>
          </>
        ) : (
          <><span>·</span><span style={{ color: M.warn }}>Not downloaded</span></>
        )}
      </div>
    </div>
    <div style={{
      width: 30, marginRight: 10,
      display:"flex", alignItems:"center", justifyContent:"center",
      color: ep.have ? M.success : M.warn,
    }}>
      <window.Icon name={ep.have ? "check" : "warn"} size={14} stroke={2.4} />
    </div>
  </div>
);

// ─────────────────────────────────────────────────────────────
// Edit metadata — BOTTOM SHEET (mobile pattern)
// ─────────────────────────────────────────────────────────────
const EditSheet = ({ item, onClose }) => {
  const [tab, setTab] = useS("ids");
  const [selPoster, setSelPoster] = useS(0);
  const [selBd, setSelBd] = useS(0);
  const [tags, setTags] = useS(item.tags || []);
  const [tagInput, setTagInput] = useS("");

  const posterCandidates = useM(() => {
    const variants = [0, 30, -30, 60, -60, 90, 120, 150];
    return variants.map((d, i) => ({
      id: `${item.id}-c${i}`, title: item.title, cjk: item.cjk, year: item.year,
      type: item.type, hue: (item.hue + d + 360) % 360,
      bd: Object.values(window.BD)[i % Object.values(window.BD).length],
      source: ["TMDB","TMDB","MAL","TMDB","MAL","IMDb","TMDB","Local"][i],
    }));
  }, [item.id, item.hue]);

  const bdCandidates = useM(() => Object.values(window.BD), []);

  const addTag = () => {
    const t = tagInput.trim();
    if (!t || tags.includes(t)) return;
    setTags([...tags, t]);
    setTagInput("");
  };

  return (
    <>
      <div onClick={onClose} style={{
        position:"absolute", inset: 0, background:"rgba(0,0,0,0.6)",
        backdropFilter:"blur(4px)", zIndex: 50,
        animation:"sheet-bg .25s ease",
      }} />
      <div style={{
        position:"absolute", left: 0, right: 0, bottom: 0,
        height: "88%",
        background:"linear-gradient(180deg, var(--surface) 0%, var(--surface-2) 100%)",
        borderTopLeftRadius: 22, borderTopRightRadius: 22,
        borderTop:"1px solid var(--border-strong)",
        zIndex: 51,
        display:"flex", flexDirection:"column",
        animation:"sheet-in .3s cubic-bezier(.16,1,.3,1)",
        overflow:"hidden",
      }}>
        {/* grabber */}
        <div style={{
          padding: "10px 0 6px",
          display:"flex", justifyContent:"center",
        }}>
          <span style={{ width: 36, height: 4, borderRadius: 4, background:"rgba(255,255,255,0.18)" }} />
        </div>

        {/* header */}
        <div style={{
          padding: "10px 18px 14px",
          borderBottom: `1px solid ${M.border}`,
          display:"flex", alignItems:"flex-start", gap: 12,
        }}>
          <div style={{
            width: 36, height: 36, borderRadius: 8, display:"grid", placeItems:"center",
            background: M.accentSoft, color: M.accentHi, border:`1px solid var(--accent-line)`,
          }}>
            <window.Icon name="pencil" size={15} />
          </div>
          <div style={{ flex: 1 }}>
            <div style={{ fontFamily: M.mono, fontSize: 10, color: M.accentHi, letterSpacing: 1.2 }}>EDIT METADATA</div>
            <div style={{ fontFamily: M.display, fontSize: 17, letterSpacing:-0.3, marginTop: 3, lineHeight: 1.15 }}>{item.title}</div>
          </div>
          <button onClick={onClose} style={{ all:"unset", cursor:"pointer", color: M.dim, padding: 4 }}>
            <window.Icon name="x" size={18} />
          </button>
        </div>

        {/* tabs */}
        <div style={{
          display:"flex", overflowX:"auto", scrollbarWidth:"none",
          padding:"4px 14px 0", gap: 4,
          borderBottom:`1px solid ${M.border}`,
        }}>
          {[
            ["ids","Source IDs"], ["basics","Basics"],
            ["poster","Poster"], ["backdrop","Backdrop"], ["tags","Tags"],
          ].map(([k, l]) => (
            <button key={k} onClick={() => setTab(k)} style={{
              all:"unset", cursor:"pointer", flexShrink: 0,
              padding:"10px 12px", fontSize: 12.5, fontWeight: 600,
              color: tab === k ? M.text : M.dim,
              borderBottom: tab === k ? `2px solid ${M.accent}` : "2px solid transparent",
              marginBottom: -1,
            }}>{l}</button>
          ))}
        </div>

        {/* body */}
        <div style={{ flex: 1, overflowY:"auto", padding: "18px 18px 110px" }}>
          {tab === "ids" && (
            <div style={{ display:"flex", flexDirection:"column", gap: 14 }}>
              <div style={{
                background:"linear-gradient(135deg, var(--accent-soft), transparent 70%), var(--bg-1)",
                border:`1px solid var(--accent-line)`, borderRadius: 10, padding: 12,
                display:"flex", alignItems:"center", gap: 10,
              }}>
                <div style={{ width: 28, height: 28, borderRadius: 6, background: M.accent, display:"grid", placeItems:"center", color:"#fff" }}>
                  <window.Icon name="sparkle" size={13} />
                </div>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontSize: 12.5, fontWeight: 600 }}>Auto-match · {Math.round(item.conf*100)}%</div>
                  <div style={{ fontSize: 11, color: M.dim }}>qwen2.5:1.5b · normalized OK</div>
                </div>
              </div>

              {["TMDB ID","MAL ID","IMDb ID","AniDB ID"].map(l => (
                <SheetField key={l} label={l}>
                  <SheetInput placeholder={`Enter ${l}…`} />
                </SheetField>
              ))}

              <SheetField label="Or paste a source URL" hint="Animarr extracts the ID automatically.">
                <SheetInput placeholder="https://www.themoviedb.org/tv/…" mono />
              </SheetField>
            </div>
          )}

          {tab === "basics" && (
            <div style={{ display:"flex", flexDirection:"column", gap: 14 }}>
              <SheetField label="Display title"><SheetInput defaultValue={item.title} /></SheetField>
              <SheetField label="English title"><SheetInput defaultValue={item.title} /></SheetField>
              <SheetField label="Original (CJK)"><SheetInput defaultValue={item.cjk} style={{ fontFamily: M.cjk }} /></SheetField>
              <div style={{ display:"grid", gridTemplateColumns:"1fr 1fr", gap: 12 }}>
                <SheetField label="Year"><SheetInput defaultValue={item.year} mono /></SheetField>
                <SheetField label="Type">
                  <select defaultValue={item.type} style={sheetSelectStyle}>
                    <option>Anime</option><option>Movie</option>
                    <option>Series</option><option>Multserials</option>
                  </select>
                </SheetField>
              </div>
              <SheetField label="Language">
                <select defaultValue={item.lang} style={sheetSelectStyle}>
                  <option>Mandarin</option><option>Japanese</option><option>English</option>
                </select>
              </SheetField>
              <SheetField label="Studio"><SheetInput defaultValue={item.studio} /></SheetField>
            </div>
          )}

          {tab === "poster" && (
            <div>
              <div style={{ fontSize: 12.5, color: M.dim, marginBottom: 12 }}>
                Pick a poster from sources, or upload your own.
              </div>
              <div style={{ display:"grid", gridTemplateColumns:"1fr 1fr", gap: 10 }}>
                {posterCandidates.map((c, i) => (
                  <button key={c.id} onClick={() => setSelPoster(i)} style={{
                    all:"unset", cursor:"pointer", position:"relative",
                    padding: 3, borderRadius: 12,
                    background: selPoster === i ? M.accent : "transparent",
                  }}>
                    <div style={{ borderRadius: 10, overflow:"hidden" }}>
                      <window.Poster item={c} w={"100%"} h={195} ribbon={false} />
                    </div>
                    {selPoster === i && (
                      <div style={{
                        position:"absolute", top: 8, right: 8,
                        width: 22, height: 22, borderRadius: 22,
                        background: M.accent, color:"#fff",
                        display:"grid", placeItems:"center",
                        border:"2px solid #fff",
                      }}><window.Icon name="check" size={11} stroke={3} /></div>
                    )}
                    <div style={{ fontFamily: M.mono, fontSize: 9.5, color: M.faint, textAlign:"center", marginTop: 6 }}>{c.source}</div>
                  </button>
                ))}
              </div>
              <button style={{
                all:"unset", cursor:"pointer", width:"100%",
                marginTop: 14, padding:"12px 0",
                border:`1px dashed ${M.borderStrong}`, borderRadius: 10,
                fontSize: 12.5, color: M.dim, textAlign:"center",
                display:"flex", alignItems:"center", justifyContent:"center", gap: 8,
              }}>
                <window.Icon name="upload" size={14} /> Upload custom
              </button>
            </div>
          )}

          {tab === "backdrop" && (
            <div>
              <div style={{ fontSize: 12.5, color: M.dim, marginBottom: 12 }}>
                Backdrop sits behind the hero and the app background.
              </div>
              <div style={{ display:"flex", flexDirection:"column", gap: 10 }}>
                {bdCandidates.map((url, i) => (
                  <button key={url} onClick={() => setSelBd(i)} style={{
                    all:"unset", cursor:"pointer",
                    position:"relative", height: 100, borderRadius: 10,
                    backgroundImage:`url("${url}")`, backgroundSize:"cover", backgroundPosition:"center",
                    border: selBd === i ? `2px solid ${M.accent}` : "2px solid transparent",
                    overflow:"hidden",
                  }}>
                    <div style={{ position:"absolute", inset: 0, background:"linear-gradient(0deg, rgba(0,0,0,0.55), transparent 55%)" }} />
                    {selBd === i && (
                      <div style={{
                        position:"absolute", top: 8, right: 8,
                        width: 22, height: 22, borderRadius: 22,
                        background: M.accent, color:"#fff",
                        display:"grid", placeItems:"center",
                        border:"2px solid #fff",
                      }}><window.Icon name="check" size={11} stroke={3} /></div>
                    )}
                    <div style={{
                      position:"absolute", left: 10, bottom: 8,
                      fontFamily: M.mono, fontSize: 9.5, color:"#fff",
                      letterSpacing: 0.6, textShadow:"0 1px 4px rgba(0,0,0,0.7)",
                    }}>BD-{String(i+1).padStart(2,"0")} · TMDB</div>
                  </button>
                ))}
              </div>
            </div>
          )}

          {tab === "tags" && (
            <div>
              <SheetField label="Add tag">
                <div style={{ display:"flex", gap: 6 }}>
                  <SheetInput value={tagInput} onChange={e => setTagInput(e.target.value)}
                    onKeyDown={e => { if (e.key === "Enter") { e.preventDefault(); addTag(); }}}
                    placeholder="e.g. Cultivation" style={{ flex: 1 }} />
                  <button onClick={addTag} style={{
                    all:"unset", cursor:"pointer",
                    background: M.accent, color:"#fff", borderRadius: 10, padding: "0 14px",
                    fontSize: 12.5, fontWeight: 600, display:"inline-flex", alignItems:"center", gap: 5,
                  }}><window.Icon name="plus" size={13} /> Add</button>
                </div>
              </SheetField>
              <div style={{ marginTop: 16, display:"flex", flexWrap:"wrap", gap: 8 }}>
                {tags.map(t => (
                  <span key={t} style={{
                    display:"inline-flex", alignItems:"center", gap: 6,
                    background: M.accentSoft, color: M.accentHi,
                    border:`1px solid var(--accent-line)`, borderRadius: 6,
                    padding:"6px 8px 6px 10px",
                    fontFamily: M.mono, fontSize: 11.5, letterSpacing: 0.4,
                  }}>
                    {t}
                    <button onClick={() => setTags(tags.filter(x => x !== t))} style={{
                      all:"unset", cursor:"pointer", padding: 2, color: M.accentHi,
                    }}><window.Icon name="x" size={10} stroke={2.6} /></button>
                  </span>
                ))}
              </div>
              <div style={{ marginTop: 22 }}>
                <div style={{ fontFamily: M.mono, fontSize: 10, color: M.faint, letterSpacing: 1, marginBottom: 8 }}>SUGGESTED</div>
                <div style={{ display:"flex", flexWrap:"wrap", gap: 6 }}>
                  {["Cultivation","Donghua","Action","Romance","Fantasy","Mecha"]
                    .filter(t => !tags.includes(t)).map(t => (
                    <button key={t} onClick={() => setTags([...tags, t])} style={{
                      all:"unset", cursor:"pointer",
                      background: M.surface2, border:`1px solid ${M.border}`, borderRadius: 5,
                      padding:"5px 9px", fontFamily: M.mono, fontSize: 11, color: M.dim,
                    }}>+ {t}</button>
                  ))}
                </div>
              </div>
            </div>
          )}
        </div>

        {/* footer */}
        <div style={{
          position:"absolute", left: 0, right: 0, bottom: 0,
          padding:"14px 18px 26px",
          background:"linear-gradient(180deg, transparent, var(--surface-2) 30%)",
          borderTop:`1px solid ${M.border}`,
          display:"flex", gap: 10,
        }}>
          <button onClick={onClose} style={{
            all:"unset", cursor:"pointer",
            background: M.surface, color: M.text, border:`1px solid ${M.borderStrong}`,
            borderRadius: 12, padding:"12px 18px", fontSize: 13.5, fontWeight: 600,
          }}>Cancel</button>
          <button onClick={onClose} style={{
            all:"unset", cursor:"pointer", flex: 1,
            background: M.accent, color:"#fff",
            borderRadius: 12, padding:"12px 0", fontSize: 13.5, fontWeight: 700,
            textAlign:"center",
            display:"inline-flex", alignItems:"center", justifyContent:"center", gap: 8,
          }}>
            <window.Icon name="check" size={14} stroke={2.4} /> Save changes
          </button>
        </div>
      </div>

      <style>{`
        @keyframes sheet-in { 0%{transform:translateY(100%);} 100%{transform:translateY(0);} }
        @keyframes sheet-bg { 0%{opacity:0;} 100%{opacity:1;} }
      `}</style>
    </>
  );
};

const SheetField = ({ label, hint, children }) => (
  <label style={{ display:"flex", flexDirection:"column", gap: 6 }}>
    <span style={{ fontFamily: M.mono, fontSize: 9.5, letterSpacing: 1, color: M.faint, textTransform:"uppercase" }}>{label}</span>
    {children}
    {hint && <span style={{ fontSize: 11, color: M.faint }}>{hint}</span>}
  </label>
);

const sheetInputStyle = {
  background: "var(--surface)", color: "var(--text)",
  border: "1px solid var(--border-strong)", borderRadius: 10,
  padding: "11px 13px", fontSize: 14, outline:"none",
  fontFamily: "var(--font-ui)",
};
const sheetSelectStyle = {
  ...sheetInputStyle, appearance:"none",
  backgroundImage: `url("data:image/svg+xml,%3Csvg width='10' height='6' xmlns='http://www.w3.org/2000/svg'%3E%3Cpath d='M1 1l4 4 4-4' stroke='%23a8a097' fill='none' stroke-width='1.5'/%3E%3C/svg%3E")`,
  backgroundRepeat:"no-repeat", backgroundPosition:"right 12px center",
  paddingRight: 32,
};
const SheetInput = (props) => (
  <input {...props} style={{ ...sheetInputStyle, fontFamily: props.mono ? M.mono : M.ui, ...(props.style || {}) }} />
);

// ─────────────────────────────────────────────────────────────
// TORRENTS (mobile)
// ─────────────────────────────────────────────────────────────
const TorrentsMobile = () => {
  const init = window.__init || {};
  const [tab, setTab] = useS("active");
  const [adding, setAdding] = useS(!!init.openAddTorrentDrawer);
  const [tick, setTick] = useS(0);
  useE(() => { const t = setInterval(() => setTick(x => x+1), 1100); return () => clearInterval(t); }, []);

  const torrents = window.TORRENTS.map(t => {
    if (t.state !== "downloading") return t;
    const phase = ((Date.now() / 12000) + parseInt(t.id.slice(1)) * 0.13) % 1;
    return { ...t, progress: Math.min(0.999, t.progress + phase * 0.08) };
  });
  const filtered = torrents.filter(t => tab === "active" ? t.state !== "queued" : tab === "queued" ? t.state === "queued" : true);

  return (
    <Scroll>
      <div style={{ padding:"14px 16px 4px" }}>
        <div style={{ display:"flex", alignItems:"center", justifyContent:"space-between" }}>
          <div>
            <div style={{ fontFamily: M.mono, fontSize: 10, letterSpacing: 1.2, color: M.accentHi }}>LIVE · {torrents.filter(t=>t.state==="downloading").length} ACTIVE</div>
            <h1 style={{ margin: "4px 0 0", fontFamily: M.display, fontSize: 30, letterSpacing:-0.6 }}>TORRENTS</h1>
          </div>
          <button onClick={() => setAdding(true)} style={{
            all:"unset", cursor:"pointer",
            width: 44, height: 44, borderRadius: 14,
            background: M.accent, color:"#fff",
            display:"grid", placeItems:"center",
            boxShadow:`0 8px 22px var(--accent-soft)`,
          }}>
            <window.Icon name="plus" size={20} stroke={2.4} />
          </button>
        </div>

        <div style={{
          display:"flex", gap: 10, marginTop: 14,
          background: M.surface, border:`1px solid ${M.border}`, borderRadius: 12,
          padding:"10px 14px",
        }}>
          <div style={{ flex: 1 }}>
            <div style={{ fontFamily: M.mono, fontSize: 9, color: M.faint, letterSpacing: 0.8 }}>DOWN</div>
            <div style={{ fontFamily: M.display, fontSize: 18, color: M.accentHi }}>
              {torrents.reduce((s,t) => s + (t.state==="downloading"?t.dn:0), 0).toFixed(1)}
              <span style={{ fontSize: 10, color: M.faint, marginLeft: 4 }}>MB/s</span>
            </div>
          </div>
          <div style={{ width: 1, background: M.border }} />
          <div style={{ flex: 1 }}>
            <div style={{ fontFamily: M.mono, fontSize: 9, color: M.faint, letterSpacing: 0.8 }}>UP</div>
            <div style={{ fontFamily: M.display, fontSize: 18, color: M.success }}>
              {torrents.reduce((s,t) => s + t.up, 0).toFixed(1)}
              <span style={{ fontSize: 10, color: M.faint, marginLeft: 4 }}>MB/s</span>
            </div>
          </div>
        </div>

        <div style={{ display:"flex", gap: 4, padding: 4, background: M.surface, border:`1px solid ${M.border}`, borderRadius: 10, marginTop: 14 }}>
          {[["active","Active"],["queued","Queued"],["all","All"]].map(([k,l]) => (
            <button key={k} onClick={() => setTab(k)} style={{
              all:"unset", cursor:"pointer", flex: 1, textAlign:"center",
              padding:"8px 0", borderRadius: 7,
              fontSize: 12.5, fontWeight: 600,
              color: tab === k ? M.text : M.dim,
              background: tab === k ? M.surface3 || "var(--surface-3)" : "transparent",
              boxShadow: tab === k ? `0 1px 0 ${M.accent} inset` : "none",
            }}>{l}</button>
          ))}
        </div>
      </div>

      <div style={{ padding: "14px 16px", display:"flex", flexDirection:"column", gap: 10 }}>
        {filtered.map(t => <TorrentCardM key={t.id} t={t} />)}
      </div>

      {adding && <AddTorrentSheet onClose={() => setAdding(false)} />}
    </Scroll>
  );
};

// ── Add torrent bottom sheet ─────────────────────────────────────────────
const AddTorrentSheet = ({ onClose }) => {
  const [mode, setMode] = useS("file");
  const [destIdx, setDestIdx] = useS(0);
  const [paused, setPaused] = useS(false);
  const [noSeed, setNoSeed] = useS(false);
  const [flatten, setFlatten] = useS(true);
  const [autoRename, setAutoRename] = useS(true);
  const [magnet, setMagnet] = useS("");
  const [hasFile, setHasFile] = useS(true);
  const dests = window.LIBRARY.slice(0, 12);

  return (
    <>
      <div onClick={onClose} style={{
        position:"absolute", inset: 0, background:"rgba(0,0,0,0.6)",
        backdropFilter:"blur(4px)", zIndex: 50,
        animation:"sheet-bg .25s ease",
      }} />
      <div style={{
        position:"absolute", left: 0, right: 0, bottom: 0,
        height:"92%",
        background:"linear-gradient(180deg, var(--surface) 0%, var(--surface-2) 100%)",
        borderTopLeftRadius: 22, borderTopRightRadius: 22,
        borderTop:"1px solid var(--border-strong)",
        zIndex: 51, display:"flex", flexDirection:"column",
        animation:"sheet-in .3s cubic-bezier(.16,1,.3,1)",
        overflow:"hidden",
      }}>
        <div style={{ padding: "10px 0 6px", display:"flex", justifyContent:"center" }}>
          <span style={{ width: 36, height: 4, borderRadius: 4, background:"rgba(255,255,255,0.18)" }} />
        </div>

        <div style={{
          padding:"10px 18px 14px",
          borderBottom:`1px solid ${M.border}`,
          display:"flex", alignItems:"center", gap: 12,
        }}>
          <div style={{
            width: 36, height: 36, borderRadius: 8,
            background: M.accentSoft, color: M.accentHi,
            border:`1px solid var(--accent-line)`,
            display:"grid", placeItems:"center",
          }}><window.Icon name="download" size={15} /></div>
          <div style={{ flex: 1 }}>
            <div style={{ fontFamily: M.mono, fontSize: 10, color: M.accentHi, letterSpacing: 1.2 }}>NEW</div>
            <div style={{ fontFamily: M.display, fontSize: 18, letterSpacing:-0.3, marginTop: 2 }}>ADD TORRENT</div>
          </div>
          <button onClick={onClose} style={{ all:"unset", cursor:"pointer", color: M.dim, padding: 4 }}>
            <window.Icon name="x" size={18} />
          </button>
        </div>

        <div style={{ flex: 1, overflowY:"auto", padding: "16px 18px 110px" }}>
          {/* source toggle */}
          <div style={{
            display:"grid", gridTemplateColumns:"1fr 1fr",
            background: M.bg, border:`1px solid ${M.border}`, borderRadius: 10,
            padding: 4, marginBottom: 16,
          }}>
            {[["file","File", "file"], ["magnet","Magnet","link"]].map(([k, l, icon]) => (
              <button key={k} onClick={() => setMode(k)} style={{
                all:"unset", cursor:"pointer", padding:"10px 0", textAlign:"center",
                fontSize: 12.5, fontWeight: 600,
                color: mode === k ? M.text : M.dim,
                background: mode === k ? M.surface3 || "var(--surface-3)" : "transparent",
                borderRadius: 7,
                display:"flex", alignItems:"center", justifyContent:"center", gap: 7,
              }}>
                <window.Icon name={icon} size={13} />
                {l}
              </button>
            ))}
          </div>

          {mode === "file" ? (
            <div style={{
              border:`1.5px dashed ${M.borderStrong}`, borderRadius: 12,
              padding: 18, textAlign:"center", background: M.bg,
              marginBottom: 18,
            }}>
              <window.Icon name="upload" size={20} style={{ color: M.dim }} />
              <div style={{ fontSize: 13, color: M.dim, marginTop: 10 }}>Drop .torrent file</div>
              <button onClick={() => setHasFile(!hasFile)} style={{
                all:"unset", cursor:"pointer", marginTop: 10,
                color: M.accentHi, fontSize: 12, textDecoration:"underline", letterSpacing: 0.2,
              }}>Browse</button>
              {hasFile && (
                <div style={{ fontFamily: M.mono, fontSize: 10.5, color: M.success, marginTop: 14, display:"inline-flex", alignItems:"center", gap: 5 }}>
                  <window.Icon name="check" size={10} stroke={2.4} />
                  anistar45526.torrent · 824.1 MB
                </div>
              )}
            </div>
          ) : (
            <SheetField label="Magnet URI">
              <SheetInput mono placeholder="magnet:?xt=urn:btih:…" value={magnet} onChange={e => setMagnet(e.target.value)} />
            </SheetField>
          )}

          {/* file selection (1 file) */}
          {(mode === "file" && hasFile) && (
            <div style={{ marginBottom: 18 }}>
              <div style={{ fontFamily: M.mono, fontSize: 9.5, color: M.faint, letterSpacing: 1, textTransform:"uppercase", marginBottom: 8 }}>FILES (1)</div>
              <div style={{ background: M.bg, border:`1px solid ${M.border}`, borderRadius: 10, padding: 10 }}>
                <div style={{ display:"flex", alignItems:"center", gap: 10 }}>
                  <input type="checkbox" defaultChecked style={{ accentColor:"var(--accent)" }} />
                  <window.Icon name="video" size={14} style={{ color: M.accentHi, flexShrink: 0 }} />
                  <span style={{ fontFamily: M.mono, fontSize: 10.5, flex: 1, overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap", color: M.text, minWidth: 0 }}>
                    [Anistar.org] Perfect World [TV-1] - 270 [1080p].mp4
                  </span>
                  <span style={{ fontFamily: M.mono, fontSize: 10, color: M.faint, flexShrink: 0 }}>824 MB</span>
                </div>
                <div style={{ display:"flex", gap: 6, marginTop: 8 }}>
                  {["Normal","High","Low","Skip"].map((p, i) => (
                    <button key={p} style={{
                      all:"unset", cursor:"pointer",
                      padding:"4px 9px", borderRadius: 6,
                      fontFamily: M.mono, fontSize: 10, letterSpacing: 0.4,
                      background: i === 0 ? M.accentSoft : M.surface2,
                      color: i === 0 ? M.accentHi : M.dim,
                      border: i === 0 ? `1px solid var(--accent-line)` : `1px solid ${M.border}`,
                    }}>{p}</button>
                  ))}
                </div>
              </div>
            </div>
          )}

          <SheetField label="Destination — identified titles only">
            <select value={destIdx} onChange={e => setDestIdx(+e.target.value)} style={sheetSelectStyle}>
              {dests.map((d, i) => <option key={d.id} value={i}>{d.title} ({d.year})</option>)}
            </select>
          </SheetField>

          <div style={{ marginTop: 14 }}>
            <SheetField label="Or path manually">
              <SheetInput mono defaultValue="/mnt/media/Anime" />
            </SheetField>
          </div>

          <div style={{ display:"grid", gridTemplateColumns:"1fr 1fr", gap: 12, marginTop: 14 }}>
            <SheetField label="Limit ↓ (Mbps)"><SheetInput mono defaultValue="0" /></SheetField>
            <SheetField label="Limit ↑ (Mbps)"><SheetInput mono defaultValue="0" /></SheetField>
          </div>

          <div style={{ marginTop: 20, display:"flex", flexDirection:"column", gap: 0 }}>
            <SheetToggleRow label="Start paused" on={paused} onChange={setPaused} />
            <SheetToggleRow label="Stop after download (no seed)" on={noSeed} onChange={setNoSeed} />
            <SheetToggleRow label="Flatten subfolders" on={flatten} onChange={setFlatten} />
            <SheetToggleRow label="Auto-rename via global patterns" on={autoRename} onChange={setAutoRename} last />
          </div>
        </div>

        <div style={{
          position:"absolute", left: 0, right: 0, bottom: 0,
          padding:"14px 18px 26px",
          background:"linear-gradient(180deg, transparent, var(--surface-2) 30%)",
          borderTop:`1px solid ${M.border}`,
          display:"flex", gap: 10,
        }}>
          <button onClick={onClose} style={{
            all:"unset", cursor:"pointer",
            background: M.surface, color: M.text, border:`1px solid ${M.borderStrong}`,
            borderRadius: 12, padding:"12px 18px", fontSize: 13.5, fontWeight: 600,
          }}>Cancel</button>
          <button onClick={onClose} style={{
            all:"unset", cursor:"pointer", flex: 1,
            background: M.accent, color:"#fff",
            borderRadius: 12, padding:"12px 0", fontSize: 13.5, fontWeight: 700,
            textAlign:"center",
            display:"inline-flex", alignItems:"center", justifyContent:"center", gap: 8,
          }}>
            <window.Icon name="download" size={14} stroke={2.4} /> Add torrent
          </button>
        </div>
      </div>
    </>
  );
};

const SheetToggleRow = ({ label, on, onChange, last }) => (
  <div style={{
    display:"flex", alignItems:"center", justifyContent:"space-between",
    padding:"12px 4px",
    borderBottom: last ? "none" : `1px solid ${M.border}`,
  }}>
    <span style={{ fontSize: 13.5, color: M.text }}>{label}</span>
    <window.Toggle on={on} onChange={onChange} />
  </div>
);

const TorrentCardM = ({ t }) => {
  const stateColor = t.state === "downloading" ? M.accentHi : t.state === "seeding" ? M.success : M.faint;
  return (
    <div style={{
      background: M.surface, border:`1px solid ${M.border}`,
      borderRadius: 12, padding: 12,
    }}>
      <div style={{ display:"flex", alignItems:"flex-start", gap: 8, marginBottom: 8 }}>
        <span style={{
          width: 8, height: 8, borderRadius: 8, marginTop: 4, flexShrink: 0,
          background: stateColor,
          boxShadow: t.state === "downloading" ? `0 0 8px ${stateColor}` : "none",
          animation: t.state === "downloading" ? "m-blink 1.4s infinite" : "none",
        }} />
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontFamily: M.mono, fontSize: 11.5, color: M.text, wordBreak:"break-word", lineHeight: 1.35 }}>{t.name}</div>
          <div style={{ fontSize: 11, color: t.dest.startsWith("—") ? M.warn : M.dim, marginTop: 4 }}>
            <window.Icon name="folder" size={10} stroke={1.8} style={{ verticalAlign: -1, marginRight: 4 }} />
            {t.dest}
          </div>
        </div>
      </div>

      <div style={{ height: 4, background:"rgba(255,255,255,0.05)", borderRadius: 4, overflow:"hidden", marginBottom: 6 }}>
        <div style={{
          width: `${t.progress*100}%`, height:"100%",
          background: t.state==="downloading" ? `linear-gradient(90deg, ${M.accent}, ${M.accentHi})` :
                      t.state==="seeding"     ? `linear-gradient(90deg, ${M.success}, oklch(0.85 0.15 150))` :
                      "rgba(255,255,255,0.2)",
          boxShadow: t.state==="downloading" ? `0 0 6px var(--accent-soft)` : "none",
        }} />
      </div>

      <div style={{
        display:"flex", justifyContent:"space-between", alignItems:"center",
        fontFamily: M.mono, fontSize: 10.5, color: M.faint, letterSpacing: 0.5,
      }}>
        <span style={{ color: M.text }}>{(t.progress*100).toFixed(1)}%</span>
        <span>{t.size}</span>
        <span style={{ color: M.dim }}>
          {t.state === "downloading" && <>↓ {t.dn.toFixed(1)} MB/s</>}
          {t.state === "seeding"     && <>↑ {t.up.toFixed(1)} MB/s</>}
          {t.state === "queued"      && <>queued</>}
        </span>
        <span>{t.eta}</span>
      </div>
      <style>{`@keyframes m-blink { 0%,100% { opacity: 1; } 50% { opacity: 0.4; } }`}</style>
    </div>
  );
};

// ─────────────────────────────────────────────────────────────
// EXPLORER (mobile)
// ─────────────────────────────────────────────────────────────
const ExplorerMobile = ({ onOpen }) => (
  <Scroll>
    <div style={{ padding:"14px 16px 4px" }}>
      <div style={{ fontFamily: M.mono, fontSize: 10, letterSpacing: 1.2, color: M.accentHi }}>ON DISK</div>
      <h1 style={{ margin: "4px 0 0", fontFamily: M.display, fontSize: 30, letterSpacing:-0.6 }}>LIBRARY</h1>
      <div style={{ color: M.dim, fontSize: 12.5, marginTop: 6 }}>
        Animarr never renames folders on disk — only files, by your patterns.
      </div>
    </div>

    {/* needs review banner */}
    <div style={{ padding:"14px 16px 4px" }}>
      <div style={{
        background:"linear-gradient(135deg, oklch(0.30 0.16 60 / 0.18), transparent 80%), var(--surface)",
        border:"1px solid oklch(0.55 0.16 60 / 0.4)",
        borderRadius: 14, padding: 14,
      }}>
        <div style={{ display:"flex", alignItems:"center", gap: 10, marginBottom: 10 }}>
          <div style={{
            width: 26, height: 26, borderRadius: 7, display:"grid", placeItems:"center",
            background:"oklch(0.80 0.17 75 / 0.2)", color: M.warn,
          }}><window.Icon name="warn" size={14} /></div>
          <div style={{ flex: 1 }}>
            <div style={{ fontFamily: M.display, fontSize: 14, letterSpacing:-0.2 }}>NEEDS REVIEW</div>
            <div style={{ fontSize: 11.5, color: M.dim }}>{window.NEEDS_REVIEW.length} titles · pick the right match</div>
          </div>
          <window.Icon name="chev-r" size={14} style={{ color: M.dim }} />
        </div>
      </div>
    </div>

    {/* section folders */}
    <div style={{ padding:"14px 16px 4px", display:"flex", flexDirection:"column", gap: 10 }}>
      {window.FOLDERS.map(f => (
        <div key={f.id} style={{
          position:"relative", height: 110, borderRadius: 14, overflow:"hidden",
          border:`1px solid ${M.border}`,
        }}>
          <div style={{
            position:"absolute", inset: 0,
            backgroundImage:`url("${f.bd}")`, backgroundSize:"cover", backgroundPosition:"center",
            filter: "saturate(0.8)",
          }} />
          <div style={{
            position:"absolute", inset: 0,
            background:`linear-gradient(135deg, oklch(0.18 0.06 ${f.hue} / 0.6), oklch(0.10 0.04 ${f.hue} / 0.9))`,
          }} />
          <div style={{
            position:"absolute", inset: 0,
            background:"linear-gradient(90deg, transparent 30%, rgba(0,0,0,0.7) 100%)",
          }} />
          <div style={{
            position:"absolute", top: 12, right: 14, fontFamily: M.cjk, fontSize: 56,
            color: `oklch(0.95 0.05 ${f.hue} / 0.18)`, lineHeight: 0.85,
            pointerEvents:"none",
          }}>{f.title.slice(0,1)}</div>

          <div style={{ position:"absolute", left: 16, top: 16 }}>
            <window.Icon name="folder" size={18} style={{ color:"#fff", opacity: 0.85 }} />
          </div>
          <div style={{ position:"absolute", left: 16, bottom: 14, right: 16 }}>
            <div style={{ fontFamily: M.display, fontSize: 20, letterSpacing: -0.4 }}>{f.title.toUpperCase()}</div>
            <div style={{ display:"flex", gap: 6, marginTop: 6 }}>
              <window.Pill>{f.watchers} watchers</window.Pill>
              <window.Pill tone="success">{f.ident} ID</window.Pill>
              {f.missing > 0 && <window.Pill tone="warn">{f.missing} ?</window.Pill>}
            </div>
          </div>
        </div>
      ))}
    </div>
  </Scroll>
);

// ─────────────────────────────────────────────────────────────
// SETTINGS (mobile) — root list + sub-screens for History / Folders
// ─────────────────────────────────────────────────────────────
const MoreMobile = ({ onOpen }) => {
  const init = window.__init || {};
  const [sub, setSub] = useS(init.settingsSub || null); // null | "history" | "folders" | "llm" | …

  if (sub === "history") return <MobileHistory onBack={() => setSub(null)} />;
  if (sub === "folders") return <MobileFolders onBack={() => setSub(null)} />;
  if (sub)               return <MobileSubStub title={sub} onBack={() => setSub(null)} />;

  const sections = [
    {
      title: "Library management",
      rows: [
        { icon:"folder", label:"Root folders",     hint:`${window.FOLDERS.length} watched paths`, go: "folders" },
        { icon:"clock",  label:"Rename history",   hint:`${window.HISTORY.length} entries · 1 reverted`, go: "history" },
        { icon:"magic",  label:"Rename patterns",  hint:`${window.PATTERNS.length} active`,  go: "patterns" },
        { icon:"filter", label:"Ignore rules",     hint:`${window.IGNORES.length} rules`,    go: "ignore" },
      ],
    },
    {
      title: "Identification",
      rows: [
        { icon:"sparkle", label:"AI / LLM",         hint:"Ollama · qwen2.5:1.5b",   tone:"accent", go: "llm" },
        { icon:"external", label:"Metadata sources", hint:"TMDB · MAL · IMDb",      go: "meta" },
      ],
    },
    {
      title: "Appearance & Network",
      rows: [
        { icon:"image",    label:"Backdrop & theme", hint:"Animated · Crimson", go: "appearance" },
        { icon:"download", label:"Torrent",          hint:"port 51413 · DHT on", go: "torrent" },
      ],
    },
  ];
  return (
    <Scroll>
      <div style={{ padding:"14px 16px 4px" }}>
        <div style={{ fontFamily: M.mono, fontSize: 10, letterSpacing: 1.2, color: M.accentHi }}>CONFIG</div>
        <h1 style={{ margin: "4px 0 0", fontFamily: M.display, fontSize: 30, letterSpacing:-0.6 }}>SETTINGS</h1>
        <div style={{ color: M.dim, fontSize: 12.5, marginTop: 6 }}>
          Single-user, open app — no auth, no avatars, no teams.
        </div>
      </div>

      {/* LLM status hero */}
      <div style={{ padding:"14px 16px 6px" }}>
        <button onClick={() => setSub("llm")} style={{
          all:"unset", cursor:"pointer", display:"block", width:"100%",
          background:"linear-gradient(135deg, var(--accent-soft), transparent 70%), var(--surface)",
          border:`1px solid var(--accent-line)`, borderRadius: 14, padding: 14,
        }}>
          <div style={{ display:"flex", alignItems:"center", gap: 10 }}>
            <div style={{ width: 32, height: 32, borderRadius: 8, background: M.accent, display:"grid", placeItems:"center", color:"#fff" }}>
              <window.Icon name="sparkle" size={16} />
            </div>
            <div style={{ flex: 1 }}>
              <div style={{ display:"flex", alignItems:"center", gap: 6 }}>
                <span style={{ width: 6, height: 6, borderRadius: 6, background: M.success, boxShadow:`0 0 8px ${M.success}` }} />
                <span style={{ fontFamily: M.mono, fontSize: 10, color: M.dim, letterSpacing: 0.8 }}>LLM ONLINE</span>
              </div>
              <div style={{ fontFamily: M.display, fontSize: 14, letterSpacing:-0.2, marginTop: 3 }}>ollama · qwen2.5:1.5b</div>
            </div>
            <window.Icon name="chev-r" size={16} style={{ color: M.dim }} />
          </div>
          <div style={{ height: 4, background:"rgba(255,255,255,0.04)", borderRadius: 4, marginTop: 12, overflow:"hidden" }}>
            <div style={{ width:"68%", height:"100%", background:`linear-gradient(90deg, ${M.accent}, ${M.accentHi})` }} />
          </div>
          <div style={{ display:"flex", justifyContent:"space-between", fontFamily: M.mono, fontSize: 10, color: M.faint, marginTop: 6 }}>
            <span>queue</span><span>17 / 25</span>
          </div>
        </button>
      </div>

      {sections.map(sec => (
        <div key={sec.title} style={{ padding: "14px 16px 4px" }}>
          <div style={{ fontFamily: M.mono, fontSize: 9.5, letterSpacing: 1.2, color: M.faint, padding:"0 4px 8px" }}>
            {sec.title.toUpperCase()}
          </div>
          <div style={{
            background: M.surface, border:`1px solid ${M.border}`, borderRadius: 14, overflow:"hidden",
          }}>
            {sec.rows.map((r, i) => (
              <button key={r.label} onClick={() => r.go && setSub(r.go)} style={{
                all:"unset", cursor:"pointer", display:"flex", alignItems:"center", gap: 12,
                width:"100%", padding:"13px 14px",
                borderBottom: i === sec.rows.length-1 ? "none" : `1px solid ${M.border}`,
              }}>
                <div style={{
                  width: 30, height: 30, borderRadius: 8,
                  background: r.tone === "accent" ? M.accentSoft : "rgba(255,255,255,0.06)",
                  color: r.tone === "accent" ? M.accentHi : M.dim,
                  display:"grid", placeItems:"center",
                  border: r.tone === "accent" ? `1px solid var(--accent-line)` : "none",
                }}>
                  <window.Icon name={r.icon} size={14} />
                </div>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontSize: 13.5, color: M.text }}>{r.label}</div>
                  <div style={{ fontSize: 11, color: M.faint, marginTop: 2 }}>{r.hint}</div>
                </div>
                <window.Icon name="chev-r" size={14} style={{ color: M.dim }} />
              </button>
            ))}
          </div>
        </div>
      ))}

      <div style={{ padding:"18px 16px", textAlign:"center", fontFamily: M.mono, fontSize: 10, color: M.faint, letterSpacing: 0.6 }}>
        ANIMARR · v2.0 · LOCAL
      </div>
    </Scroll>
  );
};

// ── Sub-screen: Rename history ───────────────────────────────────────────
const MobileHistory = ({ onBack }) => {
  const [filter, setFilter] = useS("");
  const groups = {};
  window.HISTORY.forEach(h => { groups[h.date] = groups[h.date] || []; groups[h.date].push(h); });
  return (
    <Scroll>
      <SubHeader title="Rename history" sub={`${window.HISTORY.length} entries · revertable`} onBack={onBack} />
      <div style={{ padding:"0 16px 6px" }}>
        <div style={{
          display:"flex", alignItems:"center", gap: 8,
          background: M.surface, border:`1px solid ${M.border}`,
          borderRadius: 12, padding:"0 12px", height: 38,
        }}>
          <window.Icon name="search" size={14} />
          <input value={filter} onChange={e => setFilter(e.target.value)}
            placeholder="Filter file or pattern"
            style={{ all:"unset", flex: 1, color: M.text, fontSize: 13 }} />
        </div>
      </div>
      <div style={{ padding:"14px 16px 40px" }}>
        {Object.entries(groups).map(([date, rows]) => {
          const v = rows.filter(r => !filter || r.file.toLowerCase().includes(filter.toLowerCase()) || r.pattern.toLowerCase().includes(filter.toLowerCase()));
          if (!v.length) return null;
          return (
            <div key={date} style={{ marginBottom: 18 }}>
              <div style={{ fontFamily: M.mono, fontSize: 10, letterSpacing: 1, color: M.faint, padding:"0 4px 6px" }}>
                {new Date(date).toLocaleDateString("en", { day:"2-digit", month:"long", year:"numeric" }).toUpperCase()}
              </div>
              <div style={{ background: M.surface, border:`1px solid ${M.border}`, borderRadius: 12, overflow:"hidden" }}>
                {v.map((r, i) => (
                  <div key={r.id} style={{
                    padding:"12px 14px", display:"flex", flexDirection:"column", gap: 4,
                    borderBottom: i === v.length-1 ? "none" : `1px solid ${M.border}`,
                    opacity: r.reverted ? 0.5 : 1,
                  }}>
                    <div style={{ display:"flex", alignItems:"center", gap: 8 }}>
                      <span style={{ fontFamily: M.mono, fontSize: 10, color: M.faint }}>{r.at}</span>
                      <window.Pill tone="accent">{r.pattern}</window.Pill>
                      <div style={{ flex: 1 }} />
                      {r.reverted ? (
                        <window.Pill tone="warn">REVERTED</window.Pill>
                      ) : (
                        <button style={{
                          all:"unset", cursor:"pointer",
                          color: M.dim, fontSize: 11, fontFamily: M.mono, letterSpacing: 0.5,
                          display:"inline-flex", alignItems:"center", gap: 4,
                        }}><window.Icon name="undo" size={10} /> REVERT</button>
                      )}
                    </div>
                    <div style={{ fontFamily: M.mono, fontSize: 11, color: M.dim, wordBreak:"break-all", textDecoration: r.reverted ? "line-through" : "none" }}>{r.file}</div>
                    <div style={{ fontFamily: M.mono, fontSize: 11, color: M.text, display:"flex", alignItems:"center", gap: 6, wordBreak:"break-all" }}>
                      <window.Icon name="chev-r" size={10} style={{ color: M.accent }} />
                      {r.to}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          );
        })}
      </div>
    </Scroll>
  );
};

// ── Sub-screen: Root folders ─────────────────────────────────────────────
const MobileFolders = ({ onBack }) => (
  <Scroll>
    <SubHeader title="Root folders" sub="Each subdirectory is auto-registered as a folder watcher." onBack={onBack} />
    <div style={{ padding:"0 16px 20px", display:"flex", flexDirection:"column", gap: 10 }}>
      {window.FOLDERS.map(f => (
        <div key={f.id} style={{
          background: M.surface, border:`1px solid ${M.border}`, borderRadius: 12,
          padding: 12, display:"flex", alignItems:"center", gap: 12,
        }}>
          <div style={{
            width: 44, height: 44, borderRadius: 10,
            backgroundImage:`url("${f.bd}")`, backgroundSize:"cover", backgroundPosition:"center",
            position:"relative", overflow:"hidden", flexShrink: 0,
          }}>
            <div style={{
              position:"absolute", inset: 0,
              background:`linear-gradient(135deg, oklch(0.20 0.06 ${f.hue} / 0.4), oklch(0.10 0.04 ${f.hue} / 0.7))`,
            }} />
            <div style={{
              position:"absolute", inset: 0, display:"grid", placeItems:"center", color:"#fff",
            }}><window.Icon name="folder" size={15} /></div>
          </div>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontSize: 14, fontWeight: 600, color: M.text }}>{f.title}</div>
            <div style={{ fontFamily: M.mono, fontSize: 10.5, color: M.faint, marginTop: 2, overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap" }}>{f.path}</div>
            <div style={{ display:"flex", gap: 6, marginTop: 6 }}>
              <window.Pill>{f.watchers}w</window.Pill>
              <window.Pill tone="success">{f.ident}</window.Pill>
              {f.missing > 0 && <window.Pill tone="warn">{f.missing}?</window.Pill>}
            </div>
          </div>
          <button style={{ all:"unset", cursor:"pointer", padding: 8, color: M.faint }}>
            <window.Icon name="pencil" size={15} />
          </button>
        </div>
      ))}
      <button style={{
        all:"unset", cursor:"pointer", marginTop: 6,
        background: M.accent, color:"#fff",
        borderRadius: 12, padding:"14px 0", textAlign:"center",
        fontSize: 13.5, fontWeight: 700,
        display:"inline-flex", alignItems:"center", justifyContent:"center", gap: 7,
      }}>
        <window.Icon name="plus" size={14} stroke={2.4} /> Add section folder
      </button>
    </div>
  </Scroll>
);

const SubHeader = ({ title, sub, onBack }) => (
  <div style={{ padding:"14px 16px 14px", display:"flex", alignItems:"center", gap: 12 }}>
    <button onClick={onBack} style={{
      all:"unset", cursor:"pointer",
      width: 36, height: 36, borderRadius: 12,
      background: M.surface, border:`1px solid ${M.border}`,
      display:"grid", placeItems:"center", color: M.text,
    }}>
      <window.Icon name="chev-l" size={15} stroke={2} />
    </button>
    <div style={{ flex: 1, minWidth: 0 }}>
      <h1 style={{ margin: 0, fontFamily: M.display, fontSize: 22, letterSpacing:-0.4 }}>{title.toUpperCase()}</h1>
      {sub && <div style={{ fontSize: 11.5, color: M.dim, marginTop: 2 }}>{sub}</div>}
    </div>
  </div>
);

const MobileSubStub = ({ title, onBack }) => (
  <Scroll>
    <SubHeader title={title} onBack={onBack} />
    <div style={{
      padding:"40px 24px", textAlign:"center",
      color: M.dim, fontSize: 13.5,
    }}>
      <div style={{
        width: 56, height: 56, borderRadius: 14, margin:"0 auto 16px",
        background: M.surface, border:`1px solid ${M.border}`,
        display:"grid", placeItems:"center", color: M.faint,
      }}><window.Icon name="settings" size={22} /></div>
      <div style={{ fontFamily: M.display, fontSize: 18, letterSpacing:-0.3, color: M.text }}>NOT WIRED YET</div>
      <p style={{ marginTop: 10, lineHeight: 1.55 }}>
        This sub-screen is part of the desktop config and isn't built out for mobile yet.
        Open <span style={{ color: M.accentHi }}>Animarr</span> on a larger screen to configure it.
      </p>
    </div>
  </Scroll>
);

// ─────────────────────────────────────────────────────────────
// App shell — single page, single iPhone, internal route state
// ─────────────────────────────────────────────────────────────
const MobileApp = () => {
  const init = window.__init || {};
  const [route, setRoute] = useS(init.route || "catalog");
  const [openId, setOpenId] = useS(init.openId || null);

  const firstItem = window.LIBRARY[0];
  const [bdImage, setBdImage] = useS({ url: firstItem.bd, hue: firstItem.hue });
  const setBd = (url, hue) => { if (url && url !== bdImage.url) setBdImage({ url, hue: hue ?? 0 }); };

  const open = (id) => setOpenId(id);
  const back = () => setOpenId(null);
  const go = (r) => { setOpenId(null); setRoute(r); };

  // The whole device is on a vertical stage. The backdrop sits behind
  // the screen, blurred + dim — same idea as desktop but contained inside
  // the phone frame.
  return (
    <div style={{
      width:"100%", height:"100vh",
      display:"grid", placeItems:"center",
      background:"radial-gradient(80% 80% at 50% 30%, #1a1612, #0a0807 70%)",
      overflow:"hidden",
    }}>
      <window.IOSDevice width={402} height={874} dark title="Animarr">
        {/* phone screen */}
        <div style={{
          position:"absolute", inset: 0, overflow:"hidden",
          background: M.bg,
        }}>
          {/* dimmed backdrop inside device */}
          <div aria-hidden style={{ position:"absolute", inset: 0 }}>
            <div key={bdImage.url} style={{
              position:"absolute", inset: "-4%",
              backgroundImage:`url("${bdImage.url}")`,
              backgroundSize:"cover", backgroundPosition:"center",
              filter:"blur(14px) brightness(38%) saturate(0.95)",
              transition:"opacity 1.6s ease",
            }} />
            <div style={{
              position:"absolute", inset: 0,
              background:"radial-gradient(120% 80% at 50% 30%, transparent 0%, rgba(10,8,7,0.5) 60%, rgba(10,8,7,0.95) 100%)",
            }} />
          </div>

          {/* current screen */}
          {openId
            ? <MediaDetailMobile id={openId} onBack={back} setBdImage={setBd} />
            : route === "catalog"  ? <CatalogMobile onOpen={open} setBdImage={setBd} />
            : route === "torrents" ? <TorrentsMobile />
            : route === "more"     ? <MoreMobile onOpen={open} />
            : <CatalogMobile onOpen={open} setBdImage={setBd} />}

          {/* tab bar (always visible) — hidden when inside a detail */}
          {!openId && <TabBar route={route} onRoute={go} />}
        </div>
      </window.IOSDevice>
    </div>
  );
};

ReactDOM.createRoot(document.getElementById("root")).render(<MobileApp />);
