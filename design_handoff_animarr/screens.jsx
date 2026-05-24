// ====== screens ======
const { useState: useStateS, useEffect: useEffectS, useMemo: useMemoS, useRef: useRefS } = React;

// ============================================================
// CATALOG
// ============================================================
const CatalogScreen = ({ onOpen, heroStyle = "fullbleed", density = "comfortable", rotateSec = 18, setBdImage }) => {
  const [filter, setFilter] = useStateS("All");
  const [folderFilter, setFolderFilter] = useStateS("All");
  const [query, setQuery]   = useStateS("");
  const [heroIdx, setHeroIdx] = useStateS(0);
  const [nrOpen, setNrOpen] = useStateS(false);

  const featured = useMemoS(() => window.LIBRARY.filter(i => i.rating >= 8.0).slice(0, 5), []);
  // Hero rotation drives BOTH the in-page hero AND the global backdrop.
  useEffectS(() => {
    const t = setInterval(() => setHeroIdx(i => (i + 1) % featured.length), (rotateSec || 18) * 1000);
    return () => clearInterval(t);
  }, [featured.length, rotateSec]);

  const hero = featured[heroIdx] || window.LIBRARY[0];

  // Push current hero to the global backdrop so they always match.
  useEffectS(() => {
    setBdImage?.(hero.bd, hero.hue);
  }, [hero.bd, hero.hue, setBdImage]);

  const items = window.LIBRARY.filter(i => {
    if (filter !== "All" && i.type !== filter && !(filter === "Donghua" && i.tags?.includes("Donghua"))) return false;
    if (folderFilter !== "All") {
      // Map folder title → matches by item type for the mock data
      const f = window.FOLDERS.find(x => x.title === folderFilter);
      if (f) {
        if (folderFilter === "Donghua" && !i.tags?.includes("Donghua")) return false;
        else if (folderFilter !== "Donghua" && i.type !== folderFilter && !(folderFilter === "Multserials" && i.type === "Multserials")) return false;
      }
    }
    if (query && !i.title.toLowerCase().includes(query.toLowerCase())) return false;
    return true;
  });

  const heroEl = (
    <HeroBlock
      hero={hero} featured={featured} heroIdx={heroIdx} setHeroIdx={setHeroIdx}
      onOpen={onOpen} variant={heroStyle} density={density}
    />
  );

  const sidePad = density === "compact" ? 36 : 48;

  // FILTER + GRID — contained 1480px (v1 look).
  const filterAndGrid = (
    <window.Container density={density}>
      <div style={{ display:"flex", alignItems:"center", gap: 12, marginBottom: 16, flexWrap:"wrap" }}>
        <div style={{ display:"flex", gap: 4, padding: 4, background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 10 }}>
          {["All","Anime","Movie","Series","Multserials"].map(tab => (
            <button key={tab} onClick={() => setFilter(tab)} style={{
              all:"unset", cursor:"pointer",
              padding:"7px 14px", borderRadius: 7, fontSize: 12.5, fontWeight: 600,
              color: filter === tab ? "var(--text)" : "var(--text-dim)",
              background: filter === tab ? "var(--surface-3)" : "transparent",
              boxShadow: filter === tab ? "0 1px 0 var(--accent) inset" : "none",
            }}>{tab}</button>
          ))}
        </div>
        <div style={{
          flex: 1, display:"flex", alignItems:"center", gap: 8,
          background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 10,
          padding:"7px 14px", maxWidth: 320,
        }}>
          <Icon name="search" size={14} />
          <input value={query} onChange={e => setQuery(e.target.value)} placeholder="Filter by title…"
            style={{ all:"unset", flex: 1, color:"var(--text)", fontSize: 13 }} />
          {query && <button onClick={() => setQuery("")} style={{ all:"unset", cursor:"pointer", color:"var(--text-faint)" }}><Icon name="x" size={12} /></button>}
        </div>
        <Btn kind="ghost" icon="refresh">Rescan all</Btn>
        <NRBadge count={window.NEEDS_REVIEW.length} onClick={() => setNrOpen(true)} />
        <div style={{ fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-faint)", letterSpacing: 0.6 }}>
          {items.length} / {window.LIBRARY.length}
        </div>
      </div>

      {/* folder chips */}
      <div style={{ display:"flex", gap: 8, marginBottom: 26, flexWrap:"wrap", alignItems:"center" }}>
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
        gridTemplateColumns:"repeat(auto-fill, minmax(180px, 1fr))",
        gap: 18,
      }}>
        {items.map(i => (
          <Poster key={i.id} item={i} w={"100%"} h={278} onClick={() => onOpen(i.id)} />
        ))}
      </div>
    </window.Container>
  );

  // Card variant keeps the hero inside a constrained container.
  if (heroStyle === "card") {
    return (
      <>
        <window.Container density={density} top>{heroEl}</window.Container>
        {filterAndGrid}
        {nrOpen && <NRModal onClose={() => setNrOpen(false)} />}
      </>
    );
  }

  return (
    <>
      {heroEl}
      <div style={{ height: 38 }} />
      {filterAndGrid}
      {nrOpen && <NRModal onClose={() => setNrOpen(false)} />}
    </>
  );
};

// ── NeedsReview chip in the toolbar ──────────────────────────────────────
const NRBadge = ({ count, onClick }) => {
  if (count === 0) {
    return (
      <button onClick={onClick} title="No items need review" style={{
        all:"unset", cursor:"default", position:"relative",
        width: 38, height: 36, borderRadius: 10,
        background:"var(--surface)", border:"1px solid var(--border)",
        display:"grid", placeItems:"center", color:"var(--text-faint)",
      }}>
        <Icon name="warn" size={15} />
      </button>
    );
  }
  return (
    <button onClick={onClick} title={`${count} needs review`} style={{
      all:"unset", cursor:"pointer", position:"relative",
      height: 36, padding:"0 12px 0 10px", borderRadius: 10,
      background:"oklch(0.80 0.17 75 / 0.16)",
      border:"1px solid oklch(0.60 0.16 75 / 0.4)",
      color:"var(--warn)",
      display:"inline-flex", alignItems:"center", gap: 7,
      fontFamily:"var(--font-mono)", fontSize: 11, fontWeight: 700, letterSpacing: 0.6,
    }}>
      <Icon name="warn" size={14} />
      NEEDS REVIEW · {count}
      <span style={{
        position:"absolute", top: -4, right: -4,
        width: 8, height: 8, borderRadius: 8,
        background:"var(--warn)", boxShadow:"0 0 8px var(--warn)",
        animation:"nr-pulse 1.6s ease-in-out infinite",
      }} />
      <style>{`@keyframes nr-pulse { 0%,100%{opacity:1;} 50%{opacity:0.4;} }`}</style>
    </button>
  );
};

// ── NeedsReview modal (popover) ──────────────────────────────────────────
const NRModal = ({ onClose }) => (
  <>
    <div onClick={onClose} style={{
      position:"fixed", inset: 0, background:"rgba(0,0,0,0.55)", backdropFilter:"blur(3px)",
      zIndex: 40,
    }} />
    <div style={{
      position:"fixed", top:"50%", left:"50%", transform:"translate(-50%, -50%)",
      width: 720, maxHeight:"82vh",
      background:"linear-gradient(180deg, var(--surface) 0%, var(--surface-2) 100%)",
      border:"1px solid var(--border-strong)", borderRadius: 16,
      zIndex: 41, overflow:"hidden", display:"flex", flexDirection:"column",
      boxShadow:"0 40px 80px -20px rgba(0,0,0,0.7)",
      animation:"nr-modal-in .25s ease",
    }}>
      <div style={{
        padding: "20px 24px 16px",
        borderBottom:"1px solid var(--border)",
        display:"flex", alignItems:"flex-start", gap: 14,
      }}>
        <div style={{
          width: 40, height: 40, borderRadius: 8, display:"grid", placeItems:"center",
          background:"oklch(0.80 0.17 75 / 0.18)", color:"var(--warn)",
          border:"1px solid oklch(0.60 0.16 75 / 0.4)",
        }}><Icon name="warn" size={17} /></div>
        <div style={{ flex: 1 }}>
          <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color:"var(--warn)" }}>NEEDS REVIEW</div>
          <div style={{ fontFamily:"var(--font-display)", fontSize: 22, letterSpacing:-0.4, marginTop: 4 }}>
            {window.NEEDS_REVIEW.length} TITLES BELOW CONFIDENCE
          </div>
          <div style={{ fontSize: 12.5, color:"var(--text-dim)", marginTop: 4 }}>
            Pick the right match — Animarr stores it in SQLite, leaves folders on disk untouched.
          </div>
        </div>
        <button onClick={onClose} style={{ all:"unset", cursor:"pointer", color:"var(--text-dim)", padding: 4 }}>
          <Icon name="x" size={18} />
        </button>
      </div>

      <div style={{ flex: 1, overflow:"auto", padding: 22 }}>
        {window.NEEDS_REVIEW.map(nr => (
          <div key={nr.id} style={{
            background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10,
            padding: 16, marginBottom: 14,
          }}>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 11.5, color:"var(--text-dim)", marginBottom: 12 }}>{nr.folder}</div>
            <div style={{ display:"grid", gridTemplateColumns:"repeat(auto-fill, minmax(260px, 1fr))", gap: 12 }}>
              {nr.candidates.map(c => (
                <div key={c.title} style={{
                  display:"flex", gap: 12,
                  background:"var(--surface-2)", border:"1px solid var(--border)", borderRadius: 8,
                  padding: 8,
                }}>
                  <Poster item={{ id: c.title, title: c.title, cjk: c.cjk, year: c.year, type:"?", hue: c.hue }} w={64} h={92} ribbon={false} />
                  <div style={{ flex: 1, display:"flex", flexDirection:"column", justifyContent:"space-between", minWidth: 0 }}>
                    <div>
                      <div style={{ fontFamily:"var(--font-display)", fontSize: 13, letterSpacing:-0.2 }}>{c.title}</div>
                      <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-faint)", marginTop: 4 }}>{c.year} · {c.source}</div>
                    </div>
                    <div style={{ display:"flex", justifyContent:"space-between", alignItems:"center" }}>
                      <span style={{
                        fontFamily:"var(--font-mono)", fontSize: 10, letterSpacing: 0.6,
                        color: c.conf > 0.7 ? "var(--success)" : "var(--warn)",
                      }}>{Math.round(c.conf*100)}%</span>
                      <Btn kind="primary" style={{ padding:"4px 9px", fontSize: 11 }}>Use</Btn>
                    </div>
                  </div>
                </div>
              ))}
              <button style={{
                all:"unset", cursor:"pointer",
                display:"flex", alignItems:"center", justifyContent:"center", gap: 8,
                background:"transparent", border:"1px dashed var(--border-strong)",
                borderRadius: 8, padding: "0 12px", minHeight: 108,
                fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-dim)", letterSpacing: 0.6,
              }}>
                <Icon name="search" size={13} /> SEARCH MANUALLY
              </button>
            </div>
          </div>
        ))}
      </div>
    </div>
    <style>{`
      @keyframes nr-modal-in {
        0%   { opacity: 0; transform: translate(-50%, -48%) scale(0.96); }
        100% { opacity: 1; transform: translate(-50%, -50%) scale(1); }
      }
    `}</style>
  </>
);

