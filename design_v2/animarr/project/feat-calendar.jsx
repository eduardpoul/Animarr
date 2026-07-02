// ====== Animarr — Ongoing calendar (Календарь релизов) ======
// Loads after feat-shared.jsx. Exposes window.CalendarPage (route "calendar").
const { useState: fcS } = React;

const CAL_STATUS = {
  "upcoming":      { c: "var(--text-dim)", bg: "rgba(255,255,255,0.05)",     b: "var(--border-strong)",       label: () => window.RU.upcoming },
  "aired-waiting": { c: "var(--warn)",     bg: "oklch(0.80 0.17 75 / 0.16)", b: "oklch(0.60 0.16 75 / 0.5)",   label: () => window.RU.airedWaiting },
  "in-library":    { c: "var(--success)",  bg: "oklch(0.74 0.15 150 / 0.16)", b: "var(--success)",             label: () => window.RU.inLibrary },
};

const startOfWeek = (offset) => {
  const d = new Date(); d.setHours(0, 0, 0, 0);
  const dow = (d.getDay() + 6) % 7; // Monday = 0
  d.setDate(d.getDate() - dow + offset * 7);
  return d;
};

const CalEventCard = ({ ev, onOpen }) => {
  const st = CAL_STATUS[ev.status] || CAL_STATUS.upcoming;
  const clickable = ev.status === "in-library";
  return (
    <button onClick={() => clickable && onOpen && onOpen(ev.item.id)} className="tv-focus" style={{
      all: "unset", cursor: clickable ? "pointer" : "default", display: "block",
      background: "var(--surface)", border: "1px solid var(--border)", borderRadius: 10, overflow: "hidden",
      opacity: ev.status === "upcoming" ? 0.96 : 1,
    }}>
      <div style={{ position: "relative", height: 78, backgroundImage: `url("${ev.item.bd}")`, backgroundSize: "cover", backgroundPosition: `${(ev.ep * 17) % 100}% center` }}>
        <div style={{ position: "absolute", inset: 0, background: "linear-gradient(180deg, rgba(0,0,0,.15), rgba(0,0,0,.78))" }} />
        <div style={{ position: "absolute", top: 7, right: 8, fontFamily: "var(--font-mono)", fontSize: 12, fontWeight: 700, color: "#fff", textShadow: "0 1px 4px rgba(0,0,0,.85)" }}>{window.fmtTime(ev.airingAt)}</div>
        <div style={{ position: "absolute", left: 0, right: 0, bottom: 0, padding: "6px 9px" }}>
          <div style={{ fontSize: 12, fontWeight: 700, color: "#fff", textShadow: "0 1px 5px rgba(0,0,0,.85)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{ev.item.title}</div>
        </div>
      </div>
      <div style={{ padding: "8px 10px 10px" }}>
        <div style={{ fontFamily: "var(--font-mono)", fontSize: 10.5, color: "var(--accent-hi)", fontWeight: 700 }}>S{ev.season} · EP {ev.ep}</div>
        <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 6, marginTop: 8 }}>
          <span className="feat-chip" style={{ fontSize: 9, padding: "2px 6px", borderRadius: 4, color: st.c, background: st.bg, border: "1px solid " + st.b }}>{st.label()}</span>
          <span style={{ fontFamily: "var(--font-mono)", fontSize: 9.5, color: "var(--text-faint)" }}>{window.relLabel(ev.airingAt)}</span>
        </div>
      </div>
    </button>
  );
};

const calNavBtn = { all: "unset", cursor: "pointer", width: 34, height: 34, borderRadius: 9, display: "grid", placeItems: "center", background: "var(--surface)", border: "1px solid var(--border-strong)", color: "var(--text-dim)", fontSize: 18, boxSizing: "border-box" };

