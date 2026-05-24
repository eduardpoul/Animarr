// ====== screens v3 — full-width edition ======
// All content uses the full viewport width. Episode "ON DISK / MISSING"
// pills replaced with a status icon + left edge strip. Filter bar uses
// fixed-height controls so the search input and tab segment line up.
const { useState: useStateV3, useEffect: useEffectV3, useMemo: useMemoV3, useRef: useRefV3 } = React;

const SIDE_PAD = 40;
const CTRL_H = 36; // unified height for filter controls

// Reuse v1 building blocks
const { Icon: IconV, Logo: LogoV, Sidebar: SidebarV, Backdrop: BackdropV,
        Poster: PosterV, Btn: BtnV, Pill: PillV, Toggle: ToggleV,
        Field: FieldV, Input: InputV, Select: SelectV,
        PageHeader: PageHeaderV } = window;

// ============================================================
// WIDE PAGE — no max-width container, just side padding
// ============================================================
const WidePage = ({ children, top = false }) => (
  <div style={{
    padding: top ? `38px ${SIDE_PAD}px 0` : `0 ${SIDE_PAD}px`,
  }}>{children}</div>
);

// ============================================================
// CATALOG (v3) — wide grid, hero full-bleed
// ============================================================
const CatalogV3 = ({ onOpen, setBdImage, rotateSec = 18 }) => {
  const init = window.__init || {};
  const [filter, setFilter] = useStateV3("All");
  const [folderFilter, setFolderFilter] = useStateV3("All");
  const [query, setQuery]   = useStateV3("");
  const [heroIdx, setHeroIdx] = useStateV3(0);
  const [nrOpen, setNrOpen] = useStateV3(!!init.openNRModal);

  const featured = useMemoV3(() => window.LIBRARY.filter(i => i.rating >= 8.0).slice(0, 5), []);
  useEffectV3(() => {
    const t = setInterval(() => setHeroIdx(i => (i + 1) % featured.length), (rotateSec || 18) * 1000);
    return () => clearInterval(t);
  }, [featured.length, rotateSec]);
  const hero = featured[heroIdx] || window.LIBRARY[0];

  // Hero cross-fade
  const [bgLayers, setBgLayers] = useStateV3([{ url: hero.bd, key: 0, on: true }]);
  const counter = useRefV3(0);
  useEffectV3(() => {
    setBgLayers(prev => {
      if (prev[prev.length-1]?.url === hero.bd) return prev;
      counter.current += 1;
      const k = counter.current;
      const next = [...prev.slice(-2), { url: hero.bd, key: k, on: false }];
      requestAnimationFrame(() => {
        setBgLayers(p => p.map(l => l.key === k ? { ...l, on: true } : l));
      });
      return next;
    });
    setBdImage?.(hero.bd, hero.hue);
  }, [hero.bd, hero.hue, setBdImage]);

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
    <div>
      {/* HERO — full-bleed, 70vh */}
      <div style={{
        position:"relative", height: "70vh", minHeight: 620, overflow:"hidden",
      }}>
        {bgLayers.map(l => (
          <div key={l.key} style={{
            position:"absolute", inset: 0,
            backgroundImage: `url("${l.url}")`,
            backgroundSize:"cover", backgroundPosition:"center",
            opacity: l.on ? 1 : 0,
            transition: "opacity 1.4s ease",
            animation: "hero-pan-v3 28s ease-in-out infinite alternate",
          }} />
        ))}
        {/* gradients */}
        <div style={{
          position:"absolute", inset: 0,
          background:`linear-gradient(90deg, oklch(0.10 0.04 ${hero.hue} / 0.78) 0%, oklch(0.10 0.04 ${hero.hue} / 0.30) 38%, transparent 70%)`,
          transition:"background 1.4s ease",
        }} />
        <div style={{ position:"absolute", inset: 0, background:"linear-gradient(0deg, rgba(0,0,0,0.55), transparent 50%)" }} />
        <div style={{
          position:"absolute", inset: 0,
          background:`radial-gradient(70% 100% at 0% 100%, oklch(0.45 0.18 ${hero.hue} / 0.28), transparent 70%)`,
          mixBlendMode:"screen", transition:"background 1.4s ease",
        }} />

        {/* CJK watermark */}
        <div key={hero.id+"-cjk"} style={{
          position:"absolute", right:"4%", top:"6%",
          fontFamily:"var(--font-cjk)", fontSize: 360, lineHeight: 0.85,
          color:`oklch(0.95 0.1 ${hero.hue} / 0.08)`,
          writingMode:"vertical-rl", textOrientation:"upright", letterSpacing: 6,
          userSelect:"none", pointerEvents:"none",
          animation: "hero-cjk-in-v3 1.4s ease forwards",
        }}>{hero.cjk}</div>

        {/* content */}
        <div style={{
          position:"absolute", left: SIDE_PAD, right: SIDE_PAD, bottom: 60,
          display:"flex", alignItems:"flex-end", gap: 40,
        }}>
          <div key={hero.id+"-content"} style={{ flex: 1, maxWidth: 900, animation:"hero-content-in-v3 .8s ease" }}>
            <div style={{ display:"flex", alignItems:"center", gap: 10, marginBottom: 16 }}>
              <PillV tone="accent">FEATURED</PillV>
              <PillV>{hero.type.toUpperCase()}</PillV>
              {hero.tags?.slice(0,2).map(t => <PillV key={t}>{t}</PillV>)}
            </div>
            <h2 style={{
              margin: 0, fontFamily:"var(--font-display)",
              fontSize: 120, lineHeight: 0.86, letterSpacing: -3.8,
              color:"#fff", textShadow:"0 4px 30px rgba(0,0,0,0.6)",
            }}>{hero.title.toUpperCase()}</h2>
            <div style={{
              display:"flex", alignItems:"center", gap: 22, marginTop: 20,
              fontFamily:"var(--font-mono)", fontSize: 12, letterSpacing: 1, color:"var(--text-dim)",
            }}>
              <span style={{ color:"var(--accent-hi)", fontWeight: 700 }}>★ {hero.rating.toFixed(1)}</span>
              <span>{hero.year}</span>
              <span>{hero.episodes} EP</span>
              <span>{hero.studio}</span>
              <span>{hero.lang}</span>
            </div>
            <p style={{
              color:"#e7dfd7", marginTop: 18, fontSize: 16, lineHeight: 1.55,
              textWrap:"pretty", maxWidth: 720,
              textShadow:"0 2px 10px rgba(0,0,0,0.55)",
            }}>{hero.overview}</p>
            <div style={{ display:"flex", gap: 10, marginTop: 24 }}>
              <BtnV kind="primary" icon="play" onClick={() => onOpen(hero.id)}>Open detail</BtnV>
              <BtnV kind="solid" icon="external">TMDB</BtnV>
              <BtnV kind="solid" icon="external">MAL</BtnV>
            </div>
          </div>

          {/* featured pager - right side */}
          <div style={{ display:"flex", flexDirection:"column", gap: 6, paddingBottom: 12 }}>
            {featured.map((f, i) => (
              <button key={f.id} onClick={() => setHeroIdx(i)} style={{
                all:"unset", cursor:"pointer",
                fontFamily:"var(--font-mono)", fontSize: 10,
                color: i === heroIdx ? "var(--accent-hi)" : "var(--text-faint)",
                padding:"6px 0", display:"flex", alignItems:"center", gap:8,
              }}>
                <span style={{
                  width: i === heroIdx ? 28 : 14, height: 2,
                  background: i === heroIdx ? "var(--accent)" : "rgba(255,255,255,0.18)",
                  transition:"width .25s ease",
                }} />
                {String(i+1).padStart(2,"0")}
              </button>
            ))}
          </div>
        </div>
      </div>

      {/* FILTER BAR + GRID */}
      <div style={{ padding: `36px ${SIDE_PAD}px 80px` }}>
        <div style={{ display:"flex", alignItems:"center", gap: 12, flexWrap:"wrap", marginBottom: 14 }}>
          <FilterBar tab={filter} setTab={setFilter} query={query} setQuery={setQuery} count={items.length} total={window.LIBRARY.length} />
          <BtnV kind="ghost" icon="refresh">Rescan all</BtnV>
          <NRBadgeV3 count={window.NEEDS_REVIEW.length} onClick={() => setNrOpen(true)} />
        </div>

        {/* folder chips */}
        <div style={{ display:"flex", gap: 8, marginBottom: 30, flexWrap:"wrap", alignItems:"center" }}>
          <span style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)", letterSpacing: 1, marginRight: 4 }}>FOLDER</span>
          {["All", ...window.FOLDERS.map(f => f.title)].map(name => {
            const active = folderFilter === name;
            return (
              <button key={name} onClick={() => setFolderFilter(name)} style={{
                all:"unset", cursor:"pointer",
                padding:"5px 11px", borderRadius: 999,
                fontSize: 11.5, fontWeight: 600, letterSpacing: 0.2,
                color: active ? "#fff" : "var(--text-dim)",
                background: active ? "var(--accent)" : "var(--surface)",
                border: `1px solid ${active ? "var(--accent)" : "var(--border)"}`,
              }}>{name}</button>
            );
          })}
        </div>

        <div style={{
          display:"grid",
          gridTemplateColumns:"repeat(auto-fill, minmax(220px, 1fr))",
          gap: 22,
        }}>
          {items.map(i => (
            <PosterV key={i.id} item={i} w={"100%"} h={325} onClick={() => onOpen(i.id)} />
          ))}
        </div>
      </div>

      {nrOpen && window.NRModal && <window.NRModal onClose={() => setNrOpen(false)} />}

      <style>{`
        @keyframes hero-pan-v3 {
          0%   { transform: scale(1.04) translate(-1%, -1%); }
          100% { transform: scale(1.10) translate(1.5%, 1.5%); }
        }
        @keyframes hero-cjk-in-v3 {
          0%   { opacity: 0; transform: translateY(-12px); }
          100% { opacity: 1; transform: translateY(0); }
        }
        @keyframes hero-content-in-v3 {
          0%   { opacity: 0; transform: translateY(10px); }
          100% { opacity: 1; transform: translateY(0); }
        }
      `}</style>
    </div>
  );
};

