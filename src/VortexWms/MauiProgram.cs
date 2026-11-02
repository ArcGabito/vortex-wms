using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using VortexApi.Client.Sessions;
using VortexWms.Services;

namespace VortexWms;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        CultureInfo.DefaultThreadCurrentCulture = Fmt.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Fmt.Culture;

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        using (var settings = Assembly.GetExecutingAssembly().GetManifestResourceStream("VortexWms.appsettings.json"))
        {
            if (settings is not null)
                builder.Configuration.AddJsonStream(settings);
        }

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<HubSettings>();
        builder.Services.AddSingleton<ISessionStore, SecureStorageSessionStore>();
        builder.Services.AddSingleton<AppSession>();
        builder.Services.AddSingleton<Toasts>();
        builder.Services.AddSingleton<FileExporter>();

#if WINDOWS
        // Botones de la barra de título (minimizar, maximizar, cerrar) en tinta sobre la barra blanca de App.CreateWindow;
        // si no, con el tema oscuro de Windows quedan blancos sobre blanco.
        builder.ConfigureLifecycleEvents(events => events.AddWindows(windows => windows.OnWindowCreated(window =>
        {
            var bar = window.AppWindow.TitleBar;
            var ink = Windows.UI.Color.FromArgb(255, 0x1C, 0x12, 0x0D);
            bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            bar.ButtonForegroundColor = ink;
            bar.ButtonHoverForegroundColor = ink;
            bar.ButtonPressedForegroundColor = ink;
            bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 0xF6, 0xF4, 0xF2);
            bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 0xEC, 0xE8, 0xE4);
            bar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 0x9A, 0x90, 0x8A);
        })));
#endif

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
