// ====== Animarr v4 — auth + app shell ======
const { useState: u4S, useEffect: u4E, useMemo: u4M, useCallback: u4C, useRef: u4R } = React;

const TWEAK_DEFAULTS_V4 = /*EDITMODE-BEGIN*/{
  "accent": "amber",
  "showBackdrop": true,
  "backdropBlur": 14,
  "backdropBrightness": 38,
  "rotateSec": 18,
  "tvMode": false,
  "asUser": "u-admin",
  "heroPager": "F",
  "fontSet": "soft",
  "badgeStyle": "chips"
}/*EDITMODE-END*/;

// Type personalities. "original" restores the old Archivo Black + Geist Mono
// (blocky/digital) look; the rest are warmer, more editorial — and route the
// label slot (--font-mono) onto a proportional sans so caption labels stop
// reading like terminal text.
const FONT_MAP_V4 = {
  editorial: { ui: "'Hanken Grotesque', system-ui, sans-serif", display: "'Bricolage Grotesque', 'Hanken Grotesque', sans-serif", label: "'Hanken Grotesque', system-ui, sans-serif" },
  soft:      { ui: "'Hanken Grotesque', system-ui, sans-serif", display: "'Hanken Grotesque', system-ui, sans-serif",             label: "'Hanken Grotesque', system-ui, sans-serif" },
  clean:     { ui: "'Schibsted Grotesk', system-ui, sans-serif", display: "'Schibsted Grotesk', system-ui, sans-serif",          label: "'Schibsted Grotesk', system-ui, sans-serif" },
  original:  { ui: "'Geist', system-ui, sans-serif",            display: "'Archivo Black', 'Geist', sans-serif",                label: "'Geist Mono', ui-monospace, monospace" },
};

const ACCENT_MAP_V4 = {
  crimson: { base: "oklch(0.66 0.20 25)",  hi: "oklch(0.74 0.21 25)",  line: "oklch(0.66 0.20 25 / 0.40)", soft: "oklch(0.66 0.20 25 / 0.16)" },
  amber:   { base: "oklch(0.72 0.17 60)",  hi: "oklch(0.80 0.16 60)",  line: "oklch(0.72 0.17 60 / 0.40)", soft: "oklch(0.72 0.17 60 / 0.16)" },
  green:   { base: "oklch(0.74 0.15 150)", hi: "oklch(0.82 0.15 150)", line: "oklch(0.74 0.15 150 / 0.40)", soft: "oklch(0.74 0.15 150 / 0.16)" },
  blue:    { base: "oklch(0.66 0.16 240)", hi: "oklch(0.74 0.15 240)", line: "oklch(0.66 0.16 240 / 0.40)", soft: "oklch(0.66 0.16 240 / 0.16)" },
  violet:  { base: "oklch(0.66 0.20 290)", hi: "oklch(0.74 0.20 290)", line: "oklch(0.66 0.20 290 / 0.40)", soft: "oklch(0.66 0.20 290 / 0.16)" },
};

