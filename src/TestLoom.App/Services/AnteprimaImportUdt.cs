namespace TestLoom.App.Services;

/// <summary>Una scrittura prevista da un import da log UnitLogger (UX-4): campo, valore precedente e valore nuovo, calcolati
/// SENZA toccare nulla. <see cref="Applica"/> esegue la scrittura vera: la chiamano solo <see cref="PianoImportUdt.Esegui"/>
/// (import senza sovrascritture) oppure il percorso di import dopo la conferma dell'anteprima.</summary>
public sealed record ScritturaCampoUdt(string Campo, string? Precedente, string Valore, Action Applica)
{
    /// <summary>True solo se sostituisce un valore gia presente e diverso: un campo vuoto si compila senza domande.</summary>
    public bool Sovrascrive => Precedente != null && !string.Equals(Precedente.Trim(), Valore.Trim(), StringComparison.Ordinal);

    /// <summary>Dizionario dell'editor su cui <see cref="Applica"/> scrive, e chiave: servono a registrare lo stato precedente
    /// per "Annulla ultimo import" (6.6.0). Null per le scritture che non toccano un dizionario.</summary>
    public Dictionary<string, string>? Destinazione { get; init; }
    public string? ChiaveDestinazione { get; init; }

    /// <summary>Opzione da aggiungere al catalogo UnitLogger condiviso (nome campo, opzione). Viene scritta una sola volta, prima
    /// dell'editor, da <see cref="PianoImportUdt.Esegui"/>: se la scrittura del catalogo fallisce, l'editor non cambia.</summary>
    public (string NomeCampo, string Opzione)? OpzioneCatalogo { get; init; }

    /// <summary>Valore gia presente per <paramref name="chiave"/> (null se assente o solo spazi).</summary>
    public static string? ValorePresente(IReadOnlyDictionary<string, string>? valori, string chiave) =>
        valori != null && valori.TryGetValue(chiave, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>Notifica e ripristina eventuale stato del modello dopo il rollback della chiave nel dizionario.</summary>
    public Action? DopoRipristino { get; init; }

    /// <summary>Scrittura su una chiave di un dizionario: il valore precedente si legge ora, la scrittura solo in Applica.</summary>
    public static ScritturaCampoUdt Su(string campo, Dictionary<string, string> destinazione, string chiave, string valore, Action? dopo = null) =>
        new(campo, ValorePresente(destinazione, chiave), valore, () =>
        {
            destinazione[chiave] = valore;
            dopo?.Invoke();
        })
        {
            Destinazione = destinazione,
            ChiaveDestinazione = chiave,
        };
}

/// <summary>Stato precedente di una chiave dell'editor scritta da un import UnitLogger (6.6.0): assente, oppure con il valore di prima.
/// <see cref="Scritto"/> e' il valore lasciato dall'import: se la chiave non e' piu' quella, l'annullamento viene rifiutato.</summary>
public sealed record AnnulloVoceUdt(Dictionary<string, string> Destinazione, string Chiave, bool Presente, string? Precedente, string Scritto,
    Action? DopoRipristino = null);

/// <summary>Stato precedente di tutte le chiavi dell'editor scritte da un import UnitLogger: serve a "Annulla ultimo import".
/// Non riguarda il catalogo UnitLogger condiviso. Vale solo se nessuna chiave e' stata cambiata dopo l'import.</summary>
public sealed record AnnulloImportUdt(IReadOnlyList<AnnulloVoceUdt> Voci)
{
    /// <summary>True se ogni chiave ha ancora il valore lasciato dall'import (nessuna modifica successiva).</summary>
    public bool InvariatoDopoImport => Voci.All(v =>
        v.Destinazione.TryGetValue(v.Chiave, out var attuale) && string.Equals(attuale, v.Scritto, StringComparison.Ordinal));

