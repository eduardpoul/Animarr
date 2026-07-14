// ====== Animarr — Player + SyncPlay (Смотреть вместе) ======
// Loads after feat-shared.jsx. Exposes window.PlayerScreen (route "player")
// and window.__play(id, ep) to open it. Playback is simulated; SyncPlay is the
// focus — only time is shared, each viewer keeps their own audio/subs.
const { useState: fpS, useEffect: fpE, useRef: fpR } = React;

const fmtT = (s) => { s = Math.max(0, Math.floor(s)); const m = Math.floor(s / 60), sec = s % 60; return `${m}:${String(sec).padStart(2, "0")}`; };

// tiny inline glyphs
const G = {
  play: <svg width="20" height="20" viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z" /></svg>,
  pause: <svg width="20" height="20" viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="5" width="4" height="14" rx="1" /><rect x="14" y="5" width="4" height="14" rx="1" /></svg>,
  prev: <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor"><path d="M6 5h2v14H6zM20 5v14l-11-7z" /></svg>,
  next: <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor"><path d="M16 5h2v14h-2zM4 5v14l11-7z" /></svg>,
  vol: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><path d="M4 9v6h4l5 4V5L8 9H4z" /><path d="M17 8a5 5 0 0 1 0 8" /></svg>,
  cc: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><rect x="3" y="5" width="18" height="14" rx="3" /><path d="M9 10.5a2 2 0 100 3M15 10.5a2 2 0 100 3" strokeLinecap="round" /></svg>,
  audio: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round"><path d="M12 3v18M8 7v10M16 7v10M4 10v4M20 10v4" /></svg>,
  gear: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><circle cx="12" cy="12" r="3" /><path d="M19 12a7 7 0 00-.1-1l2-1.6-2-3.4-2.4 1a7 7 0 00-1.7-1L16.5 2h-4l-.3 2.6a7 7 0 00-1.7 1l-2.4-1-2 3.4L5.1 11a7 7 0 000 2l-2 1.6 2 3.4 2.4-1a7 7 0 001.7 1l.3 2.6h4l.3-2.6a7 7 0 001.7-1l2.4 1 2-3.4-2-1.6a7 7 0 00.1-1z" /></svg>,
  full: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M8 3H5a2 2 0 00-2 2v3M16 3h3a2 2 0 012 2v3M8 21H5a2 2 0 01-2-2v-3M16 21h3a2 2 0 002-2v-3" /></svg>,
  users: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><path d="M17 21v-2a4 4 0 00-4-4H5a4 4 0 00-4 4v2" /><circle cx="9" cy="7" r="4" /><path d="M23 21v-2a4 4 0 00-3-3.87M16 3.13A4 4 0 0116 11" /></svg>,
  back: <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M15 18l-6-6 6-6" /></svg>,
};

const HudBtn = ({ children, label, onClick, disabled, dimTip, active }) => (
  <button onClick={disabled ? undefined : onClick} className="tv-focus" title={disabled ? dimTip : label} style={{
    all: "unset", cursor: disabled ? "not-allowed" : "pointer", position: "relative",
    display: "inline-flex", alignItems: "center", gap: 8, padding: "8px 12px", borderRadius: 9,
    color: active ? "var(--accent-hi)" : disabled ? "rgba(255,255,255,0.32)" : "#fff",
    background: active ? "var(--accent-soft)" : "transparent", fontSize: 12.5, fontWeight: 600,
    border: active ? "1px solid var(--accent-line)" : "1px solid transparent",
  }}>{children}{label && <span>{label}</span>}</button>
);

const QR = () => {
  const cells = [];
  let a = 20261;
  for (let i = 0; i < 121; i++) { a = (a * 1103515245 + 12345) & 0x7fffffff; const corner = (i % 11 < 3 && i < 33) || (i % 11 > 7 && i < 33) || (i % 11 < 3 && i > 87); cells.push(corner || (a % 100) > 52); }
  return <div style={{ width: 128, height: 128, background: "#fff", borderRadius: 10, padding: 8, display: "grid", gridTemplateColumns: "repeat(11,1fr)", gap: 1 }}>{cells.map((c, i) => <div key={i} style={{ background: c ? "#0a0807" : "#fff", borderRadius: 1 }} />)}</div>;
};

