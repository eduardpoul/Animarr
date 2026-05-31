// ====== mock library data ======
// Original poster compositions are generated procedurally — we don't reuse copyrighted art.
// Each item has: title, alt-CJK, year, type, tags, hue (for the generated poster tint),
// optional backdrop URL (cinematic atmosphere photos), confidence, etc.

// Curated atmospheric backdrops (Unsplash) — moody, painterly, cinematic
const BD = {
  snow:   "https://images.unsplash.com/photo-1483356345088-49e5b1bfacdc?auto=format&fit=crop&w=2000&q=70",
  mist:   "https://images.unsplash.com/photo-1502472584811-0a2f2feb8968?auto=format&fit=crop&w=2000&q=70",
  peak:   "https://images.unsplash.com/photo-1464822759023-fed622ff2c3b?auto=format&fit=crop&w=2000&q=70",
  forest: "https://images.unsplash.com/photo-1518495973542-4542c06a5843?auto=format&fit=crop&w=2000&q=70",
  storm:  "https://images.unsplash.com/photo-1500964757637-c85e8a162699?auto=format&fit=crop&w=2000&q=70",
  red:    "https://images.unsplash.com/photo-1518709594023-6eab9bab7b23?auto=format&fit=crop&w=2000&q=70",
  temple: "https://images.unsplash.com/photo-1528360983277-13d401cdc186?auto=format&fit=crop&w=2000&q=70",
  city:   "https://images.unsplash.com/photo-1542931287-023b922fa89b?auto=format&fit=crop&w=2000&q=70",
  blade:  "https://images.unsplash.com/photo-1606293459275-3b9a0e6dcabd?auto=format&fit=crop&w=2000&q=70",
  cosmos: "https://images.unsplash.com/photo-1465101162946-4377e57745c3?auto=format&fit=crop&w=2000&q=70",
  cliff:  "https://images.unsplash.com/photo-1454496522488-7a8e488e8606?auto=format&fit=crop&w=2000&q=70",
  ink:    "https://images.unsplash.com/photo-1518621736915-f3b1c41bfd00?auto=format&fit=crop&w=2000&q=70",
};

