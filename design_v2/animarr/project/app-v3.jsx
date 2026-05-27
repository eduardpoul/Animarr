// ====== Main App v3 — full-width ======
const { useState: useStateA3, useEffect: useEffectA3, useMemo: useMemoA3, useCallback: useCallbackA3, useRef: useRefA3 } = React;

const TWEAK_DEFAULTS_V3 = /*EDITMODE-BEGIN*/{
  "accent": "crimson",
  "showBackdrop": true,
  "backdropBlur": 14,
  "backdropBrightness": 38,
  "rotateSec": 18
}/*EDITMODE-END*/;

const ACCENT_MAP_V3 = {
  crimson: { base: "oklch(0.66 0.20 25)",  hi: "oklch(0.74 0.21 25)",  line: "oklch(0.66 0.20 25 / 0.40)", soft: "oklch(0.66 0.20 25 / 0.16)" },
  amber:   { base: "oklch(0.72 0.17 60)",  hi: "oklch(0.80 0.16 60)",  line: "oklch(0.72 0.17 60 / 0.40)", soft: "oklch(0.72 0.17 60 / 0.16)" },
  green:   { base: "oklch(0.74 0.15 150)", hi: "oklch(0.82 0.15 150)", line: "oklch(0.74 0.15 150 / 0.40)", soft: "oklch(0.74 0.15 150 / 0.16)" },
  blue:    { base: "oklch(0.66 0.16 240)", hi: "oklch(0.74 0.15 240)", line: "oklch(0.66 0.16 240 / 0.40)", soft: "oklch(0.66 0.16 240 / 0.16)" },
  violet:  { base: "oklch(0.66 0.20 290)", hi: "oklch(0.74 0.20 290)", line: "oklch(0.66 0.20 290 / 0.40)", soft: "oklch(0.66 0.20 290 / 0.16)" },
};

const AppV3 = () => {
  const init = window.__init || {};
  const [route, setRoute] = useStateA3(init.route || "catalog");
  const [openId, setOpenId] = useStateA3(init.openId || null);
  const [tweaks, setTweak] = window.useTweaks(TWEAK_DEFAULTS_V3);

  const firstItem = window.LIBRARY[0];
  const [bdImage, setBdImageRaw] = useStateA3({ url: firstItem.bd, hue: firstItem.hue });
  const bdImage_ref = useRefA3(bdImage);
  bdImage_ref.current = bdImage;
  const setBdImage = useCallbackA3((url, hue) => {
    if (!url) return;
    if (bdImage_ref.current.url === url) return;
    setBdImageRaw({ url, hue: hue ?? 0 });
  }, []);

  useEffectA3(() => {
    const a = ACCENT_MAP_V3[tweaks.accent] || ACCENT_MAP_V3.crimson;
    const r = document.documentElement.style;
    r.setProperty("--accent",      a.base);
    r.setProperty("--accent-hi",   a.hi);
    r.setProperty("--accent-line", a.line);
    r.setProperty("--accent-soft", a.soft);
  }, [tweaks.accent]);

  const handleOpen  = (id) => { setOpenId(id); };
  const handleBack  = () => { setOpenId(null); };
  const handleRoute = (r) => { setOpenId(null); setRoute(r); };

  return (
    <div style={{ display:"flex", height:"100vh", width:"100vw", position:"relative" }}>
      {tweaks.showBackdrop && (
        <window.Backdrop image={bdImage.url} blur={tweaks.backdropBlur} brightness={tweaks.backdropBrightness} hue={bdImage.hue} />
      )}
      <window.Sidebar route={route} onRoute={handleRoute} />
      <main style={{
        flex: 1, height:"100%", overflow:"auto",
        position:"relative", zIndex: 2,
      }}>
        <div style={{ paddingBottom: 100 }}>
          {openId
            ? <window.MediaDetailV3 id={openId} onBack={handleBack} setBdImage={setBdImage} />
            : route === "catalog"  ? <window.CatalogV3 onOpen={handleOpen} rotateSec={tweaks.rotateSec} setBdImage={setBdImage} />
            : route === "torrents" ? <window.TorrentsV3 />
            : route === "settings" ? <window.SettingsV3 />
            : <window.CatalogV3 onOpen={handleOpen} rotateSec={tweaks.rotateSec} setBdImage={setBdImage} />}
        </div>
      </main>

      <TweaksUIV3 tweaks={tweaks} setTweak={setTweak} />
    </div>
  );
};

const TweaksUIV3 = ({ tweaks, setTweak }) => {
  if (!window.TweaksPanel) return null;
  return (
    <window.TweaksPanel title="Tweaks">
      <window.TweakSection label="Theme">
        <window.TweakSelect
          label="Accent"
          value={tweaks.accent}
          onChange={v => setTweak("accent", v)}
          options={[
            { value: "crimson", label: "Crimson (default)" },
            { value: "amber",   label: "Amber"             },
            { value: "green",   label: "Green"             },
            { value: "blue",    label: "Blue"              },
            { value: "violet",  label: "Violet"            },
          ]}
        />
      </window.TweakSection>

      <window.TweakSection label="Backdrop">
        <window.TweakToggle
          label="Show on all pages"
          value={tweaks.showBackdrop}
          onChange={v => setTweak("showBackdrop", v)}
        />
        <window.TweakSlider
          label="Blur"
          value={tweaks.backdropBlur}
          min={0} max={30} step={1} unit="px"
          onChange={v => setTweak("backdropBlur", v)}
        />
        <window.TweakSlider
          label="Brightness"
          value={tweaks.backdropBrightness}
          min={10} max={80} step={1} unit="%"
          onChange={v => setTweak("backdropBrightness", v)}
        />
        <window.TweakSlider
          label="Hero rotate"
          value={tweaks.rotateSec}
          min={5} max={120} step={1} unit="s"
          onChange={v => setTweak("rotateSec", v)}
        />
      </window.TweakSection>
    </window.TweaksPanel>
  );
};

ReactDOM.createRoot(document.getElementById("root")).render(<AppV3 />);
