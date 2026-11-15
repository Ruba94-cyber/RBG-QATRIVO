namespace TestLoom.App.Services;

/// <summary>Esito della schermata di confronto grafico.</summary>
public enum MergeUiEsito { Unisci, VersioneIntera, Annulla }

public sealed record MergeUiRequest(string Titolo, string Sottotitolo, RecordMergePlan Plan, string EtichettaMia, string EtichettaAltra);

public sealed record MergeUiResult(MergeUiEsito Esito, RecordMergeChoices? Scelte);

/// <summary>Una delle due versioni nella schermata "scegli la versione intera": solo lettura, mai JSON.</summary>
/// <summary>Una riga leggibile della versione; <see cref="Diverso"/> la evidenzia rispetto all'altra versione.</summary>
public sealed record WholeVersionField(string Etichetta, string Testo, bool Diverso);

/// <param name="Problema">Se la versione non si può scegliere: motivo in italiano chiaro (campo e valore problematici).</param>
/// <param name="Campi">Righe per campo con le differenze evidenziate (null = solo <paramref name="Anteprima"/>).</param>
public sealed record WholeVersionSide(string Etichetta, string Descrizione, string Anteprima, bool Scegliibile,
    string? Problema = null, IReadOnlyList<WholeVersionField>? Campi = null);

public sealed record WholeVersionRequest(string Titolo, string Motivo, WholeVersionSide Mia, WholeVersionSide Altra);

public enum WholeVersionChoice { TieniMia, TieniAltra }

/// <summary>
/// Ponte servizi/UI della risoluzione dei conflitti (impostato dalla MainWindow, sostituibile nei test). Due schermate:
/// il confronto campo per campo con anteprima del risultato, e la scelta della versione INTERA (solo lettura) quando il
/// confronto non e' possibile o l'utente lo preferisce. Senza handler (nessuna UI) le richieste valgono "annullato":
/// nessun dato viene mai modificato in modo implicito.
/// </summary>
public static class ConflictResolverUi
{
    public static Func<MergeUiRequest, Task<MergeUiResult>>? MergeHandler { get; set; }
    public static Func<WholeVersionRequest, Task<WholeVersionChoice?>>? WholeVersionHandler { get; set; }

    public static Task<MergeUiResult> Merge(MergeUiRequest request) =>
        MergeHandler == null ? Task.FromResult(new MergeUiResult(MergeUiEsito.Annulla, null)) : MergeHandler(request);

    public static Task<WholeVersionChoice?> WholeVersion(WholeVersionRequest request) =>
        WholeVersionHandler == null ? Task.FromResult<WholeVersionChoice?>(null) : WholeVersionHandler(request);
}