// Filter bar with unified-height controls. The tab segment outer padding +
// inner button padding used to overshoot the search input by ~8px; now both
// rows are explicit height: CTRL_H (36px) so they align perfectly.
const FilterBar = ({ tab, setTab, query, setQuery, count, total }) => (
  <div style={{ display:"flex", alignItems:"center", gap: 14, flexWrap:"wrap" }}>
    {/* tabs */}
    <div style={{
      display:"flex", gap: 2, padding: 3,
      background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 10,
      height: CTRL_H, boxSizing:"border-box",
    }}>
      {["All","Anime","Movie","Series","Multserials"].map(t => (
        <button key={t} onClick={() => setTab(t)} style={{
          all:"unset", cursor:"pointer",
          padding:"0 14px", borderRadius: 7, fontSize: 12.5, fontWeight: 600,
          color: tab === t ? "var(--text)" : "var(--text-dim)",
          background: tab === t ? "var(--surface-3)" : "transparent",
          boxShadow: tab === t ? "0 1px 0 var(--accent) inset" : "none",
          display:"inline-flex", alignItems:"center",
        }}>{t}</button>
      ))}
    </div>

    {/* search */}
    <div style={{
      display:"flex", alignItems:"center", gap: 8,
      background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 10,
      padding:"0 14px", maxWidth: 360, width: 280,
      height: CTRL_H, boxSizing:"border-box",
    }}>
      <IconV name="search" size={14} />
      <input
        value={query}
        onChange={e => setQuery(e.target.value)}
        placeholder="Filter by title…"
        style={{ all:"unset", flex: 1, color:"var(--text)", fontSize: 13 }}
      />
      {query && (
        <button onClick={() => setQuery("")} style={{ all:"unset", cursor:"pointer", color:"var(--text-faint)" }}>
          <IconV name="x" size={12} />
        </button>
      )}
    </div>

    <div style={{ flex: 1 }} />
    <div style={{ fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-faint)", letterSpacing: 0.6 }}>
      {count} / {total} TITLES
    </div>
  </div>
);

// NeedsReview badge for v3's filter row — same visual as v1 but height CTRL_H.
const NRBadgeV3 = ({ count, onClick }) => {
  if (count === 0) return null;
  return (
    <button onClick={onClick} title={`${count} need review`} style={{
      all:"unset", cursor:"pointer", position:"relative",
      height: CTRL_H, padding:"0 14px 0 12px", borderRadius: 10,
      background:"oklch(0.80 0.17 75 / 0.16)",
      border:"1px solid oklch(0.60 0.16 75 / 0.4)",
      color:"var(--warn)",
      display:"inline-flex", alignItems:"center", gap: 8,
      fontFamily:"var(--font-mono)", fontSize: 11, fontWeight: 700, letterSpacing: 0.6,
    }}>
      <IconV name="warn" size={14} />
      NEEDS REVIEW · {count}
      <span style={{
        position:"absolute", top: -4, right: -4,
        width: 8, height: 8, borderRadius: 8,
        background:"var(--warn)", boxShadow:"0 0 8px var(--warn)",
        animation:"nr-pulse-v3 1.6s ease-in-out infinite",
      }} />
      <style>{`@keyframes nr-pulse-v3 { 0%,100%{opacity:1;} 50%{opacity:0.4;} }`}</style>
    </button>
  );
};

// ============================================================
// MEDIA DETAIL (v3) — 3-column body, full-width
// ============================================================
const MediaDetailV3 = ({ id, onBack, setBdImage }) => {
  const init = window.__init || {};
  const item = window.LIBRARY.find(x => x.id === id) || window.LIBRARY[0];
  const [season, setSeason] = useStateV3(1);
  const [editing, setEditing] = useStateV3(!!init.openEditDrawer);
  const seasons = item.type === "Anime" || item.type === "Series" ? [1, 2] : [];
  const totalEps = item.episodes || 12;
  const eps = useMemoV3(() => Array.from({ length: Math.min(totalEps, 24) }, (_, i) => ({
    n: i + 1,
    title: `Episode ${i+1}`,
    have: Math.random() > 0.18 || i < 12,
    runtime: item.runtime,
  })), [item.id]);

  useEffectV3(() => { setBdImage?.(item.bd, item.hue); }, [item.bd, item.hue, setBdImage]);

  return (
    <div>
      {/* HERO — full-bleed with poster + title in single row at the bottom */}
      <div style={{ position:"relative", height: "68vh", minHeight: 580, overflow:"hidden" }}>
        <div key={item.bd} style={{
          position:"absolute", inset: 0,
          backgroundImage:`url("${item.bd}")`, backgroundSize:"cover", backgroundPosition:"center",
          animation:"detail-pan-v3 28s ease-in-out infinite alternate",
        }} />
        <div style={{
          position:"absolute", inset: 0,
          background:`linear-gradient(180deg, transparent 30%, rgba(10,8,7,0.85) 80%, var(--bg-0) 100%), linear-gradient(90deg, rgba(10,8,7,0.55) 0%, transparent 60%)`,
        }} />
        <div style={{
          position:"absolute", right:"4%", top: 40, fontFamily:"var(--font-cjk)",
          fontSize: 320, lineHeight: 0.85, color: `oklch(0.95 0.05 ${item.hue} / 0.07)`,
          writingMode:"vertical-rl", textOrientation:"upright", letterSpacing: 6,
          pointerEvents:"none", userSelect:"none",
        }}>{item.cjk}</div>

        {/* BACK button overlay */}
        <button onClick={onBack} style={{
          all:"unset", cursor:"pointer", position:"absolute",
          top: 24, left: SIDE_PAD, zIndex: 5,
          display:"inline-flex", alignItems:"center", gap: 8,
          padding:"9px 14px", borderRadius: 8,
          background:"rgba(10,8,7,0.55)", backdropFilter:"blur(10px)",
          border:"1px solid rgba(255,255,255,0.12)",
          color:"var(--text)", fontSize: 12.5, fontWeight: 600,
          fontFamily:"var(--font-mono)", letterSpacing: 0.6,
          transition:"background .15s ease, border-color .15s ease",
        }}
        onMouseEnter={e => { e.currentTarget.style.background = "var(--accent)"; e.currentTarget.style.borderColor = "var(--accent)"; }}
        onMouseLeave={e => { e.currentTarget.style.background = "rgba(10,8,7,0.55)"; e.currentTarget.style.borderColor = "rgba(255,255,255,0.12)"; }}>
          <IconV name="chev-l" size={14} /> BACK
        </button>

        {/* EDIT icon button — top right of hero */}
        <button onClick={() => setEditing(true)} title="Edit metadata" style={{
          all:"unset", cursor:"pointer", position:"absolute",
          top: 24, right: SIDE_PAD, zIndex: 5,
          display:"inline-flex", alignItems:"center", gap: 8,
          padding:"9px 14px", borderRadius: 8,
          background:"rgba(10,8,7,0.55)", backdropFilter:"blur(10px)",
          border:"1px solid rgba(255,255,255,0.12)",
          color:"var(--text)", fontSize: 12.5, fontWeight: 600,
          fontFamily:"var(--font-mono)", letterSpacing: 0.6,
          transition:"background .15s ease, border-color .15s ease",
        }}
        onMouseEnter={e => { e.currentTarget.style.background = "var(--accent)"; e.currentTarget.style.borderColor = "var(--accent)"; }}
        onMouseLeave={e => { e.currentTarget.style.background = "rgba(10,8,7,0.55)"; e.currentTarget.style.borderColor = "rgba(255,255,255,0.12)"; }}>
          <IconV name="pencil" size={13} /> EDIT METADATA
        </button>

        {/* hero bottom row */}
        <div style={{
          position:"absolute", left: SIDE_PAD, right: SIDE_PAD, bottom: 90,
          display:"flex", alignItems:"flex-end", gap: 38,
        }}>
          <div style={{ width: 260, flexShrink: 0 }}>
            <PosterV item={item} w={260} h={380} ribbon={false} />
          </div>
          <div style={{ flex: 1, paddingBottom: 8 }}>
            <div style={{ display:"flex", gap: 8, marginBottom: 14, flexWrap:"wrap" }}>
              <PillV tone="accent">{item.type.toUpperCase()}</PillV>
              {item.tags?.map(t => <PillV key={t}>{t}</PillV>)}
            </div>
            <h1 style={{
              margin: 0, fontFamily:"var(--font-display)",
              fontSize: 96, lineHeight: 0.88, letterSpacing: -3, color:"#fff",
              textShadow:"0 4px 30px rgba(0,0,0,0.6)",
              maxWidth: 1400,
            }}>{item.title.toUpperCase()}</h1>
            <div style={{
              display:"flex", flexWrap:"wrap", gap: 24, marginTop: 18,
              fontFamily:"var(--font-mono)", fontSize: 12, color:"var(--text-dim)", letterSpacing: 0.8,
            }}>
              <span style={{ color:"var(--accent-hi)", fontWeight: 700, fontSize: 13 }}>★ {item.rating.toFixed(1)}</span>
              <span>{item.year}</span>
              <span>{item.studio}</span>
              <span>{item.runtime} / ep</span>
              <span>{item.episodes} episodes</span>
              <span>{item.lang}</span>
              <span style={{ color: item.conf > 0.9 ? "var(--success)" : "var(--warn)" }}>
                {Math.round(item.conf*100)}% MATCH
              </span>
            </div>
          </div>
        </div>
      </div>

      {/* 3-COLUMN BODY — uses full width */}
      <div style={{
        padding: `60px ${SIDE_PAD}px 20px`,
        display:"grid",
        gridTemplateColumns: "minmax(0, 2.2fr) minmax(0, 1fr) minmax(280px, 360px)",
        gap: 48,
        alignItems:"start",
        marginBottom: 50,
      }}>
        <div>
          <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color:"var(--accent-hi)", marginBottom: 12 }}>SYNOPSIS</div>
          <p style={{ margin: 0, fontSize: 16, lineHeight: 1.65, color:"var(--text)", textWrap:"pretty" }}>{item.overview}</p>
          <div style={{ display:"flex", gap: 10, marginTop: 26, flexWrap:"wrap" }}>
            <BtnV kind="primary" icon="play">Play first episode</BtnV>
            <BtnV kind="solid" icon="pencil" onClick={() => setEditing(true)}>Edit metadata</BtnV>
            <BtnV kind="ghost" icon="magic">Re-identify</BtnV>
          </div>
        </div>

        <div>
          <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color:"var(--accent-hi)", marginBottom: 12 }}>DETAILS</div>
          <div style={{ display:"grid", gridTemplateColumns: "auto 1fr", gap: "10px 18px", fontSize: 13, fontFamily:"var(--font-mono)" }}>
            <span style={{ color:"var(--text-faint)" }}>Studio</span><span style={{ color:"var(--text)" }}>{item.studio}</span>
            <span style={{ color:"var(--text-faint)" }}>Language</span><span style={{ color:"var(--text)" }}>{item.lang}</span>
            <span style={{ color:"var(--text-faint)" }}>Runtime</span><span style={{ color:"var(--text)" }}>{item.runtime}</span>
            <span style={{ color:"var(--text-faint)" }}>Episodes</span><span style={{ color:"var(--text)" }}>{item.episodes} · {seasons.length || 1} season{seasons.length>1?"s":""}</span>
            <span style={{ color:"var(--text-faint)" }}>Tags</span>
            <span style={{ color:"var(--text)", display:"flex", flexWrap:"wrap", gap: 4 }}>
              {item.tags?.map(t => <span key={t} style={{ color:"var(--text-dim)" }}>{t}</span>).reduce((p, c, i) => i === 0 ? [c] : [...p, " · ", c], [])}
            </span>
          </div>

          <div style={{ marginTop: 22, display:"flex", gap: 8, flexWrap:"wrap" }}>
            <BtnV kind="solid" icon="external">TMDB</BtnV>
            <BtnV kind="solid" icon="external">MAL</BtnV>
            <BtnV kind="solid" icon="external">IMDb</BtnV>
          </div>
        </div>

        <div style={{
          background:"var(--surface)", border:"1px solid var(--border)",
          borderRadius: 12, padding: 18,
        }}>
          <div style={{ fontFamily:"var(--font-mono)", fontSize: 10, letterSpacing: 1.2, color:"var(--text-faint)", marginBottom: 12 }}>IDENTIFICATION</div>
          {[
            ["TMDB",  item.id + "-tmdb-id",  0.99],
            ["MAL",   item.id + "-mal-id",   0.95],
            ["IMDb",  item.id + "-imdb-id",  item.conf],
          ].map(([src, idv, c]) => (
            <div key={src} style={{
              display:"flex", alignItems:"center", gap: 10, padding: "8px 0",
              borderBottom: "1px dashed var(--border)",
            }}>
              <span style={{ width: 38, fontFamily:"var(--font-mono)", fontSize: 10, letterSpacing: 0.8, color:"var(--text-dim)" }}>{src}</span>
              <span style={{ flex: 1, fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-faint)", overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap" }}>{idv}</span>
              <span style={{
                fontFamily:"var(--font-mono)", fontSize: 10.5,
                color: c > 0.9 ? "var(--success)" : c > 0.7 ? "var(--warn)" : "var(--text-faint)",
              }}>{Math.round(c*100)}%</span>
            </div>
          ))}
          <div style={{ marginTop: 14, fontFamily:"var(--font-mono)", fontSize: 10, letterSpacing: 1.2, color:"var(--text-faint)", marginBottom: 8 }}>ON DISK</div>
          <div style={{ fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-dim)", lineHeight: 1.6, wordBreak:"break-all" }}>
            /Pool-D1/Media/{item.type}/{item.title}
          </div>
        </div>
      </div>

      {/* EPISODES — full-width with redesigned cards */}
      {seasons.length > 0 && (
        <div style={{ padding: `30px ${SIDE_PAD}px 60px` }}>
          <div style={{ display:"flex", alignItems:"center", justifyContent:"space-between", marginBottom: 26 }}>
            <div>
              <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color:"var(--accent-hi)" }}>EPISODES</div>
              <h2 style={{ margin: "6px 0 0", fontFamily:"var(--font-display)", fontSize: 44, letterSpacing:-1 }}>
                SEASON {String(season).padStart(2,"0")}
                <span style={{ fontSize: 18, color:"var(--text-faint)", marginLeft: 14, letterSpacing: 0 }}>
                  {eps.filter(e=>e.have).length}/{eps.length} on disk
                </span>
              </h2>
            </div>
            <div style={{ display:"flex", gap: 6 }}>
              {seasons.map(s => (
                <button key={s} onClick={() => setSeason(s)} style={{
                  all:"unset", cursor:"pointer",
                  padding:"10px 18px", borderRadius: 8,
                  fontFamily:"var(--font-mono)", fontSize: 11.5, letterSpacing: 0.8,
                  background: s === season ? "var(--accent-soft)" : "var(--surface)",
                  color: s === season ? "var(--accent-hi)" : "var(--text-dim)",
                  border: s === season ? "1px solid var(--accent-line)" : "1px solid var(--border)",
                }}>SEASON {String(s).padStart(2,"0")}</button>
              ))}
            </div>
          </div>

          <div style={{
            display:"grid",
            gridTemplateColumns:"repeat(auto-fill, minmax(260px, 1fr))",
            gap: 16,
          }}>
            {eps.map(ep => <EpisodeCardV3 key={ep.n} ep={ep} item={item} />)}
          </div>
        </div>
      )}

      <style>{`
        @keyframes detail-pan-v3 {
          0%   { transform: scale(1.04) translate(-1.5%, -1%); }
          100% { transform: scale(1.10) translate(1.5%, 1%); }
        }
      `}</style>

      {/* Edit metadata drawer */}
      {editing && <EditMetadataDrawer item={item} onClose={() => setEditing(false)} initialTab={init.editDrawerTab || "ids"} />}
    </div>
  );
};