// ---- Hero block — 3 variants. Card mode paints its own image (the v1 look).
// Fullbleed/split rely on the global Backdrop being visible behind them.
const HeroBlock = ({ hero, featured, heroIdx, setHeroIdx, onOpen, variant, density = "comfortable" }) => {
  const isFull  = variant === "fullbleed";
  const isCard  = variant === "card";
  const isSplit = variant === "split";

  const height = isFull ? 560 : isSplit ? 540 : 480;

  // Cross-fade between hero backgrounds (used by card mode).
  const [bgLayers, setBgLayers] = useStateS([{ url: hero.bd, key: 0, on: true }]);
  const counter = useRefS(0);
  useEffectS(() => {
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
  }, [hero.bd]);

  const wrap = {
    position:"relative",
    height,
    overflow: "hidden",
    marginBottom: isCard ? 38 : 0,
    borderRadius: isCard ? 18 : 0,
    boxShadow: isCard ? "0 30px 60px -30px rgba(0,0,0,0.7)" : "none",
  };

  return (
    <div style={wrap}>
      {/* Card variant paints its own bg image (the original v1 look). */}
      {isCard && bgLayers.map(l => (
        <div key={l.key} style={{
          position:"absolute", inset: 0,
          backgroundImage: `url("${l.url}")`,
          backgroundSize:"cover", backgroundPosition:"center",
          opacity: l.on ? 1 : 0,
          transition: "opacity 1.4s ease",
          animation: "hero-pan 24s ease-in-out infinite alternate",
        }} />
      ))}

      {/* Legibility overlays */}
      {isSplit ? (
        <>
          <div style={{
            position:"absolute", inset: 0,
            background: `linear-gradient(90deg, rgba(10,8,7,0.92) 0%, rgba(10,8,7,0.70) 28%, transparent 60%)`,
          }} />
          <div style={{ position:"absolute", inset: 0, background:"linear-gradient(0deg, rgba(10,8,7,0.55), transparent 50%)" }} />
        </>
      ) : (
        <>
          <div style={{
            position:"absolute", inset: 0,
            background: `linear-gradient(90deg, oklch(0.10 0.04 ${hero.hue} / 0.78) 0%, oklch(0.10 0.04 ${hero.hue} / 0.30) 38%, transparent 70%)`,
            transition: "background 1.4s ease",
          }} />
          <div style={{
            position:"absolute", inset: 0,
            background: `linear-gradient(0deg, rgba(0,0,0,0.55), transparent 50%)`,
          }} />
          <div style={{
            position:"absolute", inset: 0,
            background:`radial-gradient(80% 100% at 0% 100%, oklch(0.4 0.18 ${hero.hue} / 0.28), transparent 70%)`,
            mixBlendMode:"screen",
            transition: "background 1.4s ease",
          }} />
        </>
      )}

      {/* CJK watermark */}
      <div key={hero.id + "-cjk"} style={{
        position:"absolute", right: -10, top: -20,
        fontFamily:"var(--font-cjk)",
        fontSize: 280, lineHeight: 0.85,
        color: `oklch(0.95 0.1 ${hero.hue} / 0.07)`,
        fontWeight: 900, writingMode:"vertical-rl", textOrientation:"upright",
        letterSpacing: 8, userSelect:"none", pointerEvents:"none",
        animation: "hero-cjk-in 1.4s ease forwards",
      }}>{hero.cjk}</div>

      {/* CONTENT */}
      {isSplit ? (
        <window.Container density={density} style={{ position:"absolute", inset: 0 }}>
          <div style={{
            height:"100%", display:"grid", gridTemplateColumns:"minmax(420px, 1fr) 280px",
            alignItems:"center", gap: 60, paddingTop: 40,
          }}>
            <div key={hero.id + "-content"} style={{ animation:"hero-content-in .8s ease" }}>
              <div style={{ display:"flex", alignItems:"center", gap: 10, marginBottom: 14 }}>
                <Pill tone="accent">FEATURED · {String(heroIdx+1).padStart(2,"0")}</Pill>
                <Pill>{hero.type.toUpperCase()}</Pill>
              </div>
              <h2 style={{
                margin: 0, fontFamily:"var(--font-display)",
                fontSize: 92, lineHeight: 0.85, letterSpacing: -3.2, color:"#fff",
              }}>{hero.title.split(" ").map((w,i) => (
                <div key={i}>{w.toUpperCase()}</div>
              ))}</h2>
              <div style={{
                display:"flex", alignItems:"center", gap: 16, marginTop: 18,
                fontFamily:"var(--font-mono)", fontSize: 11, letterSpacing: 1, color:"var(--text-dim)",
              }}>
                <span style={{ color:"var(--accent-hi)" }}>★ {hero.rating.toFixed(1)}</span>
                <span>{hero.year}</span><span>{hero.episodes} EP</span><span>{hero.studio}</span>
              </div>
              <p style={{
                color:"#e2d8cf", marginTop: 16, fontSize: 14.5, lineHeight: 1.55,
                textWrap:"pretty", maxWidth: 460,
                textShadow:"0 2px 10px rgba(0,0,0,0.6)",
              }}>{hero.overview}</p>
              <div style={{ display:"flex", gap: 10, marginTop: 22 }}>
                <Btn kind="primary" icon="play" onClick={() => onOpen(hero.id)}>Open detail</Btn>
                <Btn kind="solid" icon="external">TMDB</Btn>
              </div>
            </div>
            <div style={{ position:"relative", height: 380 }}>
              <Poster item={hero} w={280} h={400} ribbon={false} onClick={() => onOpen(hero.id)} />
              <div style={{
                position:"absolute", top: -10, right: -10,
                background:"var(--accent)", color:"#fff",
                fontFamily:"var(--font-mono)", fontSize: 10, letterSpacing: 1.2,
                padding:"4px 8px", borderRadius: 4,
                boxShadow:"0 6px 16px var(--accent-soft)",
              }}>FEATURED</div>
            </div>
          </div>
        </window.Container>
      ) : (
        <window.Container density={density} style={{ position:"absolute", inset: 0 }}>
          <div style={{
            height:"100%", display:"flex", alignItems:"flex-end",
            paddingBottom: 40, gap: 32,
          }}>
            <div key={hero.id + "-content"} style={{ flex: 1, maxWidth: 720, animation:"hero-content-in .8s ease" }}>
              <div style={{ display:"flex", alignItems:"center", gap: 10, marginBottom: 14 }}>
                <Pill tone="accent">FEATURED</Pill>
                <Pill>{hero.type.toUpperCase()}</Pill>
                {hero.tags?.slice(0,2).map(t => <Pill key={t}>{t}</Pill>)}
              </div>
              <h2 style={{
                margin: 0, fontFamily:"var(--font-display)",
                fontSize: 80, lineHeight: 0.88, letterSpacing: -2.4,
                color:"#fff", textShadow:"0 4px 30px rgba(0,0,0,0.6)",
              }}>{hero.title.toUpperCase()}</h2>
              <div style={{
                display:"flex", alignItems:"center", gap: 16, marginTop: 16,
                fontFamily:"var(--font-mono)", fontSize: 11.5, letterSpacing: 1, color:"var(--text-dim)",
              }}>
                <span style={{ color:"var(--accent-hi)" }}>★ {hero.rating.toFixed(1)}</span>
                <span>{hero.year}</span><span>{hero.episodes} EP</span>
                <span>{hero.studio}</span><span>{hero.lang}</span>
              </div>
              <p style={{
                color:"#e7dfd7", marginTop: 18, fontSize: 14.5, lineHeight: 1.6,
                textWrap:"pretty", maxWidth: 600,
                textShadow:"0 2px 10px rgba(0,0,0,0.55)",
              }}>{hero.overview}</p>
              <div style={{ display:"flex", gap: 10, marginTop: 22 }}>
                <Btn kind="primary" icon="play" onClick={() => onOpen(hero.id)}>Open detail</Btn>
                <Btn kind="solid" icon="external">TMDB</Btn>
                <Btn kind="solid" icon="external">MAL</Btn>
              </div>
            </div>

            <div style={{ display:"flex", flexDirection:"column", gap: 6, paddingBottom: 6 }}>
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
        </window.Container>
      )}
      <style>{`
        @keyframes hero-pan {
          0%   { transform: scale(1.04) translate(-1%, -1%); }
          100% { transform: scale(1.10) translate(1.5%, 1.5%); }
        }
        @keyframes hero-cjk-in {
          0%   { opacity: 0; transform: translateY(-12px); }
          100% { opacity: 1; transform: translateY(0); }
        }
        @keyframes hero-content-in {
          0%   { opacity: 0; transform: translateY(10px); }
          100% { opacity: 1; transform: translateY(0); }
        }
      `}</style>
    </div>
  );
};

