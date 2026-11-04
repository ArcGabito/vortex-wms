using System.Globalization;
using System.Text;
using VortexWms.Core.Domain;

namespace VortexWms.Services;

public enum ToastKind
{
    Success,
    Error,
    Info,
}

public sealed record Toast(Guid Id, ToastKind Kind, string Message);

/// <summary>Avisos breves en la esquina inferior derecha.</summary>
public sealed class Toasts
{
    private readonly List<Toast> _items = [];

    public event Action? Changed;
    public IReadOnlyList<Toast> Items => _items;

    public void Success(string message) => Show(ToastKind.Success, message);
    public void Error(string message) => Show(ToastKind.Error, message);
    public void Info(string message) => Show(ToastKind.Info, message);

    public void Dismiss(Guid id)
    {
        _items.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }

    private void Show(ToastKind kind, string message)
    {
        var toast = new Toast(Guid.NewGuid(), kind, message);
        _items.Add(toast);
        Changed?.Invoke();
        _ = Task.Delay(kind == ToastKind.Error ? 7000 : 4000).ContinueWith(_ => Dismiss(toast.Id), TaskScheduler.Default);
    }
}

/// <summary>Exporta archivos: en escritorio los guarda en Documentos/Vortex WMS y los abre; en móvil, los comparte.</summary>
public sealed class FileExporter
{
    public async Task<string> ExportAsync(string fileName, string content, string contentType = "text/csv")
    {
        var cache = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllTextAsync(cache, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
#if WINDOWS || MACCATALYST
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Vortex WMS");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, fileName);
        File.Copy(cache, target, overwrite: true);
        await Launcher.Default.OpenAsync(new OpenFileRequest(fileName, new ReadOnlyFile(target)));
        return target;
#else
        await Share.Default.RequestAsync(new ShareFileRequest { Title = fileName, File = new ShareFile(cache, contentType) });
        return cache;
#endif
    }
}

/// <summary>Formatos y textos de la interfaz (es-PE).</summary>
public static class Fmt
{
    /// <summary>Cultura de la interfaz: idioma del usuario y país de la empresa (<see cref="SetRegion"/>).</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("es-PE");

    public static string Language { get; private set; } = VortexApi.Suite.Regional.Languages.Spanish;

    /// <summary>Zona horaria de la empresa (IANA): las fechas se muestran en su hora, no en la del equipo.</summary>
    public static string? TimeZone { get; private set; }

    public static void SetRegion(string language, string? countryCode, string? timeZone)
    {
        Language = VortexApi.Suite.Regional.Languages.Normalize(language);
        Culture = VortexApi.Suite.Regional.RegionalSettings.CultureFor(Language, countryCode);
        TimeZone = timeZone;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    public static string Qty(decimal value) => value.ToString(value % 1 == 0 ? "#,0" : "#,0.##", Culture);

    /// <summary>Cifras de las tarjetas: 1,284 · 12.9 mil · 4.2 M.</summary>
    public static string Compact(decimal value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.#", Culture) + " M",
        >= 10_000 => (value / 1_000).ToString("0.#", Culture) + " mil",
        _ => Qty(Math.Round(value, 2)),
    };

    /// <summary>"1 categoría", "3 categorías".</summary>
    public static string Count(int value, string singular, string plural) => $"{Qty(value)} {(value == 1 ? singular : plural)}";

    public static string Money(decimal value, string currency) => VortexApi.Suite.Money.Currencies.Get(currency).Format(value, Culture);

    public static string MoneyCompact(decimal value, string currency) => $"{Symbol(currency)} {Compact(value)}";

    public static string Symbol(string currency) => VortexApi.Suite.Money.Currencies.Get(currency).Symbol;

    public static DateTimeOffset Local(DateTimeOffset value) =>
        TimeZone is null ? value.ToLocalTime() : VortexApi.Suite.Regional.RegionalSettings.ToCompanyTime(value, TimeZone);

    public static string Date(DateTimeOffset value) => Local(value).ToString("d MMM yyyy", Culture);
    public static string DateTime(DateTimeOffset value) => Local(value).ToString("d MMM yyyy, HH:mm", Culture);

    public static string Ago(DateTimeOffset value)
    {
        var span = DateTimeOffset.Now - value;
        return span.TotalSeconds < 60 ? "hace un momento"
            : span.TotalMinutes < 60 ? $"hace {(int)span.TotalMinutes} min"
            : span.TotalHours < 24 ? $"hace {(int)span.TotalHours} h"
            : Date(value);
    }

    public static string Initials(string? text)
    {
        var words = (text ?? "?").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}",
        };
    }

    public static string MovementKind(string kind) => kind switch
    {
        MovementKinds.Receipt => "Recepción",
        MovementKinds.Adjustment => "Ajuste",
        MovementKinds.TransferOut => "Transferencia (salida)",
        MovementKinds.TransferIn => "Transferencia (entrada)",
        MovementKinds.Shipment => "Despacho",
        MovementKinds.Sale => "Venta POS",
        MovementKinds.SaleVoid => "Venta anulada",
        MovementKinds.SaleReturn => "Devolución",
        _ => kind,
    };

    public static string Reason(string? reason) => reason switch
    {
        AdjustmentReasons.Count => "Conteo físico",
        AdjustmentReasons.Damage => "Merma / daño",
        AdjustmentReasons.Loss => "Pérdida / robo",
        AdjustmentReasons.Found => "Sobrante encontrado",
        AdjustmentReasons.Correction => "Corrección",
        null => "",
        _ => reason,
    };

    public static string OrderStatus(string status) => status switch
    {
        OrderStatuses.Pending => "Pendiente",
        OrderStatuses.Picking => "En preparación",
        OrderStatuses.Dispatched => "Despachado",
        OrderStatuses.Cancelled => "Cancelado",
        _ => status,
    };

    public static string ReceiptStatus(string status) => status switch
    {
        ReceiptStatuses.Expected => "Por recibir",
        ReceiptStatuses.Received => "Recibida",
        ReceiptStatuses.Cancelled => "Anulada",
        _ => status,
    };

    public static string AuditAction(string action) => action switch
    {
        "stock.adjusted" => "Ajuste de stock",
        "document.cancelled" => "Pedido cancelado",
        "document.voided" => "Recepción anulada",
        _ => action,
    };

    public static string SiteKind(string kind) => kind switch
    {
        "Warehouse" => "Almacén",
        "Store" => "Tienda",
        "Office" => "Oficina",
        _ => "Otro",
    };
}