// Episode card — status edge strip + icon chip. Missing episodes are
// visually muted (lower opacity, grayscale, dotted border) and on hover
// show an "empty / not downloaded" indicator rather than the play button.
const EpisodeCardV3 = ({ ep, item }) => {
  const have = ep.have;
  const stripColor = have ? "var(--success)" : "var(--warn)";
  const [hover, setHover] = useStateV3(false);
  return (
    <div
      onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)}
      style={{
        position:"relative",
        background: have ? "var(--surface)" : "rgba(21,17,14,0.55)",
        border: have ? "1px solid var(--border)" : "1px dashed rgba(255,240,220,0.12)",
        borderRadius: 10,
        overflow:"hidden",
        opacity: have ? 1 : 0.50,
        cursor: have ? "pointer" : "default",
        transform: hover && have ? "translateY(-2px)" : "none",
        borderColor: hover && have ? "var(--accent-line)" : (have ? "var(--border)" : "rgba(255,240,220,0.12)"),
        transition:"transform .2s ease, border-color .2s ease, opacity .2s ease",
      }}
    >
      {/* status edge strip */}
      <div style={{
        position:"absolute", left: 0, top: 0, bottom: 0, width: 3,
        background: stripColor,
        boxShadow: have ? "0 0 12px var(--success)" : "none",
        zIndex: 2,
      }} />

      {/* thumbnail */}
      <div style={{
        height: 150, position:"relative",
        backgroundImage:`url("${item.bd}")`,
        backgroundSize:"cover",
        backgroundPosition: `${(ep.n * 19) % 100}% center`,
        filter: have ? "none" : "grayscale(1) brightness(0.55)",
      }}>
        <div style={{
          position:"absolute", inset: 0,
          background: "linear-gradient(180deg, transparent 30%, rgba(0,0,0,0.78) 100%)",
        }} />
        <div style={{
          position:"absolute", top: 12, left: 14,
          fontFamily:"var(--font-display)", fontSize: 36, color: have ? "#fff" : "rgba(255,255,255,0.55)",
          textShadow: "0 2px 8px rgba(0,0,0,0.7)", lineHeight: 1,
        }}>{String(ep.n).padStart(2,"0")}</div>

        {/* status icon chip */}
        <div title={have ? "On disk" : "Missing — not downloaded"} style={{
          position:"absolute", top: 10, right: 10,
          width: 26, height: 26, borderRadius: 6,
          background: have ? "oklch(0.74 0.15 150 / 0.18)" : "oklch(0.80 0.17 75 / 0.20)",
          border: `1px solid ${stripColor}`,
          display:"grid", placeItems:"center",
          color: stripColor,
          backdropFilter:"blur(6px)",
        }}>
          <IconV name={have ? "check" : "warn"} size={13} stroke={2.4} />
        </div>

        {/* hover overlay — play if on disk, empty/download hint if missing */}
        {hover && (
          have ? (
            <div style={{
              position:"absolute", left:"50%", top:"50%", transform:"translate(-50%, -50%)",
              width: 50, height: 50, borderRadius: 50,
              background:"rgba(0,0,0,0.5)", backdropFilter:"blur(8px)",
              display:"grid", placeItems:"center",
              border:"1px solid rgba(255,255,255,0.3)",
            }}>
              <IconV name="play" size={20} style={{ color:"#fff", marginLeft: 2 }} />
            </div>
          ) : (
            <div style={{
              position:"absolute", left:"50%", top:"50%", transform:"translate(-50%, -50%)",
              width: 56, height: 56, borderRadius: 12,
              background:"rgba(20,15,12,0.7)", backdropFilter:"blur(8px)",
              display:"flex", flexDirection:"column", alignItems:"center", justifyContent:"center",
              border:"1px dashed rgba(255,240,220,0.25)",
              color:"var(--warn)",
              fontFamily:"var(--font-mono)", fontSize: 9, letterSpacing: 1, textTransform:"uppercase",
              gap: 3,
            }}>
              <IconV name="download" size={16} stroke={1.8} />
              <span>Empty</span>
            </div>
          )
        )}
      </div>

      <div style={{ padding: "12px 14px 14px 17px" }}>
        <div style={{ fontSize: 14, fontWeight: 600, color: have ? "var(--text)" : "var(--text-dim)" }}>{ep.title}</div>
        <div style={{
          fontFamily:"var(--font-mono)", fontSize: 10.5,
          color:"var(--text-faint)", marginTop: 4,
          display:"flex", alignItems:"center", gap: 8,
        }}>
          <span>{ep.runtime}</span>
          {have ? (
            <>
              <span>·</span>
              <span>1080p</span>
              <span>·</span>
              <span>H.265</span>
              <span style={{ marginLeft:"auto" }} />
              <span style={{ color: "var(--text-dim)" }}>{(Math.random()*0.4 + 0.5).toFixed(2)} GB</span>
            </>
          ) : (
            <>
              <span>·</span>
              <span style={{ color:"var(--warn)" }}>Not downloaded</span>
            </>
          )}
        </div>
      </div>
    </div>
  );
};

