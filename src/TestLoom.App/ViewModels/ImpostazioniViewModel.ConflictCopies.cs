using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Una copia di conflitto OneDrive/SharePoint (ritirata in quarantena oppure ancora nella cartella del record).</summary>
public sealed class ConflittoCopiaRiga
{
    public ConflittoCopiaRiga(ConflictCopySource? sorgente, string titolo, string dettaglio, string chiave)
    {
        Sorgente = sorgente;
        Titolo = titolo;
        Dettaglio = dettaglio;
        AutomationId = "ConflittoCopia_Risolvi_" + chiave;
    }

    public ConflictCopySource? Sorgente { get; }
    public string Titolo { get; }
    public string Dettaglio { get; }
    public string AutomationId { get; }
    public bool Risolvibile => Sorgente != null;
}

public partial class ImpostazioniViewModel
{
    [ObservableProperty] private IReadOnlyList<ConflittoCopiaRiga> conflittiCopie = Array.Empty<ConflittoCopiaRiga>();
    [ObservableProperty] private bool risoluzioneConflittoCopiaInCorso;

    public bool HaConflittiCopie => ConflittiCopie.Count > 0;
    public bool PuoRisolvereConflittoCopia => _state.EMaster && _state.PuoScrivere && !RisoluzioneConflittoCopiaInCorso;

    partial void OnConflittiCopieChanged(IReadOnlyList<ConflittoCopiaRiga> value) => OnPropertyChanged(nameof(HaConflittiCopie));
    partial void OnRisoluzioneConflittoCopiaInCorsoChanged(bool value) => OnPropertyChanged(nameof(PuoRisolvereConflittoCopia));

    private static string Tx(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);

    public string ConfrontaERisolviTesto => Tx("Confronta e risolvi", "Compare and resolve", "Comparer et résoudre");
    public string ConflittiCopieTitolo => Tx("File in conflitto lasciati dalla sincronizzazione", "Conflicting files left by synchronization", "Fichiers en conflit laissés par la synchronisation");
    public string ConflittiCopieNota => Tx(
        "Quando due postazioni salvano lo stesso record quasi insieme, OneDrive/SharePoint lascia una seconda copia del file. TestLoom la sposta qui, fuori dalle cartelle dei dati, senza perdere nulla. Apri il confronto per scegliere campo per campo, oppure tieni una versione intera: l'altra va nel cestino di recupero.",
        "When two stations save the same record almost together, OneDrive/SharePoint leaves a second copy of the file. TestLoom moves it here, out of the data folders, losing nothing. Open the comparison to choose field by field, or keep a whole version: the other goes to the recovery bin.",
        "Quand deux postes enregistrent le même enregistrement presque ensemble, OneDrive/SharePoint laisse une seconde copie du fichier. TestLoom la déplace ici, hors des dossiers de données, sans rien perdre. Ouvrez la comparaison pour choisir champ par champ, ou gardez une version entière : l'autre va dans la corbeille de récupération.");
    public string ConflittiCopieNessuno => Tx("Nessun file in conflitto.", "No conflicting files.", "Aucun fichier en conflit.");
    public string ForkTestTitoloConflitti => Tx("Conflitti sui Test fra postazioni", "Test conflicts between stations", "Conflits sur les Tests entre postes");

    private void NotificaConflittiCopieProperties()
    {
        OnPropertyChanged(nameof(ConflittiCopie));
        OnPropertyChanged(nameof(HaConflittiCopie));
        OnPropertyChanged(nameof(PuoRisolvereConflittoCopia));
    }

    /// <summary>Ritira le nuove copie dalle cartelle dei record (scansione completa, fuori dal thread UI) e rilegge l'elenco.</summary>
    [RelayCommand]
    private Task AggiornaConflittiCopie() =>
        ComandoAsincrono.EseguiAsync(() => CaricaConflittiCopieAsync(ritira: true), "Aggiornamento dei file in conflitto", m => MessaggioErrore = m);