const P_STATUS = { ok: { c: "var(--success)", t: "в синхроне" }, buffering: { c: "var(--warn)", t: "буферизует" }, behind: { c: "var(--accent-hi)", t: "отстаёт" } };

const RoomModal = ({ role, onClose, onJoinScreen, onLeave, wait, setWait, parts }) => (
  <>
    <div onClick={onClose} style={{ position: "fixed", inset: 0, zIndex: 300, background: "rgba(0,0,0,0.6)", backdropFilter: "blur(6px)" }} />
    <div style={{ position: "fixed", zIndex: 301, top: "50%", left: "50%", transform: "translate(-50%,-50%)", width: 440, maxWidth: "92vw", background: "linear-gradient(180deg, var(--surface), var(--surface-2))", border: "1px solid var(--border-strong)", borderRadius: 18, boxShadow: "var(--sh-drawer, 0 30px 60px -30px rgba(0,0,0,0.7))", overflow: "hidden" }}>
      <div style={{ padding: "18px 22px", borderBottom: "1px solid var(--border)", display: "flex", alignItems: "center", gap: 10 }}>
        <span style={{ color: "var(--accent-hi)" }}>{G.users}</span>
        <div style={{ flex: 1 }}>
          <div style={{ fontFamily: "var(--font-display)", fontSize: 17, fontWeight: 700 }}>{role === "guest" ? `Комната ${window.SYNC_HOST || "Anna"}` : "Пригласить друзей"}</div>
          <div style={{ fontSize: 11, color: "var(--text-faint)" }}>Синхронизируется только время. Аудио и субтитры — каждому свои.</div>
        </div>
        <button onClick={onClose} style={{ all: "unset", cursor: "pointer", color: "var(--text-dim)", fontSize: 20 }}>×</button>
      </div>

      {role === "host" ? (
        <>
          <div style={{ padding: "18px 22px 14px" }}>
            <div style={{ fontSize: 12.5, color: "var(--text-dim)", marginBottom: 16 }}>Отправьте код или ссылку — друзья откроют её и подключатся к вашему просмотру.</div>
            <div style={{ display: "flex", gap: 20 }}>
              <div style={{ flex: 1 }}>
                <div style={{ fontFamily: "var(--font-mono)", fontSize: 10, letterSpacing: 1, color: "var(--text-faint)", textTransform: "uppercase" }}>Код комнаты</div>
                <div style={{ fontFamily: "var(--font-mono)", fontSize: 34, fontWeight: 700, letterSpacing: 6, color: "var(--accent-hi)", margin: "6px 0 14px" }}>492 815</div>
                <button style={{ all: "unset", cursor: "pointer", display: "inline-flex", alignItems: "center", gap: 8, padding: "9px 14px", borderRadius: 9, background: "var(--surface-2)", border: "1px solid var(--border-strong)", fontSize: 12.5, fontWeight: 600, color: "var(--text)" }}>Копировать ссылку</button>
                <div style={{ marginTop: 14, fontFamily: "var(--font-mono)", fontSize: 10.5, color: "var(--text-faint)" }}>animarr.local/watch/492815</div>
              </div>
              <QR />
            </div>
          </div>
          <div style={{ padding: "0 22px 8px" }}>
            <div style={{ fontFamily: "var(--font-mono)", fontSize: 10, letterSpacing: 1, color: "var(--text-faint)", textTransform: "uppercase", marginBottom: 10 }}>В комнате · {parts.length + 1}</div>
            <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              <PartRow name={window.CURRENT_USER.name + " (вы, хост)"} status="ok" />
              {parts.map(p => <PartRow key={p.name} name={p.name} status={p.status} />)}
            </div>
          </div>
          <div style={{ padding: "12px 22px 20px", display: "flex", alignItems: "center", justifyContent: "space-between", borderTop: "1px solid var(--border)", marginTop: 12 }}>
            <label style={{ display: "flex", alignItems: "center", gap: 10, cursor: "pointer" }}>
              <span style={{ fontSize: 12.5, color: "var(--text)" }}>Ждать отстающих</span>
              <span onClick={() => setWait(!wait)} style={{ width: 34, height: 20, borderRadius: 20, background: wait ? "var(--accent)" : "var(--surface-3)", border: "1px solid var(--border-strong)", position: "relative", transition: ".15s" }}><span style={{ position: "absolute", top: 2, left: wait ? 15 : 2, width: 14, height: 14, borderRadius: 14, background: wait ? "#15100b" : "var(--text-dim)", transition: ".15s" }} /></span>
            </label>
            <button onClick={onJoinScreen} style={{ all: "unset", cursor: "pointer", fontSize: 12, color: "var(--accent-hi)", fontWeight: 600 }}>Войти в чужую комнату →</button>
          </div>
        </>
      ) : (
        <>
          <div style={{ padding: "18px 22px 8px" }}>
            <div style={{ display: "flex", alignItems: "center", gap: 12, padding: "12px 14px", borderRadius: 12, background: "var(--accent-soft)", border: "1px solid var(--accent-line)", marginBottom: 16 }}>
              <span style={{ width: 8, height: 8, borderRadius: 8, background: "var(--success)", boxShadow: "0 0 8px var(--success)", flexShrink: 0 }} />
              <div style={{ flex: 1, fontSize: 13, color: "var(--text)", lineHeight: 1.5 }}>Вы подключились. Воспроизведением управляет <b>{window.SYNC_HOST || "Anna"}</b> — вам остаются свои звук и субтитры.</div>
            </div>
            <div style={{ fontFamily: "var(--font-mono)", fontSize: 10, letterSpacing: 1, color: "var(--text-faint)", textTransform: "uppercase", marginBottom: 10 }}>В комнате · {parts.length + 1}</div>
            <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              <PartRow name={(window.SYNC_HOST || "Anna") + " (хост)"} status="ok" />
              <PartRow name={window.CURRENT_USER.name + " (вы)"} status="ok" />
              <PartRow name="Pavel" status="buffering" />
            </div>
          </div>
          <div style={{ padding: "14px 22px 20px", borderTop: "1px solid var(--border)", marginTop: 12 }}>
            <button onClick={onLeave} style={{ all: "unset", cursor: "pointer", boxSizing: "border-box", width: "100%", textAlign: "center", padding: "11px 0", borderRadius: 10, background: "rgba(200,70,50,0.14)", border: "1px solid rgba(220,90,70,0.6)", color: "#e0836a", fontWeight: 700, fontSize: 13 }}>Покинуть комнату</button>
          </div>
        </>
      )}
    </div>
  </>
);

