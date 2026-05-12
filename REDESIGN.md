# Animarr — Редизайн под автопилот

> 2026-05-12. Базируется на `PROJECT.md` (фактическое состояние) и видении
> юзера: «добавил папку — система сама всё нашла, переименовала, опознала,
> разложила красиво. Минимум движений, всё под капотом».

---

## 0. Что сделано в этой ветке (Phase 1+2+3)

### Phase 1 (auto-pilot основа)
- **1.1** Авто-`IdentificationQueue` при добавлении папки/раздела ([Folders.razor](src/Animarr.Web/Components/Pages/Folders.razor), [Explorer.razor](src/Animarr.Web/Components/Pages/Explorer.razor)) — кнопка Identify не нужна.
- **1.2** Auto-rename контейнерной папки в `Title (Year)` после идентификации с confidence ≥ 0.85 ([MetadataService.cs:TryRenameContainerFolderAsync](src/Animarr.Web/Services/MetadataService.cs)). Останавливает FSW, делает Directory.Move, обновляет БД, перезапускает FSW. Защита от перезаписи активного torrent SavePath.
- **1.3** Имя эпизода в файле: `S01E03 - Honky Tonk Women.mkv` ([PatternMatchService.cs](src/Animarr.Web/Services/PatternMatchService.cs), [RenameService.cs](src/Animarr.Web/Services/RenameService.cs)). Map (season, episode) → name строится из `MediaItem.SeasonsJson`. Опция в Settings.
- **1.4** Один master-toggle «Активна» вместо трёх ([Folders.razor](src/Animarr.Web/Components/Pages/Folders.razor), [Explorer.razor](src/Animarr.Web/Components/Pages/Explorer.razor)). 3 toggle спрятаны под `<details>` «Расширенные настройки».
- **1.5** AI prompt extended: видит parent + sample имён файлов внутри ([MicrosoftAiLlmService.cs:SampleFileNames](src/Animarr.Web/Services/MicrosoftAiLlmService.cs)). `LlmType` пробрасывается в MetadataService как bias TMDB-поиска (LLM сказала "movie" → не дёргаем TV-endpoint).

### Phase 2 (стабильность)
- **2.1** Удалены overlapping pages: `Folders.razor`, `SectionFolders.razor`, `Browse.razor`, `Patterns.razor` заменены на thin-redirects → `/explorer` или `/settings`. Sidebar и так показывает только Catalog/Explorer/Torrents/History/Settings.
- **2.3** Confidence-based UX в [MetadataService.cs](src/Animarr.Web/Services/MetadataService.cs):
  - ≥ `AutoApplyConfidence` (0.85) → `Identified`, auto-rename
  - ≥ `NeedsReviewConfidence` (0.50) → `NeedsReview`, банер с top-3 кандидатами в [MediaDetail.razor](src/Animarr.Web/Components/Pages/MediaDetail.razor), кнопка «Это оно» вызывает `ApplyManualAsync`
  - < 0.50 → `Failed`
- **2.4** Fuzzy fallback file↔episode mapping в [MediaDetail.razor:BuildEpisodeFileMapAsync](src/Animarr.Web/Components/Pages/MediaDetail.razor). Pass 1 (regex) → Pass 2 (если количества совпадают — `NaturalStringComparer` по порядку).
- **2.5** FSW корректно стопится/перезапускается при auto-rename папки. Защита от перезаписи активного torrent SavePath.

