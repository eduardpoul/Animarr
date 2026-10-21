using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Microsoft.Maui.Controls.Shapes;

namespace Animarr.App;

/// <summary>
/// Native TV PIN keypad — D-pad focusable 3×4 grid (1–9, OK, 0, ⌫), plus the
/// remote's number keys (routed from MainActivity.OnKeyDown). Three modes, all
/// verified / stored by the server (per user per device):
///
///   • Verify — sign in as a picked profile (POST /api/auth/switch-user).
///     "Forgot PIN?" re-pairs with ResetPin, the TV's stand-in for the
///     password sign-in. Back / Cancel returns to the picker.
///   • Setup — the blocking "Set a PIN" step after a sign-in: 4–6 digits,
///     entered twice, no cancel; "Sign out" is the only way out. It's a root
///     page, so the remote's Back closes the app rather than skipping it.
///   • Change — current PIN, new PIN, repeat (POST /api/auth/device/pin).
///
/// Built in code (no XAML) — it's a fixed small layout.
/// </summary>
public sealed class TvPinPage : ContentPage
{
    public enum PinMode { Verify, Setup, Change }

    // Remote number keys: MainActivity forwards KEYCODE_0..9 / DEL here while
    // a keypad is on screen (Border keys don't consume them).
    private static TvPinPage? _active;

    public static bool HandleGlobalKey(int keyCode)
    {
        var page = _active;
        if (page is null) return false;
        const int Key0 = 7, Key9 = 16, KeyDel = 67, Num0 = 144, Num9 = 153;
        int? value = keyCode switch
        {
            >= Key0 and <= Key9 => keyCode - Key0,
            >= Num0 and <= Num9 => keyCode - Num0,
            _ => null,
        };
        if (value is int v)
        {
            var digit = (char)('0' + v);
            page.Dispatcher.Dispatch(() => page.Push(digit));
            return true;
        }
        if (keyCode == KeyDel)
        {
            page.Dispatcher.Dispatch(page.Backspace);
            return true;
        }
        return false;
    }

    public static TvPinPage ForVerify(DeviceProfileDto target, bool? makeDefault, int min, int max)
        => new(PinMode.Verify, target, makeDefault, min, max);

    public static TvPinPage ForSetup(bool makeDefault, string? userName = null)
        => new(PinMode.Setup, null, makeDefault ? true : null, 4, 6, userName);

    public static TvPinPage ForChange(int min, int max)
        => new(PinMode.Change, null, null, min, max);

    private static readonly Color Bg     = Color.FromArgb("#1c1c1c");
    private static readonly Color Card   = Color.FromArgb("#2b2b2b");
    private static readonly Color Accent = Color.FromArgb("#bb5000");
    private static readonly Color Dim    = Color.FromArgb("#9a9a9a");
    private static readonly Color Warn   = Color.FromArgb("#e0908f");

    private readonly PinMode _mode;
    private readonly DeviceProfileDto? _target;
    private readonly bool? _makeDefault;
    private readonly int _min, _max;

    private string _buffer = "";
    private bool _busy;
    private int _step;            // Setup: 0 enter, 1 repeat · Change: 0 current, 1 new, 2 repeat
    private string? _firstPin;    // Setup / Change: the PIN to repeat
    private string? _currentPin;  // Change: the old PIN
    private readonly string? _userName;   // Setup: whose PIN ("Set a PIN for Anna")

