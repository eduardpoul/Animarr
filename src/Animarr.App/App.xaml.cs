namespace Animarr.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// POC: boot straight into the native CollectionView catalog to A/B its
		// scroll vs the Blazor-WebView grid. Swap back to new MainPage() to
		// restore the real app.
		return new Window(new NavigationPage(new CatalogNativePage())) { Title = "Animarr.App" };
	}
}
