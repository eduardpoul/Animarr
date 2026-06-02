namespace Animarr.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// Device-gated shell. TV (Leanback / UI_MODE_TYPE_TELEVISION) boots the
		// native CollectionView catalog — tuned for 10-foot + D-pad, and the
		// reason the native UI exists (weak-GPU WebView scroll lag). Phone /
		// tablet keep the mature, responsive Blazor app (touch-first, every
		// screen) via MainPage — they never had the scroll problem. One APK,
		// the right front-end per device.
		Page root = DeviceKind.IsTv
			? new NavigationPage(new CatalogNativePage())
			: new MainPage();
		return new Window(root) { Title = "Animarr.App" };
	}
}
