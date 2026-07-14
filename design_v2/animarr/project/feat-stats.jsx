// ====== Animarr — Statistics + "Твой год в аниме" (Wrapped) ======
// Loads after feat-shared.jsx. Exposes window.StatsPage (route "stats").
const { useState: fstS } = React;

// deterministic PRNG (local — feat-data's is private)
function stHash(s) { let h = 2166136261; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); } return h >>> 0; }
function stRng(seed) { let a = seed >>> 0; return () => { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }

function statsFor(user, period) {
  const r = stRng(stHash(user.id + ":" + period));
  const mult = period === "all" ? 3.4 : 1;
  const totalHours = Math.round((180 + r() * 300) * mult);
  const months = Array.from({ length: 12 }, () => Math.round(6 + r() * 42));
  const weekdays = Array.from({ length: 7 }, (_, i) => Math.round((i >= 5 ? 40 : 18) + r() * 30));
  const lib = window.LIBRARY.filter(x => x.type !== "Movie");
  const top = lib.map(x => ({ item: x, hours: Math.round(4 + r() * 60) })).sort((a, b) => b.hours - a.hours).slice(0, 5);
  const genreCount = {};
  window.LIBRARY.forEach(x => (x.tags || []).forEach(t => { genreCount[t] = (genreCount[t] || 0) + 1 + r(); }));
  const genres = Object.entries(genreCount).sort((a, b) => b[1] - a[1]).slice(0, 6);
  const gTotal = genres.reduce((s, g) => s + g[1], 0);
  // heatmap: 53 weeks x 7 days
  const heat = Array.from({ length: 53 * 7 }, () => { const v = r(); return v < 0.45 ? 0 : v < 0.62 ? 1 : v < 0.8 ? 2 : v < 0.93 ? 3 : 4; });
  return {
    totalHours,
    episodes: Math.round(totalHours * (2.2 + r() * 0.6)),
    titles: Math.round((10 + r() * 26) * (period === "all" ? 2.6 : 1)),
    streak: Math.round(3 + r() * 41),
    months, weekdays, top, genres, gTotal, heat,
    topGenreRu: (window.TAG_RU && (window.TAG_RU[genres[0][0]] || genres[0][0])) || genres[0][0],
    bestDayHours: Math.max(...months) / 4 + 3 | 0,
  };
}

const HEAT_COLORS = ["var(--surface-2)", "oklch(0.66 0.20 60 / 0.28)", "oklch(0.70 0.19 60 / 0.5)", "oklch(0.74 0.18 60 / 0.75)", "var(--accent-hi)"];
const MONTHS_RU = ["Я", "Ф", "М", "А", "М", "И", "И", "А", "С", "О", "Н", "Д"];

const StatCard = ({ value, label, sub, accent }) => (
  <div style={{ background: "var(--surface)", border: "1px solid var(--border)", borderRadius: 14, padding: "18px 20px" }}>
    <div style={{ fontFamily: "var(--font-display)", fontSize: 40, fontWeight: 800, lineHeight: 1, letterSpacing: -1.5, color: accent ? "var(--accent-hi)" : "var(--text)" }}>{value}</div>
    <div style={{ fontSize: 13, fontWeight: 600, color: "var(--text-dim)", marginTop: 8 }}>{label}</div>
    {sub && <div style={{ fontFamily: "var(--font-mono)", fontSize: 10.5, color: "var(--text-faint)", marginTop: 3 }}>{sub}</div>}
  </div>
);

const Panel = ({ title, right, children, style }) => (
  <div style={{ background: "var(--surface)", border: "1px solid var(--border)", borderRadius: 14, padding: "18px 20px", ...style }}>
    <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 16 }}>
      <div style={{ fontFamily: "var(--font-mono)", fontSize: 10.5, letterSpacing: 1.2, color: "var(--accent-hi)", textTransform: "uppercase" }}>{title}</div>
      {right}
    </div>
    {children}
  </div>
);

