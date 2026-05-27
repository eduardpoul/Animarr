# Changelog #3 for Claude Code — Multi-user / Welcome / Roles

This is the v4 branch. It adds authentication, multi-user accounts with roles,
a global Welcome screen, replaces the left sidebar with a top-bar, and reorganises
settings into per-user "Profile" vs admin-only "Server Settings".

Pull on top of the previous changelogs (CW + favorites + downloads + watch tracking).

---

## 1. Multi-user — full reversal of "single user" decision

Animarr now has accounts, sessions and roles. Anything previously "global per server"
that is actually personal (favorites, watch state, theme, audio prefs, language) is now
**scoped per user**.

### Domain additions

```
User { id, name, username, email?, avatar?, roleId, created, lastSeen, passwordHash }
Role {
  id, name, builtIn (master/user can't be deleted),
  perms: { viewContent, uploadContent, systemSettings, manageUsers },
  folders: "all" | string[]  // FolderWatcher ids
  description
}
UserWatchState { userId, mediaFileId, watched, progress, lastSeen }   // was global
UserFavorite   { userId, mediaItemId }                                // was global
UserPreferences { userId, theme/accent/backdrop/audio/language/tvMode }
```

### Built-in roles

| Role     | viewContent | uploadContent | systemSettings | manageUsers | Folders |
|----------|-------------|---------------|----------------|-------------|---------|
| Master   | ✓           | ✓             | ✓              | ✓           | all     |
| User     | ✓           | —             | —              | —           | all     |

Custom roles can mix perms freely and scope to specific source folders (e.g.
"Donghua uploader" — uploadContent ✓ but only into `/Pool-D1/Media/Donghua`).

### Catalog visibility

A user only sees library items whose source folder is in their role's `folders`
list (or "all"). The catalog page reads `WATCHING` and `FAVORITES` from the current
user — different users see different "Continue Watching" hero slots, different
favorite stars.

---

## 2. New auth flow

```
GET /        → Welcome (anonymous)
  └─ "START" → /login
                └─ POST /api/auth/login → cookie/session, redirect to /catalog

GET /catalog  (any signed-in user)
GET /downloads (requires uploadContent OR systemSettings)
GET /server   (requires systemSettings)
POST /api/auth/logout → back to /welcome
```

Welcome and Login share the dark fanart-backdrop aesthetic. Login is a single 420×~480
card with username + password + a demo-credentials hint block.

After login, a returning user lands directly on `/catalog` — Welcome only shows when
not authenticated.

---

## 3. Left sidebar removed → top-bar

The persistent left rail (Catalog / Downloads / Settings) is gone. Replaced by a 60px
fixed top-bar:

```
┌────────────────────────────────────────────────────────────────────────────┐
│ [A] ANIMARR    Catalog              ● 17/25   [↓2]  [⚙]   [@Yuri · master] │
└────────────────────────────────────────────────────────────────────────────┘
   brand  primary nav            LLM status   Downloads  Admin   Profile chip
```

- **Brand** click → /catalog (also the canonical "home" target)
- **Primary nav** — currently just "Catalog". MediaDetail keeps Catalog selected.
- **LLM status pill** — green pulsing dot + `magic` icon + queue count `17/25`.
  Click → `LLMStatusPopup` (anchored to the pill, top-right of screen).
- **Downloads button** — only renders if `can(user, "uploadContent")`. Counter badge
  shows active downloads. Active state = accent tint.
- **Admin server-settings button** — only renders if `can(user, "systemSettings")`.
- **Profile chip** — avatar + name + role. Click → opens the `ProfilePanel` drawer.

Mobile: keeps the bottom tab bar from previous work. Top-bar is desktop-only.

### Downloads placement — picked option A
Option A (shipped): standalone icon in the top-bar that opens a full `/downloads` page.
Visible to anyone with upload permission, hidden from view-only users.
Option B was "bury it inside Server Settings as a sub-tab" — rejected because
non-admin users with uploadContent need it too.

The admin's Server Settings still has a `Downloads` tab — but it's the **config**
(ports, encryption, global Mbps limits), not the live queue.

---

## 4. ProfilePanel — drawer of personal settings

Right-edge drawer 480px wide. Tabs:

| Tab        | Contains                                                           |
|------------|--------------------------------------------------------------------|
| Identity   | Account info (read-only mostly), change password, edit profile, sign out (destructive button at bottom) |
| Appearance | Accent color (5 swatches), animated backdrop toggle, TV mode toggle |
| Audio      | **NEW** — preferred audio language, subtitle language, subtitle size slider, default volume slider, audio passthrough toggle, normalize-loudness toggle |
| Language   | Interface language dropdown                                        |

These all persist to `UserPreferences`, not global config.

---

## 5. Server Settings — admin-only

New `/server` route. Vertical-tab layout (240px sidebar inside the page):

| Tab            | Notes                                                             |
|----------------|-------------------------------------------------------------------|
| Users & Roles  | **NEW** — see section 6                                           |
| Root folders   | Moved from old Settings — unchanged                               |
| Rename history | Moved from old Settings — unchanged                               |
| AI / LLM       | Moved — unchanged                                                 |
| Patterns       | Moved — unchanged                                                 |
| Ignore rules   | Moved — unchanged                                                 |
| Downloads      | Moved — torrent listen port / global Mbps caps / encryption / DHT / PEX / UPnP toggles |
| Metadata       | Moved — TMDB / MAL / IMDb / AniDB order                           |
| About          | **NEW** — version, build, runtime, GitHub link, license, contributors |

