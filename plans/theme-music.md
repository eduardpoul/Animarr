# План: заглавная музыка (OP/ED) для тайтлов

Ветка: `feature/trailers-and-theme-music`
Статус scope: **только музыка** (трейлеры отложены — см. §10).

---

## 1. Что делаем

Подтягиваем заглавную тему аниме (опенинг/эндинг) из **AnimeThemes.moe**, кешируем
аудиофайл в `.animarr/` **рядом с медиа** (не в data-волюме докера) и тихо проигрываем
его **только когда зашёл внутрь конкретного тайтла** (страница деталей) — если
пользователь включил это в профиле. Поведение в духе Plex/Apple TV: тема плавно
появляется и глушится, когда стартует реальное воспроизведение.

### Решения (зафиксированы)
- **Хранилище музыки → `.animarr/` в медиапапке**, НЕ центральный `image-cache/`.
  Причина (со слов владельца): не забивать Docker data-волюм файлами — тяжёлые
  медиа-смежные ассеты должны жить на большом медиа-томе рядом с видео. Для будущих
  трейлеров (видео) это критично вдвойне.
- **Картинки остаются где есть** (центральный `image-cache/<folderId>/`, коммит `02ed4d0`).
  → Бэклог §11: позже вынести и картинки с data-волюма.
- **Играть только на экране конкретного тайтла**, НЕ на главной/каталоге.

---

## 2. Источник и мост по ID

**AnimeThemes API** (бесплатно, без ключа):
```
GET https://api.animethemes.moe/anime
    ?filter[has]=resources&filter[site]=MyAnimeList&filter[external_id]={malId}
    &include=animethemes.animethemeentries.videos.audio,animethemes.song
→ anime[0].animethemes[]  (type OP/ED)
        → animethemeentries[0].videos[0].audio.link   (.ogg, скачиваемый)
        → song.title                                  (название песни, для подписи)
```
Берём первый OP (fallback на первый ED), его первый entry/video, `audio.link`.