// ============================================================
// MEDIA DETAIL
// ============================================================
const MediaDetailScreen = ({ id, onBack, density = "comfortable", setBdImage }) => {
  const item = window.LIBRARY.find(x => x.id === id) || window.LIBRARY[0];
  const [season, setSeason] = useStateS(1);
  const seasons = item.type === "Anime" || item.type === "Series" ? [1, 2] : [];
  const totalEps = item.episodes || 12;
  const eps = Array.from({ length: Math.min(totalEps, 24) }, (_, i) => ({
    n: i + 1,
    title: `Episode ${i+1}`,
    have: Math.random() > 0.18 || i < 12,
    runtime: item.runtime,
  }));

  // Push this item's backdrop into the global state so the page backdrop and
  // the hero stay in sync.
  useEffectS(() => {
    setBdImage?.(item.bd, item.hue);
  }, [item.bd, item.hue, setBdImage]);

  const sidePad = density === "compact" ? 36 : 48;

  return (
    <div>
      {/* back link — constrained */}
      <window.Container density={density} top>
        <button onClick={onBack} style={{
          all:"unset", cursor:"pointer", color:"var(--text-dim)", fontSize: 12,
          display:"inline-flex", alignItems:"center", gap: 6, marginBottom: 18,
          fontFamily:"var(--font-mono)", letterSpacing: 1,
        }}>
          <Icon name="chev-l" size={12} /> BACK TO CATALOG
        </button>
      </window.Container>

      {/* fanart hero — contained card with its own image */}
      <window.Container density={density}>
        <div style={{
          position:"relative", height: 460, borderRadius: 18, overflow:"hidden", marginBottom: 30,
          boxShadow:"0 30px 60px -30px rgba(0,0,0,0.7)",
        }}>
          <div key={item.bd} style={{
            position:"absolute", inset: 0,
            backgroundImage:`url("${item.bd}")`, backgroundSize:"cover", backgroundPosition:"center",
            animation:"detail-pan 28s ease-in-out infinite alternate",
          }} />
          <div style={{
            position:"absolute", inset: 0,
            background:`linear-gradient(180deg, transparent 30%, rgba(10,8,7,0.85) 80%, var(--bg-0) 100%), linear-gradient(90deg, rgba(10,8,7,0.6) 0%, transparent 60%)`,
          }} />
          <div style={{
            position:"absolute", right: 30, top: 30, fontFamily:"var(--font-cjk)",
            fontSize: 220, lineHeight: 0.85, color: `oklch(0.95 0.05 ${item.hue} / 0.08)`,
            writingMode:"vertical-rl", textOrientation:"upright", letterSpacing: 4,
            pointerEvents:"none", userSelect:"none",
          }}>{item.cjk}</div>

          <div style={{ position:"absolute", left: 36, bottom: 30, right: 36, display:"flex", alignItems:"flex-end", gap: 28 }}>
            <div style={{ width: 200, height: 290, flexShrink: 0, transform:"translateY(40px)" }}>
              <Poster item={item} w={200} h={290} ribbon={false} />
            </div>
            <div style={{ flex: 1, paddingBottom: 4 }}>
              <div style={{ display:"flex", gap: 8, marginBottom: 12, flexWrap:"wrap" }}>
                <Pill tone="accent">{item.type.toUpperCase()}</Pill>
                {item.tags?.map(t => <Pill key={t}>{t}</Pill>)}
              </div>
              <h1 style={{ margin: 0, fontFamily:"var(--font-display)", fontSize: 56, lineHeight: 0.95, letterSpacing: -1.6 }}>
                {item.title.toUpperCase()}
              </h1>
              <div style={{ display:"flex", flexWrap:"wrap", gap: 18, marginTop: 12, fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-dim)", letterSpacing: 0.8 }}>
                <span style={{ color:"var(--accent-hi)" }}>★ {item.rating.toFixed(1)}</span>
                <span>{item.year}</span>
                <span>{item.studio}</span>
                <span>{item.runtime} / ep</span>
                <span>{item.lang}</span>
                <span style={{ color: item.conf > 0.9 ? "var(--success)" : "var(--warn)" }}>
                  {Math.round(item.conf*100)}% MATCH
                </span>
              </div>
            </div>
          </div>
        </div>

        {/* metadata + actions */}
        <div style={{ display:"grid", gridTemplateColumns: "1fr 320px", gap: 36, marginBottom: 36 }}>
          <div>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color:"var(--accent-hi)", marginBottom: 10 }}>SYNOPSIS</div>
            <p style={{ margin: 0, fontSize: 15, lineHeight: 1.65, color:"var(--text)", textWrap:"pretty", maxWidth: 720 }}>{item.overview}</p>

            <div style={{ display:"flex", gap: 12, marginTop: 22, flexWrap:"wrap" }}>
              <Btn kind="primary" icon="external">Open in TMDB</Btn>
              <Btn kind="solid" icon="external">MAL</Btn>
              <Btn kind="solid" icon="external">IMDb</Btn>
              <Btn kind="ghost" icon="magic">Re-identify</Btn>
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
                <span style={{ flex: 1, fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-faint)" }}>{idv}</span>
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

        {/* SEASONS / EPISODES */}
        {seasons.length > 0 && (
          <div>
            <div style={{ display:"flex", alignItems:"center", justifyContent:"space-between", marginBottom: 18 }}>
              <div>
                <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color:"var(--accent-hi)" }}>EPISODES</div>
                <h2 style={{ margin: "6px 0 0", fontFamily:"var(--font-display)", fontSize: 28, letterSpacing:-0.6 }}>
                  SEASON {String(season).padStart(2,"0")}
                </h2>
              </div>
              <div style={{ display:"flex", gap: 6 }}>
                {seasons.map(s => (
                  <button key={s} onClick={() => setSeason(s)} style={{
                    all:"unset", cursor:"pointer",
                    padding:"8px 14px", borderRadius: 7,
                    fontFamily:"var(--font-mono)", fontSize: 11, letterSpacing: 0.8,
                    background: s === season ? "var(--accent-soft)" : "var(--surface)",
                    color: s === season ? "var(--accent-hi)" : "var(--text-dim)",
                    border: s === season ? "1px solid var(--accent-line)" : "1px solid var(--border)",
                  }}>SEASON {String(s).padStart(2,"0")}</button>
                ))}
              </div>
            </div>

            <div style={{
              display:"grid",
              gridTemplateColumns:"repeat(auto-fill, minmax(220px, 1fr))",
              gap: 14,
            }}>
              {eps.map(ep => (
                <div key={ep.n} style={{
                  position:"relative",
                  background: ep.have ? "var(--surface)" : "rgba(21,17,14,0.55)",
                  border: ep.have ? "1px solid var(--border)" : "1px dashed rgba(255,240,220,0.12)",
                  borderRadius: 10,
                  overflow:"hidden",
                  opacity: ep.have ? 1 : 0.50,
                  transition:"transform .2s ease, border-color .2s ease, opacity .2s ease",
                  cursor: ep.have ? "pointer" : "default",
                }}
                onMouseEnter={e => {
                  if (ep.have) {
                    e.currentTarget.style.transform = "translateY(-2px)";
                    e.currentTarget.style.borderColor = "var(--accent-line)";
                  }
                  const ind = e.currentTarget.querySelector('.ep-hover');
                  if (ind) ind.style.opacity = '1';
                }}
                onMouseLeave={e => {
                  e.currentTarget.style.transform = "";
                  e.currentTarget.style.borderColor = ep.have ? "var(--border)" : "rgba(255,240,220,0.12)";
                  const ind = e.currentTarget.querySelector('.ep-hover');
                  if (ind) ind.style.opacity = '0';
                }}>
                  <div style={{
                    height: 100, position:"relative",
                    backgroundImage:`url("${item.bd}")`,
                    backgroundSize:"cover",
                    backgroundPosition: `${(ep.n * 19) % 100}% center`,
                    filter: ep.have ? "none" : "grayscale(1) brightness(0.55)",
                  }}>
                    <div style={{
                      position:"absolute", inset: 0,
                      background: "linear-gradient(180deg, transparent 30%, rgba(0,0,0,0.75) 100%)",
                    }} />
                    <div style={{
                      position:"absolute", top: 8, left: 10,
                      fontFamily:"var(--font-display)", fontSize: 26, color: ep.have ? "#fff" : "rgba(255,255,255,0.55)",
                      textShadow: "0 2px 8px rgba(0,0,0,0.7)",
                    }}>{String(ep.n).padStart(2,"0")}</div>
                    {/* hover indicator */}
                    {ep.have ? (
                      <div className="ep-hover" style={{
                        position:"absolute", left:"50%", top:"50%", transform:"translate(-50%, -50%)",
                        width: 40, height: 40, borderRadius: 40,
                        background:"rgba(0,0,0,0.5)", backdropFilter:"blur(8px)",
                        display:"grid", placeItems:"center",
                        border:"1px solid rgba(255,255,255,0.3)",
                        opacity: 0, transition: "opacity .15s ease",
                      }}>
                        <Icon name="play" size={16} style={{ color:"#fff", marginLeft: 2 }} />
                      </div>
                    ) : (
                      <div className="ep-hover" style={{
                        position:"absolute", left:"50%", top:"50%", transform:"translate(-50%, -50%)",
                        width: 50, height: 50, borderRadius: 10,
                        background:"rgba(20,15,12,0.7)", backdropFilter:"blur(8px)",
                        display:"flex", flexDirection:"column", alignItems:"center", justifyContent:"center",
                        border:"1px dashed rgba(255,240,220,0.25)",
                        color:"var(--warn)",
                        fontFamily:"var(--font-mono)", fontSize: 8, letterSpacing: 1, textTransform:"uppercase",
                        gap: 2, opacity: 0, transition: "opacity .15s ease",
                      }}>
                        <Icon name="download" size={13} stroke={1.8} />
                        <span>Empty</span>
                      </div>
                    )}
                    {ep.have ? (
                      <div style={{ position:"absolute", bottom: 8, right: 8 }}>
                        <Pill tone="success">ON DISK</Pill>
                      </div>
                    ) : (
                      <div style={{ position:"absolute", bottom: 8, right: 8 }}>
                        <Pill tone="warn">MISSING</Pill>
                      </div>
                    )}
                  </div>
                  <div style={{ padding: "10px 12px 12px" }}>
                    <div style={{ fontSize: 13, fontWeight: 600, color: ep.have ? "var(--text)" : "var(--text-dim)" }}>{ep.title}</div>
                    <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-faint)", marginTop: 4 }}>
                      {ep.runtime} · 1080p {!ep.have && <span style={{ color:"var(--warn)" }}>· Not downloaded</span>}
                    </div>
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}
      </window.Container>
      <style>{`
        @keyframes detail-pan {
          0%   { transform: scale(1.04) translate(-1.5%, -1%); }
          100% { transform: scale(1.10) translate(1.5%, 1%); }
        }
      `}</style>
    </div>
  );
};

// ============================================================
// EXPLORER
// ============================================================
const ExplorerScreen = ({ onOpen }) => {
  const [openId, setOpenId] = useStateS(null);
  return (
    <div>
      <PageHeader
        overline="ON DISK"
        title="EXPLORER"
        sub="Watched section folders. Each subdirectory is auto-registered as a folder watcher. Animarr never renames folders on disk — only files, only by your patterns."
        right={
          <>
            <Btn kind="ghost" icon="refresh">Rescan all</Btn>
            <Btn kind="primary" icon="plus">Add section folder</Btn>
          </>
        }
      />

      {/* needs review banner */}
      <div style={{
        marginBottom: 28,
        background: "linear-gradient(135deg, oklch(0.30 0.16 60 / 0.18), transparent 80%), var(--surface)",
        border:"1px solid oklch(0.55 0.16 60 / 0.4)",
        borderRadius: 14,
        padding: 22,
      }}>
        <div style={{ display:"flex", alignItems:"center", gap: 10, marginBottom: 14 }}>
          <div style={{
            width: 28, height: 28, borderRadius: 8, display:"grid", placeItems:"center",
            background:"oklch(0.80 0.17 75 / 0.2)", color:"var(--warn)",
          }}><Icon name="warn" size={15} /></div>
          <div>
            <div style={{ fontFamily:"var(--font-display)", fontSize: 17, letterSpacing: -0.3 }}>NEEDS REVIEW</div>
            <div style={{ fontSize: 12.5, color:"var(--text-dim)" }}>{window.NEEDS_REVIEW.length} titles below the confidence threshold — pick the right match.</div>
          </div>
        </div>
        {window.NEEDS_REVIEW.map(nr => (
          <div key={nr.id} style={{
            background:"rgba(0,0,0,0.25)", borderRadius: 10, padding: 14, marginTop: 12,
          }}>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 11.5, color:"var(--text-dim)", marginBottom: 10 }}>{nr.folder}</div>
            <div style={{ display:"flex", gap: 12, flexWrap:"wrap" }}>
              {nr.candidates.map(c => (
                <div key={c.title} style={{
                  display:"flex", gap: 12,
                  background:"var(--surface-2)", border:"1px solid var(--border)", borderRadius: 8,
                  padding: 8, minWidth: 280, flex: "1 1 280px",
                }}>
                  <Poster item={{ id: c.title, title: c.title, cjk: c.cjk, year: c.year, type:"?", hue: c.hue }} w={64} h={92} ribbon={false} />
                  <div style={{ flex: 1, display:"flex", flexDirection:"column", justifyContent:"space-between" }}>
                    <div>
                      <div style={{ fontFamily:"var(--font-display)", fontSize: 14, letterSpacing:-0.2 }}>{c.title}</div>
                      <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-faint)", marginTop: 4 }}>{c.year} · {c.source}</div>
                    </div>
                    <div style={{ display:"flex", justifyContent:"space-between", alignItems:"center" }}>
                      <span style={{
                        fontFamily:"var(--font-mono)", fontSize: 10, letterSpacing: 0.6,
                        color: c.conf > 0.7 ? "var(--success)" : "var(--warn)",
                      }}>{Math.round(c.conf*100)}% match</span>
                      <Btn kind="primary" style={{ padding:"4px 9px", fontSize: 11 }}>Use</Btn>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          </div>
        ))}
      </div>

      {/* section folders */}
      <div style={{ display:"grid", gridTemplateColumns:"repeat(auto-fill, minmax(280px, 1fr))", gap: 18 }}>
        {window.FOLDERS.map(f => {
          const isOpen = openId === f.id;
          return (
            <div key={f.id}
              onClick={() => setOpenId(isOpen ? null : f.id)}
              style={{
                cursor:"pointer",
                position:"relative", borderRadius: 14, overflow:"hidden",
                height: 200,
                border:"1px solid var(--border)",
                gridColumn: isOpen ? "1 / -1" : "auto",
                transition: "grid-column .3s ease",
            }}>
              <div style={{
                position:"absolute", inset: 0,
                backgroundImage:`url("${f.bd}")`, backgroundSize:"cover", backgroundPosition:"center",
                filter:`saturate(0.8)`,
              }} />
              <div style={{
                position:"absolute", inset: 0,
                background:`linear-gradient(135deg, oklch(0.18 0.06 ${f.hue} / 0.7), oklch(0.10 0.04 ${f.hue} / 0.95))`,
              }} />
              <div style={{
                position:"absolute", inset: 0,
                background:"linear-gradient(180deg, transparent 50%, rgba(0,0,0,0.75) 100%)",
              }} />
              <div style={{
                position:"absolute", top: 10, right: 14, fontFamily:"var(--font-cjk)", fontSize: 80,
                color: `oklch(0.95 0.05 ${f.hue} / 0.12)`, lineHeight: 0.85,
                pointerEvents:"none",
              }}>{f.title.slice(0,1)}</div>

              <div style={{ position:"absolute", left: 18, top: 18, right: 18 }}>
                <Icon name="folder" size={22} style={{ color:"#fff", opacity: 0.85 }} />
              </div>
              <div style={{ position:"absolute", left: 18, bottom: 16, right: 18 }}>
                <div style={{ fontFamily:"var(--font-display)", fontSize: 26, letterSpacing: -0.5 }}>{f.title.toUpperCase()}</div>
                <div style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)", marginTop: 4, wordBreak:"break-all" }}>{f.path}</div>
                <div style={{ display:"flex", gap: 8, marginTop: 10 }}>
                  <Pill>{f.watchers} watchers</Pill>
                  <Pill tone="success">{f.ident} ID</Pill>
                  {f.missing > 0 && <Pill tone="warn">{f.missing} ?</Pill>}
                </div>
              </div>
            </div>
          );
        })}
      </div>

      {/* expanded folder body */}
      {openId && (
        <div style={{
          marginTop: 22, background:"var(--surface)", borderRadius: 14,
          border:"1px solid var(--border)", padding: 22,
        }}>
          <div style={{ display:"flex", alignItems:"center", gap: 12, marginBottom: 16 }}>
            <Btn kind="ghost" icon="refresh">Rescan</Btn>
            <Btn kind="ghost" icon="magic">Identify selected</Btn>
            <Btn kind="ghost" icon="pencil">Edit patterns</Btn>
            <Btn kind="ghost" icon="undo">Restore deleted</Btn>
            <div style={{ flex: 1 }} />
            <Btn kind="primary" icon="check">Apply 12 renames</Btn>
            <Btn kind="danger" icon="trash">Delete</Btn>
          </div>

          <div style={{ display:"grid", gridTemplateColumns:"repeat(auto-fill, minmax(140px, 1fr))", gap: 14 }}>
            {window.LIBRARY.slice(0, 10).map(i => (
              <div key={i.id}>
                <Poster item={i} w={"100%"} h={210} onClick={() => onOpen(i.id)} />
                <div style={{ fontSize: 11, color:"var(--text-dim)", marginTop: 6, textAlign:"center" }}>{i.title}</div>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
};

// ============================================================
// TORRENTS
// ============================================================
const TorrentsScreen = () => {
  const init = window.__init || {};
  const [adding, setAdding] = useStateS(!!init.openAddTorrentDrawer);
  const [tab, setTab] = useStateS("active");
  const [addMode, setAddMode] = useStateS("file");
  const [destIdx, setDestIdx] = useStateS(0);
  const [tickT, setTickT] = useStateS(Date.now());

  // live tick — simulate progress on downloading rows
  useEffectS(() => {
    const t = setInterval(() => setTickT(Date.now()), 1100);
    return () => clearInterval(t);
  }, []);

  const torrents = window.TORRENTS.map(t => {
    if (t.state !== "downloading") return t;
    // bounce progress visually
    const phase = ((Date.now() / 12000) + parseInt(t.id.slice(1)) * 0.13) % 1;
    return { ...t, progress: Math.min(0.999, t.progress + phase * 0.08) };
  });

  const filtered = torrents.filter(t => {
    if (tab === "active") return t.state !== "queued";
    if (tab === "queued") return t.state === "queued";
    return true;
  });

  const total = torrents.reduce((s, t) => s + (t.state === "downloading" ? t.dn : 0), 0);
  const totalUp = torrents.reduce((s, t) => s + t.up, 0);

  return (
    <div>
      <PageHeader
        overline={`LIVE · ${torrents.filter(t=>t.state==="downloading").length} ACTIVE`}
        title="TORRENTS"
        sub="Per-file priority, per-torrent speed caps in Mbps, auto-rename after download via global patterns. Updates over SignalR — no refresh."
        right={
          <>
            <div style={{
              display:"flex", gap: 18,
              background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 10,
              padding: "10px 18px",
            }}>
              <div>
                <div style={{ fontFamily:"var(--font-mono)", fontSize: 9, color:"var(--text-faint)", letterSpacing: 1 }}>DOWN</div>
                <div style={{ fontFamily:"var(--font-display)", fontSize: 18, color:"var(--accent-hi)" }}>{total.toFixed(1)} <span style={{ fontSize: 10, color:"var(--text-faint)" }}>MB/s</span></div>
              </div>
              <div>
                <div style={{ fontFamily:"var(--font-mono)", fontSize: 9, color:"var(--text-faint)", letterSpacing: 1 }}>UP</div>
                <div style={{ fontFamily:"var(--font-display)", fontSize: 18, color:"var(--success)" }}>{totalUp.toFixed(1)} <span style={{ fontSize: 10, color:"var(--text-faint)" }}>MB/s</span></div>
              </div>
            </div>
            <Btn kind="primary" icon="plus" onClick={() => setAdding(true)}>Add torrent</Btn>
          </>
        }
      />

      {/* tabs */}
      <div style={{ display:"flex", gap: 4, padding: 4, background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 10, marginBottom: 18, width:"fit-content" }}>
        {[
          ["active","Active",torrents.filter(t=>t.state!=="queued").length],
          ["queued","Queued",torrents.filter(t=>t.state==="queued").length],
          ["all","History", torrents.length],
        ].map(([k,l,c]) => (
          <button key={k} onClick={() => setTab(k)} style={{
            all:"unset", cursor:"pointer",
            padding:"7px 18px", borderRadius: 7, fontSize: 12.5, fontWeight: 600,
            color: tab === k ? "var(--text)" : "var(--text-dim)",
            background: tab === k ? "var(--surface-3)" : "transparent",
            boxShadow: tab === k ? "0 1px 0 var(--accent) inset" : "none",
            display:"flex", alignItems:"center", gap: 8,
          }}>{l} <span style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)" }}>{c}</span></button>
        ))}
      </div>

      {/* table */}
      <div style={{ background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 14, overflow:"hidden" }}>
        <div style={{
          display:"grid",
          gridTemplateColumns:"1fr 240px 160px 150px 90px 100px",
          gap: 16,
          padding:"12px 22px",
          borderBottom:"1px solid var(--border)",
          fontFamily:"var(--font-mono)", fontSize: 9.5, letterSpacing: 1.2, color:"var(--text-faint)", textTransform:"uppercase",
        }}>
          <span>NAME</span><span>DESTINATION</span><span>PROGRESS</span><span>SPEED</span><span>ETA</span><span>SIZE</span>
        </div>
        {filtered.map((t, i) => (
          <TorrentRow key={t.id} t={t} idx={i} />
        ))}
      </div>

      {/* add drawer */}
      {adding && <AddTorrentDrawer onClose={() => setAdding(false)} mode={addMode} setMode={setAddMode} destIdx={destIdx} setDestIdx={setDestIdx} />}
    </div>
  );
};

const TorrentRow = ({ t, idx }) => {
  const [hover, setHover] = useStateS(false);
  const stateColor = t.state === "downloading" ? "var(--accent-hi)" : t.state === "seeding" ? "var(--success)" : "var(--text-faint)";
  return (
    <div
      onMouseEnter={()=>setHover(true)} onMouseLeave={()=>setHover(false)}
      style={{
        display:"grid",
        gridTemplateColumns:"1fr 240px 160px 150px 90px 100px",
        gap: 16,
        padding:"14px 22px",
        alignItems:"center",
        borderBottom: "1px solid var(--border)",
        background: hover ? "rgba(255,255,255,0.025)" : "transparent",
        fontSize: 13,
        position:"relative",
      }}
    >
      <div style={{ display:"flex", alignItems:"center", gap: 10, minWidth: 0 }}>
        <span style={{
          width: 8, height: 8, borderRadius: 8, flexShrink: 0,
          background: stateColor,
          boxShadow: t.state === "downloading" ? `0 0 10px ${stateColor}` : "none",
          animation: t.state === "downloading" ? "blink 1.4s ease-in-out infinite" : "none",
        }} />
        <span style={{ fontFamily:"var(--font-mono)", fontSize: 12, color:"var(--text)", overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap" }}>
          {t.name}
        </span>
      </div>
      <span style={{
        fontSize: 12.5,
        color: t.dest.startsWith("—") ? "var(--warn)" : "var(--text-dim)",
        overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap",
      }}>{t.dest}</span>
      <div>
        <div style={{ height: 4, background:"rgba(255,255,255,0.05)", borderRadius: 4, overflow:"hidden" }}>
          <div style={{
            width: `${t.progress*100}%`, height:"100%",
            background: t.state === "downloading"
              ? `linear-gradient(90deg, var(--accent), var(--accent-hi))`
              : t.state === "seeding"
              ? `linear-gradient(90deg, var(--success), oklch(0.85 0.15 150))`
              : "rgba(255,255,255,0.2)",
            boxShadow: t.state === "downloading" ? `0 0 6px var(--accent-soft)` : "none",
          }} />
        </div>
        <div style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)", marginTop: 4 }}>{(t.progress*100).toFixed(1)}%</div>
      </div>
      <div style={{ fontFamily:"var(--font-mono)", fontSize: 11.5, color:"var(--text-dim)" }}>
        {t.state === "downloading" ? (
          <span>↓ <span style={{ color:"var(--accent-hi)" }}>{t.dn.toFixed(1)}</span> <span style={{ color:"var(--text-faint)" }}>{t.peers}</span></span>
        ) : t.state === "seeding" ? (
          <span>↑ <span style={{ color:"var(--success)" }}>{t.up.toFixed(1)}</span> <span style={{ color:"var(--text-faint)" }}>{t.peers}</span></span>
        ) : (
          <span style={{ color:"var(--text-faint)" }}>—</span>
        )}
      </div>
      <span style={{ fontFamily:"var(--font-mono)", fontSize: 11.5, color:"var(--text-dim)" }}>{t.eta}</span>
      <span style={{ fontFamily:"var(--font-mono)", fontSize: 11.5, color:"var(--text-dim)" }}>{t.size}</span>
      <style>{`@keyframes blink { 0%,100% { opacity: 1; } 50% { opacity: 0.4; } }`}</style>
    </div>
  );
};

const AddTorrentDrawer = ({ onClose, mode, setMode, destIdx, setDestIdx }) => {
  const dests = window.LIBRARY.slice(0, 12);
  return (
    <>
      <div onClick={onClose} style={{
        position:"fixed", inset: 0, background:"rgba(0,0,0,0.55)", backdropFilter:"blur(3px)",
        zIndex: 30,
      }} />
      <div style={{
        position:"fixed", top: 0, right: 0, bottom: 0, width: 480,
        background: "linear-gradient(180deg, var(--surface) 0%, var(--surface-2) 100%)",
        borderLeft:"1px solid var(--border-strong)",
        zIndex: 31, padding: 28, overflow:"auto",
        boxShadow:"-30px 0 60px -20px rgba(0,0,0,0.6)",
      }}>
        <div style={{ display:"flex", justifyContent:"space-between", alignItems:"flex-start", marginBottom: 22 }}>
          <div>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--accent-hi)", letterSpacing: 1.4 }}>NEW</div>
            <h2 style={{ margin: "6px 0 0", fontFamily:"var(--font-display)", fontSize: 32, letterSpacing: -0.6 }}>ADD TORRENT</h2>
          </div>
          <button onClick={onClose} style={{ all:"unset", cursor:"pointer", color:"var(--text-dim)", padding: 4 }}>
            <Icon name="x" size={18} />
          </button>
        </div>

        {/* source toggle */}
        <div style={{
          display:"grid", gridTemplateColumns:"1fr 1fr", gap: 0,
          background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 8,
          marginBottom: 20, padding: 4,
        }}>
          {["file","magnet"].map(m => (
            <button key={m} onClick={() => setMode(m)} style={{
              all:"unset", cursor:"pointer", padding: "9px 0", textAlign:"center",
              fontSize: 12.5, fontWeight: 600,
              color: mode === m ? "var(--text)" : "var(--text-dim)",
              background: mode === m ? "var(--surface-3)" : "transparent",
              borderRadius: 6,
              display:"flex", alignItems:"center", justifyContent:"center", gap: 8,
            }}>
              <Icon name={m === "file" ? "file" : "link"} size={13} />
              {m === "file" ? ".torrent file" : "Magnet link"}
            </button>
          ))}
        </div>

        {mode === "file" ? (
          <div style={{ marginBottom: 20 }}>
            <div style={{
              border:"1.5px dashed var(--border-strong)", borderRadius: 10,
              padding: 22, textAlign:"center",
              background:"var(--bg-1)",
            }}>
              <Icon name="upload" size={20} style={{ color:"var(--text-dim)" }} />
              <div style={{ fontSize: 13, color:"var(--text-dim)", marginTop: 10 }}>Drop a .torrent file or <span style={{ color:"var(--accent-hi)", textDecoration:"underline" }}>browse</span></div>
              <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--success)", marginTop: 14 }}>
                <Icon name="check" size={10} stroke={2.4} /> anistar45526.torrent loaded · 824.1 MB
              </div>
            </div>
          </div>
        ) : (
          <Field label="Magnet URI">
            <Input mono placeholder="magnet:?xt=urn:btih:…" defaultValue="magnet:?xt=urn:btih:1a3b…" />
          </Field>
        )}

        {/* file selection */}
        <div style={{ marginBottom: 20 }}>
          <div style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)", letterSpacing: 1, textTransform:"uppercase", marginBottom: 8 }}>FILES (1)</div>
          <div style={{ background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 8, padding: 10 }}>
            <div style={{ display:"flex", alignItems:"center", gap: 10 }}>
              <input type="checkbox" defaultChecked style={{ accentColor:"var(--accent)" }} />
              <Icon name="video" size={14} style={{ color:"var(--accent-hi)" }} />
              <span style={{ fontFamily:"var(--font-mono)", fontSize: 11.5, flex: 1, overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap", color:"var(--text)" }}>
                [Anistar.org] Perfect World [TV-1] - 270 [1080p].mp4
              </span>
              <span style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-faint)" }}>824.1 MB</span>
              <Select defaultValue="Normal" style={{ padding:"3px 22px 3px 8px", fontSize: 11 }}>
                <option>Normal</option><option>High</option><option>Low</option><option>Skip</option>
              </Select>
            </div>
          </div>
        </div>

        <Field label="Destination — identified titles only">
          <div style={{ display:"flex", gap: 6 }}>
            <Select value={destIdx} onChange={e => setDestIdx(+e.target.value)} style={{ flex: 1 }}>
              {dests.map((d,i) => <option key={d.id} value={i}>{d.title} ({d.year})</option>)}
            </Select>
            <Btn kind="solid" icon="plus" style={{ padding:"7px 10px" }}>Subfolder</Btn>
          </div>
        </Field>

        <div style={{ marginTop: 14 }}>
          <Field label="Or path manually">
            <Input mono defaultValue="/mnt/media/Anime" />
          </Field>
        </div>

        <div style={{ display:"grid", gridTemplateColumns:"1fr 1fr", gap: 12, marginTop: 14 }}>
          <Field label="Limit ↓ (Mbps)"><Input mono defaultValue="0" /></Field>
          <Field label="Limit ↑ (Mbps)"><Input mono defaultValue="0" /></Field>
        </div>

        <div style={{ marginTop: 18, display:"flex", flexDirection:"column", gap: 10 }}>
          <ToggleRow label="Start paused" />
          <ToggleRow label="Stop after download (no seed)" />
          <ToggleRow label="Flatten subfolders after download" on />
          <ToggleRow label="Strip root folder before download" />
          <ToggleRow label="Auto-rename files via global patterns" on />
        </div>

        <div style={{ display:"flex", gap: 10, marginTop: 26 }}>
          <Btn kind="primary" icon="download">Add torrent</Btn>
          <Btn kind="ghost" onClick={onClose}>Cancel</Btn>
        </div>
      </div>
    </>
  );
};

const ToggleRow = ({ label, on: initial = false }) => {
  const [on, setOn] = useStateS(initial);
  return (
    <div style={{ display:"flex", alignItems:"center", gap: 10, fontSize: 13 }}>
      <Toggle on={on} onChange={setOn} />
      <span style={{ color:"var(--text-dim)" }}>{label}</span>
    </div>
  );
};

// ============================================================
// HISTORY
// ============================================================
const HistoryScreen = () => {
  const [filter, setFilter] = useStateS("");
  const groups = {};
  window.HISTORY.forEach(h => {
    groups[h.date] = groups[h.date] || [];
    groups[h.date].push(h);
  });

  return (
    <div>
      <PageHeader
        overline="JOURNAL"
        title="RENAME HISTORY"
        sub="Every file rename, persisted in SQLite. One-click revert restores the original filename on disk. Folder names are never touched."
        right={
          <>
            <div style={{ display:"flex", alignItems:"center", gap: 8, background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 10, padding:"7px 12px", width: 280 }}>
              <Icon name="search" size={14} />
              <input value={filter} onChange={e=>setFilter(e.target.value)} placeholder="Filter file or pattern…" style={{ all:"unset", flex:1, color:"var(--text)", fontSize: 13 }} />
            </div>
            <Btn kind="ghost" icon="filter">Filters</Btn>
          </>
        }
      />

      <div style={{
        display:"grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 14, marginBottom: 26,
      }}>
        {[
          ["TOTAL RENAMES", window.HISTORY.length, "var(--text)"],
          ["TODAY",         4,                     "var(--accent-hi)"],
          ["REVERTED",      1,                     "var(--warn)"],
          ["PATTERNS USED", 4,                     "var(--success)"],
        ].map(([l, v, c]) => (
          <div key={l} style={{ background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 12, padding: 18 }}>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)", letterSpacing: 1.2 }}>{l}</div>
            <div style={{ fontFamily:"var(--font-display)", fontSize: 36, color: c, marginTop: 6, lineHeight: 1 }}>{v}</div>
          </div>
        ))}
      </div>

      {Object.entries(groups).map(([date, rows]) => (
        <div key={date} style={{ marginBottom: 22 }}>
          <div style={{
            display:"flex", alignItems:"center", gap: 12, marginBottom: 12,
          }}>
            <div style={{ fontFamily:"var(--font-display)", fontSize: 22, letterSpacing:-0.4 }}>{new Date(date).toLocaleDateString("en", { day:"2-digit", month:"long", year:"numeric" }).toUpperCase()}</div>
            <div style={{ flex:1, height: 1, background:"var(--border)" }} />
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-faint)" }}>{rows.length} actions</div>
          </div>
          <div style={{ background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 12, overflow:"hidden" }}>
            {rows.filter(r => !filter || r.file.toLowerCase().includes(filter.toLowerCase()) || r.pattern.toLowerCase().includes(filter.toLowerCase())).map((r,i) => (
              <div key={r.id} style={{
                display:"grid", gridTemplateColumns: "80px 1fr auto 220px 130px",
                gap: 16, padding: "14px 20px", alignItems:"center",
                borderBottom: i === rows.length-1 ? "none" : "1px solid var(--border)",
                opacity: r.reverted ? 0.5 : 1,
              }}>
                <div style={{ fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-faint)" }}>{r.at}</div>
                <div style={{ minWidth: 0 }}>
                  <div style={{ fontFamily:"var(--font-mono)", fontSize: 12, color:"var(--text-dim)", overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap", textDecoration: r.reverted ? "line-through" : "none" }}>{r.file}</div>
                  <div style={{ fontFamily:"var(--font-mono)", fontSize: 12, color:"var(--text)", marginTop: 3, display:"flex", alignItems:"center", gap: 6 }}>
                    <Icon name="chev-r" size={11} style={{ color:"var(--accent)" }} />
                    {r.to}
                  </div>
                </div>
                <Pill tone="accent">{r.pattern}</Pill>
                <div style={{ fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-faint)" }}>{r.folder}</div>
                <div style={{ display:"flex", justifyContent:"flex-end", gap: 6 }}>
                  {r.reverted
                    ? <Pill tone="warn">REVERTED</Pill>
                    : <Btn kind="flat" icon="undo" style={{ padding:"5px 9px", fontSize: 11.5 }}>Revert</Btn>
                  }
                </div>
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
};

// ============================================================
// SETTINGS
// ============================================================
const SettingsScreen = () => {
  const init = window.__init || {};
  const [tab, setTab] = useStateS(init.settingsTab || "general");
  const tabs = [
    ["general","General"],
    ["folders","Root folders"],
    ["history","Rename history"],
    ["appearance","Appearance"],
    ["llm","AI / LLM"],
    ["patterns","Patterns"],
    ["ignore","Ignore rules"],
    ["torrent","Torrent"],
    ["meta","Metadata"],
  ];

  return (
    <div>
      <PageHeader overline="CONFIG" title="SETTINGS" sub="Single-user, no auth. Changes apply live — no restart required." />

      <div style={{ display:"grid", gridTemplateColumns: "220px 1fr", gap: 32 }}>
        {/* vertical tabs */}
        <div style={{
          position:"sticky", top: 20, alignSelf:"start",
          background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 12,
          padding: 8, display:"flex", flexDirection:"column", gap: 2,
        }}>
          {tabs.map(([k,l]) => (
            <button key={k} onClick={() => setTab(k)} style={{
              all:"unset", cursor:"pointer",
              padding:"9px 14px", borderRadius: 7, fontSize: 12.5, fontWeight: 600,
              color: tab === k ? "var(--text)" : "var(--text-dim)",
              background: tab === k ? "var(--surface-3)" : "transparent",
              borderLeft: tab === k ? "2px solid var(--accent)" : "2px solid transparent",
              paddingLeft: 12,
            }}>{l}</button>
          ))}
        </div>

        <div>
          {tab === "general" && <SettingsGeneral />}
          {tab === "folders" && <SettingsFolders />}
          {tab === "history" && <SettingsHistory />}
          {tab === "appearance" && <SettingsAppearance />}
          {tab === "llm" && <SettingsLLM />}
          {tab === "patterns" && <SettingsPatterns />}
          {tab === "ignore" && <SettingsIgnore />}
          {tab === "torrent" && <SettingsTorrent />}
          {tab === "meta" && <SettingsMeta />}
        </div>
      </div>
    </div>
  );
};

const SettingsCard = ({ title, sub, children }) => (
  <section style={{
    background:"var(--surface)", border:"1px solid var(--border)", borderRadius: 14,
    padding: 26, marginBottom: 20,
  }}>
    <div style={{ marginBottom: 18 }}>
      <h3 style={{ margin: 0, fontFamily:"var(--font-display)", fontSize: 22, letterSpacing:-0.4 }}>{title.toUpperCase()}</h3>
      {sub && <div style={{ color:"var(--text-dim)", fontSize: 13, marginTop: 6 }}>{sub}</div>}
    </div>
    {children}
  </section>
);

// ── Root folders tab — manage watched section folders ────────────────────
const SettingsFolders = () => (
  <SettingsCard title="Root folders" sub="Section folders Animarr watches. Each immediate subdirectory becomes its own FolderWatcher. Animarr never renames folders on disk.">
    <div style={{ display:"flex", flexDirection:"column", gap: 10, marginBottom: 18 }}>
      {window.FOLDERS.map(f => (
        <div key={f.id} style={{
          display:"grid", gridTemplateColumns:"44px 1fr 110px auto auto",
          gap: 14, alignItems:"center",
          background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10,
          padding: "12px 14px",
        }}>
          <div style={{
            width: 36, height: 36, borderRadius: 8,
            backgroundImage:`url("${f.bd}")`, backgroundSize:"cover", backgroundPosition:"center",
            position:"relative", overflow:"hidden",
          }}>
            <div style={{
              position:"absolute", inset: 0,
              background:`linear-gradient(135deg, oklch(0.20 0.06 ${f.hue} / 0.4), oklch(0.10 0.04 ${f.hue} / 0.7))`,
            }} />
            <div style={{
              position:"absolute", inset: 0,
              display:"grid", placeItems:"center", color:"#fff",
            }}><Icon name="folder" size={14} /></div>
          </div>

          <div style={{ minWidth: 0 }}>
            <div style={{ fontSize: 13.5, fontWeight: 600, color:"var(--text)" }}>{f.title}</div>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-faint)", marginTop: 2, overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap" }}>{f.path}</div>
          </div>

          <div style={{ display:"flex", gap: 6 }}>
            <Pill>{f.watchers}w</Pill>
            <Pill tone="success">{f.ident}</Pill>
            {f.missing > 0 && <Pill tone="warn">{f.missing}?</Pill>}
          </div>

          <Btn kind="ghost" icon="refresh" style={{ padding:"5px 9px", fontSize: 11.5 }}>Rescan</Btn>
          <div style={{ display:"flex", gap: 4 }}>
            <button title="Edit" style={{ all:"unset", cursor:"pointer", padding: 6, color:"var(--text-faint)" }}><Icon name="pencil" size={14} /></button>
            <button title="Remove" style={{ all:"unset", cursor:"pointer", padding: 6, color:"var(--text-faint)" }}><Icon name="trash" size={14} /></button>
          </div>
        </div>
      ))}
    </div>
    <Btn kind="primary" icon="plus">Add section folder</Btn>
  </SettingsCard>
);

// ── Rename history tab — moved from /history route ───────────────────────
const SettingsHistory = () => {
  const [filter, setFilter] = useStateS("");
  const groups = {};
  window.HISTORY.forEach(h => {
    groups[h.date] = groups[h.date] || [];
    groups[h.date].push(h);
  });
  const filteredRows = (rs) => rs.filter(r => !filter || r.file.toLowerCase().includes(filter.toLowerCase()) || r.pattern.toLowerCase().includes(filter.toLowerCase()));

  return (
    <SettingsCard title="Rename history" sub="Every file rename, persisted in SQLite. One-click revert restores the original filename on disk. Folder names are never touched.">
      <div style={{ display:"flex", alignItems:"center", gap: 12, marginBottom: 18 }}>
        <div style={{ display:"flex", alignItems:"center", gap: 8, background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10, padding:"7px 12px", flex: 1, maxWidth: 320 }}>
          <Icon name="search" size={14} />
          <input value={filter} onChange={e=>setFilter(e.target.value)} placeholder="Filter file or pattern…" style={{ all:"unset", flex:1, color:"var(--text)", fontSize: 13 }} />
        </div>
        <Btn kind="ghost" icon="filter">Filters</Btn>
      </div>

      <div style={{ display:"grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 12, marginBottom: 22 }}>
        {[
          ["TOTAL RENAMES", window.HISTORY.length, "var(--text)"],
          ["TODAY",         4,                     "var(--accent-hi)"],
          ["REVERTED",      1,                     "var(--warn)"],
          ["PATTERNS USED", 4,                     "var(--success)"],
        ].map(([l, v, c]) => (
          <div key={l} style={{ background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10, padding: 14 }}>
            <div style={{ fontFamily:"var(--font-mono)", fontSize: 9.5, color:"var(--text-faint)", letterSpacing: 1.2 }}>{l}</div>
            <div style={{ fontFamily:"var(--font-display)", fontSize: 28, color: c, marginTop: 4, lineHeight: 1 }}>{v}</div>
          </div>
        ))}
      </div>

      {Object.entries(groups).map(([date, rows]) => {
        const visible = filteredRows(rows);
        if (!visible.length) return null;
        return (
          <div key={date} style={{ marginBottom: 18 }}>
            <div style={{ display:"flex", alignItems:"center", gap: 12, marginBottom: 10 }}>
              <div style={{ fontFamily:"var(--font-display)", fontSize: 16, letterSpacing:-0.3, color:"var(--text-dim)" }}>
                {new Date(date).toLocaleDateString("en", { day:"2-digit", month:"long", year:"numeric" }).toUpperCase()}
              </div>
              <div style={{ flex:1, height: 1, background:"var(--border)" }} />
              <div style={{ fontFamily:"var(--font-mono)", fontSize: 10, color:"var(--text-faint)" }}>{visible.length}</div>
            </div>
            <div style={{ background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10, overflow:"hidden" }}>
              {visible.map((r,i) => (
                <div key={r.id} style={{
                  display:"grid", gridTemplateColumns: "72px 1fr 200px 130px",
                  gap: 14, padding: "12px 16px", alignItems:"center",
                  borderBottom: i === visible.length-1 ? "none" : "1px solid var(--border)",
                  opacity: r.reverted ? 0.5 : 1,
                }}>
                  <div style={{ fontFamily:"var(--font-mono)", fontSize: 10.5, color:"var(--text-faint)" }}>{r.at}</div>
                  <div style={{ minWidth: 0 }}>
                    <div style={{ fontFamily:"var(--font-mono)", fontSize: 11.5, color:"var(--text-dim)", overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap", textDecoration: r.reverted ? "line-through" : "none" }}>{r.file}</div>
                    <div style={{ fontFamily:"var(--font-mono)", fontSize: 11.5, color:"var(--text)", marginTop: 2, display:"flex", alignItems:"center", gap: 6 }}>
                      <Icon name="chev-r" size={10} style={{ color:"var(--accent)" }} />
                      {r.to}
                    </div>
                  </div>
                  <Pill tone="accent">{r.pattern}</Pill>
                  <div style={{ display:"flex", justifyContent:"flex-end" }}>
                    {r.reverted
                      ? <Pill tone="warn">REVERTED</Pill>
                      : <Btn kind="flat" icon="undo" style={{ padding:"4px 8px", fontSize: 11 }}>Revert</Btn>
                    }
                  </div>
                </div>
              ))}
            </div>
          </div>
        );
      })}
    </SettingsCard>
  );
};

const SettingsGeneral = () => (
  <>
    <SettingsCard title="Language" sub="Switches instantly. All copy is translated server-side.">
      <Field label="Interface language">
        <Select defaultValue="en"><option value="en">English</option><option value="ru">Русский</option></Select>
      </Field>
    </SettingsCard>
    <SettingsCard title="Application parameters" sub="Read-only — set via appsettings.json or Docker env vars (e.g. AppSettings__WatcherDelayMs=2000).">
      <div style={{ display:"grid", gridTemplateColumns:"160px 1fr", gap: "10px 24px", fontFamily:"var(--font-mono)", fontSize: 12 }}>
        <span style={{ color:"var(--text-faint)" }}>Watcher delay</span><span style={{ color:"var(--text)" }}>2000 ms</span>
        <span style={{ color:"var(--text-faint)" }}>Video ext.</span><span style={{ color:"var(--text-dim)" }}>.mkv .mp4 .avi .mov .m4v .ts .wmv .flv .webm .rmvb</span>
        <span style={{ color:"var(--text-faint)" }}>Subtitles</span><span style={{ color:"var(--text-dim)" }}>.srt .ass .ssa .vtt .sub</span>
        <span style={{ color:"var(--text-faint)" }}>Images</span><span style={{ color:"var(--text-dim)" }}>.jpg .jpeg .png .webp</span>
      </div>
    </SettingsCard>
  </>
);

const SettingsAppearance = () => {
  const [theme, setTheme] = useStateS("dark");
  const [bd, setBd] = useStateS(true);
  const [blur, setBlur] = useStateS(14);
  const [bright, setBright] = useStateS(38);
  const [interval, setInterval] = useStateS(30);
  const accents = [
    "oklch(0.66 0.20 25)",   // crimson (default)
    "oklch(0.66 0.18 60)",   // amber
    "oklch(0.74 0.15 150)",  // green
    "oklch(0.66 0.14 220)",  // blue
    "oklch(0.66 0.18 290)",  // violet
    "oklch(0.72 0.15 340)",  // pink
  ];
  const [accent, setAccent] = useStateS(0);
  return (
    <>
      <SettingsCard title="Theme" sub="Light, dark, or follow system. Dark recommended for the cinematic backdrop.">
        <div style={{ display:"flex", gap: 10 }}>
          {[["system","System"],["light","Light"],["dark","Dark"]].map(([k,l]) => (
            <button key={k} onClick={() => setTheme(k)} style={{
              all:"unset", cursor:"pointer",
              padding:"14px 20px", borderRadius: 10,
              background: theme === k ? "var(--accent-soft)" : "var(--bg-1)",
              border: theme === k ? "1px solid var(--accent-line)" : "1px solid var(--border)",
              color: theme === k ? "var(--text)" : "var(--text-dim)",
              fontSize: 13, fontWeight: 600, minWidth: 100, textAlign:"center",
            }}>{l}</button>
          ))}
        </div>
      </SettingsCard>

      <SettingsCard title="Accent" sub="A single accent color flows through buttons, links, progress bars and active states.">
        <div style={{ display:"flex", gap: 12 }}>
          {accents.map((c, i) => (
            <button key={i} onClick={() => setAccent(i)} style={{
              all:"unset", cursor:"pointer",
              width: 42, height: 42, borderRadius: 22,
              background: c,
              boxShadow: accent === i ? `0 0 0 2px var(--bg-0), 0 0 0 4px ${c}` : "none",
              position:"relative",
            }}>
              {accent === i && <Icon name="check" size={16} style={{ position:"absolute", top: 13, left: 13, color:"#fff" }} stroke={2.6} />}
            </button>
          ))}
        </div>
      </SettingsCard>

      <SettingsCard title="Backdrop" sub="Ambient fanart slideshow behind every page. Sourced from your library.">
        <div style={{ display:"flex", alignItems:"center", justifyContent:"space-between", marginBottom: 16 }}>
          <span style={{ fontSize: 13.5 }}>Show animated backdrop on all pages</span>
          <Toggle on={bd} onChange={setBd} />
        </div>
        <div style={{ display:"grid", gridTemplateColumns:"1fr 1fr 1fr", gap: 20, opacity: bd ? 1 : 0.4 }}>
          <Field label="Rotation interval (s)"><Input mono type="number" value={interval} onChange={e=>setInterval(+e.target.value)} /></Field>
          <Field label={`Blur · ${blur}px`}><input type="range" min="0" max="30" value={blur} onChange={e=>setBlur(+e.target.value)} style={{ accentColor:"var(--accent)" }} /></Field>
          <Field label={`Brightness · ${bright}%`}><input type="range" min="10" max="100" value={bright} onChange={e=>setBright(+e.target.value)} style={{ accentColor:"var(--accent)" }} /></Field>
        </div>
      </SettingsCard>
    </>
  );
};

const SettingsLLM = () => (
  <SettingsCard title="AI / LLM Identification" sub="The local model normalizes raw folder names into title + year + english_title before TMDB/MAL/IMDb are queried. Hot-swappable — no restart.">
    <div style={{
      background:"linear-gradient(135deg, var(--accent-soft), transparent 60%), var(--bg-1)",
      border:"1px solid var(--accent-line)", borderRadius: 12, padding: 16, marginBottom: 24,
      display:"flex", alignItems:"center", gap: 14,
    }}>
      <div style={{ width: 36, height: 36, borderRadius: 8, background:"var(--accent)", display:"grid", placeItems:"center", color:"#fff" }}>
        <Icon name="sparkle" size={18} />
      </div>
      <div style={{ flex: 1 }}>
        <div style={{ fontFamily:"var(--font-display)", fontSize: 14, letterSpacing:-0.2 }}>ACTIVE · ollama · qwen2.5:1.5b</div>
        <div style={{ fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-dim)" }}>17 / 25 in queue · avg 480ms / item · 99.2% TMDB hit-rate after normalization</div>
      </div>
      <Btn kind="solid">Test connection</Btn>
    </div>

    <div style={{ display:"grid", gridTemplateColumns:"1fr 1fr", gap: 20 }}>
      <Field label="Provider">
        <Select defaultValue="ollama">
          <option value="ollama">Ollama (local)</option>
          <option>OpenAI</option>
          <option>Groq</option>
          <option>LM Studio</option>
          <option>OpenAI-compatible</option>
        </Select>
      </Field>
      <Field label="Model">
        <Select defaultValue="qwen">
          <option value="qwen">qwen2.5:1.5b</option>
          <option>qwen2.5:7b</option>
          <option>llama3.2:3b</option>
        </Select>
      </Field>
      <Field label="Base URL">
        <Input mono defaultValue="http://localhost:11434" />
      </Field>
      <Field label="API Key" hint="Leave empty for local providers.">
        <Input mono type="password" defaultValue="" placeholder="sk-…" />
      </Field>
    </div>

    <div style={{ display:"flex", gap: 10, marginTop: 22 }}>
      <Btn kind="primary" icon="check">Save & reload</Btn>
      <Btn kind="ghost">Reset</Btn>
    </div>
  </SettingsCard>
);

const SettingsPatterns = () => (
  <SettingsCard title="Rename patterns" sub="Regex with named groups: (?<season>) (?<episode>) (?<title>) (?<year>). Higher priority wins. Exclusion scope skips matching files entirely.">
    <div style={{ background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10, overflow:"hidden" }}>
      <div style={{
        display:"grid", gridTemplateColumns:"40px 1fr 1.5fr 110px 80px 70px",
        gap: 12, padding:"10px 16px", borderBottom:"1px solid var(--border)",
        fontFamily:"var(--font-mono)", fontSize: 9.5, color:"var(--text-faint)", letterSpacing: 1.2, textTransform:"uppercase",
      }}>
        <span></span><span>NAME</span><span>REGEX</span><span>SCOPE</span><span>PRIO</span><span></span>
      </div>
      {window.PATTERNS.map(p => (
        <div key={p.id} style={{
          display:"grid", gridTemplateColumns:"40px 1fr 1.5fr 110px 80px 70px",
          gap: 12, padding:"12px 16px", alignItems:"center",
          borderBottom:"1px solid var(--border)",
        }}>
          <Toggle on={p.enabled} onChange={()=>{}} />
          <span style={{ fontSize: 13, color: p.enabled ? "var(--text)" : "var(--text-faint)" }}>{p.name}</span>
          <span style={{ fontFamily:"var(--font-mono)", fontSize: 11, color:"var(--text-dim)", overflow:"hidden", textOverflow:"ellipsis", whiteSpace:"nowrap" }}>{p.regex}</span>
          <Pill tone={p.scope === "Global" ? "accent" : p.scope === "Exclusion" ? "danger" : "neutral"}>{p.scope}</Pill>
          <span style={{ fontFamily:"var(--font-mono)", fontSize: 12, color:"var(--text-dim)" }}>{p.priority}</span>
          <div style={{ display:"flex", gap: 4, justifyContent:"flex-end" }}>
            <button style={{ all:"unset", cursor:"pointer", padding: 4, color:"var(--text-faint)" }}><Icon name="pencil" size={14} /></button>
            <button style={{ all:"unset", cursor:"pointer", padding: 4, color:"var(--text-faint)" }}><Icon name="trash" size={14} /></button>
          </div>
        </div>
      ))}
    </div>
    <Btn kind="primary" icon="plus" style={{ marginTop: 16 }}>New pattern</Btn>
  </SettingsCard>
);

const SettingsIgnore = () => (
  <SettingsCard title="Ignore rules" sub="Glob patterns. Matching files are invisible to watchers, scans and rename queues.">
    <div style={{ background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10, overflow:"hidden" }}>
      {window.IGNORES.map(r => (
        <div key={r.id} style={{
          display:"grid", gridTemplateColumns:"40px 1fr 130px 70px",
          gap: 12, padding:"12px 16px", alignItems:"center",
          borderBottom:"1px solid var(--border)",
        }}>
          <Toggle on={r.on} onChange={()=>{}} />
          <span style={{ fontFamily:"var(--font-mono)", fontSize: 13, color:"var(--text)" }}>{r.glob}</span>
          <Pill>{r.scope}</Pill>
          <div style={{ display:"flex", gap: 4, justifyContent:"flex-end" }}>
            <button style={{ all:"unset", cursor:"pointer", padding: 4, color:"var(--text-faint)" }}><Icon name="trash" size={14} /></button>
          </div>
        </div>
      ))}
    </div>
    <Btn kind="primary" icon="plus" style={{ marginTop: 16 }}>New rule</Btn>
  </SettingsCard>
);

const SettingsTorrent = () => (
  <SettingsCard title="Torrent" sub="Global limits override individual torrents only when lower.">
    <div style={{ display:"grid", gridTemplateColumns:"1fr 1fr", gap: 20 }}>
      <Field label="Listen port"><Input mono defaultValue="51413" /></Field>
      <Field label="DHT port"><Input mono defaultValue="6881" /></Field>
      <Field label="Global ↓ limit (Mbps)" hint="0 = unlimited"><Input mono defaultValue="0" /></Field>
      <Field label="Global ↑ limit (Mbps)" hint="0 = unlimited"><Input mono defaultValue="20" /></Field>
      <Field label="Max active downloads"><Input mono defaultValue="3" /></Field>
      <Field label="Max active seeds"><Input mono defaultValue="6" /></Field>
    </div>
    <div style={{ marginTop: 18, display:"flex", flexDirection:"column", gap: 10 }}>
      <ToggleRow label="Encrypt connections (BT-PE)" on />
      <ToggleRow label="Enable DHT" on />
      <ToggleRow label="Enable PEX" on />
      <ToggleRow label="UPnP / NAT-PMP port mapping" />
    </div>
  </SettingsCard>
);

const SettingsMeta = () => (
  <SettingsCard title="Metadata sources" sub="Order matters — first match with high confidence wins.">
    <div style={{ background:"var(--bg-1)", border:"1px solid var(--border)", borderRadius: 10, overflow:"hidden" }}>
      {[
        ["TMDB", "api-key set", 0.99],
        ["MAL",  "client-id set", 0.96],
        ["IMDb", "scraping mode", 0.78],
        ["AniDB", "off",          0.0],
      ].map(([s, st, c], i) => (
        <div key={s} style={{ display:"grid", gridTemplateColumns:"40px 1fr 1fr 80px 70px", gap: 12, padding:"14px 16px", alignItems:"center", borderBottom: i < 3 ? "1px solid var(--border)" : "none" }}>
          <Toggle on={c > 0} onChange={()=>{}} />
          <span style={{ fontFamily:"var(--font-display)", fontSize: 14, letterSpacing: -0.2 }}>{s}</span>
          <span style={{ fontSize: 12, color:"var(--text-dim)" }}>{st}</span>
          <span style={{ fontFamily:"var(--font-mono)", fontSize: 11, color: c > 0.9 ? "var(--success)" : c > 0 ? "var(--warn)" : "var(--text-faint)" }}>{c > 0 ? `${Math.round(c*100)}% avg` : "—"}</span>
          <Btn kind="flat" style={{ padding:"4px 8px", fontSize: 11 }}>Edit</Btn>
        </div>
      ))}
    </div>
  </SettingsCard>
);

Object.assign(window, {
  CatalogScreen, MediaDetailScreen, ExplorerScreen, TorrentsScreen, HistoryScreen, SettingsScreen,
  NRModal, NRBadge,
});
