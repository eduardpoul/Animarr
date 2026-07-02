// ====== Animarr — LLM recommendations ("Для тебя", "Открой новое", "На этой неделе") ======
// Loads after feat-calendar/feat-stats. Exposes window.HomeRails (Home injection),
// window.DiscoverSection (Home bottom block) and window.DiscoverPage (route "discover").
const { useState: frS } = React;

const magicGlyph = <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><path d="M12 2l1.4 4.6L18 8l-4.6 1.4L12 14l-1.4-4.6L6 8l4.6-1.4zM19 14l.8 2.5L22 17l-2.2.5L19 20l-.8-2.5L16 17l2.2-.5z" /></svg>;

// ── mode A: "Для тебя" — unwatched titles from the library, each with a reason
function forYouData() {
  const watching = window.WATCHING || [];
  const watchingIds = new Set(watching.map(w => w.id));
  const wt = watching.map(w => (window.LIBRARY.find(x => x.id === w.id) || {}).title).filter(Boolean);
  const pool = window.LIBRARY.filter(x => !watchingIds.has(x.id) && x.type !== "Movie");
  const reasonFor = (x) => {
    const tag = (x.tags || [])[1] || (x.tags || [])[0] || "Anime";
    const ru = (window.TAG_RU && window.TAG_RU[tag]) || tag.toLowerCase();
    const opts = [];
    if (wt[0]) opts.push(`Потому что вы смотрите «${wt[0]}»`);
    if (wt[1]) opts.push(`В духе «${wt[1]}»`);
    opts.push(`Любите ${ru} — здесь её много`);
    opts.push(`Высокий рейтинг в вашем вкусе`);
    return opts[x.id.length % opts.length];
  };
  return pool.map(x => ({ item: x, reason: reasonFor(x) })).slice(0, 12);
}

// ── mode B: "Открой новое" — external titles (not in library), TMDB-checked
const DISCOVER = [
  { title: "Frieren: Beyond Journey's End", year: 2023, genres: ["Фэнтези", "Приключения", "Драма"], reason: "После «A Record of a Mortal's Journey» — та же медитативная магия и долгий путь", hue: 168 },
  { title: "Solo Leveling", year: 2024, genres: ["Экшен", "Фэнтези"], reason: "Любите культивацию и прокачку героя — тут этого в достатке", hue: 265 },
  { title: "Dandadan", year: 2024, genres: ["Экшен", "Комедия", "Мистика"], reason: "Динамично, как «Bleach», но совсем свежее", hue: 300 },
  { title: "The Apothecary Diaries", year: 2023, genres: ["Драма", "Детектив"], reason: "Для смены темпа после боевика — интриги дворца", hue: 28 },
  { title: "Delicious in Dungeon", year: 2024, genres: ["Фэнтези", "Приключения"], reason: "Фэнтези-приключение с необычной подачей", hue: 135 },
  { title: "Vinland Saga", year: 2019, genres: ["Экшен", "Драма", "Историческое"], reason: "Серьёзный экшен-эпик — под ваш вкус на сильные истории", hue: 205 },
  { title: "Mushoku Tensei", year: 2021, genres: ["Фэнтези", "Приключения"], reason: "Классический исекай про рост героя с нуля", hue: 220 },
  { title: "Chainsaw Man", year: 2022, genres: ["Экшен", "Ужасы"], reason: "Жёсткий современный сёнэн с ярким стилем", hue: 6 },
];
const discoverBd = (i) => { const L = window.LIBRARY; return L[(i * 3 + 2) % L.length].bd; };

// ── "Для тебя" card ──
const ForYouCard = ({ rec, onOpen, onDismiss, showReason }) => {
  const [hover, setHover] = frS(false);
  const [leaving, setLeaving] = frS(false);
  const dismiss = (e) => { e.stopPropagation(); e.preventDefault(); setLeaving(true); setTimeout(onDismiss, 220); };
  return (
    <div onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)}
      style={{ width: 180, flexShrink: 0, scrollSnapAlign: "start", opacity: leaving ? 0 : 1, transform: leaving ? "scale(.92)" : "none", transition: "opacity .2s ease, transform .2s ease" }}>
      <div style={{ position: "relative" }}>
        <window.Poster item={rec.item} w={180} h={252} onClick={() => onOpen && onOpen(rec.item.id)} />
        {hover && (
          <div role="button" tabIndex={0} onClick={dismiss} title="Не предлагать" className="tv-focus"
            style={{ position: "absolute", top: 8, left: 8, zIndex: 6, width: 26, height: 26, borderRadius: 8, background: "rgba(10,8,7,0.72)", border: "1px solid rgba(255,255,255,0.22)", color: "#fff", display: "grid", placeItems: "center", cursor: "pointer", backdropFilter: "blur(6px)", fontSize: 13 }}>✕</div>
        )}
      </div>
      {showReason && (
        <div title={rec.reason} style={{ marginTop: 9, fontSize: 11.5, color: "var(--text-dim)", lineHeight: 1.4, display: "-webkit-box", WebkitLineClamp: 2, WebkitBoxOrient: "vertical", overflow: "hidden" }}>
          <span style={{ color: "var(--accent-hi)" }}>●</span> {rec.reason}
        </div>
      )}
    </div>
  );
};