    private readonly Label _title = new() { TextColor = Colors.White, FontFamily = "sans-serif-medium", FontAttributes = FontAttributes.Bold, FontSize = 30, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label _sub   = new() { TextColor = Color.FromArgb("#c8c8c8"), FontFamily = "sans-serif", FontSize = 15, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label _error = new() { TextColor = Warn, FontFamily = "sans-serif", FontSize = 14, HorizontalTextAlignment = TextAlignment.Center, IsVisible = false };
    private readonly HorizontalStackLayout _dots = new() { Spacing = 14, HorizontalOptions = LayoutOptions.Center };
    private readonly Label _okLabel = new() { Text = "OK", TextColor = Colors.White, FontFamily = "sans-serif", FontAttributes = FontAttributes.Bold, FontSize = 18, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
    private Border? _firstKey;

    private TvPinPage(PinMode mode, DeviceProfileDto? target, bool? makeDefault, int min, int max, string? userName = null)
    {
        _mode = mode;
        _target = target;
        _makeDefault = makeDefault;
        _userName = userName;
        _min = min;
        _max = max;
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Bg;
        Content = BuildLayout();
        ApplyStep();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _active = this;
        // Land the D-pad on "1" once the native views exist.
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), FocusFirstKey);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_active == this) _active = null;
    }

    // ── Layout ─────────────────────────────────────────────────────────────