const LIBRARY = [
  // donghua / cultivation
  { id: "perfect-world",  title: "Perfect World",  cjk: "完美世界", year: 2018, type: "Anime", tags:["Donghua","Cultivation"], hue: 12,  bd: BD.storm,  conf: 0.98, episodes: 270, season: "TV-1", rating: 8.4, runtime: "20m", studio:"Yuewen", lang:"Mandarin", overview:"Shi Hao, born with extraordinary talent, walks the cruel road of cultivation across a perfect world soaked in war, betrayal and forbidden ancient ruins." },
  { id: "swallowed-star", title: "Swallowed Star", cjk: "吞噬星空", year: 2020, type: "Anime", tags:["Donghua","Sci-fi"], hue: 220, bd: BD.cosmos, conf: 0.97, episodes: 224, season: "TV-1", rating: 7.9, runtime: "20m", studio:"Sparkly Key", lang:"Mandarin", overview:"After a meteor shower mutates Earth's life, Luo Feng rises from a militia trainee to a planetary-class warrior in a universe ruled by cosmic predators." },
  { id: "xian-ni",        title: "Xian Ni",        cjk: "仙逆",     year: 2023, type: "Anime", tags:["Donghua","Cultivation"], hue: 200, bd: BD.snow,   conf: 0.96, episodes: 141, season: "TV-1", rating: 8.2, runtime: "22m", studio:"Foch Films", lang:"Mandarin", overview:"Wang Lin, a quiet boy from a poor village, is dragged into a sect of immortals and forced to defy heaven itself to claim his place." },
  { id: "shrouding",      title: "Shrouding the Heavens", cjk:"遮天", year:2024, type:"Anime", tags:["Donghua","Cultivation"], hue: 195, bd: BD.mist, conf: 0.95, episodes: 162, season:"TV-1", rating: 8.0, runtime:"22m", studio:"Yuewen", lang:"Mandarin", overview:"Nine coffins drag a college bus into a star sea. A handful of survivors must learn the forbidden cultivation arts to crawl back to a darkening Earth." },
  { id: "renegade",       title: "Renegade Immortal", cjk:"仙逆 ", year:2023, type:"Anime", tags:["Donghua","Cultivation"], hue: 28, bd: BD.cliff, conf: 0.93, episodes: 80, season:"TV-1", rating: 8.1, runtime:"22m", studio:"Foch Films", lang:"Mandarin", overview:"A heaven-defying journey of one mortal who refuses to bow — even to the immortals themselves." },
  { id: "mortals",        title: "A Record of a Mortal's Journey",  cjk:"凡人修仙传", year:2020, type:"Anime", tags:["Donghua","Cultivation"], hue: 168, bd: BD.forest, conf: 0.99, episodes: 110, season:"TV-2", rating: 8.5, runtime:"22m", studio:"Wan Wei Mao Donghua", lang:"Mandarin", overview:"Han Li, a plain mortal of common birth, climbs the cruel ladder of the immortal world step by quiet step — outliving emperors, beasts and time itself." },
  { id: "will-eternal",   title: "A Will Eternal", cjk:"一念永恒", year:2020, type:"Anime", tags:["Donghua","Cultivation"], hue: 290, bd: BD.peak, conf: 0.92, episodes: 156, season:"TV-3", rating: 7.8, runtime:"22m", studio:"B.CMAY", lang:"Mandarin", overview:"With a single thought he ascends, with a single thought he destroys. A trickster cultivator turns the rules of the sect upside down." },
  { id: "stellar",        title: "Stellar Transformation", cjk:"星辰变", year:2018, type:"Anime", tags:["Donghua","Cultivation"], hue: 230, bd: BD.cosmos, conf: 0.94, episodes: 64, season:"TV-1", rating: 7.7, runtime:"22m", studio:"Sparkly Key", lang:"Mandarin", overview:"A boy born without a cultivation meridian forges his own path — turning his weakness into a star that swallows worlds." },
  { id: "throne-seal",    title: "Throne of Seal", cjk:"神印王座", year:2022, type:"Anime", tags:["Donghua","Fantasy"], hue: 220, bd: BD.temple, conf: 0.93, episodes: 92, season:"TV-1", rating: 7.5, runtime:"22m", studio:"Bilibili", lang:"Mandarin", overview:"Across the demon-scarred continent, six holy orders defend humanity. A boy chosen by the god of death must seize the seal — or be devoured by it." },
  { id: "martial",        title: "Martial Universe", cjk:"武动乾坤", year:2019, type:"Anime", tags:["Donghua","Cultivation"], hue: 35, bd: BD.red, conf: 0.92, episodes: 78, season:"TV-2", rating: 7.6, runtime:"22m", studio:"Tencent Penguin", lang:"Mandarin", overview:"In a family torn by feud, a young heir digs deep into the ancient martial scriptures buried beneath his clan." },
  { id: "fights-break",   title: "Fights Break Sphere", cjk:"斗破苍穹", year:2017, type:"Anime", tags:["Donghua","Cultivation"], hue: 5, bd: BD.storm, conf: 0.97, episodes: 116, season:"TV-3", rating: 8.0, runtime:"22m", studio:"Shanghai Motion", lang:"Mandarin", overview:"Xiao Yan, once a prodigy stripped of his power, walks the long road back — flame in one hand, an ancient master's spirit in the other." },
  { id: "fulltime",       title: "Full-Time Magister", cjk:"全职法师", year:2017, type:"Anime", tags:["Donghua","Fantasy"], hue: 245, bd: BD.city, conf: 0.91, episodes: 156, season:"TV-7", rating: 7.4, runtime:"22m", studio:"Shenman", lang:"Mandarin", overview:"In a modern world overrun by magical beasts, a single boy awakens four elements at once and rewrites the rules of the magic academy." },
  { id: "beyond-gaze",    title: "Beyond Time's Gaze", cjk:"凡尘", year:2025, type:"Anime", tags:["Donghua","Sci-fi"], hue: 295, bd: BD.cosmos, conf: 0.86, episodes: 24, season:"TV-1", rating: 7.9, runtime:"23m", studio:"Bilibili", lang:"Mandarin", overview:"A girl gifted with second sight watches the same disaster repeat across a thousand parallel cities — and decides to break the loop." },
  { id: "embers",         title: "Embers",          cjk:"灰烬", year:2025, type:"Anime", tags:["Donghua","Mystery"], hue: 18, bd: BD.red,    conf: 0.78, episodes: 18, season:"TV-1", rating: 7.6, runtime:"22m", studio:"Bilibili", lang:"Mandarin", overview:"After a city burns to ash, a young investigator must read the bones of the past to find the hand that struck the first match." },

  // jp anime
  { id: "bleach",         title: "Bleach", cjk:"ブリーチ", year:2004, type:"Anime", tags:["Shonen","Action"], hue: 35,  bd: BD.blade, conf: 0.99, episodes: 366, season:"TV-1", rating: 8.2, runtime:"24m", studio:"Pierrot", lang:"Japanese", overview:"Ichigo, a delinquent who can see ghosts, accidentally takes on the duty of a soul reaper — and the entire afterlife along with it." },
  { id: "gundam-seed",    title: "Mobile Suit Gundam SEED", cjk:"機動戦士", year:2002, type:"Anime", tags:["Mecha","Sci-fi"], hue: 220, bd: BD.cosmos, conf: 0.98, episodes: 50, season:"TV-1", rating: 7.9, runtime:"24m", studio:"Sunrise", lang:"Japanese", overview:"Two friends on opposite sides of a war between genetically engineered humans and naturals — each forced to pilot a god-machine they never asked for." },
  { id: "gundam-00",      title: "Mobile Suit Gundam 00", cjk:"00", year:2007, type:"Anime", tags:["Mecha","Sci-fi"], hue: 195, bd: BD.cosmos, conf: 0.98, episodes: 50, season:"TV-1", rating: 8.0, runtime:"24m", studio:"Sunrise", lang:"Japanese", overview:"A private paramilitary force intervenes in every armed conflict on Earth — to force humanity into a single, terrified peace." },
  { id: "jade",           title: "Jade Dynasty", cjk:"诛仙", year:2022, type:"Anime", tags:["Donghua","Cultivation"], hue: 145, bd: BD.forest, conf: 0.92, episodes: 36, season:"TV-1", rating: 7.5, runtime:"22m", studio:"Shanghai Motion", lang:"Mandarin", overview:"Orphaned and untalented, Zhang Xiaofan stumbles into the most prestigious sect of the cultivation world — and into a forbidden art that will damn him." },

  // movies & live-action
  { id: "arcane",         title: "Arcane", cjk:"奥术", year:2021, type:"Series", tags:["Animation","Adult"], hue: 270, bd: BD.city, conf: 0.99, episodes: 18, season:"S2", rating: 9.0, runtime:"40m", studio:"Fortiche", lang:"English", overview:"Two sisters torn apart by a city that runs on stolen magic — one rises in the gleaming towers, one burns in the underground." },
  { id: "mi-fallout",     title: "Mission: Impossible — Fallout", cjk:"碟中谍", year:2018, type:"Movie", tags:["Action","Thriller"], hue: 5, bd: BD.cliff, conf: 0.99, episodes: 1, season:"-", rating: 7.7, runtime:"2h27m", studio:"Paramount", lang:"English", overview:"An IMF agent races across three continents to recover stolen plutonium before a stateless network detonates the world." },
  { id: "ne-zha",         title: "Ne Zha 2", cjk:"哪吒", year:2025, type:"Movie", tags:["Animation","Fantasy"], hue: 18, bd: BD.red, conf: 0.95, episodes: 1, season:"-", rating: 8.3, runtime:"2h24m", studio:"Coloroom", lang:"Mandarin", overview:"Born as a demon child fated to destroy the world, Ne Zha defies heaven, hell, and his own father to claim the path he refuses to inherit." },
  { id: "gorge",          title: "The Gorge", cjk:"裂谷", year:2025, type:"Movie", tags:["Action","Mystery"], hue: 230, bd: BD.mist, conf: 0.88, episodes: 1, season:"-", rating: 7.4, runtime:"2h7m", studio:"Apple", lang:"English", overview:"Two snipers on opposite cliffs of a forbidden gorge must defend the world from what crawls out of the fog between them." },
];