    /// <summary>Riporta ogni chiave allo stato precedente: il valore di prima, oppure l'assenza della chiave.</summary>
    public void Annulla()
    {
        foreach (var v in Voci)
        {
            if (v.Presente) v.Destinazione[v.Chiave] = v.Precedente!;
            else v.Destinazione.Remove(v.Chiave);
            v.DopoRipristino?.Invoke();
        }
    }
}

/// <summary>Una voce del piano di import da log UnitLogger. Senza <see cref="Scrittura"/> il motivo e' uno solo: se
/// <see cref="NonConfigurato"/> il campo non esiste sullo strumento (mai scritto come chiave fantasma, segnalato nel
/// riepilogo); altrimenti il valore manca nel log.</summary>
public sealed record VoceImportUdt(string Campo, ScritturaCampoUdt? Scrittura, bool NonConfigurato = false);

/// <summary>Piano di un import da log UnitLogger: scritture previste, campi trovati, campi mancanti nel log e campi non configurati
/// sullo strumento (per il riepilogo). Costruirlo non ha effetti: <see cref="Esegui"/> applica tutto,
/// <see cref="ConfermaSeSovrascriveAsync"/> chiede conferma solo se l'import sostituirebbe valori gia presenti.</summary>
public sealed record PianoImportUdt(IReadOnlyList<ScritturaCampoUdt> Scritture, IReadOnlyList<string> Importati,
    IReadOnlyList<string> Mancanti, IReadOnlyList<string> NonConfigurati)
{
    /// <summary>Righe elencate nell'anteprima; le altre si riassumono con un conteggio.</summary>
    public const int MaxRigheAnteprima = 10;

    /// <summary>Costruisce il piano dalle voci: scrittura presente = importato; altrimenti non configurato o mancante.</summary>
    public static PianoImportUdt DaVoci(IEnumerable<VoceImportUdt> voci)
    {
        var scritture = new List<ScritturaCampoUdt>();
        var importati = new List<string>();
        var mancanti = new List<string>();
        var nonConfigurati = new List<string>();
        foreach (var voce in voci)
        {
            if (voce.Scrittura != null) { scritture.Add(voce.Scrittura); importati.Add(voce.Campo); }
            else if (voce.NonConfigurato) nonConfigurati.Add(voce.Campo);
            else mancanti.Add(voce.Campo);
        }
        return new PianoImportUdt(scritture, importati, mancanti, nonConfigurati);
    }

    /// <summary>Costruisce il piano da coppie (campo, scrittura): una scrittura nulla significa campo non trovato o vuoto nel log.</summary>
    public static PianoImportUdt Da(IEnumerable<(string Campo, ScritturaCampoUdt? Scrittura)> voci) =>
        DaVoci(voci.Select(v => new VoceImportUdt(v.Campo, v.Scrittura)));

    /// <summary>Scritture che sostituirebbero un valore gia presente e diverso.</summary>
    public IReadOnlyList<ScritturaCampoUdt> Sovrascritture => Scritture.Where(s => s.Sovrascrive).ToList();

    /// <summary>Applica le scritture nell'ordine previsto (stesso ordine dell'import senza anteprima), in due fasi (6.6.0):
    /// 1) le opzioni da aggiungere al catalogo UnitLogger condiviso, con UNA sola scrittura tramite <paramref name="salvaOpzioniCatalogo"/>:
    /// se fallisce non e' cambiato nulla; 2) le scritture sull'editor, solo in memoria (non possono fallire).
    /// Restituisce lo stato precedente delle chiavi toccate, per "Annulla ultimo import".</summary>
    public AnnulloImportUdt Esegui(Action<IReadOnlyList<(string NomeCampo, string Opzione)>>? salvaOpzioniCatalogo = null)
    {
        var opzioni = Scritture.Where(s => s.OpzioneCatalogo != null).Select(s => s.OpzioneCatalogo!.Value).ToList();
        if (opzioni.Count > 0)
        {
            if (salvaOpzioniCatalogo == null) throw new InvalidOperationException("Import UnitLogger con opzioni di catalogo senza salvataggio del catalogo.");
            salvaOpzioniCatalogo(opzioni);
        }

        // Stato precedente di ogni chiave dell'editor, catturato prima di qualunque scrittura (una voce per chiave).
        var precedenti = new Dictionary<(Dictionary<string, string> Destinazione, string Chiave), (bool Presente, string? Valore)>();
        var callbackRipristino = new Dictionary<(Dictionary<string, string> Destinazione, string Chiave), Action?>();
        foreach (var s in Scritture)
        {
            if (s.Destinazione == null || s.ChiaveDestinazione == null) continue;
            var chiave = (s.Destinazione, s.ChiaveDestinazione);
            if (!precedenti.ContainsKey(chiave))
            {
                if (s.Destinazione.TryGetValue(s.ChiaveDestinazione, out var valore)) precedenti[chiave] = (true, valore);
                else precedenti[chiave] = (false, null);
            }
            if (s.DopoRipristino != null) callbackRipristino[chiave] = s.DopoRipristino;
        }

        foreach (var scrittura in Scritture) scrittura.Applica();

        var voci = precedenti.Select(kv => new AnnulloVoceUdt(
            kv.Key.Destinazione, kv.Key.Chiave, kv.Value.Presente, kv.Value.Valore,
            kv.Key.Destinazione.TryGetValue(kv.Key.Chiave, out var scritto) ? scritto : string.Empty,
            callbackRipristino.GetValueOrDefault(kv.Key))).ToList();
        return new AnnulloImportUdt(voci);
    }

    /// <summary>Senza sovrascritture non chiede nulla e restituisce true. Altrimenti mostra campo, valore attuale e
    /// valore nuovo: restituisce true solo se l'utente conferma (Annulla: nessun campo cambia).</summary>
    public async Task<bool> ConfermaSeSovrascriveAsync()
    {
        var sovrascritture = Sovrascritture;
        if (sovrascritture.Count == 0) return true;
        var righe = sovrascritture.Take(MaxRigheAnteprima).Select(s => $"- {s.Campo}: {s.Precedente} → {s.Valore}").ToList();
        if (sovrascritture.Count > MaxRigheAnteprima) righe.Add($"(altri {sovrascritture.Count - MaxRigheAnteprima})");
        var messaggio = "Il log UnitLogger sostituisce valori già presenti (campo: valore attuale → nuovo):\n" + string.Join("\n", righe)
            + "\n\nI campi vuoti vengono solo compilati. Nulla è salvato finché non premi Salva.";
        return await ConfirmService.Chiedi("Importa dal log UnitLogger", messaggio, "Sovrascrivi", "Annulla");
    }
}