    private async Task CaricaConflittiCopieAsync(bool ritira)
    {
        if (!_state.CartellaValida)
        {
            ConflittiCopie = Array.Empty<ConflittoCopiaRiga>();
            return;
        }
        var paths = _state.Paths;
        var operatore = _state.Config.OperatoreAttivo;
        var puoScrivere = _state.PuoScrivere;
        var inLoco = _state.TutteLeCopieDiConflitto().Select(c => c.Percorso).ToList();
        var righe = await Task.Run(() =>
        {
            if (ritira && puoScrivere)
            {
                var report = ConflictCopyQuarantineService.Run(paths, operatore);
                foreach (var errore in report.Errori) CrashLogService.Registra("Quarantena conflitti (aggiornamento)", new IOException(errore));
            }

            var risultato = new List<ConflittoCopiaRiga>();
            foreach (var voce in ConflictCopyQuarantineService.List(paths.Root))
            {
                var src = ConflictCopySource.FromEntry(voce);
                var env = voce.Envelope;
                var dettaglio = string.Format(Tx("Rilevata il {0:dd/MM/yyyy HH:mm} su {1}", "Detected {0:dd/MM/yyyy HH:mm} on {1}", "Détectée le {0:dd/MM/yyyy HH:mm} sur {1}"),
                    env.DetectedUtc.ToLocalTime(), string.IsNullOrWhiteSpace(env.Device) ? "?" : env.Device);
                if (env.IdenticalToCanonical)
                    dettaglio += Tx(" · identica alla versione corrente", " · identical to the current version", " · identique à la version actuelle");
                risultato.Add(new ConflittoCopiaRiga(src, src.Titolo, dettaglio, voce.Id[..12]));
            }
            // Copie rimaste nella cartella perche' illeggibili o non ancora stabili: stessa sezione, schermata "versione intera".
            foreach (var percorso in inLoco.Where(File.Exists))
            {
                var src = ConflictCopySource.FromInPlace(paths, percorso);
                var nome = Path.GetFileName(percorso);
                risultato.Add(new ConflittoCopiaRiga(src, src?.Titolo ?? nome,
                    Tx("Ancora nella cartella condivisa: il file non è stato ritirato (illeggibile o ancora in sincronizzazione).",
                       "Still in the shared folder: the file has not been moved (unreadable or still syncing).",
                       "Toujours dans le dossier partagé : fichier non déplacé (illisible ou en cours de synchronisation)."),
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(percorso)))[..12]));
            }
            return risultato;
        });
        ConflittiCopie = righe;
        NotificaConflittiCopieProperties();
    }

    [RelayCommand]
    private Task RisolviConflittoCopia(ConflittoCopiaRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => RisolviConflittoCopiaCoreAsync(riga), "Risoluzione di un file in conflitto", m => MessaggioErrore = m);

    private async Task RisolviConflittoCopiaCoreAsync(ConflittoCopiaRiga? riga)
    {
        if (riga?.Sorgente == null || !PuoRisolvereConflittoCopia) return;
        RisoluzioneConflittoCopiaInCorso = true;
        Messaggio = null;
        MessaggioErrore = null;
        try
        {
            var esito = await ConflictResolutionService.RisolviCopiaAsync(_state, riga.Sorgente);
            if (esito.Risolto)
            {
                _state.RicaricaTutto();
                _state.NotificaDatiRicaricati();
                _onCambiato();
                Messaggio = esito.Messaggio;
            }
            else Messaggio = esito.Messaggio;
        }
        catch (ConflittoRevisioneException)
        {
            MessaggioErrore = Tx("Il record è cambiato mentre risolvevi il conflitto. Nessuna modifica è stata persa: riapri il confronto.",
                "The record changed while you were resolving the conflict. Nothing was lost: reopen the comparison.",
                "L'enregistrement a changé pendant la résolution. Rien n'a été perdu : rouvrez la comparaison.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or RecordMergeException)
        {
            CrashLogService.Registra("Risoluzione file in conflitto", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("RisoluzioneNonEseguitaFmt"), ex.Message);
        }
        finally
        {
            RisoluzioneConflittoCopiaInCorso = false;
            await CaricaConflittiCopieAsync(ritira: false);
        }
    }

    // ---------------------------------------------------------------- fork NC/CR/Segnalazione: confronto grafico

    [RelayCommand]
    private Task RisolviRecordForkConEditor(RecordForkRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => RisolviRecordForkConEditorCoreAsync(riga), "Risoluzione guidata di un conflitto fra postazioni", m => MessaggioErrore = m);

    private async Task RisolviRecordForkConEditorCoreAsync(RecordForkRiga? riga)
    {
        if (riga == null || !PuoRisolvereRecordFork) return;
        RisoluzioneRecordForkInCorso = true;
        Messaggio = null;
        MessaggioErrore = null;
        try
        {
            await Task.Run(() => _state.RecordJournal?.Refresh());
            var fork = _state.RecordJournal?.OpenForks.FirstOrDefault(f => f.Key == riga.Fork.Key);
            if (fork == null)
            {
                Messaggio = LocalizationService.Instance.Traduci("ForkGiaRisolto");
                return;
            }
            var esito = await RecordForkResolutionService.RisolviConEditorAsync(_state, fork);
            if (esito == null) { Messaggio = Tx("Risoluzione annullata: nessuna modifica.", "Resolution cancelled: no changes.", "Résolution annulée : aucune modification."); return; }
            _state.RicaricaTutto();
            _state.NotificaDatiRicaricati();
            Messaggio = esito.Message;
            _onCambiato();
        }
        catch (ConflittoRevisioneException)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("ForkCambiatoDuranteRisoluzione");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or RecordMergeException)
        {
            CrashLogService.Registra("Risoluzione guidata conflitto fra postazioni", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("RisoluzioneNonEseguitaFmt"), ex.Message);
        }
        finally
        {
            RisoluzioneRecordForkInCorso = false;
            NotificaRecordForkProperties();
        }
    }

    // ---------------------------------------------------------------- cataloghi master: confronto grafico

    [RelayCommand]
    private Task RisolviConflittoMasterConEditor(MasterConflittoRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => RisolviConflittoMasterConEditorCoreAsync(riga), "Risoluzione guidata di un conflitto master", m => MessaggioErrore = m);

    private async Task RisolviConflittoMasterConEditorCoreAsync(MasterConflittoRiga? riga)
    {
        if (riga == null || !PuoRisolvereConflittoMaster) return;
        RisoluzioneConflittoMasterInCorso = true;
        Messaggio = null;
        MessaggioErrore = null;
        try
        {
            var esito = await ConflictResolutionService.RisolviMasterAsync(_state, riga.Conflitto);
            if (esito.Risolto)
            {
                _state.RicaricaTutto();
                _state.NotificaDatiRicaricati();
                _onCambiato();
            }
            Messaggio = esito.Messaggio;
            await CaricaConflittiMasterAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or RecordMergeException or PhaseDMasterConflictException)
        {
            CrashLogService.Registra("Risoluzione guidata conflitto master", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("RisoluzioneNonEseguitaFmt"), ex.Message);
        }
        finally
        {
            RisoluzioneConflittoMasterInCorso = false;
            OnPropertyChanged(nameof(PuoRisolvereConflittoMaster));
        }
    }
}