// ============================================================
// EDIT METADATA — slide-out drawer
// ============================================================
// Lets the user manually correct what the LLM/TMDB pipeline got wrong:
// override the source IDs (TMDB / MAL / IMDb / AniDB), pick a different
// poster or backdrop from the source's gallery, edit basic info and tags.
// Apply rewrites SQLite metadata only — no folders on disk are renamed.
const EditMetadataDrawer = ({ item, onClose, initialTab = "ids" }) => {
  const [tab, setTab] = useStateV3(initialTab);
  const [selPoster, setSelPoster] = useStateV3(0);
  const [selBd, setSelBd] = useStateV3(Object.values(window.BD).indexOf(item.bd));
  const [tags, setTags] = useStateV3(item.tags || []);
  const [tagInput, setTagInput] = useStateV3("");

  // Generate 10 mock poster candidates by tweaking the hue around the base.
  const posterCandidates = useMemoV3(() => {
    const base = item.hue;
    const variants = [0, +30, -30, +60, -60, +90, +120, +150, +180, +210];
    return variants.map((d, i) => ({
      id: `${item.id}-c${i}`,
      title: item.title,
      cjk: item.cjk,
      year: item.year,
      type: item.type,
      hue: (base + d + 360) % 360,
      bd: Object.values(window.BD)[i % Object.values(window.BD).length],
      source: ["TMDB","TMDB","MAL","TMDB","MAL","IMDb","TMDB","TMDB","MAL","Local"][i],
    }));
  }, [item.id, item.hue]);

  const bdCandidates = useMemoV3(() => Object.values(window.BD), []);

  const addTag = () => {
    const t = tagInput.trim();
    if (!t || tags.includes(t)) return;
    setTags([...tags, t]);
    setTagInput("");
  };

  return (
    <>
      {/* dim overlay */}
      <div onClick={onClose} style={{
        position:"fixed", inset: 0, background:"rgba(0,0,0,0.55)", backdropFilter:"blur(3px)",
        zIndex: 30,
      }} />

      {/* drawer */}
      <div style={{
        position:"fixed", top: 0, right: 0, bottom: 0, width: 560,
        background: "linear-gradient(180deg, var(--surface) 0%, var(--surface-2) 100%)",
        borderLeft:"1px solid var(--border-strong)",
        zIndex: 31, display:"flex", flexDirection:"column",
        boxShadow:"-30px 0 60px -20px rgba(0,0,0,0.6)",
        animation:"drawer-in .3s ease",
      }}>
        {/* header */}
        <div style={{
          padding: "22px 26px 18px",
          borderBottom:"1px solid var(--border)",
          display:"flex", alignItems:"flex-start", gap: 14,
        }}>
          <div style={{ width: 44, height: 44, borderRadius: 8, display:"grid", placeItems:"center",
            background:"var(--accent-soft)", color:"var(--accent-hi)", border:"1px solid var(--accent-line)" }}>
            <IconV name="pencil" size={18} />
          </div>
          <div style={{ flex: 1 }}>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--accent-hi)", letterSpacing: 1.4 }}>EDIT METADATA</div>
            <div style={{ fontFamily:"var(--font-display)", fontSize: 22, letterSpacing:-0.4, marginTop: 4, lineHeight: 1.1 }}>{item.title}</div>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-faint)", marginTop: 4 }}>
              SQLite metadata only — folders on disk stay untouched
            </div>
          </div>
          <button onClick={onClose} style={{ all:"unset", cursor:"pointer", color:"var(--text-dim)", padding: 4 }}>
            <IconV name="x" size={20} />
          </button>
        </div>

        {/* tabs */}
        <div style={{
          display:"flex", gap: 2, padding: "10px 26px 0",
          borderBottom:"1px solid var(--border)",
        }}>
          {[
            ["ids",     "Source IDs"],
            ["basics",  "Basics"],
            ["poster",  "Poster"],
            ["backdrop","Backdrop"],
            ["tags",    "Tags"],
            ["manage",  "Manage"],
          ].map(([k, l]) => (
            <button key={k} onClick={() => setTab(k)} style={{
              all:"unset", cursor:"pointer",
              padding:"10px 14px", fontSize: 12.5, fontWeight: 600,
              color: tab === k ? "var(--text)" : "var(--text-dim)",
              borderBottom: tab === k ? "2px solid var(--accent)" : "2px solid transparent",
              marginBottom: -1,
            }}>{l}</button>
          ))}
        </div>

        {/* body */}
        <div style={{ flex: 1, overflow:"auto", padding: "22px 26px 130px" }}>
          {tab === "ids"      && <DrawerIds item={item} />}
          {tab === "basics"   && <DrawerBasics item={item} />}
          {tab === "poster"   && <DrawerPosters candidates={posterCandidates} sel={selPoster} onSel={setSelPoster} />}
          {tab === "backdrop" && <DrawerBackdrops candidates={bdCandidates} sel={selBd} onSel={setSelBd} />}
          {tab === "tags"     && <DrawerTags tags={tags} setTags={setTags} input={tagInput} setInput={setTagInput} addTag={addTag} />}
          {tab === "manage"   && <DrawerManage item={item} />}
        </div>

        {/* footer */}
        <div style={{
          position:"absolute", left: 0, right: 0, bottom: 0,
          padding:"18px 26px",
          background:"linear-gradient(180deg, transparent, var(--surface-2) 30%)",
          borderTop:"1px solid var(--border)",
          display:"flex", gap: 10, alignItems:"center",
        }}>
          <BtnV kind="primary" icon="check">Save changes</BtnV>
          <BtnV kind="ghost" onClick={onClose}>Cancel</BtnV>
          <div style={{ flex: 1 }} />
          <BtnV kind="flat" icon="magic" style={{ fontSize: 11 }}>Re-run LLM</BtnV>
        </div>
      </div>

      <style>{`
        @keyframes drawer-in {
          0%   { transform: translateX(40px); opacity: 0; }
          100% { transform: translateX(0); opacity: 1; }
        }
      `}</style>
    </>
  );
};

