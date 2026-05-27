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

// Shared rounded-chip button — used for the dynamic Tags / Folders filter row.
const ChipV3 = ({ label, active, onClick }) => (
  <button onClick={onClick} style={{
    all:"unset", cursor:"pointer",
    padding:"5px 11px", borderRadius: 999,
    fontSize: 11.5, fontWeight: 600, letterSpacing: 0.2,
    color: active ? "#fff" : "var(--text-dim)",
    background: active ? "var(--accent)" : "var(--surface)",
    border: `1px solid ${active ? "var(--accent)" : "var(--border)"}`,
  }}>{label}</button>
);

// Search input shared between Catalog and any other filterable surface.
const SearchInputV = ({ value, onChange, placeholder = "Filter by title…" }) => (
  <div style={{
    display:"flex", alignItems:"center", gap: 8,
    background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 10,
    padding:"0 14px", width: 280, height: CTRL_H, boxSizing:"border-box",
  }}>
    <IconV name="search" size={14} />
    <input value={value} onChange={e => onChange(e.target.value)} placeholder={placeholder}
      style={{ all:"unset", flex: 1, color:"var(--text)", fontSize: 13 }} />
    {value && (
      <button onClick={() => onChange("")} style={{ all:"unset", cursor:"pointer", color:"var(--text-faint)" }}>
        <IconV name="x" size={12} />
      </button>
    )}
  </div>
);

// Hero pager — three styles user can pick in Profile → Appearance:
//
//   F — Transparent named pager (no chip background)
//   G — Hover chevrons + dash bars with labels under each
//   H — Numbered pill buttons + permanent edge chevrons
//
// All three live at the bottom of the hero, share the same setHeroIdx contract,
// and render with a thin auto-rotate progress bar pinned to the bottom edge.
const HeroPagerSwitch = ({ style, slots, heroIdx, setHeroIdx }) => {
  const prev = () => setHeroIdx((heroIdx - 1 + slots.length) % slots.length);
  const next = () => setHeroIdx((heroIdx + 1) % slots.length);

  if (style === "F") {
    return (
      <>
        <div style={{
          position:"absolute", left: 0, right: 0, bottom: 22, zIndex: 4,
          display:"flex", justifyContent:"center", gap: 12, padding:"0 24px",
        }}>
          {slots.map((s, i) => {
            const active = i === heroIdx;
            return (
              <button key={s.id} onClick={() => setHeroIdx(i)} className="tv-focus" style={{
                all:"unset", cursor:"pointer",
                display:"flex", flexDirection:"column", alignItems:"center", gap: 5,
                padding:"7px 14px", borderRadius: 10,
                background: active ? "rgba(20,15,12,0.55)" : "transparent",
                border: `1px solid ${active ? "var(--accent-line)" : "transparent"}`,
                minWidth: 96,
                transition:"all .15s ease",
              }}>
                <span style={{
                  fontFamily:"var(--font-mono)", fontSize: 9.5, letterSpacing: 0.8,
                  color: active ? "var(--accent-hi)" : "var(--text-faint)", opacity: 0.7,
                }}>{String(i+1).padStart(2,"0")}</span>
                <span style={{
                  fontSize: 11, fontWeight: 600,
                  color: active ? "var(--accent-hi)" : "rgba(232,224,210,0.55)",
                  textShadow:"0 1px 4px rgba(0,0,0,0.6)",
                  maxWidth: 120, overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap",
                }}>{s.title}</span>
              </button>
            );
          })}
        </div>
        <HeroProgressBar />
      </>
    );
  }

  if (style === "G") {
    return (
      <>
        <HeroEdgeChev side="l" onClick={prev} hoverOnly />
        <HeroEdgeChev side="r" onClick={next} hoverOnly />
        <div style={{
          position:"absolute", left: "50%", bottom: 24, transform:"translateX(-50%)", zIndex: 4,
          display:"flex", gap: 12, alignItems:"center",
        }}>
          {slots.map((s, i) => {
            const active = i === heroIdx;
            return (
              <button key={s.id} onClick={() => setHeroIdx(i)} className="tv-focus" style={{
                all:"unset", cursor:"pointer",
                display:"flex", flexDirection:"column", alignItems:"center", gap: 6,
                padding:"5px 8px", minWidth: 80, borderRadius: 8,
              }}>
                <span style={{
                  height: 3, borderRadius: 3,
                  width: active ? 70 : 32,
                  background: active
                    ? "linear-gradient(90deg, var(--accent), var(--accent-hi))"
                    : "rgba(255,255,255,0.22)",
                  boxShadow: active ? "0 0 6px var(--accent-soft)" : "none",
                  transition:"width .25s ease, background .15s",
                }} />
                <span style={{
                  fontSize: 10.5, fontWeight: 600, letterSpacing: 0.2,
                  color: active ? "var(--accent-hi)" : "rgba(232,224,210,0.4)",
                  textShadow:"0 1px 4px rgba(0,0,0,0.6)",
                  maxWidth: 110, overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap",
                }}>{s.title}</span>
              </button>
            );
          })}
        </div>
        <HeroProgressBar />
      </>
    );
  }

  // H — numbered pills + edge chevrons
  return (
    <>
      <HeroEdgeChev side="l" onClick={prev} />
      <HeroEdgeChev side="r" onClick={next} />
      <div style={{
        position:"absolute", left: "50%", bottom: 24, transform:"translateX(-50%)", zIndex: 4,
        display:"flex", gap: 10,
      }}>
        {slots.map((s, i) => {
          const active = i === heroIdx;
          return (
            <button key={s.id} onClick={() => setHeroIdx(i)} className="tv-focus" style={{
              all:"unset", cursor:"pointer",
              display:"flex", alignItems:"center", gap: 8,
              padding:"6px 12px 6px 6px", borderRadius: 999,
              background: active ? "rgba(20,15,12,0.7)" : "rgba(20,15,12,0.35)",
              border:`1px solid ${active ? "var(--accent-line)" : "rgba(255,255,255,0.06)"}`,
              transition:"all .15s ease",
            }}>
              <span style={{
                width: 22, height: 22, borderRadius: 22,
                background: active ? "var(--accent)" : "rgba(0,0,0,0.5)",
                color: active ? "#fff" : "var(--text-dim)",
                fontFamily:"var(--font-mono)", fontSize: 9.5, fontWeight: 700,
                display:"grid", placeItems:"center",
              }}>{String(i+1).padStart(2,"0")}</span>
              <span style={{
                fontSize: 11, fontWeight: 600,
                color: active ? "var(--accent-hi)" : "rgba(232,224,210,0.55)",
                maxWidth: 130, overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap",
              }}>{s.title}</span>
            </button>
          );
        })}
      </div>
      <HeroProgressBar />
    </>
  );
};

