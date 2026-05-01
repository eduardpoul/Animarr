# Animarr — Полный анализ кодовой базы
> Дата: 2026-05-01 | .NET 10 / Blazor Server / FluentUI v4 / Tailwind v4 / SQLite / MonoTorrent

---

## 1. Что реализовано (и как)

### 1.1 Торрент-движок (`TorrentEngineService`)
**Реализовано:**
- Загрузка по magnet-ссылке и `.torrent`-файлу
- Выбор файлов внутри торрента с приоритетами (Skip / Low / Normal / High)
- Пауза / возобновление / остановка
- Авто-переименование файлов по завершении загрузки (`AutoRenameAfterDownload`)
- Остановка сидирования по соотношению загрузка/раздача (`StopSeedingRatio`)
- Flatten subfolders — переназначение путей файлов на корень папки до начала загрузки
- Поддержка `CustomRootFolderName` для magnet-ов
- Персистентность через `TorrentRecord` в SQLite, восстановление при рестарте
- `SuppressPath` — подавление повторной обработки FSW после переименования
- Per-torrent `SemaphoreSlim` — сериализация событий `MetadataReceived` + `StateChanged`
- Живая статистика (скорость, прогресс, пиры) — `TorrentLiveStats`, пуш через `StateChanged` каждые 500 мс

**Проблемы:**
- `UpdateLiveStats()` вызывается в цикле таймера **каждые 500 мс** и плюс ещё при каждом `StateChanged`. При 10+ одновременных торрентах это создаёт нагрузку на Blazor circuit — все активные UI-клиенты перерисовываются 2 раза в секунду даже при нулевой активности.
- `PersistStatsAsync()` вызывается только при `ShutdownAsync()`. Если контейнер убит (`SIGKILL` / OOM), статистика `Downloaded`/`Uploaded` теряется навсегда.
- `MarkFileDownloadedAsync` запускается как fire-and-forget `Task.Run(...)` — при конкурентной записи в SQLite WAL возможны транзакционные ошибки (не перехватываются).
- Скорость в UI (`TorrentEdit.razor`) конвертируется из байт/с → мегабит/с с коэффициентом `/125000` при сохранении — но поле хранит **байт/с** в БД. При отображении нигде не конвертируется обратно → лимиты показываются неверно после перезагрузки.
- `_suppressedPaths` очищается только по TTL (`TickCount64`), но TTL нигде не задан в коде — переменная `_suppressedPaths` заполняется без очистки => **утечка памяти** при долгой работе с большим количеством переименований.

---

### 1.2 Переименование файлов (`RenameService`, `PatternMatchService`, `RenameQueueProcessorService`)
**Реализовано:**
- Сканирование папки (dry-run) → предпросмотр переименований (`ScanFolderAsync`)
- Применение переименований с записью в `RenameHistory`
- Fallback crash-recovery: `RenameStatus.Pending` → резолвится при старте (`SeedDataService`)
- Глобальные + per-folder паттерны, приоритеты, исключения
- Фильтрация паттернов по типу папки (Movie / Series / Auto)
- Glob-маски для правил игнора (`*` и `?`)
- Автоматическое переименование из `FolderWatcherService` при появлении нового файла
- Очередь `RenameQueue` — файлы ждут `WatcherDelayMs` перед обработкой
- Откат переименования (`IsReverted`) — меняет название файла обратно

**Проблемы / Баги:**
- **B-1 КРИТИЧНО**: `RenameService.ScanFolderAsync` дублирует логику `ProcessSingleFileAsync` (~60 строк copy-paste). Любое изменение надо делать в двух местах — баги неизбежны.
- **B-2**: `PatternMatchService.BuildTargetName` для фильмов (Movie-папка) генерирует имя `{year}.ext` — например `2016.mkv`. Это полностью ломает Kodi/Plex/Emby, которые ожидают формат `Movie Title (2016).mkv`.
- **B-3**: `DetectSeasonFromPath` поднимается максимум на 2 уровня вверх. Для структуры `/series/Season 1/Specials/01.mkv` сезон не определяется (3 уровня).
- **B-4**: При применении переименований `ApplyRenamesAsync` создаёт все `RenameHistory` записи перед началом и использует `Zip` для совмещения. Если список `approved` отфильтрован после создания истории (например, файл исчез) — индексы съезжают, `Zip` вернёт меньше пар, часть `Pending` записей никогда не обновится до финального статуса.
- **B-5**: `GetLatestNameFromHistory` в `RenameService` (если есть) вызывается синхронно через `CreateDbContext` вместо `CreateDbContextAsync`.
- **B-6**: Глобальные ignore-правила сидируются **один раз** при первом запуске (`AnyAsync`). Если список `BuiltInIgnoreMasks` пополнится в следующей версии — новые маски не добавятся в существующую БД.

