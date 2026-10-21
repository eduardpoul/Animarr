namespace Animarr.App;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
	}

	/// <summary>System Back. On Android 13+ it lands here rather than in
	/// MainActivity.OnBackPressed, and MAUI's default sent the app to the
	/// background — Back from Settings looked like the app closing. Route it
	/// through the WebView's own history instead (see
	/// MainActivity.TryHandleWebViewBack).</summary>
	protected override bool OnBackButtonPressed()
	{
#if ANDROID
		if (Platform.CurrentActivity is MainActivity activity && activity.TryHandleWebViewBack())
			return true;
#endif
		return base.OnBackButtonPressed();
	}
}