const HeroEdgeChev = ({ side, onClick, hoverOnly = false }) => (
  <button onClick={onClick} className={hoverOnly ? "hero-chev hover" : "hero-chev"} style={{
    position:"absolute", top:"50%", transform:"translateY(-50%)", zIndex: 5,
    [side === "l" ? "left" : "right"]: 20,
    width: 54, height: 54, borderRadius: 54,
    background:"rgba(10,8,7,0.45)", backdropFilter:"blur(10px)",
    border:"1px solid rgba(255,255,255,0.12)",
    color:"rgba(255,255,255,0.65)",
    display:"grid", placeItems:"center", cursor:"pointer",
    transition:"all .15s ease",
  }} aria-label={side === "l" ? "Previous slot" : "Next slot"}>
    <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
      <polyline points={side === "l" ? "15 18 9 12 15 6" : "9 18 15 12 9 6"} />
    </svg>
    <style>{`
      .hero-chev:hover, .hero-chev:focus-visible {
        background: var(--accent) !important;
        border-color: var(--accent) !important;
        color: #fff !important;
        outline: none;
        box-shadow: 0 0 0 4px var(--accent-soft);
      }
      .hero-chev.hover { opacity: 0; }
      *:hover > .hero-chev.hover,
      .hero-chev.hover:focus-visible { opacity: 1; }
    `}</style>
  </button>
);

const HeroProgressBar = () => (
  <div style={{
    position:"absolute", left: 0, right: 0, bottom: 0, height: 2,
    background:"rgba(255,255,255,0.05)", zIndex: 3,
  }}>
    <div style={{
      height:"100%", background:"var(--accent)", width:"62%",
      boxShadow:"0 0 8px var(--accent-soft)",
    }} />
  </div>
);