---

### 1.3 Наблюдение за папками (`FolderWatcherService`)
**Реализовано:**
- `FileSystemWatcher` per folder, динамический старт/стоп без перезапуска
- Секции (section folders) — автоимпорт подпапок как отдельных `FolderWatcher`
- Подавление повторной обработки через `_suppressedPaths` (TTL-based)
- Событие `FileRenamed` / `SubfolderCreated` → UI-нотификации

**Проблемы:**
- **B-7**: `FolderWatcherService` подписывается только на `Created` события (`watcher.Created`). События `Renamed` (переместить файл в папку через `mv`) — игнорируются. Файлы, перемещённые в наблюдаемую папку, не переименовываются.
- **B-8**: При удалении папки из БД `StopWatcherAsync` вызывается, но `FileSystemWatcher` продолжает существовать до следующего GC. При быстром удалении + добавлении той же папки `_watchers.ContainsKey` может вернуть `true` по старому (уже disposed) объекту.
- **B-9**: `_suppressedPaths` нигде не чистится — утечка (описано выше в 1.1).
- **B-10**: `NotifySubfolderCreated` вызывается из FSW-треда без `InvokeAsync` — Blazor circuit thread safety не гарантируется.

---

### 1.4 Идентификация медиа (`IdentificationQueueProcessorService`, `MetadataService`, `MicrosoftAiLlmService`)
**Реализовано:**
- Очередь идентификации (`IdentificationQueue`) — serial processing, восстановление после краша
- LLM-шаг (Microsoft.Extensions.AI → OpenAI-compatible endpoint): извлечение названия из имени папки
- TMDB API: поиск сериалов и фильмов, загрузка постера / фанарта / логотипа / описания / жанров / рейтинга / сезонов / возрастного рейтинга
- MAL API: поиск аниме, обогащение метаданными
- Ручной поиск из UI (по названию, TMDB ID, MAL ID, IMDb ID, TVDB ID)
- `IdentificationStatus`: Pending → Identified / NeedsReview / Failed / Manual
- `CandidatesJson` — top-3 кандидата когда уверенность низкая

**Проблемы:**
- **B-11 КРИТИЧНО**: `MetadataService.IdentifyFolderAsync` содержит логику проверки `isNew`:
  ```csharp
  bool isNew = item.CreatedAt == item.CreatedAt && !db.MediaItems.Local.Contains(item)
               && !await db.MediaItems.AnyAsync(m => m.Id == item.Id, ct);
  ```
  Условие `item.CreatedAt == item.CreatedAt` всегда `true`. Проверка запутана и ненадёжна — при повторной идентификации существующего `MediaItem` он может быть добавлен в БД второй раз → `DbUpdateException` (unique constraint на `FolderId`).
- **B-12**: `WarmUpOllamaAsync` запускается как fire-and-forget без привязки к `CancellationToken` из `ExecuteAsync`. При остановке сервиса warm-up продолжает работать.
- **B-13**: Если TMDB API key не задан — идентификация тихо падает без записи ошибки в `IdentificationQueue.ErrorMessage`. Пользователь видит статус `Pending` вечно.
- **B-14**: `TmdbClient.SearchTvAsync` / `SearchMovieAsync` нигде не кэшируются. Если 50 папок одновременно запускают идентификацию, TMDB отдаст 429 (rate limit) — нет retry logic с backoff.
- **B-15**: Изображения скачиваются и сохраняются в **папку мониторинга** (`FolderWatcher.Path`). При следующем сканировании `RenameService` увидит `poster.jpg` и попытается переименовать его (хотя `poster*` есть в ignore rules — это работает, но только если ignore rules правильно засеяны).

