# Animarr — описание проекта и план улучшений

> Документ актуален на 2026-05-12. Составлен по фактическому состоянию кода (не по
> предыдущей `ANALYSIS.md`, в которой значительная часть «критических» багов уже
> исправлена в репозитории и более не воспроизводится).

---

## 0. Что уже исправлено в этой ветке

Изменения на 2026-05-12, ветка `claude/priceless-grothendieck-a5ff39`.

### Свежие UI-проблемы (озвученные пользователем)

- **Строки в таблице торрентов слишком низкие, кнопки не помещаются** —
  добавлены правила в [app.css](src/Animarr.Web/Styles/app.css): `fluent-data-grid-row { min-height: 44px }`
  + центрирование cells. Кнопки 28px теперь нормально умещаются.
- **Прогресс-бар мигал на каждом обновлении (становился 0 → новое значение)** —
  заменил `<FluentProgress>` (re-mount каждый StateChanged) на чистый
  CSS-прогресс с `transition: width .25s ease-out` в классах
  `.animarr-progress`. Тот же CSS используется в `TorrentEdit.razor`.

### Критические (бекенд)

- **C-1** ([TorrentEngineService.cs](src/Animarr.Web/Services/TorrentEngineService.cs)) — `_torrentLocks` теперь чистится в
  `RemoveAsync` (`TryRemove + Dispose`) — нет утечки `SemaphoreSlim`.
- **C-2** — добавил периодическую `PersistStatsAsync` в цикл таймера
  (раз в 30 сек, в фоне). SIGKILL / OOM больше не теряет статистику
  раздачи между graceful saves.
- **C-3** ([Torrents.razor](src/Animarr.Web/Components/Pages/Torrents.razor)) — `BuildDetailsFlatTree` перенесён внутрь
  `InvokeAsync(() => { … })`. Никаких мутаций состояния на потоке таймера
  до диспетчера рендера.
- **C-6** ([Program.cs](src/Animarr.Web/Program.cs)) — в `/api/image` (и
  `/api/video` через `replace_all`) проверка `FileAttributes.ReparsePoint`
  → симлинки внутри media-папки больше не позволяют утекать произвольные
  файлы.

### High-priority

- **H-1** ([PatternMatchService.cs:225](src/Animarr.Web/Services/PatternMatchService.cs:225)) — год для Movie извлекается
  отдельным regex `\b(19\d\d|20\d\d)\b` по имени файла, а не через
  костыль с `Episode∈[1900-2099]`. `Inception.2010.1080p.BluRay.mkv` →
  `Inception (2010).mkv`.
- **H-2** ([FolderWatcherService.cs](src/Animarr.Web/Services/FolderWatcherService.cs)) — `_suppressedPaths` чистится
  Timer'ом раз в 60 секунд (TTL 15 сек). Утечка устранена.
- **H-3 / M-9 / L-10** ([AppConfigService.cs](src/Animarr.Web/Services/AppConfigService.cs)) — статический
  in-memory cache, загружается один раз. ~13 round-trip'ов
  Settings.razor → 1. Same для MainLayout (4→1), `/api/image` allowed-roots.
- **H-4** ([TorrentEngineService.cs](src/Animarr.Web/Services/TorrentEngineService.cs)) — `MarkFileDownloadedAsync`
  заменена на batched `FlushPendingDownloadedWrites` (раз в 500 мс, один
  `SaveChangesAsync` per torrent). При завершении торрента с 100 файлами —
  один DB-write вместо стампида.
- **H-5** ([RenameService.cs](src/Animarr.Web/Services/RenameService.cs)) — `toProcess.Zip(histories)` заменён на
  явную пару `Select(item => (item, history))`. Контракт явный, никакой
  silent desync. Плюс выделил `LoadEffectiveRulesAsync` — устранил
  copy-paste `ScanFolderAsync`/`ProcessSingleFileAsync` (B-1).
- **H-6** ([History.razor](src/Animarr.Web/Components/Pages/History.razor)) — server-side `Skip/Take` + `CountAsync`.
  Фильтр строится в БД. Своя пагинация (prev/next + диапазон).
  10k+ записей грузятся мгновенно.
- **H-7** ([Home.razor](src/Animarr.Web/Components/Pages/Home.razor)) — рендер ограничен 240 видимыми элементами
  (`_filtered.Take(240)`) + индикатор "Showing X of Y". Поиск
  работает над полным списком. Тяги Tags из includes убраны (M-13).
- **H-8** ([TorrentEngineService.cs](src/Animarr.Web/Services/TorrentEngineService.cs)) — `StateChanged` теперь
  стреляет только при реальных изменениях (`ComputeStatsHash` сравнивает
  state/progress/rates/peers). Если никто ничего не качает — 0 рендеров.
- **H-9** ([TmdbClient.cs](src/Animarr.Web/Services/TmdbClient.cs)) — единая `GetJsonAsync<T>` с обработкой
  HTTP-статусов: 401/403 → `TmdbAuthException` (не ретраить, видна
  пользователю в `IdentificationQueue.ErrorMessage`); 429 → backoff с
  учётом `Retry-After`, до 3 попыток; 404 → null; остальное → log + null.