### Phase 3 (полировка)
- **3.1 MVP** Picker папки в Torrents add — нативный `<select>` с `<optgroup>` группировкой по разделам (вместо плоского FluentCombobox).
- **3.2** Auto-suggest destination в Torrents add при magnet/file: парсит `dn=` или имя файла, fuzzy-match по `MediaItem.Title/OriginalTitle`, score ≥ 0.6 → автоподстановка + жёлтая подсказка.
- **3.3** AI fallback file↔episode mapping ([MicrosoftAiLlmService.MapFilesToEpisodesAsync](src/Animarr.Web/Services/MicrosoftAiLlmService.cs), [MediaDetail.razor:TryLlmMapFilesToEpisodesAsync](src/Animarr.Web/Components/Pages/MediaDetail.razor)). Третий проход когда pattern + fuzzy не сработали. Opt-in через `AppConfigKeys.LlmEpisodeMapping`.
- **3.4** `FolderType` (Auto/Series/Movie) спрятан под Advanced в Explorer dialog. По умолчанию определяется AI/TMDB.
- **3.5** Парные субтитры/sidecars ([RenameService.cs:RenamePairedSidecars](src/Animarr.Web/Services/RenameService.cs)). При переименовании `video.mkv` → `S01E03 - Title.mkv` файлы `.srt/.ass/.ssa/.sub/.vtt/.idx/.nfo/.sup` рядом тоже переименовываются согласованно, сохраняя языковые суффиксы (`video.eng.srt` → `S01E03 - Title.eng.srt`).

### Дополнительные баги
- **«New Folder появляется снова»** — три слоя защиты:
  1. [MediaFolderHeuristics.LooksLikeMediaFolder](src/Animarr.Web/Services/MediaFolderHeuristics.cs) — heuristic, отбраковывает пустые и junk-named директории (`New Folder`, `Новая папка`, `.animarr`, `$RECYCLE.BIN`, `@eaDir`, и т.п.)
  2. [FolderWatcherService.DismissChildPathAsync](src/Animarr.Web/Services/FolderWatcherService.cs) — persistent список dismissed-путей per section в `AppConfig["dismissed.section.{id}"]`. Удалил из UI → больше не вернётся.
  3. `OnDirectoryCreated` теперь ждёт 2 секунды и проверяет оба фильтра.

### Новые ключи в AppConfigKeys
В [src/Animarr.Web/Data/Models/AppConfig.cs](X:\Repos\Animarr\src\Animarr.Web\Data\Models\AppConfig.cs):
- `AutoRenameContainerFolder` — default ON
- `IncludeEpisodeNameInFile` — default ON
- `AutoApplyConfidence` (double, default 0.85)
- `NeedsReviewConfidence` (double, default 0.50)
- `LlmEpisodeMapping` — default OFF (opt-in, требует LLM включенной)

---

---

## 1. Что сейчас плохо (UX)

### 1.1 Пять страниц делают одно и то же

| Страница | URL | Что показывает | Уникальное? |
|----------|-----|----------------|-------------|
| `Home.razor` | `/` | Сетка постеров (Catalog) | да — единственный «постерный» вид |
| `Explorer.razor` | `/explorer` | Файловая навигация + добавление папок | да — управление |
| `Folders.razor` | `/folders` | Список папок-наблюдателей | **дубликат Explorer** |
| `SectionFolders.razor` | `/section/{id}` | Дети раздела | **дубликат drill-in Explorer** |
| `Browse.razor` | `/browse?path=…` | Plex-стайл хиро+постер+поддиректории | **дубликат MediaDetail** |
| `Patterns.razor` | `/patterns` | 7-строчный stub | **мёртвый** |
| `MediaDetail.razor` | `/catalog/{id}` | Hero + сезоны + эпизоды + плеер | да |

Юзер не понимает, где «правильно» что-то делать. Folders.razor и Explorer оба
позволяют добавить папку, но по-разному и с разным набором настроек.

### 1.2 Юзер должен помнить лишние понятия

- **Section vs Folder.** «Раздел» — корневая папка с автоимпортом подпапок;
  «Папка» — отдельный watcher. Юзер не должен этого знать — это деталь
  реализации.
- **3 toggle на каждой папке:** `WatchEnabled` + `IdentifyEnabled` +
  `RenameEnabled`. Зачем 3? Бывает ли когда-нибудь *смотреть* папку, но
  *не* переименовывать или *не* опознавать? Это лишняя матрица состояний.
- **`FolderType: Auto | Series | Movie`.** Юзер ставит вручную, но мы и
  так дёргаем и TV-, и Movie-endpoint TMDB и скорим. Параметр нужен только
  чтобы выбрать regex-паттерн (Movie → формат `Title (Year).mkv`, иначе
  → `S01E03.mkv`). Должен определяться автоматически по результату AI/TMDB.