**Проблема моста:** у большинства тайтлов есть `TmdbId/ImdbId/TvdbId`, но **нет `MalId`**
— источник MAL по умолчанию выключен ([`ParseSourceOrder`](../src/Animarr.Web/Services/MetadataService.cs#L788): `mal=false`).
AnimeThemes ключуется на MAL/AniList/AniDB/Kitsu, не на TMDB. Поэтому:

1. Если `item.MalId` есть → запрос напрямую по нему.
2. Иначе, если `MediaType == Anime` → резолвим `idMal` через **AniList GraphQL**
   (бесплатно, без ключа) по `Title`/`EnglishTitle` + `Year`:
   ```graphql
   { Media(search:"<title>", type:ANIME){ idMal id }}
   ```
   затем AnimeThemes по полученному `idMal` (или `filter[site]=AniList&...={id}`).
3. Иначе пропускаем.

**Покрытие — честно:** AnimeThemes ориентирован на японское аниме. Китайские донхуа
(которых в этой библиотеке много — 凡人修仙传 и т.п.) там чаще всего отсутствуют →
для них темы просто не найдутся. Это ожидаемо, не баг.

---

## 3. Хранилище → `.animarr` рядом с медиа

Качаем `audio.link` в `ThemeDir(folder)` как `theme.ogg`, где:
```
ThemeDir(folder) = <base>/.animarr/<folderId-hex>/
   base = folder.Path                                    (тайтл-папка)
        = Path.GetDirectoryName(folder.SingleFilePath)   (плоский файл в общей секции)
```
- Подкаталог `<folderId>` обязателен: в плоской секции (`Movies/`) несколько фильмов
  делят один `Movies/.animarr/`, без id-подпапки `theme.ogg` будут затирать друг друга.
  Для папочных тайтлов это чуть избыточно, но единообразно и без коллизий.
- `item.ThemePath` хранит **абсолютный** путь к файлу — раздача (§5) не зависит от того,
  где он лежит.
- **NB про права:** медиа-том в докере иногда смонтирован read-only (`:ro`). Тогда
  запись `.animarr/` упадёт → ловим ошибку, логируем, `ThemePath` остаётся null,
  фича просто не активируется для этого тайтла (без краша идентификации).
- **Скан-игнор:** `.animarr` уже в списке игнора
  ([MediaFolderHeuristics.cs:28](../src/Animarr.Web/Services/MediaFolderHeuristics.cs#L28)).
  Проверить, что FolderWatcher не подхватывает содержимое `.animarr/` как новые медиа.
- Скачивание: общий `DownloadFileAsync(url, path)` (вынести из `tmdb.DownloadImageAsync`,
  который просто GET→байты→файл).

---

## 4. Модель данных + миграция

`MediaItem` ([Data/Models/MediaItem.cs](../src/Animarr.Web/Data/Models/MediaItem.cs)):
```csharp
/// <summary>theme.ogg — заглавная тема (OP/ED), абсолютный путь в <медиа>/.animarr/. null = нет/не искали.</summary>
public string? ThemePath  { get; set; }
/// <summary>Название песни темы для подписи ("Syoudou — Itou Kanako"). Опционально.</summary>
public string? ThemeTitle { get; set; }
```
`MediaItemDto` ([Shared/Models/MediaItemDto.cs](../src/Animarr.Shared/Models/MediaItemDto.cs)): те же 2 поля (`init`).
Маппинг ([MediaMappings.cs:41](../src/Animarr.Web/Mapping/MediaMappings.cs#L41)): добавить `ThemePath`/`ThemeTitle` рядом с `FanartPath`.
Миграция EF: `AddThemeMusicToMediaItem` (2 nullable‑колонки).

---

## 5. Раздача файла клиенту

Не переиспользуем `/api/image` (неверный content‑type, нет range). Новый эндпоинт:

- Маршрут в [ApiRoutes.cs](../src/Animarr.Shared/ApiRoutes.cs#L112): `MediaTheme = "/api/media/{id}/theme"` + хелпер `MediaThemeFor(id)`.
- Хендлер в [MediaEndpoints.cs](../src/Animarr.Web/Endpoints/MediaEndpoints.cs): найти `MediaItem` по id, отдать
  `Results.File(item.ThemePath, "audio/ogg", enableRangeProcessing: true)` (range нужен для seek/докачки).
  Файл лежит в медиапапке — проверить, что хост-процесс имеет к ней доступ (он и так читает видео оттуда).
- Клиент: `<audio src>`, строим URL хелпером в духе [MediaUrl.Image](../src/Animarr.UI/Services/MediaUrl.cs#L52):
  добавить `MediaUrl.Theme(Guid id)` → `/api/media/{id}/theme`.

---

## 6. Настройка в профиле

`UserPreferences` ([Data/Models/UserPreferences.cs](../src/Animarr.Web/Data/Models/UserPreferences.cs), секция Audio):
```csharp
/// <summary>Проигрывать заглавную тему на странице деталей тайтла. Default off — автоплей звука навязчив.</summary>
public bool ThemeMusicEnabled { get; set; } = false;
/// <summary>Громкость темы 0..100 (тихо под героем). Опционально.</summary>
public int  ThemeMusicVolume  { get; set; } = 40;
```
- DTO ([UserPreferencesDto.cs](../src/Animarr.Shared/Models/UserPreferencesDto.cs)): добавить 2 поля.
- Request ([UserPreferencesRequest.cs](../src/Animarr.Shared/Requests/UserPreferencesRequest.cs)): `bool? ThemeMusicEnabled`, `int? ThemeMusicVolume`
  — **добавлять в конец с `= null`** (как `Theme`/`HeroPagerStyle`), иначе позиционные
  вызовы `new UpdatePreferencesRequest(null, null, …)` в ProfilePanel поедут.
- PATCH/GET‑ToDto ([AuthEndpoints.cs:119-149](../src/Animarr.Web/Endpoints/AuthEndpoints.cs#L119) и `:492`): применить/вернуть новые поля.
- Миграция EF: `AddThemeMusicPref` (можно одной миграцией с §4).
- UI: в `RenderAudio()` ([ProfilePanel.razor:1485](../src/Animarr.UI/Components/Pages/ProfilePanel.razor#L1485)) — `Toggle`‑строка
  «Theme music» как у Normalize + слайдер громкости; хендлер `OnThemeMusicToggle` →
  `PatchAsync(new UpdatePreferencesRequest(... ThemeMusicEnabled: v))`.
- i18n: ключи в `wwwroot/lang/{en,ru,uk,de,es}.json`.

---

## 7. Проигрывание — ТОЛЬКО внутри тайтла

Играть исключительно на странице деталей конкретного тайтла
([MediaDetail.razor](../src/Animarr.UI/Components/Pages/MediaDetail.razor) / `MediaDetailHero`).
**НЕ играть** на главной/каталоге — `Home.razor`, `CatalogHero`, `CatalogContinueHero`
тему не трогают вообще (там крутится только fanart-слайдшоу).

- Если `UserCtx.Preferences.ThemeMusicEnabled` и `item.ThemePath != null` → скрытый
  `<audio loop>` с `src = MediaUrl.Theme(item.Id)`, громкость = `ThemeMusicVolume/100`.
- Плавный fade‑in при входе на тайтл; **fade‑out + stop** при: старте реального плеера,
  уходе со страницы (назад в каталог), открытии другого тайтла.
- **Каверзный момент браузеров:** автоплей со звуком блокируется до первого
  пользовательского жеста. Вход в тайтл — это клик по постеру, т.е. жест в сессии
  обычно уже был → чаще всего играет. Фоллбэк: стартовать по первому
  `pointerdown`/`keydown`, либо приглушённо. Заложить фоллбэк сразу.
- Маленький JS‑хелпер для fade (linear ramp на `audio.volume`) — рядом с `animarr-player.js`.

---

## 8. Когда качаем тему

- В пайплайне идентификации, для `MediaType == Anime`, шаг `FillThemeMusicAsync(item, folder)`
  рядом с [`FillMissingImagesAsync`](../src/Animarr.Web/Services/MetadataService.cs#L1551)
  (после успешного populate). Не блокирует основной флоу — тема не критична, ошибки в лог.
- Ленивость: если `ThemePath == null` и тайтл аниме — можно дёргать и при первом
  открытии деталей (фоновая дозагрузка), чтобы покрыть уже существующую библиотеку без
  полного reidentify. Решим на этапе реализации.
- Идемпотентно: если `theme.ogg` уже на диске и не `forceRefresh` — не качаем (как с картинками).

---

## 9. Список файлов (по слоям)

**Сервер**
- NEW `src/Animarr.Web/Services/AnimeThemesClient.cs` — клиент + DTO.
- NEW (или AniList внутри) `src/Animarr.Web/Services/AniListClient.cs` — мост title→idMal.
- `Program.cs` — регистрация клиентов + named `HttpClient` (`animethemes`, `anilist`).
- `Services/MetadataService.cs` — DI клиентов, `FillThemeMusicAsync`, **`ThemeDir`**
  (новый хелпер: `<медиа>/.animarr/<folderId>/`, НЕ `cachePaths.ForFolder`), download‑хелпер.
- `Data/Models/MediaItem.cs` — `ThemePath`, `ThemeTitle`.
- `Data/Models/UserPreferences.cs` — `ThemeMusicEnabled`, `ThemeMusicVolume`.
- `Mapping/MediaMappings.cs` — маппинг новых полей.
- `Endpoints/MediaEndpoints.cs` — `GET /api/media/{id}/theme` (range).
- `Endpoints/AuthEndpoints.cs` — PATCH/ToDto для новых prefs.
- NEW миграции: `AddThemeMusicToMediaItem`, `AddThemeMusicPref`.

**Shared**
- `Models/MediaItemDto.cs`, `Models/UserPreferencesDto.cs`,
  `Requests/UserPreferencesRequest.cs`, `ApiRoutes.cs`.

**UI**
- `Services/MediaUrl.cs` — `Theme(id)`.
- `Components/Pages/ProfilePanel.razor` — тумблер + слайдер в Audio.
- `Components/Pages/MediaDetail.razor` (+ возможно `Design/Media/MediaDetailHero.razor`) — `<audio>` + fade. **Только тут.**
- `wwwroot/animarr-player.js` (или новый мини‑хелпер) — fade/autoplay‑unlock.
- `wwwroot/lang/*.json` — подписи.

---

## 10. Трейлеры — отложено

В этой итерации не делаем (выбор «пока только музыка»). Когда вернёмся: TMDB уже отдаёт
YouTube‑ключ (добавить `videos` в `append_to_response`), AniList — `trailer{id site}`.
Решить тогда: скачивать файл (нужен `yt-dlp` в Docker‑образе) или встраивать `<iframe>`.
Видео‑трейлер по той же логике §3 ляжет в `<медиа>/.animarr/<folderId>/trailer.mp4`.
Поля/эндпоинт спроектированы расширяемо: можно обобщить до `/api/media/{id}/asset/{kind}`
и `TrailerPath` рядом с `ThemePath`.

---

## 11. Бэклог (на потом)

- **Вынести и картинки с data-волюма** в `.animarr` рядом с медиа (как музыку), либо в
  настраиваемый внешний путь — чтобы Docker data-волюм не рос. Сейчас картинки в
  центральном `image-cache/<folderId>/`. Owner попросил записать на потом.