---

### 1.5 Каталог (`Home.razor`, `MediaDetail.razor`)
**Реализовано:**
- Сетка постеров с lazy loading
- Бейджи типа медиа (Anime / Series / Movie), статуса идентификации
- Поиск по названию (live filter)
- Теги — создание, удаление, фильтрация каталога по тегу
- Страница деталей: fanart hero, постер, описание, жанры, рейтинг, возрастной рейтинг, сезоны + эпизоды с thumbnails
- Кнопка ручного редактирования + ре-идентификации
- Ручной поиск по TMDB/MAL/IMDb/TVDB с применением результата

**Проблемы:**
- **B-16**: `Home.razor` загружает **все** `MediaItem` из БД без пагинации. При 500+ элементах это 500+ DB строк + 500+ `<img>` элементов в DOM — браузер зависает.
- **B-17**: `MediaDetail.razor` содержит `MediaDetail` (хардкод английского текста "Media not found.", "Back", "Seasons" и т.д.) без локализации — нарушает поддержку русского языка.
- **B-18**: Backdrop (фанарт на главной) загружается через `/api/image?path=...` — сервер читает файл с диска при каждом запросе. Нет ETag / If-None-Match — браузер не использует cache (хотя `Cache-Control: public, max-age=86400` выставлен — этого недостаточно без ETag).
- **B-19**: `MediaDetail.razor` показывает сезоны только если `_seasons.Count > 0` — но `_seasons` заполняется из `SeasonsJson` (JSON в БД). Если TMDB не вернул сезоны — секция пустая без объяснений.
- **B-20**: Теги `MediaTag` не привязаны к `MediaItem` при создании — диалог создания тега просто добавляет тег в общий список. Привязка к конкретному элементу делается отдельно, но UI это никак не объясняет.

---

### 1.6 Папки (`Folders.razor`, `SectionFolders.razor`)
**Реализовано:**
- CRUD папок и секций
- Toggle мониторинга без перезапуска
- Автоимпорт подпапок секции
- Инициирование идентификации одной папки / всех папок
- Тип папки (Auto / Series / Movie) влияет на паттерны переименования

**Проблемы:**
- **B-21**: При сохранении папки `_formFolderTypeStr` конвертируется в `FolderType` через `Enum.Parse`. Если пользователь передаёт неверную строку — `InvalidOperationException` без try-catch → UI зависает.
- **B-22**: `IdentifyAllAsync` перечисляет все `FolderWatcher` где `!IsSection` и добавляет их в `IdentificationQueue` одним запросом — без проверки уже стоящих в очереди. При повторном нажатии создаются дубли в очереди.
- **B-23**: После удаления секции дочерние папки (с `ParentSectionId`) **не удаляются** — остаются осиротевшими в БД. Каскадное удаление через `OnDelete(DeleteBehavior.Cascade)` не настроено для этой связи.

---

### 1.7 Проводник (`Explorer.razor`)
**Реализовано:**
- Навигация по папкам файловой системы (внутри зарегистрированных путей)
- Breadcrumb навигация
- Показ постеров папок (из `MediaItem.PosterPath`)
- Добавление / редактирование / удаление секций и папок прямо из проводника
- Встроенные панели `FolderScanPanel` и `FolderEditPanel`

**Проблемы:**
- **B-24**: `Explorer.razor` читает файловую систему напрямую без ограничений на глубину — при нажатии на `/` (корень, если пользователь добавил `/mnt`) может рекурсивно перечислять миллионы файлов.
- **B-25**: Нет пагинации файлов — при 1000+ файлов в папке DOM будет содержать 1000+ строк.
- **B-26**: `FolderScanPanel` делает `ScanFolderAsync` синхронно на UI-треде без отмены (нет `CancellationToken`). Долгое сканирование блокирует Blazor circuit.

