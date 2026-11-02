using System.Buffers.Binary;
using System.Text;

namespace TestLoom.App.Services;

/// <summary>Metadati testuali nei PNG dei grafici (chunk iTXt, UTF-8): titolo, filtri attivi e data di generazione.
/// Scrittura e lettura minimali, senza dipendenze: il chunk IHDR resta il primo, come richiede lo standard PNG.</summary>
public static class PngMetadati
{
    private static readonly byte[] Firma = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private const int FineIhdr = 8 + 4 + 4 + 13 + 4; // firma + lunghezza + tipo + dati IHDR + CRC
    private static readonly uint[] TabellaCrc = CostruisciTabellaCrc();

    /// <summary>Aggiunge i campi come chunk iTXt subito dopo IHDR. Un flusso che non e' un PNG valido viene restituito invariato.</summary>
    public static byte[] Aggiungi(byte[] png, params (string Chiave, string Valore)[] campi)
    {
        if (png.Length < FineIhdr || !png.AsSpan(0, Firma.Length).SequenceEqual(Firma)) return png;
        using var ms = new MemoryStream(png.Length + 256);
        ms.Write(png, 0, FineIhdr);
        foreach (var (chiave, valore) in campi)
            ScriviChunk(ms, "iTXt", CostruisciItxt(chiave, valore));
        ms.Write(png, FineIhdr, png.Length - FineIhdr);
        return ms.ToArray();
    }

    /// <summary>Legge i chunk iTXt (chiave -> testo). Usato dai test di verifica dell'export.</summary>
    public static IReadOnlyDictionary<string, string> Leggi(byte[] png)
    {
        var risultato = new Dictionary<string, string>(StringComparer.Ordinal);
        if (png.Length < 8 || !png.AsSpan(0, Firma.Length).SequenceEqual(Firma)) return risultato;
        var pos = Firma.Length;
        while (pos + 12 <= png.Length)
        {
            var lunghezza = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos, 4));
            var tipo = Encoding.ASCII.GetString(png, pos + 4, 4);
            var dati = png.AsSpan(pos + 8, lunghezza);
            if (tipo == "iTXt")
            {
                var fineChiave = dati.IndexOf((byte)0);
                var chiave = Encoding.Latin1.GetString(dati[..fineChiave]);
                // dopo la chiave: flag compressione, metodo, lingua\0, chiave tradotta\0, testo
                var resto = dati[(fineChiave + 3)..];
                var fineLingua = resto.IndexOf((byte)0);
                var dopoLingua = resto[(fineLingua + 1)..];
                var fineTradotta = dopoLingua.IndexOf((byte)0);
                risultato[chiave] = Encoding.UTF8.GetString(dopoLingua[(fineTradotta + 1)..]);
            }
            if (tipo == "IEND") break;
            pos += 12 + lunghezza;
        }
        return risultato;
    }

    private static byte[] CostruisciItxt(string chiave, string valore)
    {
        var chiaveBytes = Encoding.Latin1.GetBytes(chiave.Length > 79 ? chiave[..79] : chiave);
        var testoBytes = Encoding.UTF8.GetBytes(valore);
        var dati = new List<byte>(chiaveBytes.Length + testoBytes.Length + 6);
        dati.AddRange(chiaveBytes);
        dati.Add(0);     // fine keyword
        dati.Add(0);     // flag compressione: testo non compresso
        dati.Add(0);     // metodo di compressione
        dati.Add(0);     // lingua vuota
        dati.Add(0);     // keyword tradotta vuota
        dati.AddRange(testoBytes);
        return dati.ToArray();
    }

    private static void ScriviChunk(Stream flusso, string tipo, byte[] dati)
    {
        var tipoBytes = Encoding.ASCII.GetBytes(tipo);
        Span<byte> intero = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(intero, (uint)dati.Length);
        flusso.Write(intero);
        flusso.Write(tipoBytes);
        flusso.Write(dati);
        BinaryPrimitives.WriteUInt32BigEndian(intero, Crc32(tipoBytes, dati));
        flusso.Write(intero);
    }

    private static uint Crc32(byte[] primo, byte[] secondo)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in primo) crc = TabellaCrc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in secondo) crc = TabellaCrc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] CostruisciTabellaCrc()
    {
        var tabella = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            tabella[n] = c;
        }
        return tabella;
    }
}
