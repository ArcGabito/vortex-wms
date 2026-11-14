namespace VortexWms.Services;

/// <summary>Impresora Bluetooth emparejada con el equipo (<c>Id</c>: el de Windows o la dirección MAC en Android).</summary>
public sealed record BluetoothPrinterInfo(string Id, string Name);

/// <summary>
/// Envía ZPL a una Zebra por Bluetooth clásico con el perfil de puerto serie (SPP/RFCOMM), como lo traen las Zebra
/// portátiles y de escritorio. La impresora se empareja antes en el sistema. En Windows usa el servicio RFCOMM de WinRT; en
/// Android, un socket RFCOMM con el UUID de SPP. iOS y Mac no lo admiten: Apple solo permite Bluetooth clásico a accesorios
/// MFi (ahí se imprime por red o en PDF).
/// </summary>
public sealed class BluetoothPrinter
{
    /// <summary>UUID estándar del perfil de puerto serie (SPP).</summary>
    public const string SerialPortProfile = "00001101-0000-1000-8000-00805F9B34FB";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsAndroid();

    /// <summary>Las impresoras (o cualquier equipo con puerto serie) emparejadas con este equipo.</summary>
    public async Task<IReadOnlyList<BluetoothPrinterInfo>> GetPairedAsync()
    {
#if WINDOWS
        var selector = Windows.Devices.Bluetooth.Rfcomm.RfcommDeviceService.GetDeviceSelector(
            Windows.Devices.Bluetooth.Rfcomm.RfcommServiceId.SerialPort);
        var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
        return devices.Select(d => new BluetoothPrinterInfo(d.Id, string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
#elif ANDROID
        var adapter = await AdapterAsync();
        return (adapter.BondedDevices ?? [])
            .Where(d => d.Address is not null)
            .Select(d => new BluetoothPrinterInfo(d.Address!, string.IsNullOrWhiteSpace(d.Name) ? d.Address! : d.Name!))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
#else
        await Task.CompletedTask;
        return [];
#endif
    }

    /// <summary>Envía los bytes (ZPL en UTF-8) a la impresora emparejada.</summary>
    public async Task SendAsync(string id, byte[] data, CancellationToken ct = default)
    {
#if WINDOWS
        var service = await Windows.Devices.Bluetooth.Rfcomm.RfcommDeviceService.FromIdAsync(id)
            ?? throw new InvalidOperationException("La impresora no está disponible: revisa que esté encendida y emparejada.");
        using (service)
        using (var socket = new Windows.Networking.Sockets.StreamSocket())
        {
            await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName).AsTask(ct);
            using var writer = new Windows.Storage.Streams.DataWriter(socket.OutputStream);
            writer.WriteBytes(data);
            await writer.StoreAsync().AsTask(ct);
            await writer.FlushAsync().AsTask(ct);
            writer.DetachStream();
        }
#elif ANDROID
        var adapter = await AdapterAsync();
        var device = adapter.GetRemoteDevice(id)
            ?? throw new InvalidOperationException("La impresora no está emparejada con este equipo.");
        adapter.CancelDiscovery();   // buscar equipos hace lenta la conexión
        using var socket = device.CreateRfcommSocketToServiceRecord(Java.Util.UUID.FromString(SerialPortProfile))
            ?? throw new InvalidOperationException("La impresora no ofrece el perfil de puerto serie.");
        await Task.Run(socket.Connect, ct);
        var output = socket.OutputStream ?? throw new InvalidOperationException("No se pudo abrir la conexión con la impresora.");
        await output.WriteAsync(data, ct);
        await output.FlushAsync(ct);
        // La Zebra recibe el trabajo completo antes de cerrar: se espera un poco para no cortar etiquetas largas.
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(data.Length / 20, 300, 3000)), ct);
        socket.Close();
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Este equipo no imprime por Bluetooth: usa una Zebra en red o el PDF.");
#endif
    }

#if ANDROID
    private static async Task<Android.Bluetooth.BluetoothAdapter> AdapterAsync()
    {
        // Android 12+ pide el permiso de conectar con equipos cercanos; antes basta el del manifiesto.
        if (OperatingSystem.IsAndroidVersionAtLeast(31) && await Permissions.RequestAsync<ConnectPermission>() != PermissionStatus.Granted)
            throw new InvalidOperationException("Sin permiso de Bluetooth no se puede imprimir: actívalo en los ajustes de la app.");
        var manager = Android.App.Application.Context.GetSystemService(Android.Content.Context.BluetoothService) as Android.Bluetooth.BluetoothManager;
        var adapter = manager?.Adapter ?? throw new InvalidOperationException("Este equipo no tiene Bluetooth.");
        if (!adapter.IsEnabled)
            throw new InvalidOperationException("El Bluetooth está apagado: enciéndelo e inténtalo de nuevo.");
        return adapter;
    }

    /// <summary>Solo el permiso de conectar con equipos emparejados (no hace falta buscar equipos ni la ubicación).</summary>
    private sealed class ConnectPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            [("android.permission.BLUETOOTH_CONNECT", true)];
    }
#endif
}