const CalEmpty = () => (
  <div style={{ textAlign: "center", padding: "90px 20px", color: "var(--text-dim)" }}>
    <div style={{ fontFamily: "var(--font-display)", fontSize: 22, fontWeight: 700, color: "var(--text)", marginBottom: 10 }}>Нет онгоингов на этой неделе</div>
    <div style={{ fontSize: 14, maxWidth: 440, margin: "0 auto", lineHeight: 1.6 }}>Как только у выходящего тайтла появится дата следующей серии, он окажется здесь.</div>
  </div>
);

const CalendarPage = ({ onOpen }) => {
  const [wk, setWk] = fcS(0);
  const monday = startOfWeek(wk);
  const days = Array.from({ length: 7 }, (_, i) => { const d = new Date(monday); d.setDate(d.getDate() + i); return d; });
  const events = window.AIRING || [];
  const today = new Date();
  const sunday = new Date(monday); sunday.setDate(sunday.getDate() + 6); sunday.setHours(23, 59, 59, 999);
  const evFor = (d) => events.filter(e => window.sameDay(e.airingAt, d)).sort((a, b) => a.airingAt - b.airingAt);
  const total = days.reduce((n, d) => n + evFor(d).length, 0);
  const rangeLabel = `${monday.getDate()} ${window.RU.months[monday.getMonth()]} — ${sunday.getDate()} ${window.RU.months[sunday.getMonth()]}`;

  return (
    <window.WidePage top>
      <div style={{ padding: "6px 0 80px" }}>
        <window.SectionHead overline="ОНГОИНГИ" title={window.RU.calendar}
          sub="Когда выходит следующая серия каждого выходящего тайтла из библиотеки — и в каком она статусе."
          right={
            <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
              <span style={{ fontFamily: "var(--font-mono)", fontSize: 12, color: "var(--text-faint)", marginRight: 4 }}>{rangeLabel}</span>
              <button onClick={() => setWk(w => w - 1)} className="tv-focus" style={calNavBtn} title="Прошлая неделя">‹</button>
              <button onClick={() => setWk(0)} className="tv-focus" style={{ ...calNavBtn, width: "auto", padding: "0 14px", fontFamily: "var(--font-mono)", fontSize: 12, color: wk === 0 ? "var(--accent-hi)" : "var(--text-dim)" }}>{window.RU.today}</button>
              <button onClick={() => setWk(w => w + 1)} className="tv-focus" style={calNavBtn} title="Следующая неделя">›</button>
            </div>
          } />
        {total === 0 ? <CalEmpty /> : (
          <div style={{ display: "grid", gridTemplateColumns: "repeat(7, minmax(0,1fr))", gap: 12 }}>
            {days.map((d, i) => {
              const isToday = window.sameDay(d, today);
              const evs = evFor(d);
              return (
                <div key={i} style={{ minWidth: 0 }}>
                  <div style={{ display: "flex", alignItems: "baseline", gap: 6, paddingBottom: 10, marginBottom: 12, borderBottom: `1px solid ${isToday ? "var(--accent-line)" : "var(--border)"}` }}>
                    <span style={{ fontFamily: "var(--font-display)", fontSize: 18, fontWeight: 700, color: isToday ? "var(--accent-hi)" : "var(--text)" }}>{d.getDate()}</span>
                    <span style={{ fontFamily: "var(--font-mono)", fontSize: 10.5, color: isToday ? "var(--accent-hi)" : "var(--text-faint)", textTransform: "uppercase", letterSpacing: 0.6 }}>{window.RU.weekdaysShort[d.getDay()]}</span>
                  </div>
                  <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
                    {evs.length === 0
                      ? <div style={{ fontFamily: "var(--font-mono)", fontSize: 11, color: "var(--text-faint)", opacity: 0.4, padding: "4px 2px" }}>—</div>
                      : evs.map((ev, j) => <CalEventCard key={j} ev={ev} onOpen={onOpen} />)}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </window.WidePage>
  );
};

Object.assign(window, { CalendarPage, CalEventCard });
