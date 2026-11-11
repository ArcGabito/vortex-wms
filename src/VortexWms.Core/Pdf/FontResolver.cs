using PdfSharp.Fonts;

namespace VortexWms.Core.Pdf;

/// <summary>Entrega a PDFsharp la Open Sans incrustada, para que el PDF salga igual en Windows y Android.</summary>
internal sealed class FontResolver : IFontResolver
{
    public const string Family = "Open Sans";

    private static readonly Lock Gate = new();

    /// <summary>Registra el resolvedor una sola vez; PDFsharp no permite cambiarlo después de usarlo.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            GlobalFontSettings.FontResolver ??= new FontResolver();
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? "OpenSans-Bold" : "OpenSans-Regular", mustSimulateBold: false, mustSimulateItalic: isItalic);

    public byte[]? GetFont(string faceName)
    {
        using var stream = typeof(FontResolver).Assembly.GetManifestResourceStream($"VortexWms.Fonts.{faceName}.ttf")
            ?? throw new InvalidOperationException($"No se encontró la fuente incrustada {faceName}.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