// — Drawer tabs ——————————————————————————————————————————————

const DrawerIds = ({ item }) => (
  <div style={{ display:"flex", flexDirection:"column", gap: 18 }}>
    <div style={{
      background:"linear-gradient(135deg, var(--accent-soft), transparent 70%), var(--bg-1)",
      border:"1px solid var(--accent-line)", borderRadius: 10, padding: 14,
      display:"flex", alignItems:"center", gap: 12,
    }}>
      <div style={{ width: 30, height: 30, borderRadius: 6, background:"var(--accent)", display:"grid", placeItems:"center", color:"#fff" }}>
        <IconV name="sparkle" size={14} />
      </div>
      <div style={{ flex: 1 }}>
        <div style={{ fontSize: 13, fontWeight: 600 }}>Auto-match · {Math.round(item.conf*100)}%</div>
        <div style={{ fontSize: 11.5, color:"var(--text-dim)" }}>qwen2.5:1.5b normalized "[Anistar.org] {item.title}" → "{item.title}"</div>
      </div>
    </div>

    <DrawerIdField label="TMDB ID"  value={item.id + "-tmdb-id"} url="https://themoviedb.org/" />
    <DrawerIdField label="MAL ID"   value={item.id + "-mal-id"}  url="https://myanimelist.net/" />
    <DrawerIdField label="IMDb ID"  value={item.id + "-imdb-id"} url="https://imdb.com/" />
    <DrawerIdField label="AniDB ID" value=""                     url="https://anidb.net/" />

    <FieldV label="Or paste a source URL" hint="Animarr will parse out the ID automatically.">
      <InputV mono placeholder="https://www.themoviedb.org/tv/12345-…" />
    </FieldV>
  </div>
);

