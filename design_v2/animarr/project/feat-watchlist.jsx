// ====== Animarr — Watchlist page (Хочу посмотреть) ======
// Loads after feat-shared.jsx. Exposes window.WatchlistPage.
const wlTitleWord = (n) => { const d = n % 10, dd = n % 100; if (d === 1 && dd !== 11) return "тайтл"; if (d >= 2 && d <= 4 && (dd < 10 || dd >= 20)) return "тайтла"; return "тайтлов"; };

const WatchlistEmpty = () => (
  <div style={{ display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", textAlign: "center", padding: "90px 20px", gap: 16 }}>
    <div style={{ width: 64, height: 64, borderRadius: 18, display: "grid", placeItems: "center", background: "var(--surface)", border: "1px solid var(--border-strong)", color: "var(--text-faint)" }}>
      {window.BookmarkPlus ? <window.BookmarkPlus size={28} /> : null}
    </div>
    <div style={{ fontFamily: "var(--font-display)", fontSize: 24, fontWeight: 700, letterSpacing: -0.4 }}>Список пуст</div>
    <div style={{ fontSize: 14, color: "var(--text-dim)", maxWidth: 430, lineHeight: 1.6 }}>
      Добавляйте тайтлы кнопкой «Хочу» на постерах, в рекомендациях, франшизах и календаре — они соберутся здесь.
    </div>
  </div>
);

const WatchlistPage = ({ onOpen }) => {
  const wl = window.useWatchlist();
  const items = wl.list().map(id => window.LIBRARY.find(x => x.id === id)).filter(Boolean);
  return (
    <window.WidePage top>
      <div style={{ padding: "6px 0 80px" }}>
        <window.SectionHead overline="МОЙ СПИСОК" title={window.RU.watchlist}
          sub="Тайтлы, отложенные на потом — из рекомендаций, франшиз и календаря."
          right={<span style={{ fontFamily: "var(--font-mono)", fontSize: 12.5, color: "var(--text-faint)" }}>{items.length} {wlTitleWord(items.length)}</span>} />
        {items.length === 0 ? <WatchlistEmpty /> : (
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(178px, 1fr))", gap: 18 }}>
            {items.map(it => (
              <window.Poster key={it.id} item={it} w="100%" h={266} onClick={() => onOpen && onOpen(it.id)} />
            ))}
          </div>
        )}
      </div>
    </window.WidePage>
  );
};

Object.assign(window, { WatchlistPage });