const StatsPage = ({ onOpen }) => {
  const [period, setPeriod] = fstS("year");
  const [wrapped, setWrapped] = fstS(false);
  const user = window.CURRENT_USER;
  const s = statsFor(user, period);
  const maxMonth = Math.max(...s.months);
  const maxWd = Math.max(...s.weekdays);

  return (
    <>
      {wrapped && <WrappedOverlay s={s} user={user} onClose={() => setWrapped(false)} />}
      <window.WidePage top>
      <div style={{ padding: "6px 0 90px" }}>
        <window.SectionHead overline="ПРОФИЛЬ" title={window.RU.stats}
          sub={`Личная статистика просмотра для ${user.name}. Подневная история ведётся с июля 2026.`}
          right={
            <div style={{ display: "flex", padding: 3, gap: 3, borderRadius: 9, background: "var(--surface)", border: "1px solid var(--border)" }}>
              {[["year", "Год"], ["all", "Всё время"]].map(([k, l]) => (
                <button key={k} onClick={() => setPeriod(k)} className="tv-focus" style={{ all: "unset", cursor: "pointer", padding: "7px 14px", borderRadius: 6, fontSize: 12, fontWeight: 600, color: period === k ? "var(--accent-hi)" : "var(--text-dim)", background: period === k ? "var(--accent-soft)" : "transparent" }}>{l}</button>
              ))}
            </div>
          } />

        {/* number cards */}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 14, marginBottom: 14 }}>
          <StatCard value={s.totalHours + " ч"} label="Всего часов" sub={`≈ ${Math.round(s.totalHours / 24)} суток`} accent />
          <StatCard value={s.episodes} label="Серий досмотрено" />
          <StatCard value={s.titles} label="Тайтлов завершено" />
          <StatCard value={s.streak} label="Стрик — дней подряд" sub="без пропусков" />
        </div>

        {/* wrapped banner */}
        <button onClick={() => setWrapped(true)} className="tv-focus" style={{
          all: "unset", cursor: "pointer", boxSizing: "border-box", width: "100%", display: "flex", alignItems: "center", gap: 16,
          padding: "16px 22px", borderRadius: 14, marginBottom: 14,
          background: "linear-gradient(100deg, oklch(0.5 0.2 30 / 0.55), oklch(0.55 0.18 300 / 0.4))",
          border: "1px solid var(--accent-line)",
        }}>
          <div style={{ width: 44, height: 44, borderRadius: 12, display: "grid", placeItems: "center", background: "var(--accent-soft)", border: "1px solid var(--accent-line)", color: "var(--accent-hi)", flexShrink: 0 }}><svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor"><path d="M12 2l1.9 5.8L20 9.7l-4.9 3.6L17 20l-5-3.6L7 20l1.9-6.7L4 9.7l6.1-1.9z"/></svg></div>
          <div style={{ flex: 1 }}>
            <div style={{ fontFamily: "var(--font-display)", fontSize: 20, fontWeight: 800, letterSpacing: -0.4 }}>Твой год в аниме — 2026</div>
            <div style={{ fontSize: 12.5, color: "var(--text-dim)", marginTop: 2 }}>{s.totalHours} часов, {s.titles} тайтлов и топ-жанр «{s.topGenreRu}». Посмотреть историю →</div>
          </div>
          <span style={{ padding: "9px 16px", borderRadius: 9, background: "var(--accent)", color: "#15100b", fontWeight: 700, fontSize: 13 }}>Открыть</span>
        </button>

        {/* heatmap */}
        <Panel title="Активность за год" right={<span style={{ fontFamily: "var(--font-mono)", fontSize: 10, color: "var(--text-faint)" }}>интенсивность = минуты за день</span>} style={{ marginBottom: 14 }}>
          <div style={{ overflowX: "auto" }} className="feat-rail">
            <div style={{ display: "grid", gridTemplateRows: "repeat(7, 11px)", gridAutoFlow: "column", gridAutoColumns: "11px", gap: 3, minWidth: 760 }}>
              {s.heat.map((v, i) => <div key={i} title={v ? `${v * 40 + 10} мин` : "нет активности"} style={{ width: 11, height: 11, borderRadius: 2.5, background: HEAT_COLORS[v] }} />)}
            </div>
          </div>
          <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginTop: 14 }}>
            <span style={{ fontFamily: "var(--font-mono)", fontSize: 10, color: "var(--text-faint)" }}>подневная история — с июля 2026</span>
            <div style={{ display: "flex", alignItems: "center", gap: 6, fontFamily: "var(--font-mono)", fontSize: 10, color: "var(--text-faint)" }}>
              меньше {HEAT_COLORS.map((c, i) => <span key={i} style={{ width: 11, height: 11, borderRadius: 2.5, background: c }} />)} больше
            </div>
          </div>
        </Panel>

        <div style={{ display: "grid", gridTemplateColumns: "1.4fr 1fr", gap: 14, marginBottom: 14 }}>
          {/* monthly */}
          <Panel title="Часы по месяцам">
            <div style={{ display: "flex", alignItems: "flex-end", gap: 8, height: 150 }}>
              {s.months.map((m, i) => (
                <div key={i} style={{ flex: 1, display: "flex", flexDirection: "column", alignItems: "center", gap: 7 }}>
                  <div style={{ width: "100%", height: `${(m / maxMonth) * 118}px`, borderRadius: 5, background: "linear-gradient(180deg, var(--accent-hi), var(--accent))", minHeight: 3 }} title={`${m} ч`} />
                  <span style={{ fontFamily: "var(--font-mono)", fontSize: 10, color: "var(--text-faint)" }}>{MONTHS_RU[i]}</span>
                </div>
              ))}
            </div>
          </Panel>
          {/* weekday */}
          <Panel title="По дням недели">
            <div style={{ display: "flex", alignItems: "flex-end", gap: 8, height: 150 }}>
              {["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"].map((d, i) => (
                <div key={i} style={{ flex: 1, display: "flex", flexDirection: "column", alignItems: "center", gap: 7 }}>
                  <div style={{ width: "100%", height: `${(s.weekdays[i] / maxWd) * 118}px`, borderRadius: 5, background: i >= 5 ? "var(--accent)" : "var(--surface-3)", minHeight: 3 }} title={`${s.weekdays[i]} сеансов`} />
                  <span style={{ fontFamily: "var(--font-mono)", fontSize: 10, color: "var(--text-faint)" }}>{d}</span>
                </div>
              ))}
            </div>
          </Panel>
        </div>

        <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 14 }}>
          {/* top titles */}
          <Panel title="Топ-5 тайтлов">
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              {s.top.map((t, i) => (
                <button key={t.item.id} onClick={() => onOpen && onOpen(t.item.id)} className="tv-focus" style={{ all: "unset", cursor: "pointer", display: "flex", alignItems: "center", gap: 12 }}>
                  <span style={{ fontFamily: "var(--font-mono)", fontSize: 13, fontWeight: 700, color: "var(--text-faint)", width: 16 }}>{i + 1}</span>
                  <div style={{ width: 40, height: 56, borderRadius: 6, flexShrink: 0, backgroundImage: `url("${t.item.bd}")`, backgroundSize: "cover", backgroundPosition: "center" }} />
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div style={{ fontSize: 13, fontWeight: 600, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{t.item.title}</div>
                    <div style={{ height: 5, borderRadius: 5, background: "var(--surface-3)", marginTop: 7, overflow: "hidden" }}><div style={{ height: "100%", width: `${(t.hours / s.top[0].hours) * 100}%`, background: "var(--accent)" }} /></div>
                  </div>
                  <span style={{ fontFamily: "var(--font-mono)", fontSize: 12, color: "var(--accent-hi)", fontWeight: 700 }}>{t.hours} ч</span>
                </button>
              ))}
            </div>
          </Panel>
          {/* genres */}
          <Panel title="Жанры">
            <div style={{ display: "flex", flexDirection: "column", gap: 11 }}>
              {s.genres.map(([g, v], i) => {
                const pct = Math.round((v / s.gTotal) * 100);
                const ru = (window.TAG_RU && (window.TAG_RU[g] || g)) || g;
                return (
                  <div key={g}>
                    <div style={{ display: "flex", justifyContent: "space-between", fontSize: 12, marginBottom: 5 }}>
                      <span style={{ color: "var(--text)", textTransform: "capitalize" }}>{ru}</span>
                      <span style={{ fontFamily: "var(--font-mono)", color: "var(--text-faint)" }}>{pct}%</span>
                    </div>
                    <div style={{ height: 8, borderRadius: 8, background: "var(--surface-3)", overflow: "hidden" }}><div style={{ height: "100%", width: `${pct}%`, background: `oklch(${0.7 - i * 0.03} 0.18 ${45 + i * 22})` }} /></div>
                  </div>
                );
              })}
            </div>
          </Panel>
        </div>
      </div>
      </window.WidePage>
    </>
  );
};