const DrawerIdField = ({ label, value, url }) => (
  <div>
    <div style={{ display:"flex", justifyContent:"space-between", marginBottom: 6 }}>
      <span style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)", letterSpacing: 1, textTransform:"uppercase" }}>{label}</span>
      {value && (
        <a href={url} target="_blank" rel="noreferrer" style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--accent-hi)", textDecoration:"none", display:"inline-flex", alignItems:"center", gap: 4 }}>
          <IconV name="external" size={10} /> open source
        </a>
      )}
    </div>
    <div style={{ display:"flex", gap: 6 }}>
      <InputV mono defaultValue={value} placeholder={value ? "" : "(not linked)"} style={{ flex: 1 }} />
      <BtnV kind="solid" style={{ padding:"7px 11px" }} icon="refresh">Search</BtnV>
    </div>
  </div>
);

const DrawerBasics = ({ item }) => (
  <div style={{ display:"grid", gridTemplateColumns:"1fr 1fr", gap: 16 }}>
    <div style={{ gridColumn: "1 / -1" }}>
      <FieldV label="Display title">
        <InputV defaultValue={item.title} />
      </FieldV>
    </div>
    <FieldV label="English title">
      <InputV defaultValue={item.title} />
    </FieldV>
    <FieldV label="Original (CJK)">
      <InputV defaultValue={item.cjk} style={{ fontFamily:"var(--font-cjk)" }} />
    </FieldV>
    <FieldV label="Year">
      <InputV mono defaultValue={item.year} />
    </FieldV>
    <FieldV label="Type">
      <SelectV defaultValue={item.type}>
        <option>Anime</option><option>Movie</option><option>Series</option><option>Multserials</option>
      </SelectV>
    </FieldV>
    <FieldV label="Language">
      <SelectV defaultValue={item.lang}>
        <option>Mandarin</option><option>Japanese</option><option>English</option>
        <option>Russian</option><option>Korean</option>
      </SelectV>
    </FieldV>
    <FieldV label="Studio">
      <InputV defaultValue={item.studio} />
    </FieldV>
    <div style={{ gridColumn: "1 / -1" }}>
      <FieldV label="Runtime per episode">
        <InputV mono defaultValue={item.runtime} />
      </FieldV>
    </div>
  </div>
);