const ForYouRail = ({ onOpen }) => {
  const feat = window.__feat || {};
  const llmOff = feat.llm === "off";
  const thin = feat.history === "thin";
  const [recs, setRecs] = frS(() => forYouData());
  const [gen, setGen] = frS(false);
  const [dismissed, setDismissed] = frS(() => new Set());
  const refresh = () => { setGen(true); setTimeout(() => { setRecs(forYouData().slice().sort(() => Math.random() - 0.5)); setGen(false); }, 1600); };
  const shown = recs.filter(r => !dismissed.has(r.item.id));

  if (thin) {
    return (
      <window.FeatureRail overline="РЕКОМЕНДАЦИИ" title={window.RU.forYou}>
        <div style={{ width: "100%", boxSizing: "border-box", padding: "28px 22px", border: "1px dashed var(--border-strong)", borderRadius: 14, color: "var(--text-dim)", fontSize: 13.5, textAlign: "center", lineHeight: 1.6 }}>
          Посмотрите пару тайтлов — и я пойму ваш вкус.<br />Пока показываю популярное из библиотеки.
        </div>
      </window.FeatureRail>
    );
  }
  return (
    <window.FeatureRail
      overline={llmOff ? "ЭВРИСТИЧЕСКАЯ ПОДБОРКА" : null}
      title={window.RU.forYou}
      right={
        <div style={{ display: "flex", alignItems: "center", gap: 14 }}>
          <span title="Считается на вашем сервере, без облака" style={{ display: "inline-flex", alignItems: "center", gap: 6, fontFamily: "var(--font-mono)", fontSize: 10.5, color: "var(--text-faint)" }}>
            <span style={{ color: "var(--accent-hi)" }}>{magicGlyph}</span> подобрано локально
          </span>
          {!llmOff && (
            <button onClick={refresh} className="tv-focus" style={{ all: "unset", cursor: "pointer", display: "inline-flex", alignItems: "center", gap: 7, padding: "7px 12px", borderRadius: 9, background: "var(--surface)", border: "1px solid var(--border-strong)", fontSize: 12, fontWeight: 600, color: gen ? "var(--text-faint)" : "var(--text)" }}>
              {gen
                ? <><span style={{ width: 12, height: 12, border: "2px solid var(--surface-3)", borderTopColor: "var(--accent-hi)", borderRadius: 12, animation: "spin .9s linear infinite", display: "inline-block" }} /> подбираю…</>
                : <>↻ Обновить подборку</>}
            </button>
          )}
        </div>
      }
    >
      {shown.map(r => <ForYouCard key={r.item.id} rec={r} onOpen={onOpen} showReason={!llmOff} onDismiss={() => setDismissed(s => new Set([...s, r.item.id]))} />)}
    </window.FeatureRail>
  );
};

// ── "На этой неделе" rail ──
const ThisWeekRail = ({ onOpen }) => {
  const events = window.AIRING || [];
  const now = new Date(); const monday = new Date(now); monday.setHours(0, 0, 0, 0); monday.setDate(monday.getDate() - ((now.getDay() + 6) % 7));
  const sun = new Date(monday); sun.setDate(sun.getDate() + 7);
  const wk = events.filter(e => e.airingAt >= monday && e.airingAt < sun).sort((a, b) => a.airingAt - b.airingAt);
  if (!wk.length) return null;
  return (
    <window.FeatureRail overline="ОНГОИНГИ" title={window.RU.thisWeek}
      right={<button onClick={() => window.__nav && window.__nav("calendar")} className="tv-focus" style={{ all: "unset", cursor: "pointer", fontSize: 12.5, fontWeight: 600, color: "var(--accent-hi)" }}>{window.RU.calendar} →</button>}>
      {wk.map((ev, i) => (
        <div key={i} onClick={() => ev.status === "in-library" ? (onOpen && onOpen(ev.item.id)) : (window.__nav && window.__nav("calendar"))}
          style={{ width: 210, flexShrink: 0, cursor: "pointer", scrollSnapAlign: "start" }}>
          <window.CalEventCard ev={ev} onOpen={() => {}} />
        </div>
      ))}
    </window.FeatureRail>
  );
};

const HomeRails = ({ onOpen }) => (
  <div style={{ marginTop: 6 }}>
    <ThisWeekRail onOpen={onOpen} />
    <ForYouRail onOpen={onOpen} />
  </div>
);

