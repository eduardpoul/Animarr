// ====== Animarr — feature data layer (shared by all 6 features) ======
// Loads AFTER data-v4.jsx. Adds, on window.*:
//   RU            — Russian UI strings
//   epMeta(item,n)— canonical per-episode metadata (title, overview, air date,
//                   runtime, rating, kind: canon|filler|recap)
//   epKind, epStats, FILLER_MAP
//   AIRING        — ongoing-calendar dataset
//   WATCHLIST     — per-user "Хочу посмотреть" Set (mirrored like FAVORITES)
// Everything is deterministic so re-renders are stable.

(function () {
  // ── deterministic PRNG ──────────────────────────────────────
  function hash(str) {
    let h = 2166136261;
    for (let i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 16777619); }
    return h >>> 0;
  }
  function rng(seed) {
    let a = seed >>> 0;
    return () => { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
  }
  const pick = (arr, r) => arr[Math.floor(r * arr.length) % arr.length];

  // ── Russian strings ─────────────────────────────────────────
  const RU = {
    // nav / sections
    watchlist: "Хочу посмотреть", calendar: "Календарь", stats: "Статистика",
    discover: "Открой новое", forYou: "Для тебя", thisWeek: "На этой неделе",
    franchise: "Франшиза", watchTogether: "Смотреть вместе",
    // episode
    episode: "Серия", season: "Сезон", ep: "Серия", filler: "Филлер", recap: "Рекап",
    canon: "Канон", watched: "Просмотрено", resume: "Продолжить", onDisk: "На диске",
    missing: "Нет файла", rating: "Рейтинг", runtime: "Длит.",
    // views
    grid: "Сетка", list: "Список", hideFiller: "Скрыть филлеры",
    // watchlist button
    add: "Хочу", inList: "В списке", wantToWatch: "Хочу посмотреть",
    // calendar statuses
    upcoming: "Ожидается", airedWaiting: "Вышла — ждём файл", inLibrary: "В библиотеке",
    today: "Сегодня", tomorrow: "Завтра",
    weekdaysShort: ["вс", "пн", "вт", "ср", "чт", "пт", "сб"],
    weekdaysFull: ["Воскресенье", "Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота"],
    months: ["янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"],
  };

  // ── episode title / overview pools (RU, evocative but generic) ──
  const TITLES = [
    "Разлом небес", "Клятва на крови", "Тень над городом", "Первый удар", "Пробуждение",
    "Багровый рассвет", "Голос бездны", "Меч и пламя", "Слово государя", "Ледяная тишина",
    "Раскол", "Незваный гость", "Северный ветер", "Корни ненависти", "Печать судьбы",
    "Единственный путь", "За пределом", "Возвращение", "Последний рубеж", "Сосуд богов",
    "Цена силы", "Трещина во времени", "Наследие", "Расстояние между нами", "Конец начала",
  ];
  const OVERVIEWS = [
    "Герой оказывается перед выбором, который изменит расстановку сил в регионе, — и цена ошибки слишком высока.",
    "Старые союзы дают трещину: то, что казалось надёжным, оборачивается ловушкой в самый неподходящий момент.",
    "Пока противник готовит внезапный ход, наши вынуждены действовать вслепую и доверять чутью, а не расчёту.",
    "Тайна прошлого выходит на свет и переворачивает представление о том, кто здесь друг, а кто враг.",
    "Затишье перед бурей: короткая передышка обнажает истинные мотивы каждого из участников.",
    "Решающее столкновение подходит вплотную, и герою приходится заплатить за силу дороже, чем он рассчитывал.",
    "Мир на грани: одна случайность грозит миллионам, и вмешательство теперь значит куда больше, чем просто бой.",
    "Возвращение на поле боя даётся тяжело — раны ещё свежи, а доверие приходится завоёвывать заново.",
  ];

  // ── filler / recap map — keyed by title id, local episode n (1..24) ──
  // Only well-known long shows carry data; niche titles have none (это норма).
  const FILLER_MAP = {
    bleach:       { filler: new Set([8, 9, 15, 16, 17, 22]), recap: new Set([12]) },
    "fights-break": { filler: new Set([19, 20]), recap: new Set([]) },
    "swallowed-star": { filler: new Set([14]), recap: new Set([7]) },
    fulltime:     { filler: new Set([11, 21]), recap: new Set([]) },
  };
  function epKind(id, n) {
    const m = FILLER_MAP[id];
    if (!m) return "canon";
    if (m.recap.has(n)) return "recap";
    if (m.filler.has(n)) return "filler";
    return "canon";
  }
  function epStats(item) {
    const total = Math.min(item.episodes || 12, 24);
    let filler = 0, recap = 0;
    for (let n = 1; n <= total; n++) { const k = epKind(item.id, n); if (k === "filler") filler++; else if (k === "recap") recap++; }
    // scale the headline canon/filler numbers up to the show's real length for realism
    const scale = (item.episodes || total) / total;
    return {
      hasData: !!FILLER_MAP[item.id],
      canon: Math.round((total - filler - recap) * scale),
      filler: Math.round(filler * scale),
      recap: Math.round(recap * scale),
    };
  }

  // ── per-episode metadata ────────────────────────────────────
  function epMeta(item, n) {
    const r = rng(hash(item.id + ":" + n));
    const kind = epKind(item.id, n);
    // air dates: weekly cadence from a per-title base
    const baseR = rng(hash(item.id));
    const base = new Date(2007 + Math.floor(baseR() * 16), Math.floor(baseR() * 12), 1 + Math.floor(baseR() * 27));
    const air = new Date(base.getTime() + (n - 1) * 7 * 864e5);
    const rating = +(7.2 + r() * 2.4).toFixed(1);
    return {
      n, kind,
      title: kind === "recap" ? "Краткое содержание" : pick(TITLES, r()),
      overview: pick(OVERVIEWS, r()),
      air,
      rating,
      runtime: item.runtime || "24 мин",
    };
  }

  // ── ongoing calendar (feature 1 seed) ───────────────────────
  // status: upcoming | aired-waiting | in-library
  const now = new Date();
  const day = 864e5;
  function at(hAdd, hour, min) {
    const d = new Date(now.getTime() + hAdd * day);
    d.setHours(hour, min, 0, 0); return d;
  }
  const AIRING = [
    { id: "xian-ni",     season: 1, ep: 62, airingAt: at(0, 20, 0),  status: "in-library" },
    { id: "shrouding",   season: 1, ep: 88, airingAt: at(0, 12, 30), status: "aired-waiting" },
    { id: "throne-seal", season: 2, ep: 41, airingAt: at(1, 19, 0),  status: "upcoming" },
    { id: "beyond-gaze", season: 1, ep: 15, airingAt: at(2, 18, 0),  status: "upcoming" },
    { id: "swallowed-star", season: 1, ep: 143, airingAt: at(2, 21, 0), status: "upcoming" },
    { id: "jade",        season: 1, ep: 37, airingAt: at(3, 20, 30), status: "upcoming" },
    { id: "fights-break", season: 5, ep: 117, airingAt: at(4, 20, 0), status: "upcoming" },
    { id: "embers",      season: 1, ep: 19, airingAt: at(5, 17, 30), status: "upcoming" },
    { id: "shrouding",   season: 1, ep: 89, airingAt: at(6, 12, 30), status: "upcoming" }, // 2nd ep of same title this week
    { id: "fulltime",    season: 7, ep: 158, airingAt: at(-1, 20, 0), status: "in-library" },
  ].map(a => ({ ...a, item: window.LIBRARY.find(x => x.id === a.id) })).filter(a => a.item);

  // ── watchlist — per user, mirrored like FAVORITES ───────────
  const WATCHLIST_STATE = {
    "u-admin": new Set(["frieren-like", "gundam-00", "jade", "beyond-gaze"].filter(id => window.LIBRARY.some(x => x.id === id))),
    "u-anna":  new Set(["ne-zha"]),
    "u-pavel": new Set(),
  };
  // seed a couple that definitely exist
  WATCHLIST_STATE["u-admin"].add("gundam-00"); WATCHLIST_STATE["u-admin"].add("jade");

  const _applyUser = window.applyUser;
  window.applyUser = function (user) {
    _applyUser(user);
    window.WATCHLIST = WATCHLIST_STATE[user.id] || (WATCHLIST_STATE[user.id] = new Set());
  };
  window.applyUser(window.CURRENT_USER); // re-apply so WATCHLIST is set now

  // ── TAG localisation (for "похожее" reasons) ────────────────
  const TAG_RU = { Donghua: "донхуа", Cultivation: "культивация", "Sci-fi": "фантастика", Fantasy: "фэнтези", Mecha: "меху", Action: "экшен", Shonen: "сёнэн", Mystery: "мистику", Thriller: "триллеры", Animation: "анимацию", Adult: "18+", "Costume Drama": "костюмную драму", Swordplay: "уся", Adventure: "приключения" };

  // ── Franchises (in watch order; some nodes aren't in the library) ──
  const FRANCHISES = {
    gundam: { title: "Mobile Suit Gundam", nodes: [
      { id: "gundam-seed", title: "Gundam SEED", year: 2002, format: "TV", relation: "Первая часть" },
      { id: null, title: "Gundam SEED Destiny", year: 2004, format: "TV", relation: "Сиквел" },
      { id: "gundam-00", title: "Gundam 00", year: 2007, format: "TV", relation: "Новая арка" },
      { id: null, title: "Gundam 00: A wakening of the Trailblazer", year: 2010, format: "Movie", relation: "Фильм" },
      { id: null, title: "Gundam Unicorn", year: 2010, format: "OVA", relation: "Спин-офф" },
      { id: null, title: "Iron-Blooded Orphans", year: 2015, format: "TV", relation: "Спин-офф" },
    ] },
    nezha: { title: "Ne Zha", nodes: [
      { id: null, title: "Ne Zha", year: 2019, format: "Movie", relation: "Первая часть" },
      { id: "ne-zha", title: "Ne Zha 2", year: 2025, format: "Movie", relation: "Сиквел" },
    ] },
    mi: { title: "Mission: Impossible", nodes: [
      { id: null, title: "Mission: Impossible", year: 1996, format: "Movie", relation: "Первая часть" },
      { id: null, title: "M:I-2", year: 2000, format: "Movie", relation: "Сиквел" },
      { id: null, title: "M:I III", year: 2006, format: "Movie", relation: "Сиквел" },
      { id: null, title: "Ghost Protocol", year: 2011, format: "Movie", relation: "Сиквел" },
      { id: null, title: "Rogue Nation", year: 2015, format: "Movie", relation: "Сиквел" },
      { id: "mi-fallout", title: "Fallout", year: 2018, format: "Movie", relation: "Сиквел" },
      { id: null, title: "Dead Reckoning", year: 2023, format: "Movie", relation: "Сиквел" },
      { id: null, title: "The Final Reckoning", year: 2025, format: "Movie", relation: "Финал" },
    ] },
    bleach: { title: "Bleach", nodes: [
      { id: "bleach", title: "Bleach", year: 2004, format: "TV", relation: "Основной сериал", seasons: "S1–S16" },
      { id: null, title: "Bleach: Thousand-Year Blood War", year: 2022, format: "TV", relation: "Финальная арка" },
    ] },
    doupo: { title: "Battle Through the Heavens", nodes: [
      { id: "fights-break", title: "Fights Break Sphere", year: 2017, format: "TV", relation: "Основной сериал", seasons: "S1–S5" },
      { id: null, title: "Battle Through the Heavens: спешл", year: 2019, format: "OVA", relation: "Спин-офф" },
    ] },
  };
  const ITEM_FRANCHISE = { "gundam-seed": "gundam", "gundam-00": "gundam", "ne-zha": "nezha", "mi-fallout": "mi", "bleach": "bleach", "fights-break": "doupo" };

  window.franchiseFor = (item) => {
    const fid = ITEM_FRANCHISE[item.id]; if (!fid) return null;
    const f = FRANCHISES[fid]; if (!f) return null;
    const nodes = f.nodes.map(n => ({ ...n, inLib: !!(n.id && window.LIBRARY.some(x => x.id === n.id)), current: n.id === item.id }));
    const curIdx = nodes.findIndex(n => n.current);
    const watched = nodes.filter((n, i) => n.inLib && curIdx >= 0 && i <= curIdx).length;
    return { title: f.title, nodes, total: nodes.length, watched };
  };

  const TYPE_RU = { Anime: "аниме", Series: "сериал", Movie: "фильм" };
  window.similarFor = (item) => {
    const scored = window.LIBRARY.filter(x => x.id !== item.id).map(x => {
      const shared = (x.tags || []).filter(t => (item.tags || []).includes(t));
      return { item: x, shared, score: shared.length + (x.type === item.type ? 0.4 : 0) };
    }).filter(s => s.score > 0).sort((a, b) => (b.score - a.score) || (b.item.rating - a.item.rating)).slice(0, 10);
    return scored.map(s => ({ item: s.item, reason: s.shared.length ? `Тоже ${TAG_RU[s.shared[0]] || s.shared[0].toLowerCase()}` : `Тоже ${TYPE_RU[s.item.type] || "из библиотеки"}` }));
  };

  Object.assign(window, { RU, epMeta, epKind, epStats, FILLER_MAP, AIRING, WATCHLIST_STATE, TAG_RU, FRANCHISES, ITEM_FRANCHISE });
})();
