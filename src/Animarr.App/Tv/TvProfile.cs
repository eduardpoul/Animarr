#if ANDROID
using Android.Content;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;

namespace Animarr.App.Tv;

/// <summary>
/// TV profile & settings, native. Only what matters on a TV:
///  • who's watching — every profile in one row, the current one first; move
///    right to another and press OK to switch (its PIN on this TV, or a QR
///    sign-in for a profile that hasn't been here yet);
///  • this device — sign in by default, change PIN;
///  • Home — the order of library folders (OK picks a folder up, ↑/↓ move it,
///    OK puts it down);
///  • interface language and scale; server; sign out.
/// </summary>
public sealed class TvProfilePage : ContentPage
{
    private bool _leaving;

    public TvProfilePage()
    {
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#1c1c1c");
        Content = new NativeHost(ctx => new TvProfileView(ctx, this));
    }

    protected override bool OnBackButtonPressed()
    {
        if (TvProfileView.LiftedRowDrop?.Invoke() == true) return true;   // drop a picked-up folder first
        if (_leaving) return true;
        _leaving = true;
        return base.OnBackButtonPressed();
    }

    protected override void OnAppearing() { base.OnAppearing(); _leaving = false; }
}

internal sealed class TvProfileView : Android.Widget.ScrollView
{
    private static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"), ("ru", "Русский"), ("uk", "Українська"), ("de", "Deutsch"), ("es", "Español"),
    };

    /// <summary>Back while a folder is picked up puts it down instead of leaving.</summary>
    public static Func<bool>? LiftedRowDrop;

    private readonly ContentPage _page;
    private readonly TvData _data = new();
    private readonly LinearLayout _col;
    private readonly LinearLayout _profiles;
    private readonly LinearLayout _folders;
    private readonly TextView _status, _defaultHint;
    private readonly TextView _defaultBtn, _langBtn, _scaleBtn;
    private DeviceStateDto? _device;
    private string _lang = "ru";

    public TvProfileView(Context ctx, ContentPage page) : base(ctx)
    {
        _page = page;
        SetBackgroundColor(TvUi.Bg);
        FillViewport = true;
        _col = TvUi.Column(ctx);
        _col.SetClipChildren(false);
        _col.SetClipToPadding(false);
        int sx = TvUi.Dp(ctx, TvUi.SafeX);
        _col.SetPadding(sx, TvUi.Dp(ctx, TvUi.SafeY), sx, TvUi.Dp(ctx, 60));
        AddView(_col, new LayoutParams(-1, -2));

        // header: Back + title
        var head = TvUi.Row(ctx);
        head.SetGravity(GravityFlags.CenterVertical);
        head.SetClipChildren(false);
        head.AddView(TvUi.Button(ctx, "‹  " + TvL.T("common.btn_back", "Назад", "Back"), false,
            () => _ = _page.Navigation.PopAsync(), sp: 15), TvUi.Lp(-2, TvUi.Dp(ctx, 40)));
        head.AddView(TvUi.Text_(ctx, 28, TvUi.Text, TvUi.Semibold, 1, TvL.T("profile.title", "Профиль", "Profile")),
            TvUi.Lp(-2, -2, TvUi.Dp(ctx, 24)));
        _col.AddView(head);

        // who's watching
        Section(TvL.T("profile.whos_watching", "Кто смотрит", "Who's watching"),
                TvL.T("tv.profiles_hint", "Выберите профиль и нажмите OK, чтобы переключиться.", "Pick a profile and press OK to switch."));
        var hs = new HorizontalScrollView(ctx) { HorizontalScrollBarEnabled = false };
        hs.SetClipChildren(false); hs.SetClipToPadding(false);
        _profiles = TvUi.Row(ctx);
        _profiles.SetClipChildren(false); _profiles.SetClipToPadding(false);
        _profiles.SetPadding(TvUi.Dp(ctx, 4), TvUi.Dp(ctx, 10), TvUi.Dp(ctx, 4), TvUi.Dp(ctx, 4));
        hs.AddView(_profiles);
        _col.AddView(hs, TvUi.Lp(-1, -2, -TvUi.Dp(ctx, 4), TvUi.Dp(ctx, 4)));

        // this device
        Section(TvL.T("profile.group_device", "Это устройство", "This device"), null);
        var dev = Buttons();
        _defaultBtn = TvUi.Button(ctx, TvL.T("switch.default_label", "Входить по умолчанию", "Sign in by default"), false, () => _ = ToggleDefaultAsync(), sp: 15);
        dev.AddView(_defaultBtn, TvUi.Lp(-2, TvUi.Dp(ctx, 44)));
        dev.AddView(TvUi.Button(ctx, TvL.T("profile.pin_change_title", "Сменить PIN", "Change PIN"), false,
            () => _ = _page.Navigation.PushAsync(TvPinPage.ForChange(_device?.PinMinLength ?? 4, _device?.PinMaxLength ?? 6)), sp: 15),
            TvUi.Lp(-2, TvUi.Dp(ctx, 44), TvUi.Dp(ctx, 12)));
        _defaultHint = TvUi.Text_(ctx, 13, TvUi.TextFaint, TvUi.Regular, 2);
        _col.AddView(_defaultHint, TvUi.Lp(-1, -2, 0, TvUi.Dp(ctx, 10)));

        // home: folder order
        Section(TvL.T("tv.home_folders", "Папки на главной", "Folders on Home"),
                TvL.T("tv.home_folders_hint", "OK — взять папку, ↑ ↓ — переместить, OK — положить.", "OK picks a folder up, ↑ ↓ move it, OK puts it down."));
        _folders = TvUi.Column(ctx);
        _folders.SetClipChildren(false);
        _col.AddView(_folders, TvUi.Lp(TvUi.Dp(ctx, 480), -2, 0, TvUi.Dp(ctx, 6)));

        // interface
        Section(TvL.T("tv.interface", "Интерфейс", "Interface"), null);
        var ui = Buttons();
        _langBtn = TvUi.Button(ctx, "…", false, () => _ = PickLanguageAsync(), sp: 15);
        _scaleBtn = TvUi.Button(ctx, "…", false, () => _ = PickScaleAsync(), sp: 15);
        ui.AddView(_langBtn, TvUi.Lp(-2, TvUi.Dp(ctx, 44)));
        ui.AddView(_scaleBtn, TvUi.Lp(-2, TvUi.Dp(ctx, 44), TvUi.Dp(ctx, 12)));

        // server
        Section(TvL.T("profile.server", "Сервер", "Server"), _data.Base.Replace("http://", "").Replace("https://", ""));
        var srv = Buttons();
        srv.AddView(TvUi.Button(ctx, TvL.T("profile.change_server", "Сменить сервер", "Change server"), false,
            () => TvRoot.Go(TvRoot.ServerPicker()), sp: 15), TvUi.Lp(-2, TvUi.Dp(ctx, 44)));
        srv.AddView(TvUi.Button(ctx, TvL.T("profile.sign_out", "Выйти", "Sign out"), false,
            () => _ = TvAuthFlow.SignOutAsync(), sp: 15), TvUi.Lp(-2, TvUi.Dp(ctx, 44), TvUi.Dp(ctx, 12)));

        _status = TvUi.Text_(ctx, 14, TvUi.AccentHi, TvUi.Regular, 2);
        _col.AddView(_status, TvUi.Lp(-1, -2, 0, TvUi.Dp(ctx, 18)));

        PaintScale();
        _ = LoadAsync();
    }

    private void Section(string title, string? hint)
    {
        var ctx = Context!;
        _col.AddView(TvUi.Text_(ctx, TvUi.TSection, TvUi.Text, TvUi.Semibold, 1, title), TvUi.Lp(-2, -2, 0, TvUi.Dp(ctx, 30)));
        if (hint is not null)
            _col.AddView(TvUi.Text_(ctx, 13, TvUi.TextFaint, TvUi.Regular, 2, hint), TvUi.Lp(-1, -2, 0, TvUi.Dp(ctx, 4)));
    }

    private LinearLayout Buttons()
    {
        var row = TvUi.Row(Context!);
        row.SetClipChildren(false); row.SetClipToPadding(false);
        _col.AddView(row, TvUi.Lp(-1, -2, 0, TvUi.Dp(Context!, 12)));
        return row;
    }

    // ── data ─────────────────────────────────────────────────────────────────
    private async Task LoadAsync()
    {
        try { var p = await _data.Api.GetMyPreferencesAsync(); if (!string.IsNullOrWhiteSpace(p.Language)) _lang = p.Language!; } catch { }
        _langBtn.Text = TvL.T("settings.language_label", "Язык", "Language") + ":  " +
                        (Languages.FirstOrDefault(l => string.Equals(l.Code, _lang, StringComparison.OrdinalIgnoreCase)).Name ?? _lang);

        _device = await TvAuthFlow.TryGetDeviceAsync();
        RosterUserDto[] roster = Array.Empty<RosterUserDto>();
        try { roster = await _data.Api.GetRosterAsync(); } catch { }
        BuildProfiles(roster);
        RenderDefault();

        var secs = await TvData.Safe(_data.Api.GetSectionFoldersAsync());
        BuildFolders(TvPrefs.Ordered((secs ?? Array.Empty<FolderWatcherDto>()).Select(s => (s.Id, (string)s.Label))));
    }

    // ── profiles row ─────────────────────────────────────────────────────────
    private sealed record Who(Guid Id, string Name, int Hue, bool IsCurrent, DeviceProfileDto? OnDevice);

    private void BuildProfiles(RosterUserDto[] roster)
    {
        var list = new List<Who>();
        if (_device is not null)
            foreach (var p in _device.Profiles) list.Add(new Who(p.Id, p.Name, p.AvatarHue, p.IsCurrent, p));
        foreach (var u in roster)
            if (list.All(w => w.Id != u.Id)) list.Add(new Who(u.Id, u.Name, u.AvatarHue, false, null));
        // the current profile first, then this TV's profiles, then the rest
        list = list.OrderByDescending(w => w.IsCurrent).ThenByDescending(w => w.OnDevice is not null).ThenBy(w => w.Name).ToList();

        _profiles.RemoveAllViews();
        AView? current = null;
        foreach (var w in list)
        {
            var tile = ProfileTile(w);
            _profiles.AddView(tile, TvUi.Lp(TvUi.Dp(Context!, 120), -2, 0, 0, TvUi.Dp(Context!, 16)));
            if (w.IsCurrent) current = tile;
        }
        // + another account (QR sign-in from a phone)
        _profiles.AddView(ProfileTile(null), TvUi.Lp(TvUi.Dp(Context!, 120), -2));
        (current ?? _profiles.GetChildAt(0))?.RequestFocus();
    }

    private AView ProfileTile(Who? w)
    {
        var ctx = Context!;
        var tile = TvUi.Column(ctx);
        tile.SetGravity(GravityFlags.CenterHorizontal);
        tile.Focusable = true; tile.Clickable = true; tile.FocusableInTouchMode = false;
        tile.SetClipChildren(false);

        int d = TvUi.Dp(ctx, 84);
        var disc = TvUi.Text_(ctx, 32, AColor.White, TvUi.Semibold, 1,
            w is null ? "+" : (string.IsNullOrWhiteSpace(w.Name) ? "?" : w.Name.Trim()[..1].ToUpperInvariant()));
        disc.Gravity = GravityFlags.Center;
        var fill = w is null ? TvUi.Surface2 : AColor.HSVToColor(new[] { (float)w.Hue, 0.45f, 0.62f });
        GradientDrawable Disc(bool ring) { var g = new GradientDrawable(); g.SetShape(ShapeType.Oval); g.SetColor(fill);
            if (ring) g.SetStroke(TvUi.Dp(ctx, 3), AColor.White); else if (w?.IsCurrent == true) g.SetStroke(TvUi.Dp(ctx, 3), TvUi.AccentHi); return g; }
        disc.Background = Disc(false);
        tile.AddView(disc, new LinearLayout.LayoutParams(d, d));

        var name = TvUi.Text_(ctx, 15, w?.IsCurrent == true ? TvUi.AccentHi : TvUi.TextDim, w?.IsCurrent == true ? TvUi.Semibold : TvUi.Medium, 1,
            w?.Name ?? TvL.T("tv.add_profile", "Другой профиль", "Another profile"));
        name.Gravity = GravityFlags.Center;
        tile.AddView(name, TvUi.Lp(-1, -2, 0, TvUi.Dp(ctx, 10)));
        var sub = TvUi.Text_(ctx, 12, TvUi.TextFaint, TvUi.Regular, 1,
            w is null ? TvL.T("tv.by_qr", "вход по QR", "sign in by QR")
            : w.IsCurrent ? TvL.T("tv.current", "сейчас", "current")
            : w.OnDevice?.HasPin == true ? "PIN" : TvL.T("tv.by_qr", "вход по QR", "sign in by QR"));
        sub.Gravity = GravityFlags.Center;
        tile.AddView(sub, TvUi.Lp(-1, -2, 0, TvUi.Dp(ctx, 2)));

        tile.FocusChange += (_, e) =>
        {
            disc.Background = Disc(e.HasFocus);
            tile.Animate()?.ScaleX(e.HasFocus ? 1.08f : 1f)?.ScaleY(e.HasFocus ? 1.08f : 1f)?.SetDuration(TvUi.FocusAnimMs)?.Start();
            if (w?.IsCurrent != true) name.SetTextColor(e.HasFocus ? TvUi.Text : TvUi.TextDim);
        };
        tile.Click += (_, _) =>
        {
            if (w is null) { _ = _page.Navigation.PushAsync(new PairingPage()); return; }
            if (w.IsCurrent) { _status.Text = TvL.T("tv.already_current", "Это текущий профиль.", "This is the current profile."); return; }
            if (w.OnDevice is { HasPin: true } p)
                _ = _page.Navigation.PushAsync(TvPinPage.ForVerify(p, null, _device?.PinMinLength ?? 4, _device?.PinMaxLength ?? 6));
            else
                _ = _page.Navigation.PushAsync(new PairingPage());
        };
        return tile;
    }

    // ── this device ──────────────────────────────────────────────────────────
    private void RenderDefault()
    {
        var on = _device?.DefaultUserId is not null;
        _defaultBtn.Text = TvL.T("switch.default_label", "Входить по умолчанию", "Sign in by default") + ":  " +
                           (on ? TvL.T("common.on", "Вкл.", "On") : TvL.T("common.off", "Выкл.", "Off"));
        var def = _device?.DefaultUserId is Guid id ? _device.Profiles.FirstOrDefault(p => p.Id == id) : null;
        _defaultHint.Text = def is null
            ? TvL.T("profile.default_desc_off", "Выключено — при запуске Animarr спрашивает, кто смотрит.", "Off — Animarr asks who's watching when it opens.")
            : def.IsCurrent
                ? TvL.T("profile.default_desc_me", "Animarr сразу открывает ваш профиль на этом устройстве.", "Animarr opens straight into your profile on this device.")
                : TvL.F("profile.default_desc_other_fmt", "Animarr сразу открывает профиль «{0}» на этом устройстве.", "Animarr opens straight into {0}'s profile on this device.", def.Name);
    }

    private async Task ToggleDefaultAsync()
    {
        if (_device is null) return;
        try
        {
            var fresh = await _data.Api.SetDeviceDefaultAsync(new SetDeviceDefaultRequest(_device.DefaultUserId is null));
            if (fresh is not null) _device = fresh;
            else _status.Text = TvL.T("profile.default_err_pin", "Сначала задайте PIN на этом устройстве.", "Set a PIN on this device first.");
        }
        catch { _status.Text = TvL.T("switch.default_err", "Не удалось изменить профиль по умолчанию.", "Couldn't change the default profile."); }
        RenderDefault();
    }

    // ── home folder order (pick up / move / put down) ───────────────────────
    private void BuildFolders(List<(Guid Id, string Label)> sections)
    {
        _folders.RemoveAllViews();
        if (sections.Count == 0)
        {
            _folders.AddView(TvUi.Text_(Context!, 14, TvUi.TextFaint, TvUi.Regular, 1, TvL.T("tv.no_folders", "Папок нет.", "No folders.")));
            return;
        }
        foreach (var s in sections) _folders.AddView(FolderRow(s.Id, s.Label), TvUi.Lp(-1, TvUi.Dp(Context!, 48), 0, 0, 0, TvUi.Dp(Context!, 8)));
    }

    private AView FolderRow(Guid id, string label)
    {
        var ctx = Context!;
        var row = TvUi.Row(ctx);
        row.SetGravity(GravityFlags.CenterVertical);
        int hp = TvUi.Dp(ctx, 18);
        row.SetPadding(hp, 0, hp, 0);
        row.Focusable = true; row.Clickable = true; row.FocusableInTouchMode = false;
        row.Tag = id.ToString();
        var name = TvUi.Text_(ctx, 16, TvUi.Text, TvUi.Medium, 1, label);
        row.AddView(name, new LinearLayout.LayoutParams(0, -2, 1f));
        var handle = TvUi.Text_(ctx, 18, TvUi.TextFaint, TvUi.Regular, 1, "≡");
        row.AddView(handle);

        bool lifted = false;
        void Paint()
        {
            var bg = lifted ? TvUi.Accent : row.HasFocus ? AColor.White : TvUi.Surface;
            row.Background = TvUi.Round(ctx, bg, 6);
            var fg = lifted ? AColor.White : row.HasFocus ? AColor.ParseColor("#1a1a1a") : TvUi.Text;
            name.SetTextColor(fg);
            handle.Text = lifted ? "↕" : "≡";
            handle.SetTextColor(lifted ? AColor.White : row.HasFocus ? AColor.ParseColor("#404040") : TvUi.TextFaint);
        }
        bool Drop()
        {
            if (!lifted) return false;
            lifted = false; LiftedRowDrop = null; Paint(); SaveOrder();
            return true;
        }
        row.FocusChange += (_, e) => { if (!e.HasFocus) Drop(); Paint(); };
        row.Click += (_, _) =>
        {
            if (lifted) { Drop(); return; }
            lifted = true; LiftedRowDrop = Drop; Paint();
        };
        row.KeyPress += (_, e) =>
        {
            e.Handled = false;
            if (!lifted || e.Event?.Action != KeyEventActions.Down) return;
            int idx = _folders.IndexOfChild(row);
            int to = e.KeyCode == Keycode.DpadUp ? idx - 1 : e.KeyCode == Keycode.DpadDown ? idx + 1 : -2;
            if (to == -2) { if (e.KeyCode is Keycode.DpadLeft or Keycode.DpadRight) e.Handled = true; return; }
            e.Handled = true;   // while lifted, ↑/↓ move the row instead of focus
            if (to < 0 || to >= _folders.ChildCount) return;
            var lp = row.LayoutParameters;
            _folders.RemoveViewAt(idx);
            _folders.AddView(row, to, lp);
            row.RequestFocus();
            lifted = true; Paint();
        };
        Paint();
        return row;
    }

    private void SaveOrder()
    {
        var ids = new List<Guid>();
        for (int i = 0; i < _folders.ChildCount; i++)
            if (_folders.GetChildAt(i)?.Tag?.ToString() is { } t && Guid.TryParse(t, out var g)) ids.Add(g);
        TvPrefs.SectionOrder = ids;
        _status.Text = TvL.T("tv.order_saved", "Порядок папок сохранён — он уже на главной.", "Folder order saved — Home uses it now.");
        TvHomeView.SectionsChanged = true;
    }

    // ── interface ────────────────────────────────────────────────────────────
    private async Task PickLanguageAsync()
    {
        var choice = await _page.DisplayActionSheet(TvL.T("settings.language_label", "Язык интерфейса", "Interface language"),
            TvL.T("common.btn_cancel", "Отмена", "Cancel"), null, Languages.Select(l => l.Name).ToArray());
        var picked = Languages.FirstOrDefault(l => l.Name == choice);
        if (picked.Code is null) return;
        try
        {
            await _data.Api.UpdateMyPreferencesAsync(new UpdatePreferencesRequest(
                AccentHue: null, BackdropEnabled: null, BackdropBlurPx: null, BackdropBrightness: null,
                BackdropIntervalSec: null, TvMode: null, AudioPreferredLanguage: null, SubtitlePreferredLanguage: null,
                SubtitleSize: null, DefaultVolume: null, AudioPassthrough: null, NormalizeVolume: null, Language: picked.Code));
            await TvL.LoadAsync(_data.Http, picked.Code);
            TvRoot.Go(TvRoot.Home());   // pages bake strings at construction — rebuild the shell
        }
        catch { _status.Text = TvL.T("profile.language_error", "Не удалось сменить язык", "Couldn't change the language"); }
    }

    private static (string Label, float Scale)[] Scales => new[]
    {
        (TvL.T("profile.scale_small", "Маленький", "Small"), 0.85f),
        (TvL.T("profile.scale_normal", "Обычный", "Normal"), 1f),
        (TvL.T("profile.scale_large", "Большой", "Large"), 1.2f),
    };

    private static float CurrentScale()
    {
        try
        {
            var sp = Android.App.Application.Context.GetSharedPreferences(MainActivity.TvPrefsName, FileCreationMode.Private);
            return sp?.GetFloat(MainActivity.UiScalePref, 1f) ?? 1f;
        }
        catch { return 1f; }
    }

    private void PaintScale()
    {
        var cur = CurrentScale();
        var label = Scales.FirstOrDefault(s => Math.Abs(s.Scale - cur) < 0.01f).Label ?? Scales[1].Label;
        _scaleBtn.Text = TvL.T("profile.ui_scale", "Масштаб", "Scale") + ":  " + label;
    }

    private async Task PickScaleAsync()
    {
        var choice = await _page.DisplayActionSheet(TvL.T("profile.ui_scale", "Масштаб интерфейса", "Interface scale"),
            TvL.T("common.btn_cancel", "Отмена", "Cancel"), null, Scales.Select(s => s.Label).ToArray());
        var picked = Scales.FirstOrDefault(s => s.Label == choice);
        if (picked.Label is null) return;
        try
        {
            // Same start-then-kill restart as before: MAUI caches dp→px density per process.
            var ctx = Android.App.Application.Context;
            ctx.GetSharedPreferences(MainActivity.TvPrefsName, FileCreationMode.Private)?.Edit()?.PutFloat(MainActivity.UiScalePref, picked.Scale)?.Commit();
            var intent = ctx.PackageManager?.GetLaunchIntentForPackage(ctx.PackageName ?? "");
            if (intent is not null)
            {
                intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
                ctx.StartActivity(intent);
            }
            Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
        }
        catch { _status.Text = TvL.T("tv.scale_failed", "Не удалось применить масштаб", "Couldn't apply the scale"); }
    }
}
#endif