// ── "Открой новое" external card ──
const ExternalCard = ({ d, i, onHide }) => {
  const [want, setWant] = frS(false);
  const [leaving, setLeaving] = frS(false);
  const hide = () => { setLeaving(true); setTimeout(onHide, 220); };
  return (
    <div style={{ opacity: leaving ? 0 : 1, transform: leaving ? "scale(.95)" : "none", transition: ".2s ease", background: "var(--surface)", border: "1px solid var(--border)", borderRadius: 14, overflow: "hidden", display: "flex", flexDirection: "column" }}>
      <div style={{ position: "relative", aspectRatio: "2 / 3", backgroundImage: `url("${discoverBd(i)}")`, backgroundSize: "cover", backgroundPosition: "center", borderBottom: "1px dashed var(--border-strong)" }}>
        <div style={{ position: "absolute", inset: 0, background: `linear-gradient(180deg, oklch(0.25 0.06 ${d.hue} / 0.35), rgba(0,0,0,0.78))` }} />
        <div style={{ position: "absolute", top: 8, left: 8, fontFamily: "var(--font-mono)", fontSize: 8.5, letterSpacing: 0.8, color: "rgba(255,255,255,0.85)", background: "rgba(0,0,0,0.5)", border: "1px solid rgba(255,255,255,0.16)", padding: "2px 6px", borderRadius: 4 }}>TMDB ✓</div>
        <div style={{ position: "absolute", left: 0, right: 0, bottom: 0, padding: "10px 12px" }}>
          <div style={{ fontFamily: "var(--font-display)", fontSize: 15, fontWeight: 700, color: "#fff", lineHeight: 1.12, textShadow: "0 2px 8px rgba(0,0,0,.75)" }}>{d.title}</div>
          <div style={{ fontFamily: "var(--font-mono)", fontSize: 10, color: "rgba(255,255,255,0.82)", marginTop: 4 }}>{d.year} · {d.genres.join(" · ")}</div>
        </div>
      </div>
      <div style={{ padding: "12px 14px", display: "flex", flexDirection: "column", gap: 12, flex: 1 }}>
        <div title={d.reason} style={{ fontSize: 12, color: "var(--text-dim)", lineHeight: 1.45, display: "-webkit-box", WebkitLineClamp: 2, WebkitBoxOrient: "vertical", overflow: "hidden", flex: 1 }}>
          <span style={{ color: "var(--accent-hi)" }}>●</span> {d.reason}
        </div>
        <div style={{ display: "flex", gap: 8 }}>
          <button onClick={() => setWant(w => !w)} className="tv-focus" style={{ all: "unset", cursor: "pointer", flex: 1, textAlign: "center", padding: "9px 0", borderRadius: 9, fontWeight: 700, fontSize: 12.5, background: want ? "var(--accent-soft)" : "var(--accent)", color: want ? "var(--accent-hi)" : "#15100b", border: want ? "1px solid var(--accent-line)" : "1px solid transparent" }}>{want ? "✓ В списке" : "Хочу"}</button>
          <button onClick={hide} className="tv-focus" style={{ all: "unset", cursor: "pointer", padding: "9px 12px", borderRadius: 9, fontWeight: 600, fontSize: 12.5, background: "var(--surface-2)", border: "1px solid var(--border-strong)", color: "var(--text-dim)" }}>Скрыть</button>
        </div>
      </div>
    </div>
  );
};

const DiscoverGrid = () => {
  const [hidden, setHidden] = frS(() => new Set());
  const list = DISCOVER.map((d, i) => ({ d, i })).filter(({ i }) => !hidden.has(i));
  return (
    <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(196px, 1fr))", gap: 16 }}>
      {list.map(({ d, i }) => <ExternalCard key={i} d={d} i={i} onHide={() => setHidden(s => new Set([...s, i]))} />)}
    </div>
  );
};

const DiscoverSection = () => (
  <div style={{ padding: `10px ${window.SIDE_PAD || 48}px 80px` }}>
    <div style={{ display: "flex", alignItems: "flex-end", gap: 14, marginBottom: 20 }}>
      <div>
        <div style={{ fontFamily: "var(--font-mono)", fontSize: 10.5, letterSpacing: 1.4, color: "var(--accent-hi)" }}>ОТКРОЙ НОВОЕ</div>
        <h2 style={{ margin: "6px 0 0", fontFamily: "var(--font-display)", fontSize: 30, letterSpacing: -0.6, fontWeight: 700 }}>{window.RU.discover}</h2>
      </div>
      <div style={{ marginLeft: "auto", fontFamily: "var(--font-mono)", fontSize: 11, color: "var(--text-faint)" }}>Названия проверены по TMDB</div>
    </div>
    <DiscoverGrid />
  </div>
);

const DiscoverPage = () => (
  <window.WidePage top>
    <div style={{ padding: "6px 0 80px" }}>
      <window.SectionHead overline="ОТКРОЙ НОВОЕ" title={window.RU.discover}
        sub="Тайтлы вне вашей библиотеки, подобранные под вкус. Названия проверены по TMDB — без выдумок."
        right={<span style={{ display: "inline-flex", alignItems: "center", gap: 6, fontFamily: "var(--font-mono)", fontSize: 11, color: "var(--text-faint)" }}><span style={{ color: "var(--accent-hi)" }}>{magicGlyph}</span> подобрано локально</span>} />
      <DiscoverGrid />
    </div>
  </window.WidePage>
);

Object.assign(window, { HomeRails, ForYouRail, ThisWeekRail, DiscoverSection, DiscoverPage });
