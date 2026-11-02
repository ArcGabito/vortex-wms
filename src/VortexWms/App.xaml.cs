namespace VortexWms;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Light;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage())
        {
            Title = "Vortex WMS",
            // Barra de título del mismo blanco que la interfaz, para que la app no se vea enmarcada (escritorio).
            TitleBar = new TitleBar
            {
                Title = "Vortex WMS",
                BackgroundColor = Colors.White,
                ForegroundColor = Color.FromArgb("#1C120D"),
                HeightRequest = 36,
            },
            MinimumWidth = 380,
            MinimumHeight = 600,
            Width = 1360,
            Height = 860,
        };
}