const DrawerPosters = ({ candidates, sel, onSel }) => (
  <div>
    <div style={{ display:"flex", justifyContent:"space-between", alignItems:"center", marginBottom: 14 }}>
      <span style={{ fontSize: 12.5, color:"var(--text-dim)" }}>
        Pick a poster from the source galleries, or upload your own.
      </span>
      <BtnV kind="ghost" icon="upload" style={{ fontSize: 11 }}>Upload</BtnV>
    </div>
    <div style={{
      display:"grid", gridTemplateColumns:"repeat(auto-fill, minmax(140px, 1fr))",
      gap: 12,
    }}>
      {candidates.map((c, i) => (
        <button key={c.id} onClick={() => onSel(i)} style={{
          all:"unset", cursor:"pointer",
          position:"relative", borderRadius: 10, overflow:"visible",
          padding: 3,
          background: sel === i ? "var(--accent)" : "transparent",
          transition:"background .15s ease",
        }}>
          <div style={{ borderRadius: 8, overflow:"hidden", position:"relative" }}>
            <PosterV item={c} w={"100%"} h={205} ribbon={false} />
          </div>
          <div style={{
            position:"absolute", top: 10, right: 10,
            background: sel === i ? "var(--accent)" : "rgba(0,0,0,0.6)",
            color:"#fff", width: 22, height: 22, borderRadius: 22,
            display: sel === i ? "grid" : "none", placeItems:"center",
            border:"2px solid #fff",
          }}>
            <IconV name="check" size={11} stroke={3} />
          </div>
          <div style={{
            fontFamily:"var(--font-mono)", fontSize: 9.5, color:"var(--text-faint)",
            letterSpacing: 0.6, textAlign:"center", marginTop: 6,
          }}>{c.source}</div>
        </button>
      ))}
    </div>
  </div>
);

const DrawerBackdrops = ({ candidates, sel, onSel }) => (
  <div>
    <div style={{ display:"flex", justifyContent:"space-between", alignItems:"center", marginBottom: 14 }}>
      <span style={{ fontSize: 12.5, color:"var(--text-dim)" }}>
        Backdrop is shown behind hero and pages. Pick or upload.
      </span>
      <BtnV kind="ghost" icon="upload" style={{ fontSize: 11 }}>Upload</BtnV>
    </div>
    <div style={{
      display:"grid", gridTemplateColumns:"repeat(2, 1fr)",
      gap: 10,
    }}>
      {candidates.map((url, i) => (
        <button key={url} onClick={() => onSel(i)} style={{
          all:"unset", cursor:"pointer",
          position:"relative", height: 110, borderRadius: 8,
          backgroundImage:`url("${url}")`, backgroundSize:"cover", backgroundPosition:"center",
          border: sel === i ? "2px solid var(--accent)" : "2px solid transparent",
          boxShadow: sel === i ? "0 4px 18px var(--accent-soft)" : "none",
          transition:"border-color .15s ease",
          overflow:"hidden",
        }}>
          <div style={{
            position:"absolute", inset: 0,
            background:"linear-gradient(0deg, rgba(0,0,0,0.55), transparent 60%)",
          }} />
          {sel === i && (
            <div style={{
              position:"absolute", top: 8, right: 8,
              width: 22, height: 22, borderRadius: 22,
              background:"var(--accent)", color:"#fff",
              display:"grid", placeItems:"center",
              border:"2px solid #fff",
            }}>
              <IconV name="check" size={11} stroke={3} />
            </div>
          )}
          <div style={{
            position:"absolute", left: 8, bottom: 6,
            fontFamily:"var(--font-mono)", fontSize: 9, color:"#fff",
            letterSpacing: 0.6, textShadow:"0 1px 4px rgba(0,0,0,0.7)",
          }}>BD-{String(i+1).padStart(2,"0")} · TMDB</div>
        </button>
      ))}
    </div>
  </div>
);