// Currently-airing / "needs-review" stragglers — handled as a separate state
const NEEDS_REVIEW = [
  {
    id: "nr-1",
    folder: "[Anistar.org] Tian Bao Fu Yao Lu [TV-3]",
    candidates: [
      { title: "Tian Bao Fu Yao Lu",     year: 2022, source: "TMDB",  cjk:"天宝伏妖录", conf: 0.78, hue: 285 },
      { title: "Tian Bao Fuyao Lu II",   year: 2024, source: "MAL",   cjk:"天宝伏妖录", conf: 0.71, hue: 295 },
      { title: "Heaven Official's Blessing", year: 2020, source:"IMDb", cjk:"天官赐福", conf: 0.34, hue: 12 },
    ],
  },
  {
    id: "nr-2",
    folder: "薛先生的猛主日记 (2026)",
    candidates: [
      { title: "Mr. Xue's Fierce Master Diary", year: 2026, source:"MAL", cjk:"薛先生的猛主日记", conf: 0.66, hue: 168 },
      { title: "Mr. Xue's Diary",                year: 2025, source:"TMDB", cjk:"薛先生", conf: 0.41, hue: 200 },
    ],
  },
];

// Mock torrents — mix of active/done/queued
const TORRENTS = [
  { id:"t1", name:"[Anistar.org] Perfect World [TV-1] - 270 [1080p].mp4",   dest:"Perfect World (2018)",       progress: 1.00, dn: 0,    up: 1.2,  peers: "0/0",   eta:"—",       state:"seeding",    size:"824 MB" },
  { id:"t2", name:"[Anistar.org] Perfect World [TV-1] - 269 [1080p].mp4",   dest:"Perfect World (2018)",       progress: 1.00, dn: 0,    up: 0.8,  peers: "0/0",   eta:"—",       state:"seeding",    size:"802 MB" },
  { id:"t3", name:"[Anistar.org] Swallowed Star [TV-1] - 224 [1080p].mp4",  dest:"Swallowed Star (2020)",      progress: 1.00, dn: 0,    up: 2.4,  peers: "3/14", eta:"—",       state:"seeding",    size:"781 MB" },
  { id:"t4", name:"[Anistar.org] Xian Ni [TV-1] - 141 [1080p].mp4",         dest:"Xian Ni",                    progress: 0.62, dn: 14.2, up: 0.4,  peers: "11/52",eta:"4m 12s",  state:"downloading",size:"758 MB" },
  { id:"t5", name:"[Anistar.org] Shrouding the Heavens [TV-1] - 162 [1080p].mp4", dest:"Shrouding the Heavens", progress: 0.34, dn: 18.7, up: 0.1, peers: "23/64",eta:"7m 41s", state:"downloading",size:"812 MB" },
  { id:"t6", name:"[SubsKindly] Mobile Suit Gundam SEED FREEDOM [BD].mkv",  dest:"— Needs identification —",   progress: 0.00, dn: 0,    up: 0,    peers: "0/0",   eta:"queued",  state:"queued",     size:"24.6 GB" },
  { id:"t7", name:"Ne Zha 2 2025 1080p WEB-DL.mkv",                          dest:"Ne Zha 2",                   progress: 0.91, dn: 22.1, up: 0.0,  peers: "47/188",eta:"38s",    state:"downloading",size:"6.1 GB" },
];