---

### 1.8 История (`History.razor`)
**Реализовано:**
- Список переименований с фильтрацией по папке и статусу
- Пагинация (50 элементов на страницу)
- Откат переименования с подтверждением
- Лог идентификаций (`IdentificationQueue.LogDetails`) — сворачиваемые записи

**Проблемы:**
- **B-27**: `History.razor` загружает **все** `RenameHistory` из БД без серверной пагинации — `FluentDataGrid Pagination` пагинирует уже загруженные данные в памяти. При 10 000+ записях — высокое потребление памяти и долгий старт страницы.
- **B-28**: Фильтрация по `_filterStatus` делается через LINQ на загруженном списке в памяти (`FilteredItems()`). При 10 000 записей — нормально, но это всё равно не DB-level filter.
- **B-29**: Лог идентификаций (`FilteredScanJobs()`) тоже загружает всю таблицу `IdentificationQueues`. Таблица растёт неограниченно — нет TTL / purge.

---

### 1.9 Настройки (`Settings.razor`)
**Реализовано:**
- Язык интерфейса (en / ru) через `LocalizationService`
- Тема (System / Dark / Light) через `ThemeService`
- Цвет акцента (FluentUI `OfficeColor`)
- Backdrop — включение/выключение, интервал, blur, яркость
- Паттерны переименования — CRUD, тест паттерна, LLM-подсказка regex
- Правила игнора — CRUD
- API ключи: TMDB, MAL
- LLM-настройки: provider (OpenAI / compatible), base URL, API key, модель
- Тест подключения к LLM
- Конфиг торрент-движка: лимиты скорости, порт, DHT/LSD/UPnP, seeding ratio

**Проблемы:**
- **M-5 ОТКРЫТО**: Язык **не сохраняется** между сессиями. `OnLanguageChanged` вызывает `localization.LoadAsync(lang, env)` и обновляет `AppSettings.Language` только в памяти — но `AppSettings` биндится из `appsettings.json` (readonly). После перезапуска язык снова становится `en`.
  - Нужно: сохранять выбранный язык в `AppConfig` (таблица БД) и читать оттуда при старте.
- **B-30**: При сохранении torrent config (`SaveTorrentConfigAsync`) значения лимитов скорости конвертируются MB/s → bytes/s умножением на 1_000_000. Но пользователь вводит MB/s — в поле `FluentNumberField` нет единиц, не очевидно.
- **B-31**: Настройки backdrop сохраняются через `AppConfig` (БД), но считываются через `IOptions<AppSettings>` при рендере MainLayout → изменения вступают в силу только после перезагрузки страницы, не мгновенно.
- **B-32**: `AccentColor` (`ThemeService`) не персистентна — после перезапуска сервиса сбрасывается в `null` (default blue).

---

### 1.10 Торрент — добавление/редактирование (`Torrents.razor`, `TorrentEdit.razor`)
**Реализовано:**
- Боковая панель добавления torrent (magnet или .torrent файл)
- Выбор файлов с деревом папок (collapse/expand), приоритеты
- Выбор папки назначения из зарегистрированных FolderWatcher
- Создание подпапки прямо при добавлении
- `SkipSubfolderStructure` (Flatten) при добавлении
- `CustomRootFolderName` — переименование корневой папки torrent'а
- `TorrentEdit`: изменение лимитов скорости, приоритетов файлов, `StopAfterDownload`

**Проблемы:**
- **B-33**: `SkipSubfolderStructure` на странице `TorrentEdit` задизейблен (`Disabled="true"`) — изменить после добавления нельзя, хотя поле в БД есть.
- **B-34**: При добавлении magnet — дерево файлов недоступно до получения метаданных. UI показывает пустой блок без объяснений — нет placeholder'а "ожидание метаданных".
- **B-35**: `Torrents.razor` подписывается на `TorrentEngine.StateChanged` через `IDisposable`, но вызывает `StateHasChanged()` без `InvokeAsync` в некоторых обработчиках → **потенциальный race condition** на Blazor circuit.

---

