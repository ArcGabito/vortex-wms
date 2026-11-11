using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace VortexWms.Core.Labels;

public static class LabelKinds
{
    public const string Item = "item";
    public const string Location = "location";
    public const string Package = "package";
}

/// <summary>
/// Una etiqueta (fase 6c): título, línea secundaria, código de barras (Code 128) y, opcionalmente, un texto grande (el
/// código de la ubicación, "2/3" del bulto) y un pie (precio, peso).
/// </summary>
public sealed record LabelData(string Kind, string Title, string? Subtitle, string Barcode, string? Big = null, string? Footer = null);

/// <summary>Tamaño de la etiqueta en milímetros (las comunes: 100×50, 100×150 para envíos, 50×25 para artículos).</summary>
public sealed record LabelSize(double WidthMm, double HeightMm)
{
    public static readonly LabelSize Small = new(50, 25);
    public static readonly LabelSize Medium = new(100, 50);
    public static readonly LabelSize Shipping = new(100, 150);

    public static readonly IReadOnlyList<LabelSize> All = [Small, Medium, Shipping];

    public string Key => $"{WidthMm:0}x{HeightMm:0}";

    public static LabelSize Parse(string? key) => All.FirstOrDefault(s => s.Key == key) ?? Medium;
}

/// <summary>Code 128 (juego B y C para tramos numéricos pares) con su dígito de control, para dibujar las barras.</summary>
public static class Code128
{
    // Anchos de barra/espacio de cada símbolo (0–106); el 106 es el de parada (7 módulos de 4 barras y 3 espacios... 13 módulos).
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213", "221312", "231212", "112232",
        "122132", "122231", "113222", "123122", "123221", "223211", "221132", "221231", "213212", "223112", "312131", "311222", "321122",
        "321221", "312212", "322112", "322211", "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311",
        "211313", "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331", "231131", "213113",
        "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111", "314111", "221411", "431111", "111224", "111422",
        "121124", "121421", "141122", "141221", "112214", "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111",
        "241112", "134111", "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141", "214121",
        "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141", "114131", "311141", "411131", "211412",
        "211214", "211232", "2331112",
    ];

    private const int StartB = 104, StartC = 105, CodeB = 100, CodeC = 99, Stop = 106;

    /// <summary>Los símbolos (con inicio, control y parada) de <paramref name="text"/> (ASCII 32–126).</summary>
    public static IReadOnlyList<int> Encode(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Any(ch => ch < 32 || ch > 126))
            throw new ArgumentException("Code 128 admite texto ASCII imprimible.", nameof(text));
        var codes = new List<int>();
        var i = 0;
        bool? setC = null;
        while (i < text.Length)
        {
            var digits = 0;
            while (i + digits < text.Length && char.IsAsciiDigit(text[i + digits]))
                digits++;
            // Un tramo de 4 o más dígitos (o 2+ al final) va en el juego C, de a pares.
            var useC = digits >= 4 || (digits >= 2 && i + digits == text.Length && digits % 2 == 0);
            if (useC)
            {
                if (setC != true)
                    codes.Add(setC is null ? StartC : CodeC);
                setC = true;
                var pairs = digits / 2;
                for (var p = 0; p < pairs; p++, i += 2)
                    codes.Add((text[i] - '0') * 10 + (text[i + 1] - '0'));
            }
            else
            {
                if (setC != false)
                    codes.Add(setC is null ? StartB : CodeB);
                setC = false;
                codes.Add(text[i] - 32);
                i++;
            }
        }
        var check = codes[0];
        for (var k = 1; k < codes.Count; k++)
            check += codes[k] * k;
        codes.Add(check % 103);
        codes.Add(Stop);
        return codes;
    }

    /// <summary>Los módulos alternados barra/espacio (empieza en barra) de <paramref name="text"/>.</summary>
    public static IReadOnlyList<int> Modules(string text) =>
        Encode(text).SelectMany(c => Patterns[c].Select(ch => ch - '0')).ToList();
}

/// <summary>Etiquetas en ZPL II para impresoras Zebra (203 dpi, 8 puntos por mm).</summary>
public static class Zpl
{
    private const double DotsPerMm = 8;

    public static string Render(IEnumerable<LabelData> labels, LabelSize size, int copies = 1)
    {
        var w = (int)Math.Round(size.WidthMm * DotsPerMm);
        var h = (int)Math.Round(size.HeightMm * DotsPerMm);
        var m = (int)(3 * DotsPerMm);
        var small = size.HeightMm < 40;
        var sb = new StringBuilder();
        foreach (var l in labels)
        {
            sb.Append("^XA^CI28");
            sb.Append(CultureInfo.InvariantCulture, $"^PW{w}^LL{h}^LH0,0");
            var y = m;
            var title = small ? 22 : 34;
            sb.Append(CultureInfo.InvariantCulture, $"^FO{m},{y}^A0N,{title},{title}^FB{w - 2 * m},2,0,L^FD{Escape(l.Title)}^FS");
            y += title * (small ? 1 : 2) + 4;
            if (l.Subtitle is { Length: > 0 } sub)
            {
                var s = small ? 18 : 24;
                sb.Append(CultureInfo.InvariantCulture, $"^FO{m},{y}^A0N,{s},{s}^FB{w - 2 * m},1,0,L^FD{Escape(sub)}^FS");
                y += s + 6;
            }
            if (l.Big is { Length: > 0 } big && !small)
            {
                var b = Math.Min(90, h / 5);
                sb.Append(CultureInfo.InvariantCulture, $"^FO{m},{y}^A0N,{b},{b}^FB{w - 2 * m},1,0,C^FD{Escape(big)}^FS");
                y += b + 8;
            }
            var footer = l.Footer is { Length: > 0 } ? (small ? 18 : 26) : 0;
            var barHeight = Math.Max(30, h - y - m - footer - (small ? 22 : 30));
            var module = small ? 2 : 3;
            sb.Append(CultureInfo.InvariantCulture, $"^FO{m},{y}^BY{module}^BCN,{barHeight},Y,N,N^FD{Escape(l.Barcode)}^FS");
            if (footer > 0)
                sb.Append(CultureInfo.InvariantCulture, $"^FO{m},{h - m - footer}^A0N,{footer},{footer}^FB{w - 2 * m},1,0,R^FD{Escape(l.Footer!)}^FS");
            if (copies > 1)
                sb.Append(CultureInfo.InvariantCulture, $"^PQ{copies}");
            sb.Append("^XZ\n");
        }
        return sb.ToString();
    }

    /// <summary>ZPL usa ^ y ~ como comandos: se cambian por guiones para que el texto no los dispare.</summary>
    private static string Escape(string text) => text.Replace('^', '-').Replace('~', '-');

    /// <summary>Envía el ZPL a una Zebra en red (puerto RAW 9100).</summary>
    public static async Task SendAsync(string host, int port, string zpl, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(host, port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(zpl), timeout.Token);
        await stream.FlushAsync(timeout.Token);
    }
}