// rename history
const HISTORY = [
  { id:"h1", at:"15:42:08", date:"2026-05-22", file:"[Anistar.org] Perfect World [TV-1] - 270 [1080p].mp4", to:"Perfect World - S01E270 - 1080p.mp4", pattern:"Anime Episode (anistar)", folder:"Perfect World (2018)", reverted:false },
  { id:"h2", at:"15:42:08", date:"2026-05-22", file:"[Anistar.org] Perfect World [TV-1] - 269 [1080p].mp4", to:"Perfect World - S01E269 - 1080p.mp4", pattern:"Anime Episode (anistar)", folder:"Perfect World (2018)", reverted:false },
  { id:"h3", at:"14:18:33", date:"2026-05-22", file:"Ne.Zha.2.2025.1080p.WEB-DL.x265.mkv", to:"Ne Zha 2 (2025).mkv", pattern:"Movie + Year",      folder:"Ne Zha 2",            reverted:false },
  { id:"h4", at:"11:02:11", date:"2026-05-22", file:"[Erao-raws] Mobile Suit Gundam SEED FREEDOM - 01.mkv", to:"Mobile Suit Gundam SEED FREEDOM - S01E01.mkv", pattern:"Anime Episode (erao)", folder:"Mobile Suit Gundam SEED FREEDOM", reverted:true },
  { id:"h5", at:"22:51:00", date:"2026-05-21", file:"Arcane.S02E09.WEB-DL.1080p.mkv",                  to:"Arcane - S02E09 - 1080p.mkv",                pattern:"Series (S/E)",       folder:"Arcane (2021)",       reverted:false },
  { id:"h6", at:"22:50:59", date:"2026-05-21", file:"Arcane.S02E08.WEB-DL.1080p.mkv",                  to:"Arcane - S02E08 - 1080p.mkv",                pattern:"Series (S/E)",       folder:"Arcane (2021)",       reverted:false },
  { id:"h7", at:"19:08:14", date:"2026-05-21", file:"[Anistar.org] Xian Ni [TV-1] - 140 [1080p].mp4",  to:"Xian Ni - S01E140 - 1080p.mp4",              pattern:"Anime Episode (anistar)", folder:"Xian Ni",        reverted:false },
];

