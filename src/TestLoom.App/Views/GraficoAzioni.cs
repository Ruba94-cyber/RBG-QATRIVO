using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using SkiaSharp;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TestLoom.App.Views;

/// <summary>Azioni comuni dei grafici (card Home, finestra grafico): apertura della finestra, dialogo nativo di
/// salvataggio PNG e copia negli appunti. Il PNG e' generato dal ViewModel; qui solo interazione con il sistema.</summary>
internal static class GraficoAzioni
{
    /// <summary>Apre la finestra del grafico (non modale: piu' finestre possono restare aperte insieme).</summary>
    internal static GraficoWindow ApriFinestra(Window? proprietaria, GraficoSnapshot snapshot, string filtri, Action<string>? registraEsportazione)
    {
        var finestra = new GraficoWindow(new GraficoWindowViewModel(snapshot, filtri), registraEsportazione);
        if (proprietaria != null) finestra.Show(proprietaria);
        else finestra.Show();
        return finestra;
    }

    /// <summary>Dialogo nativo di salvataggio PNG, come gli altri export. Null se l'utente annulla.</summary>
    internal static async Task<string?> ScegliPercorsoPngAsync(TopLevel finestra, string titolo, string nomeProposto)
    {
        var file = await finestra.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = titolo,
            SuggestedFileName = Path.GetFileNameWithoutExtension(nomeProposto),
            DefaultExtension = "png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } } },
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Copia il PNG negli appunti come immagine: Avalonia (Win32) scrive CF_DIBV5 e il formato PNG.</summary>
    internal static async Task CopiaPngAsync(TopLevel finestra, byte[] png)
    {
        var appunti = finestra.Clipboard ?? throw new InvalidOperationException("Appunti non disponibili in questa finestra.");
        if (OperatingSystem.IsWindows() && finestra.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero)
        {
            await CopiaPngWin32Async(hwnd, png);
            return;
        }

        using var ms = new MemoryStream(png);
        using var immagine = new Bitmap(ms);
        await appunti.SetBitmapAsync(immagine);
        await appunti.FlushAsync();
    }

    /// <summary>Windows 11 clipboard history expects a real CF_DIBV5 bitmap. Avalonia 12's SetBitmapAsync has a
    /// Windows regression that can resize/corrupt alpha; publish a top-down PNG payload plus a standards-compliant
    /// bottom-up DIBV5 directly so both image-aware paste targets and Win+V receive the same transparent pixels.</summary>
    private static async Task CopiaPngWin32Async(IntPtr hwnd, byte[] png)
    {
        var dib = CreaDibV5(png);
        var pngFormat = RegisterClipboardFormat("PNG");
        var dibHandle = CreaMemoriaGlobale(dib);
        var pngHandle = pngFormat != 0 ? CreaMemoriaGlobale(png) : IntPtr.Zero;
        try
        {
            await ApriAppuntiAsync(hwnd);
            try
            {
                if (!EmptyClipboard()) throw ErroreWin32("Svuotamento appunti non riuscito");
                if (SetClipboardData(CF_DIBV5, dibHandle) == IntPtr.Zero)
                    throw ErroreWin32("Copia immagine DIBV5 non riuscita");
                dibHandle = IntPtr.Zero; // ownership transferred to the Windows clipboard
                if (pngHandle != IntPtr.Zero && SetClipboardData(pngFormat, pngHandle) != IntPtr.Zero)
                    pngHandle = IntPtr.Zero;
            }
            finally { CloseClipboard(); }
        }
        finally
        {
            if (dibHandle != IntPtr.Zero) GlobalFree(dibHandle);
            if (pngHandle != IntPtr.Zero) GlobalFree(pngHandle);
        }
    }

    private static async Task ApriAppuntiAsync(IntPtr hwnd)
    {
        for (var tentativo = 0; tentativo < 10; tentativo++)
        {
            if (OpenClipboard(hwnd)) return;
            await Task.Delay(20);
        }
        throw ErroreWin32("Appunti Windows occupati");
    }

    private static byte[] CreaDibV5(byte[] png)
    {
        using var sorgente = SKBitmap.Decode(png) ?? throw new InvalidDataException("Il PNG del grafico non è valido.");
        var larghezza = sorgente.Width;
        var altezza = sorgente.Height;
        var info = new SKImageInfo(larghezza, altezza, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(sorgente, 0, 0);
            canvas.Flush();
        }

        const int intestazione = 124; // BITMAPV5HEADER
        var passoRiga = checked(larghezza * 4);
        var risultato = new byte[checked(intestazione + passoRiga * altezza)];
        BinaryPrimitives.WriteUInt32LittleEndian(risultato.AsSpan(0, 4), intestazione);
        BinaryPrimitives.WriteInt32LittleEndian(risultato.AsSpan(4, 4), larghezza);
        BinaryPrimitives.WriteInt32LittleEndian(risultato.AsSpan(8, 4), altezza); // DIB bottom-up
        BinaryPrimitives.WriteUInt16LittleEndian(risultato.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(risultato.AsSpan(14, 2), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(risultato.AsSpan(16, 4), 3); // BI_BITFIELDS
        BinaryPrimitives.WriteUInt32LittleEndian(risultato.AsSpan(20, 4), checked((uint)(passoRiga * altezza)));
        BinaryPrimitives.WriteUInt32LittleEndian(risultato.AsSpan(40, 4), 0x00FF0000); // red
        BinaryPrimitives.WriteUInt32LittleEndian(risultato.AsSpan(44, 4), 0x0000FF00); // green
        BinaryPrimitives.WriteUInt32LittleEndian(risultato.AsSpan(48, 4), 0x000000FF); // blue
        BinaryPrimitives.WriteUInt32LittleEndian(risultato.AsSpan(52, 4), 0xFF000000); // alpha
        BinaryPrimitives.WriteUInt32LittleEndian(risultato.AsSpan(56, 4), 0x73524742); // LCS_sRGB
        BinaryPrimitives.WriteUInt32LittleEndian(risultato.AsSpan(108, 4), 4); // LCS_GM_IMAGES

        var riga = new byte[passoRiga];
        for (var y = 0; y < altezza; y++)
        {
            Marshal.Copy(IntPtr.Add(bitmap.GetPixels(), y * bitmap.RowBytes), riga, 0, passoRiga);
            Buffer.BlockCopy(riga, 0, risultato, intestazione + (altezza - y - 1) * passoRiga, passoRiga);
        }
        return risultato;
    }

    private static IntPtr CreaMemoriaGlobale(byte[] dati)
    {
        var memoria = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)dati.Length);
        if (memoria == IntPtr.Zero) throw ErroreWin32("Allocazione memoria per gli appunti non riuscita");
        var puntatore = GlobalLock(memoria);
        if (puntatore == IntPtr.Zero)
        {
            GlobalFree(memoria);
            throw ErroreWin32("Accesso ai dati immagine per gli appunti non riuscito");
        }
        try { Marshal.Copy(dati, 0, puntatore, dati.Length); }
        finally { GlobalUnlock(memoria); }
        return memoria;
    }

    private static Exception ErroreWin32(string messaggio) =>
        new InvalidOperationException($"{messaggio} (Win32 {Marshal.GetLastWin32Error()}).");

    private const uint CF_DIBV5 = 17;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr memory);
}