// ============================================================
// CATALOG (v3) — wide grid, hero full-bleed
// ============================================================
const CatalogV3 = ({ onOpen, setBdImage, rotateSec = 18 }) => {
  const init = window.__init || {};
  // Folder selection can be pushed in from the topbar (window.__folderJump).
  const [folderFilter, setFolderFilter] = useStateV3(init.folderFilter || window.__folderJump || "All");
  // Keep folder in sync with topbar nav clicks during the session.
  useEffectV3(() => {
    const t = setInterval(() => {
      if (window.__folderJump && window.__folderJump !== folderFilter) {
        setFolderFilter(window.__folderJump);
      }
    }, 200);
    return () => clearInterval(t);
  }, [folderFilter]);
  const [tagFilter, setTagFilter]   = useStateV3("All");
  const [filterMode, setFilterMode] = useStateV3("tags"); // "tags" | "folders"
  const [query, setQuery]   = useStateV3("");
  const [heroIdx, setHeroIdx] = useStateV3(0);
  const [nrOpen, setNrOpen] = useStateV3(!!init.openNRModal);

  // ── Continue Watching hero — always 5 slots ────────────────────
  // 1) entries from WATCHING (mid-watch + next-up)
  // 2) fill with featured (rating ≥ 8.0) not already in CW
  // 3) fill remainder with random library items not yet shown
  const heroSlots = useMemoV3(() => {
    const seen = new Set();
    const slots = [];
    const push = (item, extras = {}) => {
      if (!item || seen.has(item.id) || slots.length >= 5) return;
      seen.add(item.id);
      slots.push({ ...item, ...extras });
    };
    (window.WATCHING || []).forEach(w => {
      const it = window.LIBRARY.find(x => x.id === w.id);
      if (it) push(it, { cwEp: w.ep, cwProgress: w.progress, cwKind: w.kind, slotKind: "cw" });
    });
    window.LIBRARY.filter(i => i.rating >= 8.0)
      .forEach(it => push(it, { slotKind: "featured" }));
    window.LIBRARY.forEach(it => push(it, { slotKind: "random" }));
    return slots.slice(0, 5);
  }, []);

  useEffectV3(() => {
    if (heroSlots.length <= 1) return;
    const t = setInterval(() => setHeroIdx(i => (i + 1) % heroSlots.length), (rotateSec || 18) * 1000);
    return () => clearInterval(t);
  }, [heroSlots.length, rotateSec]);
  const hero = heroSlots[heroIdx] || heroSlots[0] || window.LIBRARY[0];

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

  // Other featured (not in hero slots) for the row below
  const otherFeatured = useMemoV3(() => {
    const heroIds = new Set(heroSlots.map(h => h.id));
    return window.LIBRARY.filter(i => i.rating >= 8.0 && !heroIds.has(i.id));
  }, [heroSlots]);

  // Dynamic tag list — collected from library, ranked by frequency
  const allTags = useMemoV3(() => {
    const freq = {};
    window.LIBRARY.forEach(i => (i.tags || []).forEach(t => { freq[t] = (freq[t] || 0) + 1; }));
    return Object.entries(freq).sort((a,b) => b[1]-a[1]).map(([t]) => t);
  }, []);

  const items = window.LIBRARY.filter(i => {
    if (filterMode === "folders" && folderFilter !== "All") {
      if (folderFilter === "Donghua" && !i.tags?.includes("Donghua")) return false;
      else if (folderFilter !== "Donghua" && i.type !== folderFilter) return false;
    }
    if (filterMode === "tags" && tagFilter !== "All") {
      if (!i.tags?.includes(tagFilter)) return false;
    }
    if (query && !i.title.toLowerCase().includes(query.toLowerCase())) return false;
    return true;
  });

  // Hero label & primary action depend on slot type
  const heroLabel = hero.slotKind === "cw"
    ? (hero.cwKind === "next" ? `NEXT UP · EPISODE ${String(hero.cwEp).padStart(2,"0")}` : `CONTINUE · EPISODE ${String(hero.cwEp).padStart(2,"0")}`)
    : (hero.slotKind === "featured" ? "FEATURED" : "FROM YOUR LIBRARY");
  const primaryLabel = hero.slotKind === "cw"
    ? (hero.cwKind === "next" ? `Play episode ${String(hero.cwEp).padStart(2,"0")}` : `Continue · ${Math.round((hero.cwProgress||0)*100)}%`)
    : "Open detail";

  return (
    <div>
      {/* HERO — Continue Watching, 5 slots, rotating */}
      <div style={{
        position:"relative", height: "72vh", minHeight: 640, overflow:"hidden",
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
          background:`linear-gradient(90deg, oklch(0.10 0.04 ${hero.hue} / 0.82) 0%, oklch(0.10 0.04 ${hero.hue} / 0.30) 42%, transparent 75%)`,
          transition:"background 1.4s ease",
        }} />
        <div style={{ position:"absolute", inset: 0, background:"linear-gradient(0deg, rgba(0,0,0,0.65), transparent 55%)" }} />
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
          position:"absolute", left: SIDE_PAD, right: SIDE_PAD, bottom: 64,
          display:"flex", alignItems:"flex-end", gap: 40,
        }}>
          <div key={hero.id+"-content"} style={{ flex: 1, maxWidth: 900, animation:"hero-content-in-v3 .8s ease" }}>
            <div style={{
              fontFamily:"var(--font-mono)", fontSize: 11.5, color:"var(--accent-hi)",
              letterSpacing: 1.6, textTransform:"uppercase", marginBottom: 14,
              display:"inline-flex", alignItems:"center", gap: 9,
            }}>
              {hero.slotKind === "cw" && (
                <span style={{ width: 7, height: 7, borderRadius: 7, background:"var(--accent)", boxShadow:"0 0 10px var(--accent-soft)" }} />
              )}
              {heroLabel}
              <span style={{ color:"var(--text-faint)", letterSpacing: 1 }}>· {hero.title.toUpperCase()}</span>
            </div>
            <h2 style={{
              margin: 0, fontFamily:"var(--font-display)",
              fontSize: 108, lineHeight: 0.86, letterSpacing: -3.4,
              color:"#fff", textShadow:"0 4px 30px rgba(0,0,0,0.6)",
            }}>{hero.title.toUpperCase()}</h2>
            <div style={{
              display:"flex", alignItems:"center", gap: 22, marginTop: 18,
              fontFamily:"var(--font-mono)", fontSize: 12, letterSpacing: 1, color:"var(--text-dim)",
            }}>
              <span style={{ color:"var(--accent-hi)", fontWeight: 700 }}>★ {hero.rating.toFixed(1)}</span>
              <span>{hero.year}</span>
              <span>{hero.episodes} EP</span>
              <span>{hero.studio}</span>
              <span>{hero.lang}</span>
            </div>

            {/* Progress bar (only for in-progress watch state) */}
            {hero.slotKind === "cw" && hero.cwKind === "progress" && (
              <div style={{ maxWidth: 520, marginTop: 22 }}>
                <div style={{
                  display:"flex", justifyContent:"space-between",
                  fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-dim)",
                  letterSpacing: 0.8, textTransform:"uppercase", marginBottom: 7,
                }}>
                  <span>~{Math.round((1 - hero.cwProgress) * parseInt(hero.runtime))}m remaining</span>
                  <span>{Math.round(hero.cwProgress * 100)}%</span>
                </div>
                <div style={{ height: 4, background:"rgba(255,255,255,0.08)", borderRadius: 4, overflow:"hidden" }}>
                  <div style={{
                    width: `${hero.cwProgress * 100}%`, height:"100%",
                    background:"linear-gradient(90deg, var(--accent), var(--accent-hi))",
                    boxShadow:"0 0 10px var(--accent-soft)",
                  }} />
                </div>
              </div>
            )}

            <p style={{
              color:"#e7dfd7", marginTop: 20, fontSize: 15, lineHeight: 1.55,
              textWrap:"pretty", maxWidth: 700,
              textShadow:"0 2px 10px rgba(0,0,0,0.55)",
              display:"-webkit-box", WebkitLineClamp: 3, WebkitBoxOrient:"vertical", overflow:"hidden",
            }}>{hero.overview}</p>

            <div style={{ display:"flex", gap: 10, marginTop: 24, alignItems:"center" }}>
              <BtnV kind="primary" icon="play" onClick={() => onOpen(hero.id)}>{primaryLabel}</BtnV>
              {hero.slotKind === "cw" && hero.cwKind === "progress" && (
                <BtnV kind="ghost" icon="refresh">Restart episode</BtnV>
              )}
              <BtnV kind="ghost" onClick={() => onOpen(hero.id)}>Open detail</BtnV>
            </div>
          </div>

          {/* slot pager — bottom variants render outside this column;
              this side column only renders for the legacy E fallback. */}
          {(window.__heroPager === "E" || !window.__heroPager) && (
          <div style={{ display:"flex", flexDirection:"column", gap: 7, paddingBottom: 12 }}>
            {heroSlots.map((f, i) => (
              <button key={f.id} onClick={() => setHeroIdx(i)} className="tv-focus" style={{
                all:"unset", cursor:"pointer",
                fontFamily:"var(--font-mono)", fontSize: 10,
                color: i === heroIdx ? "var(--accent-hi)" : "var(--text-faint)",
                padding:"5px 0", display:"flex", alignItems:"center", gap:8,
                whiteSpace:"nowrap",
              }}>
                <span style={{
                  width: i === heroIdx ? 28 : 14, height: 2,
                  background: i === heroIdx ? "var(--accent)" : "rgba(255,255,255,0.18)",
                  transition:"width .25s ease",
                }} />
                <span style={{ width: 18 }}>{String(i+1).padStart(2,"0")}</span>
                <span style={{
                  fontFamily:"var(--font-mono)", fontSize: 9, letterSpacing: 0.6,
                  color: i === heroIdx ? "var(--accent-hi)" : "var(--text-faint)",
                  opacity: 0.7, textTransform:"uppercase",
                }}>
                  {f.slotKind === "cw" ? (f.cwKind === "next" ? "Next up" : `${Math.round(f.cwProgress*100)}%`) : f.slotKind === "featured" ? "★" : "Lib"}
                </span>
              </button>
            ))}
          </div>
          )}
        </div>

        {/* Bottom hero pagers — F / G / H variants picked in Profile → Appearance */}
        <HeroPagerSwitch
          style={window.__heroPager || "F"}
          slots={heroSlots}
          heroIdx={heroIdx}
          setHeroIdx={setHeroIdx}
        />
      </div>

      {/* FEATURED row — items not already in hero */}
      {otherFeatured.length > 0 && (
        <div style={{ padding: `38px ${SIDE_PAD}px 8px` }}>
          <div style={{ display:"flex", alignItems:"flex-end", justifyContent:"space-between", marginBottom: 16 }}>
            <div>
              <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color:"var(--accent-hi)" }}>EDITOR'S PICKS</div>
              <h2 style={{ margin:"6px 0 0", fontFamily:"var(--font-display)", fontSize: 26, letterSpacing:-0.4 }}>FEATURED · ★ 8.0+</h2>
            </div>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-faint)", letterSpacing: 0.6 }}>{otherFeatured.length} TITLES</div>
          </div>
          <div style={{ display:"flex", gap: 14, overflowX:"auto", paddingBottom: 14, scrollbarWidth:"none" }}>
            {otherFeatured.map(i => (
              <div key={i.id} style={{ width: 184, flexShrink: 0 }}>
                <PosterV item={i} w={184} h={268} onClick={() => onOpen(i.id)} />
              </div>
            ))}
          </div>
        </div>
      )}

      {/* FILTER BAR + GRID */}
      <div style={{ padding: `28px ${SIDE_PAD}px 80px` }}>
        <div style={{ display:"flex", alignItems:"center", gap: 12, flexWrap:"wrap", marginBottom: 16 }}>
          <SearchInputV value={query} onChange={setQuery} />

          {/* Filter mode toggle: tags vs folders */}
          <div style={{
            display:"flex", gap: 3, padding: 3,
            background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 9,
            height: CTRL_H, boxSizing:"border-box",
          }}>
            {[["tags","Tags"],["folders","Folders"]].map(([k, l]) => (
              <button key={k} onClick={() => setFilterMode(k)} style={{
                all:"unset", cursor:"pointer", padding: "0 14px",
                fontSize: 12, fontWeight: 600, borderRadius: 6,
                color: filterMode === k ? "var(--text)" : "var(--text-dim)",
                background: filterMode === k ? "var(--surface-3)" : "transparent",
                boxShadow: filterMode === k ? "0 1px 0 var(--accent) inset" : "none",
                display:"inline-flex", alignItems:"center",
              }}>{l}</button>
            ))}
          </div>

          <BtnV kind="ghost" icon="refresh">Rescan all</BtnV>
          <NRBadgeV3 count={window.NEEDS_REVIEW.length} onClick={() => setNrOpen(true)} />
          <div style={{ marginLeft:"auto", fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-faint)", letterSpacing: 0.6 }}>
            {items.length} / {window.LIBRARY.length} TITLES
          </div>
        </div>

        {/* dynamic chip strip */}
        <div style={{ display:"flex", gap: 8, marginBottom: 30, flexWrap:"wrap", alignItems:"center" }}>
          <span style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)", letterSpacing: 1, marginRight: 4 }}>
            {filterMode === "tags" ? "TAG" : "FOLDER"}
          </span>
          {filterMode === "tags"
            ? ["All", ...allTags].map(name => (
                <ChipV3 key={name} label={name} active={tagFilter === name} onClick={() => setTagFilter(name)} />
              ))
            : ["All", ...window.FOLDERS.map(f => f.title)].map(name => (
                <ChipV3 key={name} label={name} active={folderFilter === name} onClick={() => setFolderFilter(name)} />
              ))
          }
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

  // Per-episode state: have (file on disk), watched (fully done), progress
  // (0..1 if mid-watch). For demo we mark the first 4 as watched, ep 5 as
  // in-progress (~38%), the rest unwatched. Toggling lifts to local state.
  const initEps = useMemoV3(() => Array.from({ length: Math.min(totalEps, 24) }, (_, i) => {
    const n = i + 1;
    const have = n < 13 || ((n * 7) % 11) > 2;
    let watched = false, progress = 0;
    if (n <= 4) { watched = true; progress = 1; }
    else if (n === 5) { watched = false; progress = 0.38; }
    return { n, title: `Episode ${n}`, have, watched, progress, runtime: item.runtime };
  }), [item.id, totalEps]);

  const [eps, setEps] = useStateV3(initEps);
  useEffectV3(() => { setEps(initEps); }, [initEps]);

  const toggleWatched = (n) => setEps(prev => prev.map(e =>
    e.n === n ? { ...e, watched: !e.watched, progress: e.watched ? 0 : 1 } : e
  ));

  // For movies the single file is just episode 1; render a dedicated card.
  const isMovie = item.type === "Movie" || item.type === "Multserials" && totalEps <= 1;
  const movieFile = useMemoV3(() => ({
    n: 1,
    title: `${item.title} (${item.year})`,
    have: true,
    watched: false,
    progress: 0.62, // mid-watch demo
    runtime: item.runtime,
    filename: `${item.title} (${item.year}) - 1080p.mkv`,
    size: "6.1 GB",
  }), [item.id, item.title, item.year, item.runtime]);

  const [mFile, setMFile] = useStateV3(movieFile);
  useEffectV3(() => { setMFile(movieFile); }, [movieFile]);
  const toggleMovieWatched = () => setMFile(p => ({ ...p, watched: !p.watched, progress: p.watched ? 0 : 1 }));

  // Continue action: pick first in-progress episode → else next-after-last-watched → else "Play first".
  const cont = useMemoV3(() => {
    if (isMovie) {
      if (mFile.watched) return { kind: "rewatch", n: 1, progress: 0, label: "Rewatch from start" };
      if (mFile.progress > 0) return { kind: "continue", n: 1, progress: mFile.progress, label: `Continue · ${Math.round(mFile.progress * 100)}%` };
      return { kind: "play", n: 1, progress: 0, label: "Play movie" };
    }
    const ip = eps.find(e => e.have && !e.watched && e.progress > 0);
    if (ip) return { kind: "continue", n: ip.n, progress: ip.progress, label: `Continue · EP ${String(ip.n).padStart(2,"0")}` };
    const nextUp = eps.find(e => e.have && !e.watched);
    if (nextUp && eps.some(e => e.watched)) return { kind: "next", n: nextUp.n, progress: 0, label: `Continue · EP ${String(nextUp.n).padStart(2,"0")}` };
    if (nextUp) return { kind: "play", n: nextUp.n, progress: 0, label: "Play first episode" };
    return { kind: "done", n: 0, progress: 1, label: "Watched · Replay" };
  }, [eps, mFile, isMovie]);

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

        {/* FAVORITE star + EDIT — top right of hero */}
        <div style={{ position:"absolute", top: 24, right: SIDE_PAD, zIndex: 5, display:"flex", gap: 8 }}>
          <FavoriteButtonV3 id={item.id} />
          <button onClick={() => setEditing(true)} title="Edit metadata" style={{
            all:"unset", cursor:"pointer",
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
        </div>

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
            <BtnV kind="primary" icon="play">{cont.label}</BtnV>
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

      {/* MOVIE FILE — same status language as episode cards, single row */}
      {isMovie && (
        <div style={{ padding: `0 ${SIDE_PAD}px 60px` }}>
          <div style={{ display:"flex", alignItems:"flex-end", justifyContent:"space-between", marginBottom: 18 }}>
            <div>
              <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color:"var(--accent-hi)" }}>FILE</div>
              <h2 style={{ margin: "6px 0 0", fontFamily:"var(--font-display)", fontSize: 32, letterSpacing:-0.7 }}>
                ON DISK
                {mFile.watched && <span style={{ fontSize: 13, color:"var(--success)", marginLeft: 12, letterSpacing: 0.6, fontFamily:"var(--font-mono)" }}>· WATCHED</span>}
                {!mFile.watched && mFile.progress > 0 && (
                  <span style={{ fontSize: 13, color:"var(--accent-hi)", marginLeft: 12, letterSpacing: 0.6, fontFamily:"var(--font-mono)" }}>
                    · {Math.round(mFile.progress * 100)}% WATCHED
                  </span>
                )}
              </h2>
            </div>
          </div>
          <MovieFileCardV3 file={mFile} item={item} onToggleWatched={toggleMovieWatched} />
        </div>
      )}

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
            {eps.map(ep => <EpisodeCardV3 key={ep.n} ep={ep} item={item} onToggleWatched={() => toggleWatched(ep.n)} />)}
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
// Inline eye-toggle icon — switches between "watched" (open eye) and
// "unwatched" (eye-off). Used on every episode/file card.
const EyeIconV3 = ({ closed, size = 15 }) => closed ? (
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

// Single-file card for movies. Mirrors the episode card status language
// (left edge strip + corner watch toggle + progress bar) but in a wider
// 16:7 row, with the file's path + size + codec inline.
const MovieFileCardV3 = ({ file, item, onToggleWatched }) => {
  const watched = file.watched;
  const inProg = !watched && file.progress > 0;
  const stripColor = watched ? "var(--text-faint)" : "var(--success)";
  const [hover, setHover] = useStateV3(false);
  return (
    <div
      onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)}
      style={{
        position:"relative", display:"grid", gridTemplateColumns:"360px 1fr",
        background:"var(--surface)", border:"1px solid var(--border)",
        borderRadius: 14, overflow:"hidden", opacity: watched ? 0.78 : 1,
        transition:"transform .2s ease, border-color .2s ease",
        transform: hover ? "translateY(-2px)" : "none",
        borderColor: hover ? "var(--accent-line)" : "var(--border)",
      }}
    >
      <div style={{
        position:"absolute", left: 0, top: 0, bottom: 0, width: 3,
        background: stripColor,
        boxShadow: !watched ? "0 0 12px var(--success)" : "none",
        zIndex: 2,
      }} />

      {/* thumbnail panel */}
      <div style={{
        position:"relative", aspectRatio: "16 / 9",
        backgroundImage:`url("${item.bd}")`, backgroundSize:"cover", backgroundPosition:"center",
        filter: watched ? "saturate(0.6) brightness(0.7)" : "none",
      }}>
        <div style={{
          position:"absolute", inset: 0,
          background:"linear-gradient(180deg, transparent 30%, rgba(0,0,0,0.78) 100%)",
        }} />
        <div style={{
          position:"absolute", top: 14, left: 18,
          fontFamily:"var(--font-display)", fontSize: 28,
          color: "#fff", textShadow:"0 2px 8px rgba(0,0,0,0.7)",
        }}>MOVIE</div>

        {/* Disk-status icon — TOP-RIGHT (immutable) */}
        <div title="On disk" style={{
          position:"absolute", top: 12, right: 12,
          width: 30, height: 30, borderRadius: 8,
          background: "oklch(0.74 0.15 150 / 0.18)",
          border: "1px solid var(--success)",
          display:"grid", placeItems:"center", color: "var(--success)",
          backdropFilter:"blur(6px)", zIndex: 3,
        }}>
          <IconV name="check" size={14} stroke={2.4} />
        </div>

        {/* Watched-toggle eye — BOTTOM-RIGHT (user-controlled) */}
        <button onClick={(e) => { e.stopPropagation(); onToggleWatched?.(); }}
          title={watched ? "Mark as unwatched" : "Mark as watched"} style={{
          all:"unset", cursor:"pointer", position:"absolute", bottom: 14, right: 12,
          width: 30, height: 30, borderRadius: 8,
          background: watched ? "oklch(0.74 0.15 150 / 0.25)" : "rgba(0,0,0,0.45)",
          border: `1px solid ${watched ? "var(--success)" : "rgba(255,255,255,0.18)"}`,
          display:"grid", placeItems:"center",
          color: watched ? "var(--success)" : "#fff",
          backdropFilter:"blur(6px)", zIndex: 4,
        }}>
          <EyeIconV3 closed={!watched} size={14} />
        </button>

        {/* progress bar at the very bottom of the thumb */}
        {(watched || inProg) && (
          <div style={{
            position:"absolute", left: 0, right: 0, bottom: 0, height: 4,
            background:"rgba(255,255,255,0.08)",
          }}>
            <div style={{
              width: `${(watched ? 1 : file.progress) * 100}%`, height:"100%",
              background: watched ? "var(--text-faint)" : "linear-gradient(90deg, var(--accent), var(--accent-hi))",
              boxShadow: watched ? "none" : "0 0 8px var(--accent-soft)",
            }} />
          </div>
        )}

        {hover && (
          <div style={{
            position:"absolute", left:"50%", top:"50%", transform:"translate(-50%, -50%)",
            width: 64, height: 64, borderRadius: 64,
            background:"rgba(0,0,0,0.5)", backdropFilter:"blur(8px)",
            display:"grid", placeItems:"center",
            border:"1px solid rgba(255,255,255,0.3)",
          }}>
            <IconV name="play" size={24} style={{ color:"#fff", marginLeft: 3 }} />
          </div>
        )}
      </div>

      <div style={{ padding: "22px 26px", display:"flex", flexDirection:"column", gap: 14, minWidth: 0 }}>
        <div style={{ fontSize: 17, fontWeight: 600, color: watched ? "var(--text-dim)" : "var(--text)", letterSpacing: -0.2 }}>
          {file.title}
        </div>
        <div style={{ fontFamily:"var(--font-mono)", fontSize: 12, color:"var(--text-faint)", wordBreak:"break-all" }}>
          {file.filename}
        </div>

        <div style={{
          display:"grid", gridTemplateColumns:"repeat(4, auto)", gap:"8px 22px",
          fontFamily:"var(--font-mono)", fontSize: 12, color:"var(--text-dim)",
          letterSpacing: 0.4, marginTop: 2,
        }}>
          <span><span style={{ color:"var(--text-faint)" }}>RUNTIME</span> {file.runtime}</span>
          <span><span style={{ color:"var(--text-faint)" }}>RES</span> 1080p</span>
          <span><span style={{ color:"var(--text-faint)" }}>CODEC</span> H.265</span>
          <span><span style={{ color:"var(--text-faint)" }}>SIZE</span> {file.size}</span>
        </div>

        <div style={{ display:"flex", gap: 8, marginTop: "auto", paddingTop: 6, flexWrap:"wrap" }}>
          <BtnV kind="primary" icon="play">
            {watched ? "Rewatch from start" : inProg ? `Continue · ${Math.round(file.progress*100)}%` : "Play movie"}
          </BtnV>
          <BtnV kind="solid" icon={watched ? "x" : "check"} onClick={onToggleWatched}>
            {watched ? "Mark as unwatched" : "Mark as watched"}
          </BtnV>
        </div>
      </div>
    </div>
  );
};