// folder watchers / section folders (Explorer)
const FOLDERS = [
  { id:"f-anime",   kind:"section", title:"Anime",      path:"/Pool-D1/Media/Anime",      watchers: 24, ident: 22, missing: 2, hue: 5,   bd: BD.snow  },
  { id:"f-movies",  kind:"section", title:"Movies",     path:"/Pool-D1/Media/Movies",     watchers: 18, ident: 18, missing: 0, hue: 215, bd: BD.peak  },
  { id:"f-multi",   kind:"section", title:"Multserials",path:"/Pool-D1/Media/Multserial", watchers: 4,  ident: 4,  missing: 0, hue: 285, bd: BD.ink   },
  { id:"f-series",  kind:"section", title:"Serials",    path:"/Pool-D1/Media/Series",     watchers: 12, ident: 11, missing: 1, hue: 35,  bd: BD.city  },
  { id:"f-donghua", kind:"section", title:"Donghua",    path:"/Pool-D1/Media/Donghua",    watchers: 31, ident: 28, missing: 3, hue: 12,  bd: BD.storm },
];

// patterns & ignore rules
const PATTERNS = [
  { id:"p1", name:"Anime Episode (anistar)", regex:"\\[Anistar\\.org\\] (?<title>.+?) \\[(?<season>TV-\\d+)\\] - (?<episode>\\d+).*", scope:"Global", priority: 100, enabled:true },
  { id:"p2", name:"Anime Episode (erao)",    regex:"\\[Erao-raws\\] (?<title>.+?) - (?<episode>\\d+) \\[.*",                       scope:"Global", priority: 95,  enabled:true },
  { id:"p3", name:"Movie + Year",            regex:"(?<title>.+?)\\.?(?<year>(19|20)\\d{2}).*",                                       scope:"Global", priority: 80,  enabled:true },
  { id:"p4", name:"Series (S/E)",            regex:"(?<title>.+?)\\.S(?<season>\\d{2})E(?<episode>\\d{2}).*",                         scope:"Global", priority: 75,  enabled:true },
  { id:"p5", name:"Strip .torrent extras",   regex:".*\\[(?<group>.+?)\\].*",                                                          scope:"Exclusion", priority: 10, enabled:false },
];

const IGNORES = [
  { id:"i1", glob:"*.nfo",      scope:"Global", on:true },
  { id:"i2", glob:"fanart*",    scope:"Global", on:true },
  { id:"i3", glob:"*.torrent",  scope:"Global", on:true },
  { id:"i4", glob:"sample.*",   scope:"Global", on:true },
  { id:"i5", glob:"*_thumb.jpg", scope:"Anime",  on:false },
];

// Mock "currently watching" state. Drives the Continue Watching hero.
// Each entry refers to a LIBRARY item by id, with optional ep / progress.
const WATCHING = [
  { id: "perfect-world",  ep: 5,   progress: 0.38, kind: "progress" }, // mid-watch
  { id: "swallowed-star", ep: 142, progress: 0.72, kind: "progress" }, // mid-watch
  { id: "xian-ni",        ep: 9,   progress: 0,    kind: "next" },     // next ep ready
  { id: "arcane",         ep: 3,   progress: 0.88, kind: "progress" }, // mid-watch
];

// Mock favorites — ids of LIBRARY items the user starred.
const FAVORITES = new Set(["perfect-world","mortals","arcane","ne-zha"]);

Object.assign(window, { LIBRARY, NEEDS_REVIEW, TORRENTS, HISTORY, FOLDERS, PATTERNS, IGNORES, BD, WATCHING, FAVORITES });