const AppV4 = () => {
  const init = window.__init || {};
  // view = "welcome" | "login" | "app"
  const [view, setView] = u4S(init.view || "welcome");
  const [user, setUser] = u4S(window.CURRENT_USER);
  const [route, setRoute] = u4S(init.route || "catalog"); // catalog | downloads | server | media
  const [openId, setOpenId] = u4S(init.openId || null);
  const [profileOpen, setProfileOpen] = u4S(!!init.openProfile);
  const [llmOpen, setLlmOpen]   = u4S(!!init.openLLM);
  const [tweaks, setTweak] = window.useTweaks(TWEAK_DEFAULTS_V4);

  // Swap user via tweaks for demo purposes (admin / user / uploader).
  u4E(() => {
    const next = window.USERS.find(u => u.id === tweaks.asUser);
    if (next) { window.applyUser(next); setUser(next); }
  }, [tweaks.asUser]);

  // Global backdrop sync
  const firstItem = window.LIBRARY[0];
  const [bdImage, setBdImageRaw] = u4S({ url: firstItem.bd, hue: firstItem.hue });
  const bdImage_ref = u4R(bdImage); bdImage_ref.current = bdImage;
  const setBdImage = u4C((url, hue) => {
    if (!url) return;
    if (bdImage_ref.current.url === url) return;
    setBdImageRaw({ url, hue: hue ?? 0 });
  }, []);

  // Apply accent
  u4E(() => {
    const a = ACCENT_MAP_V4[tweaks.accent] || ACCENT_MAP_V4.crimson;
    const r = document.documentElement.style;
    r.setProperty("--accent",      a.base);
    r.setProperty("--accent-hi",   a.hi);
    r.setProperty("--accent-line", a.line);
    r.setProperty("--accent-soft", a.soft);
  }, [tweaks.accent]);

  // Apply typography set
  u4E(() => {
    const f = FONT_MAP_V4[tweaks.fontSet] || FONT_MAP_V4.editorial;
    const r = document.documentElement.style;
    r.setProperty("--font-ui", f.ui);
    r.setProperty("--font-display", f.display);
    r.setProperty("--font-mono", f.label);
  }, [tweaks.fontSet]);

  // TV mode: bumps min focus-target sizes globally
  u4E(() => {
    document.documentElement.classList.toggle("tv-mode", !!tweaks.tvMode);
  }, [tweaks.tvMode]);

  // Hero pager style — per-user preference, pushed via window so
  // CatalogV3 can read it without an extra prop chain.
  u4E(() => { window.__heroPager = tweaks.heroPager || "F"; }, [tweaks.heroPager]);

  // Feature flags (badge style A/B) — read at render by feature components.
  u4E(() => { window.__feat = { badgeStyle: tweaks.badgeStyle || "chips" }; }, [tweaks.badgeStyle]);

  const handleOpen = (id) => { setOpenId(id); setRoute("media"); };
  const handleBack = () => { setOpenId(null); setRoute("catalog"); };
  const goRoute = (r) => { setOpenId(null); setRoute(r); };
  window.__nav = goRoute;

  // ── WELCOME ──────────────────────────────────────────────────
  if (view === "welcome") {
    return <window.WelcomeScreen onStart={() => setView("login")} />;
  }
  // ── LOGIN ────────────────────────────────────────────────────
  if (view === "login") {
    return <window.LoginScreen
      onLogin={(uname) => {
        const u = window.USERS.find(x => x.username === uname) || window.USERS[0];
        window.applyUser(u); setUser(u);
        setTweak("asUser", u.id);
        setView("app");
      }}
      onBack={() => setView("welcome")}
    />;
  }

  // ── APP SHELL (no sidebar; topbar instead) ──────────────────
  return (
    <div style={{ minHeight:"100vh", width:"100vw", position:"relative" }}>
      {tweaks.showBackdrop && (
        <window.Backdrop image={bdImage.url} blur={tweaks.backdropBlur} brightness={tweaks.backdropBrightness} hue={bdImage.hue} />
      )}
      <window.TopBarV4
        user={user} route={route} onRoute={goRoute}
        onProfile={() => setProfileOpen(true)}
        onLLM={() => setLlmOpen(true)}
        onLogout={() => setView("welcome")}
      />

      <main style={{ position:"relative", zIndex: 2, paddingTop: 60, paddingBottom: 100 }}>
        {route === "media" && openId
          ? <window.MediaDetailV3 id={openId} onBack={handleBack} setBdImage={setBdImage} onOpen={handleOpen} />
          : route === "catalog"
          ? <window.CatalogV3 onOpen={handleOpen} rotateSec={tweaks.rotateSec} setBdImage={setBdImage} />
          : route === "player" && window.PlayerScreen
          ? <window.PlayerScreen onClose={() => { const c = window.__playCtx; if (c && c.id) { setOpenId(c.id); setRoute("media"); } else setRoute("catalog"); }} />
          : route === "stats" && window.StatsPage
          ? <window.StatsPage onOpen={handleOpen} />
          : route === "calendar" && window.CalendarPage
          ? <window.CalendarPage onOpen={handleOpen} />
          : route === "watchlist" && window.WatchlistPage
          ? <window.WatchlistPage onOpen={handleOpen} />
          : route === "downloads"
          ? <window.DownloadsRoute />
          : route === "server" && window.can(user, "systemSettings")
          ? <window.ServerSettingsScreen />
          : <window.CatalogV3 onOpen={handleOpen} rotateSec={tweaks.rotateSec} setBdImage={setBdImage} />
        }
      </main>

      {profileOpen && <window.ProfilePanel
        user={user} onClose={() => setProfileOpen(false)}
        onLogout={() => { setProfileOpen(false); setView("welcome"); }}
        accent={tweaks.accent} onAccent={v => setTweak("accent", v)}
        showBackdrop={tweaks.showBackdrop} onShowBackdrop={v => setTweak("showBackdrop", v)}
        tvMode={tweaks.tvMode} onTvMode={v => setTweak("tvMode", v)}
        heroPager={tweaks.heroPager} onHeroPager={v => setTweak("heroPager", v)}
      />}
      {llmOpen && <window.LLMStatusPopup onClose={() => setLlmOpen(false)} />}

      <TweaksUIV4 tweaks={tweaks} setTweak={setTweak} />
    </div>
  );
};