- **H-10** ([SecretProtector.cs](src/Animarr.Web/Services/SecretProtector.cs) + Program.cs + AppConfigService) —
  `IDataProtectionProvider` персистит ключи в `/app/data/dp-keys`,
  `TmdbApiKey/MalClientId/LlmApiKey` шифруются префиксом `enc:v1:` при
  записи в SQLite, расшифровываются прозрачно при загрузке. Кеш
  держит plaintext. Legacy plaintext значения работают без миграции,
  перешифруются на ближайшем `SetAsync`.

### Medium

- **M-2..M-6** — выгребен хардкод текста в [Torrents.razor](src/Animarr.Web/Components/Pages/Torrents.razor),
  [TorrentEdit.razor](src/Animarr.Web/Components/Pages/TorrentEdit.razor), [Folders.razor](src/Animarr.Web/Components/Pages/Folders.razor),
  [Explorer.razor](src/Animarr.Web/Components/Pages/Explorer.razor), [MediaDetail.razor](src/Animarr.Web/Components/Pages/MediaDetail.razor). Все строки идут
  через `L["..."]`/`L.Get(...)`. Добавлены ключи в [en.json](src/Animarr.Web/Localization/en.json) и
  [ru.json](src/Animarr.Web/Localization/ru.json) (toast_added, toast_removed, seeds_label,
  files_label, season_default, monitoring_on/off, и т.д.).
- **M-7** — `Console.WriteLine` в MediaDetail заменён на инжектируемый
  `ILogger<MediaDetail>`.
- **M-8** — swallowed try/catch вокруг `File.Move`/`File.Delete` в
  Explorer теперь показывают toast (success / error с текстом исключения).
- **M-10** — `DetectSeasonFromPath` принимает `rootPath` и `maxDepth=5`,
  ходит вверх до корня FolderWatcher (а не максимум 2 уровня).
- **M-11** — `Directory.EnumerateFiles` в `ScanFolderAsync` уступает
  поток каждые 256 файлов (`await Task.Yield()`), плюс `ct.ThrowIfCancellationRequested`.
- **M-12** — `TorrentEngineService._cachedConfig` обновляется только из
  `UpdateGlobalSettingsAsync`. `OnStateChangedAsync` больше не дёргает
  `LoadConfigAsync` на каждый state change.
- **M-13** — Refresh-таймер Home сбросил `Include(Tags).ThenInclude(MediaTag)`.
- **M-14** — `Torrents.razor` хранит snapshot `_torrentSnapshot`,
  обновляемый из `OnTorrentStateChanged`. Markup больше не вызывает
  `TorrentEngine.GetAll()` inline дважды в секунду.
- **M-15** — README исправлен: «Kbps» → «Mbps» (UI и движок реально в Mbps).
- **M-16** — README: убран дубликат секции "Ignore rules".

### Low

- **L-3** — Refresh-цикл Home делает `StateHasChanged` **только** когда
  `justCompleted.Count > 0`.
- **L-7** — Битый regex паттерна логируется через
  `logger.LogWarning(ex, "Skipping pattern «{Name}» (id={Id}): invalid regex …")`.
- **L-12** — Версии NuGet зафиксированы (EF Core 10.0.7, FluentUI 4.14.1,
  Extensions.AI 10.5.2). `dotnet list package` — источник истины.
- **L-14** — Удалены `_fix_quotes.py` и `_write_panel.py`.

### Намеренно отложено