// ── "Твой год в аниме" — story mode ──────────────────────────
const WrappedOverlay = ({ s, user, onClose }) => {
  const [i, setI] = fstS(0);
  const slides = [
    { bg: "linear-gradient(160deg, oklch(0.4 0.2 30), oklch(0.25 0.16 300))", kicker: "ANIMARR 2026", big: "Твой год\nв аниме", sub: `${user.name}, вот каким он был` },
    { bg: "linear-gradient(160deg, oklch(0.45 0.2 45), oklch(0.28 0.15 20))", kicker: "Всего просмотрено", big: `${s.totalHours}\nчасов`, sub: `это примерно ${Math.round(s.totalHours / 24)} суток без сна` },
    { bg: "linear-gradient(160deg, oklch(0.4 0.18 300), oklch(0.28 0.16 260))", kicker: "Твой жанр года", big: s.topGenreRu, sub: `${s.genres[0] ? Math.round((s.genres[0][1] / s.gTotal) * 100) : 0}% всего просмотра` },
    { bg: "linear-gradient(160deg, oklch(0.45 0.2 30), oklch(0.3 0.17 350))", kicker: "Тайтл года", big: s.top[0].item.title, sub: `${s.top[0].hours} часов вместе`, poster: s.top[0].item.bd },
    { bg: "linear-gradient(160deg, oklch(0.42 0.18 150), oklch(0.28 0.15 200))", kicker: "Самый залипательный", big: `${s.bestDayHours} часов\nза день`, sub: `и стрик ${s.streak} дней подряд` },
    { share: true, bg: "linear-gradient(160deg, oklch(0.38 0.2 30), oklch(0.24 0.16 300))" },
  ];
  const sl = slides[i];
  const next = () => setI(v => Math.min(v + 1, slides.length - 1));
  const prev = () => setI(v => Math.max(v - 1, 0));
  return (
    <div style={{ position: "fixed", inset: 0, zIndex: 200, background: "rgba(0,0,0,0.8)", backdropFilter: "blur(10px)", display: "grid", placeItems: "center" }} onClick={onClose}>
      <div onClick={e => e.stopPropagation()} style={{ position: "relative", width: 380, height: 660, maxHeight: "92vh", borderRadius: 24, overflow: "hidden", background: sl.bg, boxShadow: "0 40px 100px -30px rgba(0,0,0,0.8)", display: "flex", flexDirection: "column" }}>
        {/* progress */}
        <div style={{ display: "flex", gap: 5, padding: "14px 16px 0", zIndex: 3 }}>
          {slides.map((_, k) => <div key={k} style={{ flex: 1, height: 3, borderRadius: 3, background: k <= i ? "#fff" : "rgba(255,255,255,0.3)" }} />)}
        </div>
        <button onClick={onClose} style={{ all: "unset", cursor: "pointer", position: "absolute", top: 14, right: 16, zIndex: 5, color: "rgba(255,255,255,0.85)", fontSize: 22, lineHeight: 1 }}>×</button>
        {/* tap zones */}
        <div onClick={prev} style={{ position: "absolute", left: 0, top: 0, bottom: 0, width: "35%", zIndex: 2, cursor: "pointer" }} />
        <div onClick={next} style={{ position: "absolute", right: 0, top: 0, bottom: 0, width: "65%", zIndex: 2, cursor: "pointer" }} />

        {sl.share ? (
          <div style={{ flex: 1, display: "flex", flexDirection: "column", padding: 24, justifyContent: "center", gap: 18 }}>
            <div style={{ background: "rgba(0,0,0,0.35)", border: "1px solid rgba(255,255,255,0.18)", borderRadius: 18, padding: 22 }}>
              <div style={{ fontFamily: "var(--font-mono)", fontSize: 10, letterSpacing: 1.4, color: "rgba(255,255,255,0.75)" }}>ANIMARR · 2026</div>
              <div style={{ fontFamily: "var(--font-display)", fontSize: 26, fontWeight: 800, color: "#fff", marginTop: 10 }}>{user.name} в аниме</div>
              <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 14, marginTop: 18 }}>
                {[[s.totalHours + " ч", "часов"], [s.titles, "тайтлов"], [s.topGenreRu, "жанр года"], [s.streak + " дн", "стрик"]].map(([v, l], k) => (
                  <div key={k}><div style={{ fontFamily: "var(--font-display)", fontSize: 24, fontWeight: 800, color: "#fff" }}>{v}</div><div style={{ fontSize: 11, color: "rgba(255,255,255,0.7)" }}>{l}</div></div>
                ))}
              </div>
              <div style={{ marginTop: 18, paddingTop: 14, borderTop: "1px solid rgba(255,255,255,0.15)", fontSize: 12, color: "rgba(255,255,255,0.8)" }}>Топ-тайтл: {s.top[0].item.title}</div>
            </div>
            <button style={{ all: "unset", cursor: "pointer", textAlign: "center", padding: "13px 0", borderRadius: 11, background: "#fff", color: "#15100b", fontWeight: 700, fontSize: 14 }}>Сохранить картинку</button>
          </div>
        ) : (
          <div style={{ flex: 1, display: "flex", flexDirection: "column", justifyContent: "center", padding: 30, position: "relative", zIndex: 1 }}>
            {sl.poster && <div style={{ width: 150, height: 210, borderRadius: 12, margin: "0 auto 24px", backgroundImage: `url("${sl.poster}")`, backgroundSize: "cover", backgroundPosition: "center", boxShadow: "0 20px 50px -15px rgba(0,0,0,0.7)" }} />}
            <div style={{ fontFamily: "var(--font-mono)", fontSize: 12, letterSpacing: 1.6, color: "rgba(255,255,255,0.8)", textTransform: "uppercase", marginBottom: 14 }}>{sl.kicker}</div>
            <div style={{ fontFamily: "var(--font-display)", fontSize: sl.big.length > 16 ? 40 : 52, fontWeight: 800, color: "#fff", lineHeight: 1.02, letterSpacing: -1.5, whiteSpace: "pre-line" }}>{sl.big}</div>
            <div style={{ fontSize: 15, color: "rgba(255,255,255,0.88)", marginTop: 16, lineHeight: 1.5 }}>{sl.sub}</div>
          </div>
        )}
      </div>
    </div>
  );
};

Object.assign(window, { StatsPage, WrappedOverlay });