### 1.11 Локализация (`LocalizationService`, `Localization/en.json`, `ru.json`)
**Реализовано:**
- JSON-файлы с ключами для en и ru
- `L["key"]` синтаксис во всех Razor-компонентах через `@inject LocalizationService L`
- Динамическое переключение языка без перезапуска (через `LanguageChanged` event)

**Проблемы:**
- **B-36**: `MediaDetail.razor` содержит хардкод английских строк: "Media not found.", "Back", "Seasons" — не локализованы.
- **B-37**: Некоторые toast-уведомления в `Folders.razor` и `Settings.razor` используют хардкод ("Saved", "Error saving" и т.п.) вместо ключей локализации.
- **B-38**: `LocalizationService` — **Singleton**, хранит одно состояние на весь процесс. При нескольких одновременных пользователях (если кто-то сменил язык) — язык меняется для всех.

---

### 1.12 Безопасность
- `/api/image` — путь валидируется: файл должен быть внутри зарегистрированных `FolderWatcher.Path` → directory traversal невозможен ✅
- API-ключи TMDB/MAL хранятся в SQLite в открытом виде — нет шифрования ⚠️
- LLM API key также в открытом виде в SQLite ⚠️
- Нет аутентификации вообще — приложение полностью открыто по HTTP ⚠️

---

## 2. Что не реализовано (заглушки / пустые страницы)

| Страница / Функция | Статус | Комментарий |
|---|---|---|
| `Weather.razor` | 🔴 ЗАГЛУШКА | Компонент из шаблона Blazor, не удалён |
| `Counter.razor` | 🔴 ЗАГЛУШКА | Компонент из шаблона Blazor, не удалён |
| `Patterns.razor` | 🔴 ДУБЛЬ | Страница `/patterns` существует, но паттерны уже в Settings → Patterns. Два места управления одними данными |
| Уведомления / нотификации | 🔴 НЕТ | Нет push-уведомлений о завершении загрузки, ошибках идентификации |
| RSS / автозагрузка | 🔴 НЕТ | Нет подписки на RSS-фиды (Nyaa, RuTracker и т.д.) |
| Планировщик / расписание | 🔴 НЕТ | Нет scheduled tasks (например, ночное сканирование) |
| Экспорт / импорт настроек | 🔴 НЕТ | Нет backup/restore конфигурации |
| Логи приложения в UI | 🔴 НЕТ | Только Identification scan logs. Общий журнал событий отсутствует |
| Ограничение прав на папки | 🔴 НЕТ | Нет проверки read/write прав при добавлении папки |
| Backdrop slideshow | 🟡 ЧАСТИЧНО | Код в MainLayout.razor.js подготовлен, но логика смены слайдов требует проверки |
| Дашборд на Home | 🟡 ЧАСТИЧНО | Home показывает каталог, но нет сводки: активные загрузки, последние переименования, очередь идентификации |
| Страница деталей эпизода | 🔴 НЕТ | `MediaDetail` показывает список эпизодов, но клик по эпизоду ничего не делает |
| Редактирование метаданных вручную | 🔴 НЕТ | Нельзя вручную поправить название, год, тип — только через ре-идентификацию |
| FolderWatcher → RenameEnabled | 🟡 ЧАСТИЧНО | Поле есть в модели, но UI в `Folders.razor` показывает `_formRenameEnabled` без применения: при `RenameEnabled = false` FSW всё равно запускается |

---

## 3. Узкие места и проблемы производительности

### P-1: UI перерисовывается 2 раза в секунду
`TorrentEngineService.StateChanged` вызывается каждые 500 мс из `PeriodicTimer`.  
Все страницы с `@implements IDisposable` и `TorrentEngine.StateChanged += OnStateChanged` перерисовываются.  
**Проблема:** даже страница `/settings` или `/history` перерисовывается дважды в секунду, если она открыта и `Torrents` нет.  
**Решение:** добавить throttle (например, 1 сек только для неактивных страниц) или разделить событие на `TorrentProgressChanged` (500 мс) и `TorrentStateChanged` (только при реальных изменениях состояния).