const EpisodeCardV3 = ({ ep, item, onToggleWatched }) => {
  const have = ep.have;
  const watched = ep.watched;
  const inProg = !watched && ep.progress > 0;
  // Left edge: green if on disk, amber if missing. Once watched, dim it.
  const stripColor = have ? (watched ? "var(--text-faint)" : "var(--success)") : "var(--warn)";
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
        opacity: have ? (watched ? 0.72 : 1) : 0.50,
        cursor: have ? "pointer" : "default",
        transform: hover && have ? "translateY(-2px)" : "none",
        borderColor: hover && have ? "var(--accent-line)" : (have ? "var(--border)" : "rgba(255,240,220,0.12)"),
        transition:"transform .2s ease, border-color .2s ease, opacity .2s ease",
      }}
    >
      <div style={{
        position:"absolute", left: 0, top: 0, bottom: 0, width: 3,
        background: stripColor,
        boxShadow: (have && !watched) ? "0 0 12px var(--success)" : "none",
        zIndex: 2,
      }} />

      <div style={{
        height: 150, position:"relative",
        backgroundImage:`url("${item.bd}")`,
        backgroundSize:"cover",
        backgroundPosition: `${(ep.n * 19) % 100}% center`,
        filter: have ? (watched ? "saturate(0.6) brightness(0.7)" : "none") : "grayscale(1) brightness(0.55)",
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

        {/* Disk status — TOP-RIGHT. Immutable system fact: is the file on disk? */}
        <div title={have ? "On disk" : "Missing — not downloaded"} style={{
          position:"absolute", top: 10, right: 10,
          width: 26, height: 26, borderRadius: 6,
          background: have ? "oklch(0.74 0.15 150 / 0.18)" : "oklch(0.80 0.17 75 / 0.20)",
          border: `1px solid ${have ? "var(--success)" : "var(--warn)"}`,
          display:"grid", placeItems:"center",
          color: have ? "var(--success)" : "var(--warn)",
          backdropFilter:"blur(6px)",
          zIndex: 3,
        }}>
          <IconV name={have ? "check" : "warn"} size={13} stroke={2.4} />
        </div>

        {/* Watched-toggle eye — BOTTOM-RIGHT. User-controlled state. */}
        {have && (
          <button onClick={(e) => { e.stopPropagation(); onToggleWatched?.(); }}
            title={watched ? "Mark as unwatched" : "Mark as watched"} style={{
            all:"unset", cursor:"pointer", position:"absolute", bottom: 10, right: 10,
            width: 26, height: 26, borderRadius: 6,
            background: watched ? "oklch(0.74 0.15 150 / 0.25)" : "rgba(0,0,0,0.45)",
            border: `1px solid ${watched ? "var(--success)" : "rgba(255,255,255,0.18)"}`,
            display:"grid", placeItems:"center",
            color: watched ? "var(--success)" : "#fff",
            backdropFilter:"blur(6px)",
            transition:"transform .15s ease",
            zIndex: 4,
          }}>
            <EyeIconV3 closed={!watched} size={13} />
          </button>
        )}

        {/* Progress bar — bottom edge of thumbnail */}
        {have && (watched || inProg) && (
          <div style={{
            position:"absolute", left: 0, right: 0, bottom: 0, height: 3,
            background:"rgba(255,255,255,0.08)",
          }}>
            <div style={{
              width: `${(watched ? 1 : ep.progress) * 100}%`, height: "100%",
              background: watched ? "var(--text-faint)" : "linear-gradient(90deg, var(--accent), var(--accent-hi))",
              boxShadow: watched ? "none" : "0 0 8px var(--accent-soft)",
            }} />
          </div>
        )}

        {/* hover overlay */}
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
              <span style={{ color: "var(--text-dim)" }}>{(0.5 + ((ep.n * 13) % 41) / 100).toFixed(2)} GB</span>
            </>
          ) : (
            <>
              <span>·</span>
              <span style={{ color:"var(--warn)" }}>Missing</span>
            </>
          )}
        </div>
      </div>
    </div>
  );
};