    private View BuildLayout()
    {
        var eyebrow = new Label
        {
            Text = "PIN",
            TextColor = Color.FromArgb("#f49157"), FontFamily = "sans-serif", FontSize = 12,
            CharacterSpacing = 3, HorizontalTextAlignment = TextAlignment.Center,
        };

        var keys = new Grid
        {
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) },
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) },
            ColumnSpacing = 10, RowSpacing = 10,
            HorizontalOptions = LayoutOptions.Center,
        };
        for (int n = 1; n <= 9; n++)
        {
            var digit = (char)('0' + n);
            var key = Key(new Label { Text = n.ToString() }, () => Push(digit));
            if (n == 1) _firstKey = key;
            keys.Add(key, (n - 1) % 3, (n - 1) / 3);
        }
        keys.Add(Key(_okLabel, () => _ = SubmitAsync(), accent: true), 0, 3);
        keys.Add(Key(new Label { Text = "0" }, () => Push('0')), 1, 3);
        keys.Add(Key(new Label { Text = "⌫" }, Backspace), 2, 3);

        var actions = new HorizontalStackLayout { Spacing = 12, HorizontalOptions = LayoutOptions.Center };
        switch (_mode)
        {
            case PinMode.Verify:
                actions.Children.Add(LinkButton(TvL.T("pin.forgot", "Забыли PIN?", "Forgot PIN?"),
                    async () => await Navigation.PushAsync(new PairingPage(resetPin: true))));
                actions.Children.Add(LinkButton(TvL.T("pin.cancel", "Отмена", "Cancel"),
                    async () => await Navigation.PopAsync()));
                break;
            case PinMode.Change:
                actions.Children.Add(LinkButton(TvL.T("pin.forgot", "Забыли PIN?", "Forgot PIN?"),
                    async () => await Navigation.PushAsync(new PairingPage(resetPin: true))));
                actions.Children.Add(LinkButton(TvL.T("pin.cancel", "Отмена", "Cancel"),
                    async () => await Navigation.PopAsync()));
                break;
            case PinMode.Setup:
                actions.Children.Add(LinkButton(TvL.T("profile.sign_out", "Выйти", "Sign out"),
                    async () => await TvAuthFlow.SignOutAsync(), danger: true));
                break;
        }

        // 16:9 screen: text + actions on the left, the keypad on the right, so
        // everything fits a 540dp-high TV viewport without scrolling.
        var text = new VerticalStackLayout
        {
            Spacing = 14,
            WidthRequest = 460,
            VerticalOptions = LayoutOptions.Center,
            Children = { eyebrow, _title, _sub, _dots, _error, actions },
        };
        eyebrow.HorizontalTextAlignment = TextAlignment.Start;
        _title.HorizontalTextAlignment  = TextAlignment.Start;
        _sub.HorizontalTextAlignment    = TextAlignment.Start;
        _error.HorizontalTextAlignment  = TextAlignment.Start;
        _dots.HorizontalOptions         = LayoutOptions.Start;
        actions.HorizontalOptions       = LayoutOptions.Start;
        if (_mode == PinMode.Setup)
        {
            text.Children.Add(new Label
            {
                Text = TvL.T("setpin.why",
                    "У каждого профиля свой PIN на этом устройстве, поэтому никто другой не откроет ваш.",
                    "Each profile has its own PIN on this device, so no one else can open yours."),
                TextColor = Dim, FontFamily = "sans-serif", FontSize = 12,
            });
        }

        var layout = new Grid
        {
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Auto) },
            ColumnSpacing = 64,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        layout.Add(text, 0, 0);
        keys.VerticalOptions = LayoutOptions.Center;
        layout.Add(keys, 1, 0);
        return new Grid { Padding = new Thickness(40, 24), Children = { layout } };
    }

    private static Border Key(Label label, Action act, bool accent = false)
    {
        label.TextColor = Colors.White;
        label.FontFamily ??= "ArchivoBlack";
        if (label.FontSize < 18) label.FontSize = 24;
        label.HorizontalOptions = LayoutOptions.Center;
        label.VerticalOptions = LayoutOptions.Center;
        var key = new Border
        {
            WidthRequest = 96, HeightRequest = 60,
            StrokeThickness = 0,
            BackgroundColor = accent ? Accent : Card,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Content = label,
        };
        key.Behaviors.Add(new TvFocusBehavior { Radius = 12, Command = new Command(act) });
        return key;
    }

    private static Border LinkButton(string text, Action act, bool danger = false)
    {
        var b = new Border
        {
            StrokeThickness = danger ? 1 : 0,
            Stroke = danger ? Color.FromArgb("#3a2020") : null,
            BackgroundColor = danger ? Color.FromArgb("#1a1315") : Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Padding = new Thickness(18, 10),
            Content = new Label
            {
                Text = text, FontFamily = "sans-serif", FontSize = 15,
                TextColor = danger ? Warn : Color.FromArgb("#d6d6d6"),
            },
        };
        b.Behaviors.Add(new TvFocusBehavior { Radius = 10, Command = new Command(act) });
        return b;
    }

    private void FocusFirstKey()
    {
#if ANDROID
        try { (_firstKey?.Handler?.PlatformView as global::Android.Views.View)?.RequestFocus(); }
        catch { }
#endif
    }

    // ── Steps ──────────────────────────────────────────────────────────────

    private void ApplyStep()
    {
        switch (_mode)
        {
            case PinMode.Verify:
                _title.Text = TvL.F("pin.title_for_user", "Войти как {0}", "Sign in as {0}", _target?.Name ?? "");
                _sub.Text   = TvL.T("pin.subtitle", "Введите PIN", "Enter your PIN");
                break;
            case PinMode.Setup when _step == 0:
                _title.Text = string.IsNullOrWhiteSpace(_userName)
                    ? TvL.T("setpin.new_title", "Введите новый PIN", "Enter a new PIN")
                    : TvL.F("setpin.title_fmt", "Задайте PIN для {0}", "Set a PIN for {0}", _userName);
                _sub.Text   = TvL.T("setpin.sub",
                    "4–6 цифр. Он понадобится, чтобы открыть ваш профиль на этом устройстве.",
                    "4–6 digits. You'll need it to open your profile on this device.");
                break;
            case PinMode.Change when _step == 0:
                _title.Text = TvL.T("profile.field_current_pin", "Текущий PIN", "Current PIN");
                _sub.Text   = TvL.T("pin.subtitle", "Введите PIN", "Enter your PIN");
                break;
            case PinMode.Change when _step == 1:
                _title.Text = TvL.T("setpin.new_title", "Введите новый PIN", "Enter a new PIN");
                _sub.Text   = TvL.F("profile.field_new_pin_fmt", "Новый PIN ({0}–{1} цифр)", "New PIN ({0}–{1} digits)", _min, _max);
                break;
            default: // repeat step of Setup / Change
                _title.Text = TvL.T("setpin.repeat_title", "Повторите PIN", "Repeat your PIN");
                _sub.Text   = TvL.T("setpin.repeat_sub",
                    "Введите тот же PIN ещё раз для подтверждения.", "Enter the same PIN again to confirm.");
                break;
        }
        RenderDots();
    }

    private void RenderDots()
    {
        _dots.Children.Clear();
        var count = Math.Max(_min, Math.Min(_max, _buffer.Length));
        for (int i = 0; i < count; i++)
        {
            _dots.Children.Add(new Label
            {
                Text = i < _buffer.Length ? "●" : "○",
                TextColor = i < _buffer.Length ? Accent : Color.FromArgb("#8f8f8f"),
                FontSize = 22,
            });
        }
        _okLabel.Opacity = _buffer.Length >= _min ? 1 : 0.45;
    }

    private void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void Push(char digit)
    {
        if (_busy || _buffer.Length >= _max) return;
        _buffer += digit;
        ShowError(null);
        RenderDots();
        if (_buffer.Length == _max) _ = SubmitAsync();
    }

    private void Backspace()
    {
        if (_busy || _buffer.Length == 0) return;
        _buffer = _buffer[..^1];
        RenderDots();
    }

    private void Restart(string? error)
    {
        _buffer = "";
        _step = 0;
        _firstPin = null;
        _currentPin = null;
        ApplyStep();
        ShowError(error);
    }

    private async Task SubmitAsync()
    {
        if (_busy || _buffer.Length < _min) return;
        var pin = _buffer;

        switch (_mode)
        {
            case PinMode.Verify:
                await VerifyAsync(pin);
                return;

            case PinMode.Setup when _step == 0:
            case PinMode.Change when _step == 1:
                _firstPin = pin;
                _step++;
                _buffer = "";
                ApplyStep();
                return;

            case PinMode.Change when _step == 0:
                _currentPin = pin;
                _step = 1;
                _buffer = "";
                ApplyStep();
                return;
        }

        // Repeat step of Setup / Change.
        if (pin != _firstPin)
        {
            Restart(TvL.T("setpin.mismatch", "PIN не совпали — начните заново.", "The PINs didn't match — start again."));
            return;
        }
        await SaveAsync(pin);
    }

    private async Task VerifyAsync(string pin)
    {
        if (_target is null) return;
        _busy = true;
        var result = await TvAuthFlow.Api.SwitchUserAsync(new SwitchUserRequest(_target.Id, pin));
        if (result.Ok)
        {
            await TvAuthFlow.ApplyDefaultAsync(_makeDefault);
            await TvAuthFlow.AfterSignInAsync();
            return;
        }
        _busy = false;
        _buffer = "";
        RenderDots();
        ShowError(TvAuthFlow.PinError(result, _min, _max));
    }

    private async Task SaveAsync(string pin)
    {
        _busy = true;
        var result = await TvAuthFlow.Api.SetDevicePinAsync(new SetDevicePinRequest(pin, _currentPin));
        if (result.Ok || (_mode == PinMode.Setup && result.Status == PinAuthStatus.WrongPin))
        {
            if (_mode == PinMode.Change)
            {
                await DisplayAlert(
                    TvL.T("profile.pin_change_title", "Сменить PIN", "Change PIN"),
                    TvL.T("profile.pin_toast_changed", "PIN изменён", "PIN changed"),
                    "OK");
                await Navigation.PopAsync();
                return;
            }
            // Setup done: honour a "Sign in by default" chosen in the picker.
            await TvAuthFlow.ApplyDefaultAsync(_makeDefault);
            TvRoot.Go(TvRoot.Home());
            return;
        }
        _busy = false;
        Restart(result.Status is PinAuthStatus.WrongPin or PinAuthStatus.Locked or PinAuthStatus.Invalid
            ? TvAuthFlow.PinError(result, _min, _max)
            : TvL.T("setpin.err_save", "Не удалось сохранить PIN — попробуйте ещё раз.", "Couldn't save the PIN — try again."));
    }
}