const TweaksUIV4 = ({ tweaks, setTweak }) => {
  if (!window.TweaksPanel) return null;
  return (
    <window.TweaksPanel title="Tweaks">
      <window.TweakSection label="Identity">
        <window.TweakSelect
          label="View as"
          value={tweaks.asUser}
          onChange={v => setTweak("asUser", v)}
          options={window.USERS.map(u => ({ value: u.id, label: `${u.name} · ${u.role}` }))}
        />
      </window.TweakSection>
      <window.TweakSection label="Display">
        <window.TweakSelect
          label="Typography"
          value={tweaks.fontSet}
          onChange={v => setTweak("fontSet", v)}
          options={[
            { value: "editorial", label: "Editorial · Bricolage" },
            { value: "soft",      label: "Soft · Hanken"        },
            { value: "clean",     label: "Clean · Schibsted"    },
            { value: "original",  label: "Original (gamer)"     },
          ]}
        />
        <window.TweakToggle label="TV mode" value={tweaks.tvMode} onChange={v => setTweak("tvMode", v)} />
        <window.TweakSelect
          label="Accent"
          value={tweaks.accent}
          onChange={v => setTweak("accent", v)}
          options={[
            { value: "crimson", label: "Crimson" },
            { value: "amber",   label: "Amber"   },
            { value: "green",   label: "Green"   },
            { value: "blue",    label: "Blue"    },
            { value: "violet",  label: "Violet"  },
          ]}
        />
      </window.TweakSection>
      <window.TweakSection label="Функции">
        <window.TweakSelect
          label="Бейджи серий"
          value={tweaks.badgeStyle}
          onChange={v => setTweak("badgeStyle", v)}
          options={[
            { value: "chips", label: "Чипы (ярко)" },
            { value: "meta",  label: "Тихо" },
          ]}
        />
      </window.TweakSection>
      <window.TweakSection label="Backdrop">
        <window.TweakToggle label="Show on all pages" value={tweaks.showBackdrop} onChange={v => setTweak("showBackdrop", v)} />
        <window.TweakSlider label="Blur"       value={tweaks.backdropBlur}       min={0} max={30} step={1} unit="px" onChange={v => setTweak("backdropBlur", v)} />
        <window.TweakSlider label="Brightness" value={tweaks.backdropBrightness} min={10} max={80} step={1} unit="%"  onChange={v => setTweak("backdropBrightness", v)} />
        <window.TweakSlider label="Hero rotate" value={tweaks.rotateSec}         min={5} max={120} step={1} unit="s"  onChange={v => setTweak("rotateSec", v)} />
      </window.TweakSection>
    </window.TweaksPanel>
  );
};

ReactDOM.createRoot(document.getElementById("root")).render(<AppV4 />);