Anything in this screen is gated by `can(user, "systemSettings")`. If the URL is
visited without permission, the topbar admin icon doesn't render and the route falls
back to `/catalog`.

---

## 6. Users & Roles (new admin tab)

Two sub-views toggled by a segmented control:

### Users
List rows: avatar · name + (username, email) · role pill · last-seen ·
edit / delete icons. "Master" users can't be deleted.

`+ New user` opens a 2-column form: name, username, email (optional), role,
initial password (twice). On submit: `POST /api/users`.

### Roles
Card rows per role with:
- Role name + `BUILT-IN` badge if applicable
- Description
- N users using it
- Permission pills (View / Upload / SystemSettings / ManageUsers — coloured by tone)
- Folder-scope summary (`ALL FOLDERS` or `N FOLDERS`)
- Edit icon (disabled on built-in)

`+ New role` opens `RoleBuilder`:
1. Role name input
2. **Permissions block** — 4 toggleable rows, each with title + explanation
3. **Source folders block** — segmented toggle "All folders" / "Selected only".
   When "Selected only" is active, a checkbox grid of FolderWatchers appears with
   accent-bordered cards for selected entries.
4. Create / Cancel actions.

The 4 permissions are intentionally short:
- `viewContent` — playback, marking watched, favoriting
- `uploadContent` — Add downloads (torrents, magnets, file uploads)
- `systemSettings` — access Server Settings
- `manageUsers` — CRUD users + roles (separate from systemSettings so you can have
  e.g. a "Family admin" who manages accounts but not the LLM config)

Tweaks panel exposes "View as" → switch between mock users to see how the UI changes
(no admin button when role=user, no downloads when role=user, etc).

---

## 7. LLM status popup

Anchored top-right under the topbar pill. 360px wide.

- Top row: ● ONLINE + provider/model + close
- Queue progress bar 17/25
- Two stat tiles: AVG `480ms/item` and HIT RATE `99.2%`
- "RECENT" list: 3 latest identifications with confidence colour

Click anywhere outside closes it (clear backdrop click handler).

---

## 8. TV-friendly UI (single code path)

We deliberately did **not** build a separate TV layout. The same UI works for TV with:

- `:focus-visible` rings everywhere (2px accent, 2px offset)
- A `.tv-focus` opt-in class on critical interactive elements that gets extra
  styling in TV mode (3px ring, 4px offset, 6px accent-soft glow)
- `html.tv-mode` global class — set via Tweaks/Profile toggle. In TV mode:
  - Base font-size goes from 14px → 15px
  - Min height of buttons bumped to 40px
  - Focus rings get the heavier treatment described above
- No hover-only essentials anywhere (every play / edit / toggle is also tappable
  or focusable). The "EMPTY" hint on missing episodes still requires hover/focus,
  but the file-status icon top-right already conveys it without interaction.

TV-specific affordances not built (out of scope):
- Spatial focus navigation (left/right/up/down between non-grid items)
- Number-key episode jump
- Pull-to-leave-app gesture

---

## 9. GitHub link

`window.GITHUB_URL = "https://github.com"` (placeholder — replace with real repo URL).

Appears in:
- Welcome top-right chip
- Welcome bottom-right text link
- Login bottom-right text link
- Server Settings → About → "View on GitHub" button

---

## Files added in v4

```
data-v4.jsx        — USERS, ROLES, USER_STATE, applyUser(), can(), AUDIO_DEFAULTS, GITHUB_URL
components-v4.jsx  — extra icons (github, server, audio, lock, user, users, logout, tv,
                     key, shield, globe, arrow-r) + <Avatar>
screens-v4.jsx     — WelcomeScreen, LoginScreen, TopBarV4, LLMStatusPopup,
                     ProfilePanel (+ tabs), DownloadsRoute, ServerSettingsScreen,
                     AdminUsersRoles, UsersList, RolesList, RoleBuilder, UserBuilder,
                     AdminDownloadsConfig, AdminAbout
app-v4.jsx         — auth state machine + topbar/profile/llm wiring + Tweaks panel
Animarr v4 — Multi-user.html  — entry point
```

`screens.jsx` was extended to export `SettingsCard`, `SettingsFolders`,
`SettingsHistory` etc. on window so v4's `ServerSettingsScreen` can reuse them.

Per-state HTMLs under `pages/v4/`:

```
01-welcome             04-catalog-user (view-as Anna)   09-server-folders
02-login               05-media-detail                   10-server-llm
03-catalog-admin       06-profile-panel                  11-server-history
                       07-llm-popup                      12-downloads
                       08-server-users
```

---

## Backend implications (rough sketch)

- `Users` + `Roles` + `RoleFolders` tables in SQLite (add columns to existing ones
  for `userId` foreign keys on `WatchState` / `Favorites` / `Preferences`)
- ASP.NET Core auth with cookie sessions; `[Authorize(Roles=...)]` or custom policy
  handlers driven by the `perms` columns
- A `RoleFolderFilter` LINQ helper that intersects user's folder list with the query
  on every `Library*` endpoint
- A `UserContext` scoped service exposing `CurrentUser` and `Can(perm)` for Razor /
  Blazor components
- `GET /api/me` returns the current user + role + computed permission flags so the
  frontend topbar can decide what to render without extra round-trips

Open questions:
1. Should the very first deployment auto-create a master user from a setup wizard,
   or use env vars / appsettings.json seed? (UI is built for both.)
2. SSO / OAuth / OIDC — out of scope for v4 but the auth boundary is now in place
   so adding providers later is incremental.