### P-2: Нет серверной пагинации
`History.razor`, `Home.razor` загружают всю таблицу. При росте данных (типичная установка через год — 5 000+ историй переименований, 200+ медиа) — задержки при открытии страниц.

### P-3: `AppConfigService` — N+1 запросов
Каждый `GetAsync(key)` открывает новый `DbContext` и делает отдельный SELECT. В `TorrentEngineService.OnStateChangedAsync` это вызывается каждые ~500 мс.  
**Решение:** кэш `AppConfig` в памяти с TTL или dirty-flag при изменении.

### P-4: `IdentificationQueueProcessorService` — нет rate limiting для TMDB
При 50+ папках одновременной идентификации — очередь обрабатывает их серийно (по одному), но в пределах одного задания делается несколько TMDB-запросов. TMDB бесплатно даёт ~50 req/10s. Нет backoff при 429.

### P-5: SQLite WAL + множество concurrent writers
`TorrentEngineService` (Singleton), `FolderWatcherService` (Singleton), `RenameQueueProcessorService` (Singleton) — все пишут в БД параллельно через разные `DbContext`. SQLite в WAL-режиме поддерживает параллельные читатели, но одного писателя. Под нагрузкой возможны `SqliteException: database is locked` (особенно на ARM-устройствах типа Raspberry Pi).

---

## 4. Архитектурные проблемы

### A-1: Дублирование логики сканирования
`ScanFolderAsync` и `ProcessSingleFileAsync` в `RenameService` — почти идентичный код. Нужно вынести в приватный метод `BuildEffectivePatternsAsync(folder, globalPatterns)`.

### A-2: `LocalizationService` как Singleton — не thread-safe для multi-user
При нескольких одновременных пользователях с разными языками сервис хранит один язык. Нужно перейти на Scoped или cookie-based подход.

### A-3: Magic strings в `AppConfigKeys`
Константы определены, но часть кода в `Settings.razor` читает через `AppConfig.GetAsync(AppConfigKeys.TmdbApiKey)` — всё хорошо. Но в нескольких местах есть прямые строки в `appsettings.json` (`AppSettings.Language`) — две системы хранения настроек для одного и того же.

### A-4: `ThemeService` / `LocalizationService` — состояние не персистентно
Оба сервиса — Singleton с состоянием только в памяти. После перезапуска — сброс на дефолты.

### A-5: Нет валидации входных данных в диалогах
`Folders.razor`, `Settings.razor` — нет проверки, что путь папки реально существует на диске при сохранении. Пользователь может добавить несуществующий путь, FSW не стартует (только предупреждение в лог).

---

## 5. Приоритетный план исправлений

### 🔴 Критические (ломают функциональность)

| # | Баг | Где | Что сделать |
|---|---|---|---|
| B-2 | Movie rename → `2016.mkv` | `PatternMatchService.BuildTargetName` | Для Movie генерировать `{cleanTitle} ({year}){ext}` |
| B-4 | ApplyRenames Zip race | `RenameService.ApplyRenamesAsync` | Изменить архитектуру: обрабатывать item by item с lookup по Id, не Zip |
| B-11 | `isNew` всегда true | `MetadataService.IdentifyFolderAsync` | Переписать проверку: `bool isNew = !await db.MediaItems.AnyAsync(m => m.Id == item.Id)` |
| B-23 | Orphan folders при удалении секции | `Folders.razor` / DB | Добавить `OnDelete(DeleteBehavior.Cascade)` для `ParentSectionId` или удалять дочерние в UI |
| B-35 | StateChanged без InvokeAsync | `Torrents.razor` | Обернуть в `InvokeAsync(StateHasChanged)` |

### 🟡 Важные (ухудшают UX / могут вызвать потерю данных)