const PartRow = ({ name, status, you }) => {
  const st = P_STATUS[status] || P_STATUS.ok;
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
      <div style={{ width: 28, height: 28, borderRadius: 28, background: "var(--av-red, linear-gradient(135deg,#c0503a,#7a2f22))", display: "grid", placeItems: "center", color: "#fff", fontWeight: 700, fontSize: 11 }}>{name[0]}</div>
      <span style={{ flex: 1, fontSize: 13, fontWeight: 500, color: "var(--text)" }}>{name}</span>
      <span style={{ display: "inline-flex", alignItems: "center", gap: 6, fontFamily: "var(--font-mono)", fontSize: 10.5, color: st.c }}>
        {status === "buffering" && <span className="spin" style={{ width: 11, height: 11, border: "2px solid rgba(255,255,255,0.15)", borderTopColor: st.c, borderRadius: 11, display: "inline-block", animation: "spin .9s linear infinite" }} />}
        {status !== "buffering" && <span style={{ width: 7, height: 7, borderRadius: 7, background: st.c }} />}
        {st.t}
      </span>
    </div>
  );
};

const SyncJoinScreen = ({ onJoin, onClose }) => (
  <div style={{ position: "fixed", inset: 0, zIndex: 310, background: "var(--bg-0)", display: "grid", placeItems: "center" }}>
    <div style={{ width: 400, maxWidth: "90vw", textAlign: "center" }}>
      <div style={{ color: "var(--accent-hi)", display: "flex", justifyContent: "center", marginBottom: 16 }}>{G.users}</div>
      <div style={{ fontFamily: "var(--font-display)", fontSize: 26, fontWeight: 800, letterSpacing: -0.6 }}>Присоединиться к просмотру</div>
      <div style={{ fontSize: 13, color: "var(--text-dim)", marginTop: 8, lineHeight: 1.5 }}>Введите 6-значный код от хоста<br />или откройте ссылку-приглашение</div>
      <div style={{ display: "flex", gap: 8, justifyContent: "center", margin: "26px 0 12px" }}>
        {["4", "9", "2", "8", "1", "5"].map((d, i) => <div key={i} style={{ width: 48, height: 60, borderRadius: 10, background: "var(--surface)", border: `1px solid ${i === 5 ? "var(--accent-line)" : "var(--border-strong)"}`, boxShadow: i === 5 ? "0 0 0 3px var(--accent-soft)" : "none", display: "grid", placeItems: "center", fontFamily: "var(--font-mono)", fontSize: 26, fontWeight: 700 }}>{d}</div>)}
      </div>
      <div style={{ fontFamily: "var(--font-mono)", fontSize: 11, color: "var(--text-faint)", marginBottom: 22 }}>Хост управляет воспроизведением — вам остаются звук и субтитры</div>
      <button onClick={onJoin} className="tv-focus" style={{ all: "unset", cursor: "pointer", boxSizing: "border-box", width: "100%", textAlign: "center", padding: "13px 0", borderRadius: 11, background: "var(--accent)", color: "#15100b", fontWeight: 700, fontSize: 14 }}>Присоединиться к Anna</button>
      <button onClick={onClose} style={{ all: "unset", cursor: "pointer", marginTop: 14, fontSize: 12.5, color: "var(--text-dim)" }}>← Назад</button>
    </div>
  </div>
);