- **Кнопка «Identify» / «Identify All».** Раз система знает, какие папки
  ещё не идентифицированы — пусть сама ставит в очередь. Кнопка нужна
  только для force-refresh.
- **«Patterns».** Большинству юзеров не интересно, что внутри regex.
  Должны быть в Settings (как Advanced), а не в боковой панели.

### 1.3 AI делает слишком мало

Сейчас LLM используется одной операцией:
**[папка] → [нормализованный title]**.
И всё. Дальше TMDB ищет по title — может перепутать сезон, тип, дубликаты.

Видение: AI делает **end-to-end identification** в одном вызове —
title + type (Movie/Series/Anime) + confidence + (опционально) season number.
TMDB/MAL остаются как источник постеров/описаний, но AI говорит куда
смотреть, не наоборот.

### 1.4 Нормализация — половинчатая

| Сейчас | Как должно быть |
|--------|-----------------|
| Файлы переименовываются: `S01E03.mkv` (только номер) | `S01E03 - Honky Tonk Women.mkv` (с названием эпизода из TMDB) |
| Папка остаётся с любым уродливым именем после торрент-релиза | Папка переименовывается в `Cowboy Bebop (1998)` после успешной идентификации |
| Movie файл: `Inception (2010).mkv` ✅ (после H-1) | Тоже самое + контейнер папки переименовывается |
| Субтитры существуют сами по себе | Парные с эпизодом субтитры переименовываются согласованно (`S01E03 - Honky Tonk Women.srt`) |

### 1.5 Маппинг file ↔ episode хрупкий

`MediaDetail.razor:BuildEpisodeFileMap` сканирует файлы и матчит по
`(season, episode)`. Логика:
- Применяет regex-паттерны → если матч, берёт `season`/`episode` группы.
- Если в имени файла только `episode`, season берётся через
  `DetectSeasonFromPath` (теперь walks 5 уровней — M-10).
- Если pattern не матчит, файл просто пропускается.

Проблемы:
- Юзер видит «эпизод 3 → нет файла», хотя файл `ep_3.mkv` на диске —
  просто нет паттерна под него.
- AI могла бы делать fuzzy матчинг (12 эпизодов TMDB ↔ 12 видео-файлов в
  порядке сортировки), не требуя паттерна.

### 1.6 Торрент destination — плоский dropdown

Сейчас при добавлении торрента: dropdown FolderWatcher'ов
(только не-секции) + кнопка «Создать подпапку».

Видение:
- **Дерево**, не список:
  ```
  Корни библиотеки
    ├ /media/Anime
    │   ├ Cowboy Bebop (1998)                ← опознанный сериал
    │   │   ├ Season 1                       ← существующий сезон
    │   │   └ Season 2                       ← существующий сезон
    │   ├ + Новый сериал
    │   └ + Новый сезон в Cowboy Bebop
    └ /media/Movies
        ├ Inception (2010)
        └ + Новый фильм
  ```
- Юзер кидает magnet «Cowboy Bebop S02E10», система **сама подсказывает**
  существующий показ → автокликом сохраняется в `/media/Anime/Cowboy Bebop (1998)/Season 2/`.
- Toggle `SkipSubfolderStructure` (flatten) и `SuppressRootFolder` (strip
  root) — оставить, они полезны и не должны быть default.

---

## 2. Новая информационная архитектура

### 2.1 Top-level навигация (sidebar)

```
🎬  Каталог        ← Home.razor (single source of catalog)
📁  Проводник       ← Explorer.razor (single source of file-system management)
⬇️  Загрузки       ← Torrents.razor + history
🔍  История         ← History.razor (renames + scan logs)
⚙️  Настройки      ← Settings.razor
```

**Удаляем:** `Patterns.razor`, `Browse.razor`, `Folders.razor`,
`SectionFolders.razor`. Их функции:

- **Patterns**: уже в Settings → Patterns.
- **Browse**: дублирует MediaDetail; что не дублирует — переносим в
  MediaDetail (подпапки уже там).
- **Folders**: всё в Explorer уже есть (добавление, редактирование,
  переключение мониторинга, кнопка Identify).
- **SectionFolders**: при клике на section в Explorer — drill-in
  показывает детей. То же самое.

### 2.2 Скрытые сущности (внутри системы — остаются)

- `FolderWatcher.IsSection` — техническая деталь. UI не показывает,
  устанавливается автоматически:
  - Юзер добавляет path → система сканирует.
  - Если внутри есть subdirs с видео-файлами **и** ни одного видео-файла
    напрямую в корне — `IsSection = true`, авто-импорт subdirs.
  - Иначе — `IsSection = false`, обычный watcher.
- `FolderType` — авто-определяется AI'ем + результатом TMDB. Юзер может
  override в продвинутом редакторе.

### 2.3 Унификация 3 toggle → 1

| Старое (3 toggle) | Новое (1 toggle) |
|-------------------|------------------|
| WatchEnabled = false → FSW не запущен | «Активна» = false → ничего вообще |
| IdentifyEnabled = false → не идентифицировать | внутри «Активна»: identify default ON |
| RenameEnabled = false → не переименовывать | внутри «Активна»: rename default ON |

Один toggle **«Активна»** в UI. Под ним — advanced expand-collapse с
тремя tickbox'ами для тех 5% юзеров, кому надо отключить только
переименование, например, для уже-аккуратно-названной коллекции.

---

## 3. Auto-pilot flow

### 3.1 Когда юзер добавляет library root

```
1. POST /folders {path: "/media/Anime"}
2. Backend сканирует subdirs.
3. Для каждого subdir с видео-файлами:
   3a. Кладёт subdir в IdentificationQueue (status=Queued)
4. IdentificationQueueProcessorService подбирает по одной:
   - AI: «вот папка X, что это? (title, type, year, confidence)»
   - TMDB / MAL: поиск по AI title + type-bias
   - Скоринг → winner
   - Сохраняет MediaItem с status Identified | NeedsReview | Failed
   - **Если confidence >= 0.85** И тип Series → авто-rename контейнерной
     папки в `Title (Year)`, добавление в RenameQueue для файлов внутри
   - **Иначе** статус NeedsReview, ждёт ручного подтверждения
5. UI обновляется поллингом (Home.razor → _activeQueueFolderIds)
```

**Никаких кнопок «Identify».** Только force-refresh (re-identify) в
MediaDetail на случай, когда AI ошиблась.

### 3.2 Confidence-based UX

| AI confidence | Что происходит |
|---------------|----------------|
| ≥ 0.85 | Авто-применить: rename папки + файлов |
| 0.5 – 0.85 | NeedsReview — постер показывается, но badge «требует подтверждения», клик ведёт в MediaDetail с подсветкой top-3 кандидатов |
| < 0.5 | Failed — постер с placeholder + кнопка «Поискать вручную» |

Порог конфигурируется в Settings → AI.

### 3.3 AI prompt extended

Сейчас в `MicrosoftAiLlmService.IdentifyFolderAsync`:
```
Input: folder path
Output: { title, confidence }
```

Новое:
```
Input: folder name + child file names + parent path
Output: {
  title: "Cowboy Bebop",
  alternative_titles: ["カウボーイビバップ"],
  type: "series" | "movie" | "anime" | "cartoon",
  year: 1998,
  season: 1,
  episode_count_hint: 26,
  confidence: 0.92
}
```

Это позволяет:
- Сразу понять Movie vs Series (без двойного поиска в TMDB).
- Дать TMDB-поиску type-bias (только TV или только Movie endpoint).
- Использовать `season` hint когда юзер кинул `Cowboy Bebop S02/...`.

### 3.4 Маппинг file ↔ episode — гибридный

После идентификации серии с N эпизодами:

1. **Pattern matching:** применить regex'ы, попытаться извлечь `(s, e)`
   из имени файла. Если получилось — занести в map.