- **C-4 / C-5** (Theme/Localization Singleton) — **не баг**, проект
  однопользовательский (см. [memory/project_positioning.md](https://example/memory)). Singleton с мутацией
  в self-hosted Docker — нормальная архитектура.
- **C-7** (auth) — понижено до low. Опциональный bearer-token не делал —
  пока не пробрасывается наружу, не нужен.
- **M-17** (cascade delete по `ParentSectionId`) — UI-обработчики
  (Folders.razor, Explorer.razor) и так удаляют children руками перед
  удалением раздела. Schema change через миграцию рискован без явного
  одобрения юзера.
- **L-1** (удаление `Patterns.razor` 7-строчного редирект-stub),
  **L-4** (backdrop lifecycle), **L-8** (god-component refactor),
  **L-9** (`IOptionsMonitor`), **L-11** (style mix) — низкий приоритет,
  не делал.

### Известная проблема репозитория (не моя)

`.gitignore:26: data/` (правило для runtime data-volume) на case-insensitive
Windows FS перекрывает `src/Animarr.Web/Data/` (исходники EF-моделей).
Поэтому **в этом worktree нет `Data/`** (gitignored → не клонируется
в worktree), и `dotnet build` падает на типах `FolderWatcher`,
`MediaItem`, `AppDbContext` и т.д. В основном дереве `X:\Repos\Animarr`
эти файлы есть.

**Рекомендую** заменить в `.gitignore`: `data/` → `/data/`
(якорь к корню) или `**/data/`, чтобы исключить только runtime-папку,
не трогая исходники. Альтернатива — `git add -f src/Animarr.Web/Data/`.

---

## 1. Что это за проект

**Animarr** — self-hosted веб-приложение для организации медиатеки (аниме,
сериалы, фильмы), которое объединяет три обычно разделённых инструмента:

| Роль | Что делает Animarr |
|------|--------------------|
| Sonarr / Radarr | Идентификация папок через TMDB/MAL, метаданные, постеры, фанарт |
| Filebot | Регэксп-движок переименований, история, откат, ignore-маски |
| qBittorrent | Встроенный торрент-клиент (MonoTorrent) с per-file приоритетами |

На вход: одна или несколько папок (или *секция* — корень с автоимпортом
подпапок). На выходе: красивый каталог с постерами, аккуратно
переименованные файлы (`S01E03.mkv`, `Inception (2010).mkv`) и фоновое
автообновление при появлении новых файлов.

### 1.1 Технологический стек

| Слой | Технология |
|------|-----------|
| Runtime | .NET 10 |
| UI | Blazor Server (Interactive) + Microsoft FluentUI v4 |
| Стили | Tailwind CSS v4 (`npm run css:build` запускается из MSBuild) |
| БД | SQLite + EF Core (миграции на старте, WAL-режим) |
| Торренты | MonoTorrent 3.0.2 |
| Метаданные | TMDB (TV + Movie), MyAnimeList, IMDb-API fallback |
| LLM | `Microsoft.Extensions.AI` → OpenAI-совместимый endpoint (Ollama, OpenAI, Groq, LM Studio) — для нормализации названий и подсказки regex-паттернов |
| Деплой | Docker (`ghcr.io/eduardpoul/animarr:latest`), `docker compose up -d`, том `/app/data` для SQLite + fastresume |

### 1.2 Архитектурная карта

```
Blazor UI (Razor pages, Server-side)
   │
   ├── FolderWatcherService (Singleton + IHostedService)
   │     • FileSystemWatcher per folder
   │     • Section folders → автоимпорт подпапок
   │     • Подавление self-rename событий через _suppressedPaths
   │
   ├── RenameQueueProcessorService (IHostedService)
   │     • Опрос RenameQueue
   │     • Вызывает RenameService.ProcessSingleFileAsync
   │
   ├── RenameService (Scoped)
   │     • ScanFolderAsync (dry-run)  / ApplyRenamesAsync
   │     • PatternMatchService.EvaluateFile → regex + ignore + Movie/Series ветки
   │     • RenameHistory (Pending → Renamed/Skipped/Error), revert
   │
   ├── IdentificationQueueProcessorService (IHostedService)
   │     • Сериальная обработка IdentificationQueue
   │     • LLM → нормализация → MetadataService → TMDB+MAL+IMDb
   │     • Кандидаты с скорингом, top-3 → CandidatesJson, статус NeedsReview
   │
   ├── TorrentEngineService (Singleton + IHostedService)
   │     • MonoTorrent ClientEngine, PeriodicTimer 500 ms
   │     • TorrentRecord + TorrentFileSelection (приоритеты)
   │     • Авто-rename через RenameService при завершении
   │
   ├── AppConfigService (Scoped, БД-key/value)
   ├── ThemeService / LocalizationService (Singleton — ⚠ см. C-4/C-5 ниже)
   │
   └── /api/image и /api/video (Minimal API) — раздача файлов с диска
         с whitelist по FolderWatcher.Path
```

### 1.3 Схема БД (фактическая)

```
FolderWatcher       — папки и секции (IsSection + ParentSectionId)
  ├── RenamePattern[]    (Scope = Global | Folder, IsExcluded)
  ├── IgnoreRule[]       (Scope = Global | Folder)
  ├── RenameHistory[]    (Pending → Renamed/Skipped/Error/Reverted)
  └── MediaItem (1:1)    — постер, фанарт, описание, рейтинги, сезоны
        └── MediaItemTag — many-to-many с MediaTag

TorrentRecord          (InfoHash unique, MagnetLink | TorrentFilePath)
  └── TorrentFileSelection[]   — приоритеты + IsDownloaded
TorrentConfig          — singleton (Id = 1)

RenameQueue            — буфер от FSW
IdentificationQueue    — задания на идентификацию (+ LogDetails)
AppConfig              — key/value: TmdbApiKey, MalClientId, LlmApiKey,
                         ThemeMode, AccentColor, Language, AutoIdentifyEnabled, …
MediaTag               — пользовательские теги
```

---

## 2. Что реально работает

- ✅ Загрузка и автоматическое возобновление торрентов (magnet + .torrent),
  per-file priority, flatten subfolders, переименование/удаление корневой папки.
- ✅ Идентификация: LLM-нормализация → параллельные запросы TMDB/MAL → скоринг
  → top-3 кандидатов в `CandidatesJson` при низкой уверенности.
- ✅ Каталог с постерами, fanart-героем, сезоны+эпизоды, ручной поиск по
  TMDB/MAL/IMDb/TVDB.
- ✅ Двуязычный UI (en/ru) с runtime-переключением.
- ✅ Тема (System/Dark/Light) + accent color, **сохраняется в БД и
  загружается на старте** ([Program.cs:88](src/Animarr.Web/Program.cs:88) — это уже сделано, ANALYSIS.md ошибочно
  указывает обратное).
- ✅ FSW реагирует и на `Created`, и на `Renamed` ([FolderWatcherService.cs:121](src/Animarr.Web/Services/FolderWatcherService.cs:121)) —
  ANALYSIS.md ошибочно считал, что Renamed игнорируется.
- ✅ Защита `/api/image`/`/api/video` от directory-traversal через
  whitelist `FolderWatcher.Path`.
- ✅ Crash-recovery: `RenameHistory.Status = Pending` резолвится при старте,
  активные торренты восстанавливаются из `TorrentRecord`.
- ✅ `RenameService.ApplyRenamesAsync` использует `Zip(toProcess, histories)` —
  но списки строятся из одного `toProcess`, так что drift невозможен в текущем
  коде (потенциально хрупкий контракт — см. H-5).

---

## 3. Реальные баги (проверено по коду, 2026-05)

Ссылки в формате `файл:строка`. Категории: **C** — критично, **H** — высоко,
**M** — средне, **L** — низко.

### 3.1 Критические

| # | Где | Проблема | Что увидит пользователь |
|---|-----|----------|---------------------------|
| **C-1** | [TorrentEngineService.cs:27,182](src/Animarr.Web/Services/TorrentEngineService.cs:27) + `RemoveAsync` | `_torrentLocks` хранит `SemaphoreSlim` per torrent, но при удалении торрента семафор не удаляется и не диспозится. | Утечка памяти/handles при долгой работе и активном add/remove. |
| **C-2** | [TorrentEngineService.cs:163,389](src/Animarr.Web/Services/TorrentEngineService.cs:163) | `PersistStatsAsync()` вызывается **только** в `ShutdownAsync()` через `finally`. SIGKILL / OOM / выпадение контейнера → счётчики `Downloaded`/`Uploaded` теряются. | После аварийной остановки статистика раздачи откатывается к последнему graceful save. |
| **C-3** | [Torrents.razor: ~695-700](src/Animarr.Web/Components/Pages/Torrents.razor) | `OnTorrentStateChanged` мутирует `_detailsFlatTree` через `BuildDetailsFlatTree` на потоке таймера **до** `InvokeAsync(StateHasChanged)`. | Race с рендером Blazor: возможен мерцающий/пустой список файлов в правой панели. |
| **C-4** | [ThemeService.cs](src/Animarr.Web/Services/ThemeService.cs) + [Program.cs:37](src/Animarr.Web/Program.cs:37) | `ThemeService` — **Singleton** с мутируемыми `Mode`/`AccentColor`. `OnChange` fires во все circuit'ы. | Один пользователь меняет тему — у всех остальных тема и accent тоже переключаются. |
| **C-5** | [LocalizationService.cs](src/Animarr.Web/Services/LocalizationService.cs) + [Program.cs:38](src/Animarr.Web/Program.cs:38) | То же самое: Singleton, `_strings` мутируется при `LoadAsync(lang, env)`. | Один user переключает на русский — у других UI тоже переезжает. |
| **C-6** | [Program.cs:142-148,195-201](src/Animarr.Web/Program.cs:142) | `/api/image` и `/api/video` валидируют путь через `Path.GetFullPath().StartsWith(root)`, но **не разрешают симлинки**. Симлинк внутри разрешённой папки → читает что угодно. | Возможность утечь `/etc/shadow`, `C:\Windows\...` через специально подложенный симлинк. |
| **C-7** | весь Program.cs | Нет аутентификации, все эндпойнты `AllowAnonymous`. В сочетании с C-6 — блокер для любого деплоя вне localhost. | Любой в сети видит каталог и качает файлы. |

### 3.2 Высокий приоритет

| # | Где | Проблема |
|---|-----|----------|
| **H-1** | [PatternMatchService.cs:225-240](src/Animarr.Web/Services/PatternMatchService.cs:225) | Год для Movie извлекается только если регэксп вернул `Episode` = 1900-2099. Реальные имена типа `Inception.2010.1080p.BluRay.mkv` обычно не ловятся ни одним из встроенных паттернов → `appendYear=false` → итог `Inception 2010.mkv` (или просто `Inception.mkv`) вместо `Inception (2010).mkv`. |
| **H-2** | [FolderWatcherService.cs:28,260](src/Animarr.Web/Services/FolderWatcherService.cs:28) | `_suppressedPaths` чистится только когда тот же путь снова попадает в FSW. Если файл не «всплыл» (удалён, перемещён вне папки) — запись висит до перезапуска. Памяти немного, но утечка. |
| **H-3** | [AppConfigService.cs](src/Animarr.Web/Services/AppConfigService.cs) | Каждый `GetAsync`/`SetAsync` открывает новый `DbContext`. [Settings.razor](src/Animarr.Web/Components/Pages/Settings.razor) при инициализации делает ~13 round-trip'ов, MainLayout — 4. Тормозит открытие страниц. |
| **H-4** | [TorrentEngineService.cs:329](src/Animarr.Web/Services/TorrentEngineService.cs:329) | `_ = Task.Run(... MarkFileDownloadedAsync ...)` внутри цикла каждые 500 мс. При завершении торрента с 100 файлами — 100 параллельных EF-контекстов + 100 одновременных писателей в SQLite. |
| **H-5** | [RenameService.cs:101-117](src/Animarr.Web/Services/RenameService.cs:101) | `toProcess.Zip(histories)`. Сегодня безопасно (списки строятся вместе), но контракт хрупкий — любая фильтрация в середине → silent desync статуса с не той записью. |
| **H-6** | [History.razor:~205](src/Animarr.Web/Components/Pages/History.razor) | `db.RenameHistories.OrderByDescending(...).ToListAsync()` — вся таблица в память, `FluentDataGrid` пагинирует client-side. Через год работы — 5k+ записей, секунды на открытие. |
| **H-7** | [Home.razor:~321](src/Animarr.Web/Components/Pages/Home.razor) | Загружает все `MediaItem` с `Include(Folder).Include(Tags.ThenInclude(MediaTag))`. На 500+ элементах browser тормозит, circuit memory растёт. |
| **H-8** | [TorrentEngineService.cs:52-57](src/Animarr.Web/Services/TorrentEngineService.cs:52) | `StateChanged?.Invoke()` каждые 500 мс **всегда**, даже если ничего не изменилось. Все открытые страницы перерисовываются 2 раза/сек. |
| **H-9** | [TmdbClient.cs](src/Animarr.Web/Services/TmdbClient.cs), [MalClient.cs](src/Animarr.Web/Services/MalClient.cs) | Все HTTP-исключения ловятся в общий `catch (Exception)` → возврат null/пустого списка. 401 (нет ключа), 403, 429 (rate limit) и network errors неотличимы. Нет retry/backoff. |
| **H-10** | [MicrosoftAiLlmService.cs](src/Animarr.Web/Services/MicrosoftAiLlmService.cs), [TmdbAuthHandler.cs](src/Animarr.Web/Services/TmdbAuthHandler.cs), [MalAuthHandler.cs](src/Animarr.Web/Services/MalAuthHandler.cs) | TMDB / MAL / LLM ключи в SQLite в открытом виде. Любой с доступом к `/app/data/Animarr.db` получает все ключи. |

### 3.3 Средний приоритет

| # | Где | Проблема |
|---|-----|----------|
| **M-1** | [TorrentEngineService.cs:25,329](src/Animarr.Web/Services/TorrentEngineService.cs:25) + `RemoveAsync` | `_markedDownloaded` ключи никогда не чистятся при удалении торрента. |
| **M-2** | [Torrents.razor:~215,~421,~827](src/Animarr.Web/Components/Pages/Torrents.razor) | Смешанные хардкод-строки: английские (`Files (...)`, `Seeds:`, `Peers:`) **и** русские тосты (`Торрент добавлен`, `Лимиты обновлены`) одновременно на одной странице. |
| **M-3** | [TorrentEdit.razor:~152](src/Animarr.Web/Components/Pages/TorrentEdit.razor) | `Seeds: @_live.Seeds&nbsp;Peers: @_live.Peers` — хардкод в локализованной в остальном странице. |
| **M-4** | [Folders.razor:~249-371](src/Animarr.Web/Components/Pages/Folders.razor) | 6 хардкод английских toast-строк (`Section ... added`, `Monitoring enabled for ...`, `No new subfolders found.`). |
| **M-5** | [Explorer.razor:~292,~297](src/Animarr.Web/Components/Pages/Explorer.razor) | `Title="Rename"` / `Title="Delete"` хардкод. |
| **M-6** | [MediaDetail.razor:~346](src/Animarr.Web/Components/Pages/MediaDetail.razor) | `s.Name ?? $"Season {s.Number}"` — fallback не локализован. |
| **M-7** | [MediaDetail.razor:~675](src/Animarr.Web/Components/Pages/MediaDetail.razor) | `Console.WriteLine($"[MediaDetail] BuildEpisodeFileMap failed: ...")` — пишет в stdout вместо `ILogger`. |
| **M-8** | [Explorer.razor:~823,~841](src/Animarr.Web/Components/Pages/Explorer.razor) | `try { File.Move/Delete } catch { /* TODO: surface error */ }`. Пользователь кликает Rename/Delete, тихо ничего не происходит. |
| **M-9** | [Settings.razor:~744-981](src/Animarr.Web/Components/Pages/Settings.razor) | ~13 отдельных `AppConfig.GetAsync` при инициализации (зависит от H-3). |
| **M-10** | [PatternMatchService.cs:107-127](src/Animarr.Web/Services/PatternMatchService.cs:107) | `DetectSeasonFromPath` ходит вверх максимум 2 уровня. Структура `Show/Russian Dub/Season 02/ep01.mkv` → сезон не определяется. |
| **M-11** | [RenameService.cs:65-71](src/Animarr.Web/Services/RenameService.cs:65) | `Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories)` блокирует поток запроса на больших деревьях. |
| **M-12** | [TorrentEngineService.cs:209](src/Animarr.Web/Services/TorrentEngineService.cs:209) | `OnStateChangedAsync` перечитывает `TorrentConfig` из БД на каждый state-change каждого торрента. |
| **M-13** | [Home.razor:~586](src/Animarr.Web/Components/Pages/Home.razor) | Refresh-таймер каждые 5 сек делает `Include(Tags.ThenInclude(MediaTag))`, хотя теги в плитке каталога не показываются. |
| **M-14** | [Torrents.razor:~25,~36](src/Animarr.Web/Components/Pages/Torrents.razor) | `TorrentEngine.GetAll()` вычисляется inline в разметке + `AsQueryable()` boxing → пересборка коллекции 2 раза/сек. |
| **M-15** | README.md строка 113 | Лимит скорости описан как «Kbps», по факту в коде [Settings.razor:758](src/Animarr.Web/Components/Pages/Settings.razor:758) используется коэффициент `125000` = bytes per **Mbps**. Документация не совпадает с UI. |
| **M-16** | README.md строки 117-132 | Раздел «Ignore rules» **продублирован дважды** подряд (копипаст). |
| **M-17** | [Folders.razor](src/Animarr.Web/Components/Pages/Folders.razor) | При удалении секции каскад на дочерние папки (`ParentSectionId`) **не настроен** в EF model → осиротевшие записи. |

### 3.4 Низкий приоритет / техдолг

| # | Где | Проблема |
|---|-----|----------|
| **L-1** | [Patterns.razor](src/Animarr.Web/Components/Pages/Patterns.razor) | 7-строчный redirect-stub. Безопасно, но мёртвый код. |
| **L-2** | весь репо | Файлы шаблонных страниц `Counter.razor` / `Weather.razor` уже удалены (ANALYSIS.md ошибочно утверждал обратное). |
| **L-3** | [Home.razor:~601](src/Animarr.Web/Components/Pages/Home.razor) | `InvokeAsync(StateHasChanged)` каждые 5 сек, даже если `justCompleted.Count == 0`. |
| **L-4** | [MediaDetail.razor:~599-616](src/Animarr.Web/Components/Pages/MediaDetail.razor) | Навигация между карточками каталога вызывает повторный `initBackdrop` без предварительного `stopBackdrop`. JS-эффекты могут наслаиваться. |
| **L-5** | [AppConfigService.cs:21-28](src/Animarr.Web/Services/AppConfigService.cs:21) | `Convert.ChangeType` падение проглатывается → silent default. |
| **L-6** | [MetadataService.cs:~635](src/Animarr.Web/Services/MetadataService.cs) | Постеры сезонов качаются последовательно (foreach). |
| **L-7** | [PatternMatchService.cs:68-71](src/Animarr.Web/Services/PatternMatchService.cs:68) | Битый regex из БД молча скипается, юзер не узнаёт, что его паттерн сломан. |
| **L-8** | Razor pages | `Torrents.razor` 928 LOC, `Explorer.razor` 1178, `Settings.razor` 1061, `MediaDetail.razor` 880 — god-component anti-pattern, в каждом по 5+ диалогов и 20+ полей. |
| **L-9** | [RenameQueueProcessorService.cs](src/Animarr.Web/Services/RenameQueueProcessorService.cs:20), [FolderWatcherService.cs:22](src/Animarr.Web/Services/FolderWatcherService.cs:22) | `_delayMs = appOptions.Value.WatcherDelayMs` фиксируется в конструкторе — изменение `appsettings.json` без рестарта не применяется. |
| **L-10** | [Program.cs:138-148](src/Animarr.Web/Program.cs:138) | `/api/image` делает `db.FolderWatchers.Select(f => f.Path).ToListAsync()` на **каждый** запрос изображения. |
| **L-11** | UI вообще | Mix inline `style="..."` + Tailwind на одних и тех же элементах. Cosmetic debt. |
| **L-12** | [Animarr.Web.csproj:11-17](src/Animarr.Web/Animarr.Web.csproj:11) | `Version="*"` на всех NuGet-пакетах. Невоспроизводимая сборка. |
| **L-13** | весь репо | Нет тестов. `Animarr.Tests` не существует. |
| **L-14** | root | `_fix_quotes.py`, `_write_panel.py` — служебные скрипты в корне, явно не нужны в репо. |

---

## 4. Что просто отсутствует (не баги, а пустые места)

- 🔴 **Аутентификация** — единственная защита это «не пробрасывать порт наружу».
- 🔴 **REST API** для Sonarr/Radarr/Telegram-ботов. Сейчас только Blazor UI.
- 🔴 **Health checks** (`/health`, `/ready`) и метрики (Prometheus).
- 🔴 **Logger в файл с ротацией** — только консоль.
- 🔴 **Уведомления** (email/Telegram/Discord/webhook) при завершении загрузки / переименовании / ошибке идентификации.
- 🔴 **RSS / автодобавление** торрентов из фидов.
- 🔴 **Планировщик** (ночное полное сканирование, чистка истории).
- 🔴 **Backup/restore настроек** — нет экспорта/импорта конфигурации.
- 🔴 **Логи приложения в UI** (только `IdentificationQueue.LogDetails`).
- 🔴 **Валидация пути при добавлении папки** — `Directory.Exists` нигде не проверяется в диалогах.
- 🔴 **Клик по эпизоду** в `MediaDetail` ничего не делает — нет страницы эпизода / запуска плеера (хотя `/api/video` уже отдаёт range-стрим).
- 🔴 **Ручное редактирование метаданных** — только через ре-идентификацию.
- 🟡 **`FolderWatcher.RenameEnabled`** — поле есть в модели, но в `FolderWatcherService.StartWatcherInternal` оно не проверяется: FSW запускается всё равно, очередь набивается, а уже `RenameQueueProcessorService` фильтрует. UI это не объясняет.
- 🟡 **Dashboard** на Home — сейчас просто грид каталога без сводки (сколько качается, что переименовано последним, ошибки очереди).

---

## 5. Приоритезированный план улучшений

### Фаза 1 — стоп-краны (1-2 дня)

1. **C-1, M-1** — почистить `_torrentLocks` и `_markedDownloaded` в
   `RemoveAsync` (`TryRemove` + `Dispose` семафора, `foreach` по ключам с
   префиксом `hash:`).
2. **C-2** — переместить `PersistStatsAsync()` в цикл таймера, например
   каждые 30 секунд + при `StateChanged` со сменой `TorrentState`. Не на каждый
   тик 500 мс.
3. **C-6** — после `Path.GetFullPath` проверять
   `File.GetAttributes(fullPath) & FileAttributes.ReparsePoint != 0` →
   `Forbid()`. Альтернатива: `new FileInfo(fullPath).ResolveLinkTarget(true)`
   и валидировать заново.
4. **C-7** — минимум: добавить middleware с одним API-ключом из `AppConfig`
   (header `X-Animarr-Token`). Долгосрочно — cookie auth + локальные юзеры.
5. **H-10** — обернуть TMDB/MAL/LLM ключи через `IDataProtectionProvider`
   перед записью, расшифровка на чтении. Существующие значения мигрировать
   one-shot на старте.
6. **L-12** — зафиксировать NuGet-версии (`dotnet list package --include-transitive`).

### Фаза 2 — стабильность UI (1 неделя)

7. **C-4, C-5** — `ThemeService` и `LocalizationService` сделать
   `AddScoped` или `OwningComponentBase`, состояние хранить в cookie
   (`animarr-lang`, `animarr-theme`). `Program.cs` startup-loader тогда
   читает дефолты из БД только для гостя без cookie.
8. **C-3** — поднять `BuildDetailsFlatTree` внутрь `InvokeAsync(() => { … })`.
   Параллельно — пройти по всем подписчикам `TorrentEngine.StateChanged` и
   убедиться, что `StateHasChanged()` всегда обёрнут в `InvokeAsync`.
9. **H-8** — диффить новый snapshot против последнего, `StateChanged?.Invoke()`
   только при реальных изменениях. Альтернатива: разделить на
   `ProgressTick` (500 мс) и `StateTransition` (по событию).
10. **H-3, M-9, L-10** — `AppConfigService` обернуть `IMemoryCache` (TTL 30 сек
    + invalidate на `SetAsync`). Добавить `GetManyAsync(IEnumerable<string>)`.
    `Program.cs` `/api/image`/`/api/video` — кэшировать список roots с
    invalidate на DI-событие.

### Фаза 3 — пагинация и скорость каталога (1 неделя)

11. **H-6** — `History.razor`: server-side `Skip/Take` + total count
    в одном запросе через `CountAsync` + `ToListAsync`.
12. **H-7** — `Home.razor`: либо `FluentDataGrid` с `Virtualize`, либо
    server-side query с фильтром-в-БД (LIKE по `Title` + WHERE по тегу).
    Тэги грузить отдельным `GROUP BY` запросом.
13. **M-11** — `RenameService.ScanFolderAsync` — стримить файлы пачками
    (`Directory.EnumerateFiles().Chunk(500)`) и периодически
    `await Task.Yield()` + checkpoint в БД, чтобы юзер видел прогресс.
14. **M-12, M-13, M-14** — закешировать `TorrentConfig` в полях
    `TorrentEngineService`, инвалидировать на `UpdateGlobalSettingsAsync`.
    Убрать `.Include(Tags)` из refresh-таймера Home. Снапшот торрентов
    держать в поле `Torrents.razor`, обновляемом из `OnTorrentStateChanged`.

### Фаза 4 — переименование и идентификация (1-2 недели)

15. **H-1** — переписать ветку Movie в `EvaluateFile`: год извлекать
    напрямую `Regex.Match(fileName, @"\b(19\d\d|20\d\d)\b")`, **не** через
    `ParseFileName`. Если год найден → `{cleanTitle} ({year}){ext}`,
    иначе → `{cleanTitle}{ext}`. Добавить unit-тесты на 10+ типичных
    имён (BluRay, WEB-DL, разные релизеры, кириллица).
16. **H-9** — добавить ветвление по `HttpStatusCode` в `TmdbClient` /
    `MalClient`:
    - 401/403 → проброс наверх с типизированным `ApiAuthException`,
      идентификация ставит `IdentificationQueue.ErrorMessage = "Bad/missing API key"` и не ретраит.
    - 429 → respect `Retry-After`, экспоненциальный backoff (Polly или
      ручной), max 3 попытки.
    - сеть → лог + retry.
    В UI в `IdentificationQueue` сделать видимым `ErrorMessage`.
17. **H-5** — заменить `Zip` на:
    ```csharp
    var pairs = toProcess.Select(item => (item, history: new RenameHistory { … })).ToList();
    db.RenameHistories.AddRange(pairs.Select(p => p.history));
    ```
    Делает контракт явным.
18. **M-10** — `DetectSeasonFromPath` ходить вверх до корня
    `FolderWatcher.Path` (передавать его как аргумент), не больше N=5
    уровней. Cache regex'ов.
19. **L-7** — при битом regex в `ParseFileName` логировать имя паттерна
    и помечать `RenamePattern.LastError` (новое поле, показывать в Settings).

### Фаза 5 — локализация и UX (1 неделя)

20. **M-2 .. M-6** — выгрести все хардкод-строки grepом по
    `["` без `L[` рядом, добавить ключи в [en.json](src/Animarr.Web/Localization/en.json) / [ru.json](src/Animarr.Web/Localization/ru.json).
21. **M-7, M-8** — заменить `Console.WriteLine` на `ILogger`,
    обернуть `File.Move/Delete` в try/catch + `ToastService.ShowToast(ToastIntent.Error)`.
22. **L-3, L-4** — оптимизировать таймер Home и lifecycle backdrop
    в MediaDetail.
23. **L-8** — разбить «god-components» хотя бы на 2-3 child razor'а:
    - `Torrents.razor` → `TorrentList`, `TorrentDetailsPanel`, `AddTorrentDialog`
    - `Settings.razor` → `SettingsAppearance`, `SettingsPatterns`, `SettingsIgnores`, `SettingsApiKeys`, `SettingsLlm`, `SettingsTorrents`
    - `Explorer.razor` → файловое дерево, breadcrumb, action bar

### Фаза 6 — недостающие фичи (приоритет по голосованию)

24. **REST API** + bearer-token (`POST /api/torrents/magnet`,
    `GET /api/folders`, `POST /api/folders/{id}/scan`, `GET /api/media`).
25. **Health-checks** `/health` + Prometheus `/metrics` (через
    `Microsoft.Extensions.Diagnostics.HealthChecks` + Prometheus exporter).
26. **Уведомления** — Discord webhook + Telegram bot. Триггеры: торрент
    завершён, идентификация Failed, ошибка переименования.
27. **Player-страница** для клика по эпизоду — `<video>` тег с
    `src="/api/video?path=…"` (range-стрим уже работает в Program.cs).
28. **Backup/Restore** настроек — экспорт `AppConfig` + `RenamePattern` +
    `IgnoreRule` + `MediaTag` в JSON, кнопка в Settings.
29. **Чистка истории** — фоновый сервис, удаляющий `RenameHistory.IsReverted == false && Status == Renamed` старше 90 дней (конфигурируемо).

### Фаза 7 — качество (постоянно)

30. **L-13** — `Animarr.Tests` (xUnit) минимум:
    - `PatternMatchService.ParseFileName` — 20+ кейсов (anime, серии, фильмы, edge cases).
    - `PatternMatchService.CleanMovieTitle` — 15+ кейсов.
    - `PatternMatchService.IsIgnored` — глоб-маски.
    - `NaturalStringComparer` — естественная сортировка.
    - `RenameService` с InMemory DbContext — happy path + конфликт имени + источник пропал.
31. **L-14** — удалить `_fix_quotes.py`, `_write_panel.py` (или положить в `scripts/`).
32. **CI** — GitHub Actions: build + tests + Docker image, теги по версии.
33. **README.md** — починить M-15 (Kbps → Mbps) и M-16 (дубль секции Ignore rules), добавить скриншоты, раздел про limitations (нет auth, single-user).

---

## 6. Что обновить в существующих `ANALYSIS.md` и `plans/animarr-improvements.md`

ANALYSIS.md содержит ряд уже исправленных или ошибочных пунктов:

- **B-2 (Movie → `2016.mkv`)** — в текущем коде Movie-ветка
  [PatternMatchService.cs:198-269](src/Animarr.Web/Services/PatternMatchService.cs:198) уже формирует `Title (Year).ext`.
  Реальная проблема — H-1 (хрупкое извлечение года).
- **B-7 (FSW не ловит Renamed)** — исправлено в
  [FolderWatcherService.cs:121](src/Animarr.Web/Services/FolderWatcherService.cs:121).
- **B-11 (`item.CreatedAt == item.CreatedAt`)** — переписано в
  [MetadataService.cs:55](src/Animarr.Web/Services/MetadataService.cs:55) на корректную проверку
  `!await db.MediaItems.AnyAsync(m => m.Id == item.Id, ct)`.
- **B-30 (`* 1_000_000`)** — на самом деле `* 125000` (Mbps→bytes), а не MBps.
- **M-5 (язык не сохраняется)** — `Program.cs:88-105` уже грузит
  `Language`/`ThemeMode`/`AccentColor` из `AppConfig` при старте.
  Реальная проблема — C-5 (Singleton протекает между circuit'ами).
- **L-2 (Counter/Weather stubs)** — этих файлов в `Components/Pages/` уже нет.

`plans/animarr-improvements.md` корректен по структуре, но не
содержит C-1/C-2/C-3/C-6 (новые находки) и переоценивает 1.2 как
«критическое» — реально это рефакторинг качества (H-5).

---

## 7. Итоговая оценка (по факту, 2026-05)

| Область | Сейчас | Потенциал после Фазы 1-3 |
|---------|--------|--------------------------|
| Торренты | 7/10 (работает, но C-1/C-2 теряют данные на crash) | 9/10 |
| Переименование | 7/10 (Movie-год хрупкий) | 9/10 |
| Идентификация | 6/10 (тихие 401/429) | 8/10 |
| UI / UX | 6/10 (хардкод, нет пагинации, race rendering) | 8/10 |
| Архитектура | 5/10 (Singletons протекают между users) | 8/10 |
| Безопасность | 3/10 (нет auth, симлинк-эскейп, plain secrets) | 7/10 после auth + DataProtection + symlink check |
| Производительность | 5/10 (500 мс broadcast, N+1 AppConfig) | 8/10 |
| Тестируемость | 1/10 (тестов нет) | 6/10 после Фазы 7 |