const PlayerScreen = ({ onClose }) => {
  const ctx = window.__playCtx || { id: window.LIBRARY[0].id, ep: 1 };
  const item = window.LIBRARY.find(x => x.id === ctx.id) || window.LIBRARY[0];
  const dur = 24 * 60;
  const posKey = `animarr:playpos:${item.id}:${ctx.ep}`;
  const [cur, setCur] = fpS(() => { try { return +localStorage.getItem(posKey) || 42; } catch (e) { return 42; } });
  const [playing, setPlaying] = fpS(true);
  const [showUI, setShowUI] = fpS(true);
  const [room, setRoom] = fpS(false);
  const [join, setJoin] = fpS(false);
  const [role, setRole] = fpS("host");
  const [wait, setWait] = fpS(true);
  const [overlay, setOverlay] = fpS(null); // 'epchange' | 'reconnect'
  const hideT = fpR(null);

  fpE(() => { if (!playing) return; const t = setInterval(() => setCur(c => Math.min(c + 2, dur)), 500); return () => clearInterval(t); }, [playing]);
  fpE(() => { try { localStorage.setItem(posKey, String(cur)); } catch (e) {} }, [cur]);
  const bump = () => { setShowUI(true); clearTimeout(hideT.current); hideT.current = setTimeout(() => setShowUI(false), 3200); };
  fpE(() => { bump(); return () => clearTimeout(hideT.current); }, []);

  const isGuest = role === "guest";
  const pct = cur / dur;
  const showSkipIntro = cur < 85;
  const showSkipCredits = cur > dur - 75;
  const showUpNext = cur > dur - 40;
  const parts = [{ name: "Anna", status: "ok" }, { name: "Pavel", status: "buffering" }];

  return (
    <div onMouseMove={bump} style={{ position: "fixed", inset: 0, zIndex: 150, background: "#000", overflow: "hidden", cursor: showUI ? "default" : "none" }}>
      {/* "video" */}
      <div style={{ position: "absolute", inset: 0, backgroundImage: `url("${item.bd}")`, backgroundSize: "cover", backgroundPosition: "center", filter: "brightness(0.62)" }} />
      <div style={{ position: "absolute", inset: 0, background: showUI ? "linear-gradient(0deg, rgba(0,0,0,0.7), transparent 40%), linear-gradient(180deg, rgba(0,0,0,0.5), transparent 30%)" : "none", transition: "background .25s" }} />

      {/* SyncPlay badge (both roles) */}
      <div style={{ position: "absolute", top: 20, left: "50%", transform: "translateX(-50%)", zIndex: 20, display: "inline-flex", alignItems: "center", gap: 8, padding: "7px 14px", borderRadius: 999, background: "rgba(10,8,7,0.6)", border: "1px solid var(--accent-line)", backdropFilter: "blur(8px)", color: "var(--accent-hi)", fontSize: 12, fontWeight: 600, opacity: showUI ? 1 : 0, transition: ".25s" }}>
        {G.users} Смотрим вместе
      </div>

      {/* top bar */}
      <div style={{ position: "absolute", top: 0, left: 0, right: 0, padding: "18px 24px", display: "flex", alignItems: "center", gap: 14, zIndex: 15, opacity: showUI ? 1 : 0, transition: ".25s" }}>
        <button onClick={onClose} className="tv-focus" style={{ all: "unset", cursor: "pointer", width: 40, height: 40, borderRadius: 999, background: "rgba(10,8,7,0.5)", border: "1px solid rgba(255,255,255,0.16)", display: "grid", placeItems: "center", color: "#fff", backdropFilter: "blur(8px)" }}>{G.back}</button>
        <div style={{ flex: 1 }}>
          <div style={{ fontFamily: "var(--font-mono)", fontSize: 11, letterSpacing: 1.4, color: "var(--accent-hi)", fontWeight: 700 }}>S1 · СЕРИЯ {String(ctx.ep).padStart(2, "0")}</div>
          <div style={{ fontSize: 17, fontWeight: 700, color: "#fff", marginTop: 2 }}>{item.title}</div>
        </div>
        {/* host: participant stack */}
        {role === "host" && (
          <div style={{ display: "flex", alignItems: "center" }}>
            {parts.map((p, i) => (
              <div key={p.name} title={`${p.name} · ${(P_STATUS[p.status] || {}).t}`} style={{ marginLeft: i ? -8 : 0, width: 34, height: 34, borderRadius: 34, border: `2px solid ${(P_STATUS[p.status] || P_STATUS.ok).c}`, background: "var(--surface-3)", display: "grid", placeItems: "center", color: "#fff", fontWeight: 700, fontSize: 12, position: "relative" }}>
                {p.name[0]}
                {p.status === "buffering" && <span style={{ position: "absolute", inset: -2, borderRadius: 34, border: "2px solid transparent", borderTopColor: "var(--warn)", animation: "spin .9s linear infinite" }} />}
              </div>
            ))}
          </div>
        )}
      </div>

      {/* center play/pause */}
      {showUI && (
        <button onClick={() => !isGuest && setPlaying(p => !p)} title={isGuest ? "Управляет " + (window.SYNC_HOST || "Yuri") : ""} style={{ all: "unset", cursor: isGuest ? "not-allowed" : "pointer", position: "absolute", top: "50%", left: "50%", transform: "translate(-50%,-50%)", width: 76, height: 76, borderRadius: 76, background: "rgba(10,8,7,0.45)", border: "1px solid rgba(255,255,255,0.25)", display: "grid", placeItems: "center", color: isGuest ? "rgba(255,255,255,0.4)" : "#fff", backdropFilter: "blur(8px)", zIndex: 14 }}>
          <span style={{ transform: "scale(1.6)" }}>{playing ? G.pause : G.play}</span>
        </button>
      )}

      {/* skip intro / credits */}
      {showUI && showSkipIntro && <SkipBtn label="Пропустить заставку" onClick={() => setCur(88)} />}
      {showUI && showSkipCredits && !showUpNext && <SkipBtn label="Пропустить титры" onClick={() => setCur(dur)} />}

      {/* up-next card */}
      {showUpNext && (
        <div style={{ position: "absolute", right: 24, bottom: 150, zIndex: 16, width: 300, background: "rgba(10,8,7,0.82)", border: "1px solid var(--border-strong)", borderRadius: 12, overflow: "hidden", backdropFilter: "blur(10px)" }}>
          <div style={{ display: "flex", gap: 12, padding: 12 }}>
            <div style={{ width: 92, height: 54, borderRadius: 7, backgroundImage: `url("${item.bd}")`, backgroundSize: "cover", backgroundPosition: `${(ctx.ep * 23) % 100}% center`, flexShrink: 0 }} />
            <div style={{ minWidth: 0 }}>
              <div style={{ fontFamily: "var(--font-mono)", fontSize: 10, color: "var(--accent-hi)", fontWeight: 700 }}>ДАЛЕЕ · EP {ctx.ep + 1}</div>
              <div style={{ fontSize: 13, fontWeight: 600, marginTop: 3, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>Следующая серия</div>
              {window.epKind && window.epKind(item.id, ctx.ep + 1) === "filler"
                ? <div style={{ fontSize: 10.5, color: "var(--warn)", marginTop: 4 }}>пропущен 1 филлер</div>
                : <div style={{ fontSize: 10.5, color: "var(--text-faint)", marginTop: 4 }}>авто через 8 c</div>}
            </div>
          </div>
          <button onClick={() => { setCur(42); setOverlay(role === "host" ? null : "epchange"); }} style={{ all: "unset", cursor: "pointer", display: "block", textAlign: "center", padding: "9px 0", background: "var(--accent)", color: "#15100b", fontWeight: 700, fontSize: 12.5 }}>▶ Смотреть сейчас</button>
        </div>
      )}

      {/* bottom HUD */}
      <div style={{ position: "absolute", left: 0, right: 0, bottom: 0, padding: "0 24px 18px", zIndex: 15, opacity: showUI ? 1 : 0, transition: ".25s" }}>
        {/* scrubber */}
        <div style={{ display: "flex", alignItems: "center", gap: 12, marginBottom: 8 }}>
          <span style={{ fontFamily: "var(--font-mono)", fontSize: 12, color: "rgba(255,255,255,0.85)", width: 44 }}>{fmtT(cur)}</span>
          <div title={isGuest ? "Перемоткой управляет " + (window.SYNC_HOST || "Yuri") : ""} style={{ flex: 1, height: 6, borderRadius: 6, background: "rgba(255,255,255,0.22)", overflow: "hidden", cursor: isGuest ? "not-allowed" : "pointer", opacity: isGuest ? 0.55 : 1 }}>
            <div style={{ height: "100%", width: `${pct * 100}%`, background: "var(--accent)", boxShadow: "0 0 8px var(--accent-soft)" }} />
          </div>
          <span style={{ fontFamily: "var(--font-mono)", fontSize: 12, color: "rgba(255,255,255,0.6)", width: 44, textAlign: "right" }}>{fmtT(dur)}</span>
        </div>
        {/* row 1 */}
        <div style={{ display: "flex", alignItems: "center", gap: 4 }}>
          <HudBtn onClick={() => !isGuest && setPlaying(p => !p)} disabled={isGuest} dimTip={"Управляет " + (window.SYNC_HOST || "Yuri")}>{playing ? G.pause : G.play}</HudBtn>
          <HudBtn disabled={isGuest} dimTip="Управляет хост">{G.prev}</HudBtn>
          <HudBtn disabled={isGuest} dimTip="Управляет хост">{G.next}</HudBtn>
          <HudBtn label="">{G.vol}</HudBtn>
          <div style={{ flex: 1 }} />
          <HudBtn label={role === "host" ? "Хост" : "Смотрим вместе"} active onClick={() => setRoom(true)}>{G.users}</HudBtn>
        </div>
        {/* row 2 — per-viewer, always active */}
        <div style={{ display: "flex", alignItems: "center", gap: 4, marginTop: 4 }}>
          <HudBtn label="Русская озвучка">{G.audio}</HudBtn>
          <HudBtn label="Субтитры: рус">{G.cc}</HudBtn>
          <HudBtn label="1080p">{G.gear}</HudBtn>
          <div style={{ flex: 1 }} />
          {isGuest && <HudBtn label="Покинуть комнату" onClick={() => setRole("host")}>✕</HudBtn>}
          <HudBtn label="">{G.full}</HudBtn>
        </div>
      </div>

      {/* episode-change overlay (guest) */}
      {overlay === "epchange" && <CenterOverlay onDone={() => setOverlay(null)} text={`${window.SYNC_HOST || "Yuri"} включил EP ${ctx.ep + 1}`} />}
      {overlay === "reconnect" && <CenterOverlay text="переподключение к комнате…" spin />}

      {overlay === "joined" && <CenterOverlay onDone={() => setOverlay(null)} text={`Вы подключились к комнате ${window.SYNC_HOST || "Anna"}`} />}

      {room && <RoomModal role={role} onClose={() => setRoom(false)} onJoinScreen={() => { setRoom(false); setJoin(true); }} onLeave={() => { setRole("host"); setRoom(false); }} wait={wait} setWait={setWait} parts={parts} />}
      {join && <SyncJoinScreen onJoin={() => { window.SYNC_HOST = "Anna"; setRole("guest"); setJoin(false); setOverlay("joined"); }} onClose={() => setJoin(false)} />}

      {/* demo triggers */}
      {showUI && (
        <div style={{ position: "absolute", top: 20, right: 24, zIndex: 18, display: "flex", gap: 8 }}>
          <button onClick={() => setOverlay("reconnect")} style={demoBtn}>демо: обрыв</button>
        </div>
      )}
    </div>
  );
};

const demoBtn = { all: "unset", cursor: "pointer", fontFamily: "var(--font-mono)", fontSize: 10, color: "rgba(255,255,255,0.5)", border: "1px solid rgba(255,255,255,0.16)", borderRadius: 6, padding: "4px 8px", background: "rgba(0,0,0,0.3)" };

const SkipBtn = ({ label, onClick }) => (
  <button onClick={onClick} className="tv-focus" style={{ all: "unset", cursor: "pointer", position: "absolute", right: 24, bottom: 150, zIndex: 16, padding: "11px 18px", borderRadius: 10, background: "rgba(10,8,7,0.8)", border: "1px solid rgba(255,255,255,0.25)", color: "#fff", fontSize: 13, fontWeight: 600, backdropFilter: "blur(8px)" }}>{label} ⏭</button>
);

const CenterOverlay = ({ text, spin, onDone }) => {
  fpE(() => { if (onDone) { const t = setTimeout(onDone, 2400); return () => clearTimeout(t); } }, []);
  return (
    <div style={{ position: "absolute", inset: 0, zIndex: 40, display: "grid", placeItems: "center", background: "rgba(0,0,0,0.35)", backdropFilter: "blur(3px)" }}>
      <div style={{ display: "flex", alignItems: "center", gap: 12, padding: "16px 24px", borderRadius: 12, background: "rgba(10,8,7,0.85)", border: "1px solid var(--border-strong)", color: "#fff", fontSize: 15, fontWeight: 600 }}>
        {spin && <span style={{ width: 16, height: 16, border: "2px solid rgba(255,255,255,0.2)", borderTopColor: "var(--accent-hi)", borderRadius: 16, animation: "spin .9s linear infinite" }} />}
        {text}
      </div>
    </div>
  );
};

window.__play = (id, ep) => { window.__playCtx = { id, ep: ep || 1 }; window.SYNC_HOST = "Anna"; window.__nav && window.__nav("player"); };
Object.assign(window, { PlayerScreen });