| # | Баг | Где | Что сделать |
|---|---|---|---|
| M-5 | Язык не сохраняется | `Settings.razor` + `LocalizationService` | Сохранять в `AppConfig`, читать при старте |
| B-1 | Дубликат логики scan | `RenameService` | Рефакторинг: общий метод `BuildEffectivePatternsAsync` |
| B-6 | Новые ignore masks не добавляются | `SeedDataService` | Изменить seed: сравнивать по маске, добавлять отсутствующие |
| B-7 | FSW не ловит Renamed | `FolderWatcherService` | Добавить `watcher.Renamed += OnFileRenamed` |
| B-13 | Тихая ошибка без TMDB key | `MetadataService` | Записывать `"TMDB API key not configured"` в `job.ErrorMessage` |
| B-16 | Нет пагинации в каталоге | `Home.razor` | Добавить серверную пагинацию или virtualization |
| B-27 | Нет серверной пагинации в истории | `History.razor` | Загружать только текущую страницу из БД |
| B-32 | AccentColor не персистентна | `ThemeService` + `Settings.razor` | Сохранять в `AppConfig` |
| B-36 | Хардкод строк в MediaDetail | `MediaDetail.razor` | Заменить на ключи локализации |
| P-3 | N+1 AppConfig | `AppConfigService` | Добавить in-memory cache |
| B-9 | Утечка `_suppressedPaths` | `FolderWatcherService` | Добавить периодическую очистку просроченных записей |

### 🟢 Улучшения (техдолг / polish)

| # | Описание | Где |
|---|---|---|
| A-2 | LocalizationService Singleton → не подходит для multi-user | Переход на cookie-scope или session-scope |
| B-30 | Неочевидные единицы лимита скорости | `Settings.razor` / `TorrentEdit.razor` | Добавить подпись "MB/s" к полям |
| B-33 | FlattenSubfolders нельзя менять после добавления | `TorrentEdit.razor` | Убрать `Disabled="true"` |
| B-29 | IdentificationQueue растёт бесконечно | `IdentificationQueueProcessorService` | Добавить cleanup старых Done/Failed записей (старше 30 дней) |
| P-1 | Слишком частый StateChanged | `TorrentEngineService` | Throttle до 1 сек; разделить на progress / state events |
| Нет | Удалить `Weather.razor` и `Counter.razor` | Весь проект | Мусор из шаблона |
| Нет | Объединить `/patterns` и Settings → Patterns | `Patterns.razor` | Удалить дублирующую страницу |
| A-5 | Нет валидации пути при добавлении папки | `Folders.razor` | Проверять `Directory.Exists(path)` перед сохранением |

---

## 6. Структура БД (фактическая)

```
FolderWatcher        — корень всего (папки + секции)
  RenamePattern[]    — per-folder паттерны
  IgnoreRule[]       — per-folder правила игнора
  RenameHistory[]    — история переименований
  MediaItem          — метаданные медиа (1:1 per folder)
    MediaItemTag[]   — многие-ко-многим с MediaTag

RenamePattern        — глобальные паттерны (FolderId = null)
IgnoreRule           — глобальные правила (FolderId = null)

TorrentRecord        — активные + завершённые торренты
  TorrentFileSelection[] — приоритеты файлов
TorrentConfig        — singleton row (Id=1)

RenameQueue          — очередь автопереименования от FSW
IdentificationQueue  — очередь LLM+TMDB идентификации
AppConfig            — key/value store настроек
MediaTag             — теги каталога
```

**Проблема схемы:** `FolderWatcher` используется одновременно как контейнер для секций (IsSection=true) и как описание конкретной папки. `MediaItem` имеет FK на `FolderId` — но секционные папки тоже могут иметь `MediaItem` (что бессмысленно). Нет unique constraint на `MediaItem.FolderId` — в теории возможны дубли.

---

## 7. Суммарная оценка

| Область | Оценка | Комментарий |
|---|---|---|
| Торрент-движок | 7/10 | Работает, но UTI flush и race conditions |
| Переименование | 6/10 | Movie-формат сломан, дублирование логики |
| Идентификация | 5/10 | isNew-баг критичен, нет retry для TMDB 429 |
| UI / UX | 6/10 | Заглушки, хардкод строк, нет пагинации |
| Архитектура | 5/10 | Copy-paste, Singleton с состоянием, нет кэша |
| Безопасность | 4/10 | Нет auth, API keys в открытом виде |
| Производительность | 5/10 | Нет серверной пагинации, N+1 AppConfig |
