using System.Globalization;
using System.Text;
using VortexWms.Core.Domain;

using static VortexWms.Services.Txt;

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
        VortexWms.Core.Texts.CoreText.Language = Language;
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
        >= 10_000 => (value / 1_000).ToString("0.#", Culture) + T("fmt.mil"),
        _ => Qty(Math.Round(value, 2)),
    };

    /// <summary>"1 categoría", "3 categorías".</summary>
    public static string Count(int value, string singular, string plural) => $"{Qty(value)} {(value == 1 ? singular : plural)}";

    public static string Money(decimal value, string currency) => VortexApi.Suite.Money.Currencies.Get(currency).Format(value, Culture);

    /// <summary>Monto abreviado desde 10 mil ("S/ 12.5 mil"); por debajo, con sus decimales ("S/ 18.50").</summary>
    public static string MoneyCompact(decimal value, string currency) =>
        Math.Abs(value) < 10_000 ? Money(value, currency) : $"{Symbol(currency)} {Compact(value)}";

    public static string Symbol(string currency) => VortexApi.Suite.Money.Currencies.Get(currency).Symbol;

    public static DateTimeOffset Local(DateTimeOffset value) =>
        TimeZone is null ? value.ToLocalTime() : VortexApi.Suite.Regional.RegionalSettings.ToCompanyTime(value, TimeZone);

    public static string Date(DateTimeOffset value) => Local(value).ToString("d MMM yyyy", Culture);
    public static string Date(DateOnly value) => value.ToString("d MMM yyyy", Culture);
    public static string DateTime(DateTimeOffset value) => Local(value).ToString("d MMM yyyy, HH:mm", Culture);

    public static string Ago(DateTimeOffset value)
    {
        var span = DateTimeOffset.Now - value;
        return span.TotalSeconds < 60 ? T("fmt.hace_un_momento")
            : span.TotalMinutes < 60 ? T("fmt.hace_min", (int)span.TotalMinutes)
            : span.TotalHours < 24 ? T("fmt.hace", (int)span.TotalHours)
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
        MovementKinds.Receipt => T("fmt.recepcion"),
        MovementKinds.Adjustment => T("fmt.ajuste"),
        MovementKinds.TransferOut => T("fmt.transferencia_salida"),
        MovementKinds.TransferIn => T("fmt.transferencia_entrada"),
        MovementKinds.Shipment => T("fmt.despacho"),
        MovementKinds.Sale => T("fmt.venta_pos"),
        MovementKinds.SaleVoid => T("fmt.venta_anulada"),
        MovementKinds.SaleReturn => T("fmt.devolucion"),
        MovementKinds.CustomerReturn => T("fmt.devolucion_cliente"),
        MovementKinds.SupplierReturn => T("fmt.devolucion_proveedor"),
        _ => kind,
    };

    public static string Reason(string? reason) => reason switch
    {
        AdjustmentReasons.Count => T("fmt.conteo_fisico"),
        AdjustmentReasons.Damage => T("fmt.merma_dano"),
        AdjustmentReasons.Loss => T("fmt.perdida_robo"),
        AdjustmentReasons.Found => T("fmt.sobrante_encontrado"),
        AdjustmentReasons.Correction => T("fmt.correccion"),
        AdjustmentReasons.Expired => T("fmt.vencido"),
        null => "",
        _ => reason,
    };

    public static string OrderStatus(string status) => status switch
    {
        OrderStatuses.Pending => T("fmt.pendiente"),
        OrderStatuses.Picking => T("fmt.en_preparacion"),
        OrderStatuses.Partial => T("fmt.parcial"),
        OrderStatuses.Dispatched => T("fmt.despachado"),
        OrderStatuses.Cancelled => T("fmt.cancelado"),
        _ => status,
    };

    public static string ReceiptStatus(string status) => status switch
    {
        ReceiptStatuses.Expected => T("fmt.por_recibir"),
        ReceiptStatuses.Received => T("fmt.recibida"),
        ReceiptStatuses.Cancelled => T("fmt.anulada"),
        _ => status,
    };

    public static string AuditAction(string action) => action switch
    {
        "stock.adjusted" => T("fmt.ajuste_de_stock"),
        "document.cancelled" => T("fmt.pedido_cancelado"),
        "document.voided" => T("fmt.recepcion_anulada"),
        _ => action,
    };

    public static string SiteKind(string kind) => kind switch
    {
        "Warehouse" => T("fmt.almacen"),
        "Store" => T("fmt.tienda"),
        "Office" => T("fmt.oficina"),
        _ => T("fmt.otro"),
    };
}