// ── DrawerManage: rescan / apply renames / delete with safe-by-default ──
const DrawerManage = ({ item }) => {
  const [deleteFromDisk, setDeleteFromDisk] = useStateV3(false);
  const [confirmDelete, setConfirmDelete] = useStateV3(false);
  return (
    <div style={{ display:"flex", flexDirection:"column", gap: 18 }}>
      {/* file management actions */}
      <ManageBlock icon="refresh" tone="default"
        title="Rescan files"
        body="Re-read everything inside this title's folder and add new files to the watch queue. Existing matches stay.">
        <BtnV kind="solid" icon="refresh">Rescan now</BtnV>
        <BtnV kind="flat" icon="magic">Identify queue (LLM)</BtnV>
      </ManageBlock>

      <ManageBlock icon="check" tone="default"
        title="Apply pending renames"
        body="12 files in this folder match an active pattern. Animarr renames files on disk — never folders — and writes the action to history with revert.">
        <BtnV kind="primary" icon="check">Apply 12 renames</BtnV>
        <BtnV kind="flat" icon="info">Preview</BtnV>
      </ManageBlock>

      <ManageBlock icon="undo" tone="default"
        title="Revert all renames in this folder"
        body="Restore the original filenames for every entry in history attached to this folder.">
        <BtnV kind="ghost" icon="undo">Revert all</BtnV>
      </ManageBlock>

      {/* danger zone */}
      <div style={{
        border:"1px solid oklch(0.55 0.18 25 / 0.4)",
        borderRadius: 10, padding: 16, marginTop: 6,
        background:"oklch(0.55 0.18 25 / 0.08)",
      }}>
        <div style={{ display:"flex", alignItems:"center", gap: 10, marginBottom: 10 }}>
          <div style={{ width: 28, height: 28, borderRadius: 6, background:"oklch(0.55 0.18 25 / 0.25)", color:"var(--accent-hi)", display:"grid", placeItems:"center" }}>
            <IconV name="trash" size={13} />
          </div>
          <div style={{ flex: 1 }}>
            <div style={{ fontSize: 13.5, fontWeight: 700, color:"var(--accent-hi)" }}>DANGER ZONE</div>
            <div style={{ fontSize: 11.5, color:"var(--text-dim)" }}>Remove this title from Animarr. Files on disk stay by default.</div>
          </div>
        </div>

        <label style={{ display:"flex", alignItems:"center", gap: 10, padding: "8px 0", cursor:"pointer" }}>
          <ToggleV on={deleteFromDisk} onChange={setDeleteFromDisk} />
          <div style={{ flex: 1 }}>
            <div style={{ fontSize: 13, color:"var(--text)" }}>Also delete files from disk</div>
            <div style={{ fontSize: 11, color:"var(--text-faint)" }}>
              Permanently removes every video/subtitle inside <span style={{ fontFamily:"var(--font-mono)" }}>/Pool-D1/Media/{item.type}/{item.title}</span>. The folder itself stays.
            </div>
          </div>
        </label>

        {!confirmDelete ? (
          <button onClick={() => setConfirmDelete(true)} style={{
            all:"unset", cursor:"pointer", marginTop: 12,
            background:"transparent", color:"var(--accent-hi)",
            border:"1px solid oklch(0.55 0.18 25 / 0.5)", borderRadius: 8,
            padding:"8px 14px", fontSize: 12.5, fontWeight: 600,
            display:"inline-flex", alignItems:"center", gap: 7,
          }}>
            <IconV name="trash" size={13} />
            {deleteFromDisk ? "Remove + delete files" : "Remove from Animarr"}
          </button>
        ) : (
          <div style={{ marginTop: 12, display:"flex", gap: 8 }}>
            <button style={{
              all:"unset", cursor:"pointer",
              background:"oklch(0.55 0.18 25)", color:"#fff",
              border:"none", borderRadius: 8,
              padding:"8px 14px", fontSize: 12.5, fontWeight: 700,
              display:"inline-flex", alignItems:"center", gap: 7,
            }}>
              <IconV name="trash" size={13} /> Yes, {deleteFromDisk ? "delete everything" : "remove from library"}
            </button>
            <BtnV kind="ghost" onClick={() => setConfirmDelete(false)}>Cancel</BtnV>
          </div>
        )}
      </div>
    </div>
  );
};

const ManageBlock = ({ icon, title, body, children }) => (
  <div style={{
    background:"var(--bg-1)", border:"1px solid var(--border)",
    borderRadius: 10, padding: 14,
  }}>
    <div style={{ display:"flex", alignItems:"center", gap: 10, marginBottom: 10 }}>
      <div style={{ width: 28, height: 28, borderRadius: 6, background:"rgba(255,255,255,0.05)", color:"var(--text-dim)", display:"grid", placeItems:"center" }}>
        <IconV name={icon} size={13} />
      </div>
      <div style={{ flex: 1 }}>
        <div style={{ fontSize: 13.5, fontWeight: 600, color:"var(--text)" }}>{title}</div>
        <div style={{ fontSize: 11.5, color:"var(--text-dim)" }}>{body}</div>
      </div>
    </div>
    <div style={{ display:"flex", gap: 8, flexWrap:"wrap" }}>{children}</div>
  </div>
);

const DrawerTags = ({ tags, setTags, input, setInput, addTag }) => (
  <div>
    <FieldV label="Add tag">
      <div style={{ display:"flex", gap: 6 }}>
        <InputV value={input} onChange={e => setInput(e.target.value)}
          onKeyDown={e => { if (e.key === "Enter") { e.preventDefault(); addTag(); }}}
          placeholder="e.g. Cultivation" style={{ flex: 1 }} />
        <BtnV kind="solid" icon="plus" onClick={addTag} style={{ padding:"7px 11px" }}>Add</BtnV>
      </div>
    </FieldV>
    <div style={{
      marginTop: 18, display:"flex", flexWrap:"wrap", gap: 8,
    }}>
      {tags.map(t => (
        <span key={t} style={{
          display:"inline-flex", alignItems:"center", gap: 6,
          background:"var(--accent-soft)", color:"var(--accent-hi)",
          border:"1px solid var(--accent-line)", borderRadius: 6,
          padding:"6px 8px 6px 10px",
          fontFamily:"var(--font-mono)", fontSize: 11, letterSpacing: 0.4,
        }}>
          {t}
          <button onClick={() => setTags(tags.filter(x => x !== t))} style={{
            all:"unset", cursor:"pointer", padding: 2, color:"var(--accent-hi)",
          }}><IconV name="x" size={10} stroke={2.6} /></button>
        </span>
      ))}
      {tags.length === 0 && <span style={{ color:"var(--text-faint)", fontSize: 12 }}>No tags yet.</span>}
    </div>

    <div style={{ marginTop: 26 }}>
      <div style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)", letterSpacing: 1.2, marginBottom: 8 }}>SUGGESTED</div>
      <div style={{ display:"flex", flexWrap:"wrap", gap: 6 }}>
        {["Cultivation","Donghua","Action","Romance","Fantasy","Mecha","Slice of Life","Mystery","Shonen","Adventure"]
          .filter(t => !tags.includes(t)).map(t => (
          <button key={t} onClick={() => setTags([...tags, t])} style={{
            all:"unset", cursor:"pointer",
            background:"var(--surface-2)", border:"1px solid var(--border)", borderRadius: 5,
            padding:"5px 9px",
            fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-dim)",
          }}>+ {t}</button>
        ))}
      </div>
    </div>
  </div>
);

// ============================================================
// Other screens — reuse v1 internals, wrap in WidePage (no max-width)
// ============================================================
const ExplorerV3 = ({ onOpen }) => (
  <WidePage top><window.ExplorerScreen onOpen={onOpen} /></WidePage>
);
const TorrentsV3  = () => <WidePage top><window.TorrentsScreen /></WidePage>;
const HistoryV3   = () => <WidePage top><window.HistoryScreen /></WidePage>;
const SettingsV3  = () => <WidePage top><window.SettingsScreen /></WidePage>;

Object.assign(window, {
  CatalogV3, MediaDetailV3, ExplorerV3, TorrentsV3, HistoryV3, SettingsV3,
  FilterBar, EpisodeCardV3, WidePage, SIDE_PAD,
  EditMetadataDrawer,
});