// FavoriteButtonV3 — star toggle. Reads from window.FAVORITES Set,
// mutates locally; in production this hits PATCH /api/library/{id}.
const FavoriteButtonV3 = ({ id }) => {
  const [fav, setFav] = useStateV3((window.FAVORITES || new Set()).has(id));
  const toggle = (e) => {
    e?.stopPropagation();
    setFav(v => {
      const next = !v;
      if (next) window.FAVORITES?.add(id);
      else window.FAVORITES?.delete(id);
      return next;
    });
  };
  return (
    <button onClick={toggle} title={fav ? "Remove from favorites" : "Add to favorites"} style={{
      all:"unset", cursor:"pointer",
      display:"inline-flex", alignItems:"center", gap: 8,
      padding:"9px 14px", borderRadius: 8,
      background: fav ? "oklch(0.80 0.17 60 / 0.22)" : "rgba(10,8,7,0.55)",
      backdropFilter:"blur(10px)",
      border:`1px solid ${fav ? "oklch(0.85 0.17 60)" : "rgba(255,255,255,0.12)"}`,
      color: fav ? "oklch(0.92 0.17 80)" : "var(--text)",
      fontSize: 12.5, fontWeight: 600, fontFamily:"var(--font-mono)", letterSpacing: 0.6,
      transition:"background .15s ease, border-color .15s ease, color .15s ease",
    }}>
      <IconV name={fav ? "star-fill" : "star"} size={14} stroke={fav ? 1 : 1.8} />
      {fav ? "FAVORITED" : "FAVORITE"}
    </button>
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
  FilterBar, EpisodeCardV3, MovieFileCardV3, EyeIconV3, WidePage, SIDE_PAD,
  EditMetadataDrawer,
});