2. **Fuzzy fallback** (для оставшихся):
   - Если в папке `Season X` лежит ровно `N` видео-файлов и `N` ==
     количество эпизодов TMDB для сезона `X` → сортировать
     `NaturalStringComparer` и отображать по порядку (file[0] = ep1,
     file[1] = ep2, …).
   - Опционально: AI получает список оставшихся файлов и список эпизодов
     TMDB → возвращает соответствие. Используется только если pattern
     + fuzzy не сработали.

### 3.5 Filename templates

Сейчас (в `PatternMatchService.BuildTargetName`):
- Series: `{ep:D2}.{ext}` или `S{s:D2}E{ep:D2}.{ext}`
- Movie: `{Title} ({Year}).{ext}`

Расширить (опционально, в Settings):
- Series: `S{s:D2}E{ep:D2} - {EpisodeName}.{ext}`
  - `{EpisodeName}` берётся из TMDB при идентификации → кладётся в
    MediaItem.SeasonsJson (уже сохраняется в `EpisodeVm.Name`).
  - Если эпизод не известен, fallback на старый формат без названия.
- Containing folder: `{Title} ({Year})` — переименовывается **только**
  при идентификации с высоким confidence. Не переименовывается на каждый
  scan.

### 3.6 Folder rename — отдельная фаза

Сейчас система **переименовывает только файлы**, контейнерная папка
остаётся как была (`[Group] Cowboy.Bebop.1998.1080p.BluRay.x265`).

Новое: после успешной идентификации:
- Целевое имя папки: `{Title} ({Year})`
- Если папка уже корректно названа → ничего не делать.
- Если нет → `Directory.Move(oldPath, newPath)`, обновить
  `FolderWatcher.Path` и `FolderWatcher.Label` в БД, поднять
  FSW заново.
- Опция в Settings: «Auto-rename folders» default ON.

---

## 4. Торрент destination — дерево с подсказками

### 4.1 UI

В правой панели «Add torrent» вместо плоского dropdown — древовидный
picker:

```
Сохранить в:
┌─────────────────────────────────────────┐
│ ▼ /media/Anime                          │ ← клик = сохранить здесь
│   ▶ Cowboy Bebop (1998)                 │ ← клик = drill-down
│       Season 1                          │ ← клик = save here
│       Season 2                          │
│       + Создать сезон…                  │
│   ▶ Naruto (2002)                       │
│   + Создать сериал…                     │
│ ▶ /media/Movies                         │
└─────────────────────────────────────────┘

Опции:
☐ Качать без подпапок (flatten)
☐ Убрать корневую папку торрента
```

### 4.2 Auto-suggest

Когда юзер вставляет magnet/выбирает .torrent — парсится name. Если
парсер находит `S\d{2}E\d{2}` или название известного сериала из
библиотеки (fuzzy match по `MediaItem.Title`) — auto-select
соответствующий show + season.

### 4.3 После завершения торрента

Уже работает (`AutoRenameAfterDownload`): сканируется папка → файлы
переименовываются. Дополнительно:
- Если торрент-папка попала в существующий сериал → файлы маппятся на
  следующие эпизоды (S?E?+N) с учётом уже существующих.
- Если это новый сериал → запускается идентификация.

---

## 5. Schema-изменения

### 5.1 Текущая схема, что трогаем

```
FolderWatcher                 ← остаётся, скрываем IsSection в UI
  RenameEnabled, IdentifyEnabled, WatchEnabled  ← UI: один Active toggle
  FolderType                   ← UI: автоопределяется, advanced override

MediaItem                     ← остаётся
  + LlmType (Movie/Series/Anime)              ← новое поле (necessary?)
  + LlmYearHint (int?)                        ← AI year guess
  + AutoRenameApplied (bool)                  ← чтобы не переименовывать дважды
```

### 5.2 Что НЕ трогаем (БД миграция дорого)

- `IsSection` остаётся — просто скрыт в UI.
- `FolderType` остаётся — просто автозаполняется.

---

## 6. Phased implementation

