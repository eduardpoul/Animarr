// ====== Animarr v4 — multi-user data layer ======
// Extends the original library data with user accounts, roles and
// per-user state (favorites, watch state). All globals are added to
// `window.*` so v3 screens that still read from there keep working.

const USERS = [
  { id: "u-admin", name: "Yuri",  username: "yuri",  email: "yuri@medsolutions.dev", avatar: null, role: "master", created: "2025-08-12", lastSeen: "now" },
  { id: "u-anna",  name: "Anna",  username: "anna",  email: "anna@medsolutions.dev", avatar: null, role: "user",   created: "2025-11-03", lastSeen: "2h ago" },
  { id: "u-pavel", name: "Pavel", username: "pavel", email: null,                    avatar: null, role: "uploader", created: "2026-02-19", lastSeen: "yesterday" },
];

// Roles. "master" is built-in and cannot be edited. Others are custom and
// reference a source folder (FOLDERS[].id) + a permission bag.
const ROLES = [
  { id: "r-master",   name: "Master",   builtIn: true,
    perms: { viewContent: true, uploadContent: true, systemSettings: true, manageUsers: true },
    folders: "all",                  description: "Full access. Cannot be edited or deleted." },
  { id: "r-user",     name: "User",     builtIn: true,
    perms: { viewContent: true, uploadContent: false, systemSettings: false, manageUsers: false },
    folders: "all",                  description: "View-only — playback, marking watched, favoriting." },
  { id: "r-uploader", name: "Uploader", builtIn: false,
    perms: { viewContent: true, uploadContent: true, systemSettings: false, manageUsers: false },
    folders: ["f-anime","f-donghua"], description: "Can add downloads, only into Anime/Donghua folders." },
];

// "Logged in" user — drives the prototype's identity layer.
const CURRENT_USER = USERS[0]; // admin by default; Tweaks lets you flip

// Per-user state — keyed by user id. In production this lives in SQLite.
const USER_STATE = {
  "u-admin": {
    favorites: new Set(["perfect-world","mortals","arcane","ne-zha"]),
    watching: [
      { id: "perfect-world",  ep: 5,   progress: 0.38, kind: "progress" },
      { id: "swallowed-star", ep: 142, progress: 0.72, kind: "progress" },
      { id: "xian-ni",        ep: 9,   progress: 0,    kind: "next" },
      { id: "arcane",         ep: 3,   progress: 0.88, kind: "progress" },
    ],
  },
  "u-anna": {
    favorites: new Set(["arcane"]),
    watching: [
      { id: "arcane",         ep: 7,   progress: 0.50, kind: "progress" },
    ],
  },
  "u-pavel": { favorites: new Set(), watching: [] },
};

// Mirror the per-user payload into the legacy globals v3 screens read.
function applyUser(user) {
  const st = USER_STATE[user.id] || { favorites: new Set(), watching: [] };
  window.FAVORITES = st.favorites;
  window.WATCHING  = st.watching;
  window.CURRENT_USER = user;
}
applyUser(CURRENT_USER);

// Audio defaults — new "Audio" section under Profile.
const AUDIO_DEFAULTS = {
  preferredLanguage: "Japanese",   // Japanese / Mandarin / English / Russian
  subtitleLanguage: "Russian",
  subtitleSize: 18,                // px
  audioPassthrough: false,         // for AV receivers
  normalizeVolume: true,
  defaultVolume: 78,               // 0..100
};

// Permission helper
const can = (user, perm) => {
  const role = ROLES.find(r => r.id === `r-${user?.role}`) || ROLES.find(r => r.name.toLowerCase() === user?.role);
  return !!role?.perms?.[perm];
};

Object.assign(window, {
  USERS, ROLES, USER_STATE, AUDIO_DEFAULTS, applyUser, can,
  GITHUB_URL: "https://github.com",
});