### Phase 1 — что делаю прямо сейчас (низкорискованные, без миграций БД)

| # | Что | Файлы |
|---|-----|-------|
| 1.1 | **Auto-identify при добавлении папки.** В Explorer и любых add-handlers — сразу ставить в `IdentificationQueue` если `IdentifyEnabled = true`. (Сейчас юзеру надо явно нажимать «Identify».) | Folders.razor / Explorer.razor / FolderEditPanel.razor |
| 1.2 | **Auto-rename контейнерной папки** в `Title (Year)` после успешной идентификации с confidence ≥ 0.85. Опция в Settings (`AutoRenameContainerFolder`, default ON). | MetadataService.IdentifyFolderAsync |
| 1.3 | **Имя эпизода в filename.** `S01E03 - Title.mkv` вместо `S01E03.mkv` для серий. Опция в Settings (`IncludeEpisodeNameInFile`, default ON). | PatternMatchService.BuildTargetName + RenameService |
| 1.4 | **Унификация 3 toggle → 1 + Advanced.** В Folders.razor / FolderEditPanel — главный toggle Active, под ним свёртывающаяся секция Advanced с тремя ticks. | Folders.razor / FolderEditPanel.razor |
| 1.5 | **AI prompt extended.** Расширить промпт в MicrosoftAiLlmService: возвращать `type` + `year` + `season_hint` + `confidence`. Использовать `type` для bias TMDB-поиска. | MicrosoftAiLlmService + ILlmService + MetadataService |

### Phase 2 — нужно больше работы, но без миграций

| # | Что | Сложность |
|---|-----|-----------|
| 2.1 | Удалить `Patterns.razor`, `Browse.razor`, `Folders.razor`, `SectionFolders.razor` — после миграции их функций в Explorer/Settings. | M (надо проверить все ссылки) |
| 2.2 | Drill-in section в Explorer: показывать детей с постерами + статусами. | S |
| 2.3 | Confidence threshold + NeedsReview UX в каталоге (top-3 кандидаты, кнопка Apply). | M |
| 2.4 | Fuzzy file↔episode mapping: если pattern не сработал но количества совпадают — мапить по порядку. | S |
| 2.5 | Folder rename в backend (Directory.Move + update FolderWatcher.Path + restart FSW + invalidate _suppressedPaths). | M (надо аккуратно с FSW) |

### Phase 3 — большие изменения

| # | Что |
|---|-----|
| 3.1 | Дерево destination в Torrents.razor (replace flat dropdown). |
| 3.2 | Auto-suggest при вставке magnet (fuzzy match по существующим MediaItem). |
| 3.3 | AI-fallback file↔episode mapping (LLM получает список файлов + эпизодов, возвращает соответствие). |
| 3.4 | Удалить user-facing `FolderType` enum полностью, оставить как internal hint. |
| 3.5 | Subtitle pairing: если для видео `S01E03.mkv` найден `*.srt` с тем же baseName — переименовывать парно. |

---

## 7. Что я делаю в этом коммите (Phase 1)

Сразу следом за этим документом:

1. **1.5 → AI extended** — расширить `ILlmService.IdentifyFolderAsync`
   возвращать `LlmResult` с полями `Title`, `Type`, `Year`, `Confidence`.
   Промпт переписать. `MetadataService` использует `Type` как bias.
2. **1.3 → Episode name в filename.** Опция в Settings + изменения в
   `PatternMatchService.BuildTargetName` + поиск имени эпизода через
   `MediaItem.SeasonsJson`.
3. **1.4 → Один Active toggle.** В FolderEditPanel.razor.
4. **1.2 → Auto-rename containing folder.** В MetadataService после
   успешной идентификации, опция AutoRenameContainerFolder.
5. **1.1 → Auto-identify на добавлении.** Везде, где добавляется
   FolderWatcher (Explorer, FolderEditPanel — Folders.razor пометить
   deprecated в комментарии).

Phase 2 / 3 — пометить как TODO в этом документе. Пока не критично:
квартирник без миграции уже даст 80% «как должно быть».
