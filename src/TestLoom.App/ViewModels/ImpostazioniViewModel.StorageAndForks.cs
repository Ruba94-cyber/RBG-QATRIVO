using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public sealed record ForkJournalRiga(JournalForkConflict Fork)
{
    public string TestId => string.IsNullOrWhiteSpace(Fork.EntityId) ? Fork.EntityUid.ToString("N") : Fork.EntityId;
    public string Riepilogo => string.Format(LocalizationService.Instance.Traduci("ForkRiepilogoFmt"),
        Fork.Branches.Select(b => b.NewHash).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        Fork.BaseRevision?.ToString() ?? "?");
    public string RilevatoIl => Fork.DetectedUtc == default ? "" : Fork.DetectedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
}

/// <summary>Riga amministrativa del recovery-bin. L'envelope completo non viene duplicato nel ViewModel:
/// resta nel Record e il comando lo identifica esclusivamente tramite RecoveryId.</summary>
public sealed record RecoveryBinRiga(DomainRecoveryRecord Record)
{
    public Guid RecoveryId => Record.RecoveryId;
    public string Categoria => Record.Category switch
    {
        DomainRecoveryCategory.TestCase => LocalizationService.Instance.Traduci("RecoveryCategoriaTestTesto"),
        DomainRecoveryCategory.NonConformita => LocalizationService.Instance.Traduci("RecoveryCategoriaNcTesto"),
        DomainRecoveryCategory.ChangeRequest => LocalizationService.Instance.Traduci("RecoveryCategoriaCrTesto"),
        DomainRecoveryCategory.Segnalazione => LocalizationService.Instance.Traduci("RecoveryCategoriaSegnalazioneTesto"),
        _ => Record.Category.ToString()
    };
    public string PercorsoOriginale => Record.OriginalRelativePath;
    public string EliminatoIl => Record.ArchivedUtc == default ? "" : Record.ArchivedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public string EliminatoDa => string.IsNullOrWhiteSpace(Record.ArchivedBy) ? "—" : Record.ArchivedBy;
    public string Dispositivo => string.IsNullOrWhiteSpace(Record.Device) ? "—" : Record.Device;
    public bool Ripristinato => Record.RestoredUtc.HasValue;
    public string Stato => Ripristinato
        ? string.Format(LocalizationService.Instance.Traduci("RecoveryRipristinatoFmt"), Record.RestoredUtc!.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), (string.IsNullOrWhiteSpace(Record.RestoredBy) ? "—" : Record.RestoredBy))
        : LocalizationService.Instance.Traduci("RecoveryDisponibileTesto");
}

public partial class ImpostazioniViewModel
{
    [ObservableProperty] private bool manutenzioneStorageInCorso;
    [ObservableProperty] private bool risoluzioneForkInCorso;
    [ObservableProperty] private bool ripristinoRecoveryInCorso;

    public bool PuoEseguireManutenzioneStorage => _state.CartellaValida && !ManutenzioneStorageInCorso;
    public bool PuoRisolvereFork => _state.EMaster && _state.PuoScrivere && !RisoluzioneForkInCorso;
    public bool PuoRipristinareRecovery => _state.EMaster && _state.PuoScrivere && !RipristinoRecoveryInCorso;

    public IReadOnlyList<ForkJournalRiga> ForkJournal => _state.CartellaValida
        ? _state.TestCases.JournalForks.Select(f => new ForkJournalRiga(f)).OrderByDescending(f => f.Fork.DetectedUtc).ToList()
        : Array.Empty<ForkJournalRiga>();
    public bool HaForkJournal => ForkJournal.Count > 0;
    public int NumeroForkJournal => ForkJournal.Count;

    /// <summary>Solo un Master vede il contenuto del cestino tecnico. I payload Base64 non vengono esposti
    /// dalla UI: la riga porta soltanto metadati e RecoveryId, mentre il restore rilegge e riverifica l'envelope.</summary>
    public IReadOnlyList<RecoveryBinRiga> RecoveryBin => _state.CartellaValida && _state.EMaster
        ? DomainRecoveryService.GetAll(_state.Paths).Select(r => new RecoveryBinRiga(r)).ToList()
        : Array.Empty<RecoveryBinRiga>();
    public bool HaRecoveryBin => RecoveryBin.Count > 0;
    public int NumeroRecoveryBin => RecoveryBin.Count;
    public int NumeroRecoveryDisponibili => RecoveryBin.Count(r => !r.Ripristinato);

    public string UltimaManutenzioneStorageTesto
    {
        get
        {
            var result = _state.UltimaManutenzioneStorage;
            var loc = LocalizationService.Instance;
            if (result == null) return loc.Traduci("StorageManutenzioneAutomatica");
            var errori = result.Errori > 0
                ? string.Format(loc.Traduci("StorageErroriFmt"), result.Errori)
                : ".";
            return string.Format(loc.Traduci("StorageUltimoGiroFmt"),
                result.LockRimossi,
                result.NotificheLegacyJsonEliminate + result.StatiNotificaScadutiEliminati,
                result.TemporaneiTecniciEliminati + result.BackupTemporaneiEliminati,
                errori);
        }
    }

    /// <summary>Testo di stato del backup giornaliero in background (6.4.1). Letto direttamente da
    /// AppState.BackupGiornalieroStato: nessuna copia locale, la sottoscrizione a BackupGiornalieroStatoCambiato
    /// (costruttore/Dispose di questo file, vedi sotto) serve solo a far ricalcolare il binding quando cambia.</summary>
    public string BackupGiornalieroStatoTesto
    {
        get
        {
            var loc = LocalizationService.Instance;
            var stato = _state.BackupGiornalieroStato;
            return stato.State switch
            {
                DailyBackupState.NonAvviato => loc.Traduci("BackupGiornalieroNonAvviato"),
                DailyBackupState.InCorso => loc.Traduci("BackupGiornalieroInCorso"),
                DailyBackupState.Completato => string.Format(loc.Traduci("BackupGiornalieroCompletatoFmt"), stato.AggiornatoUtc.ToLocalTime().ToString("HH:mm"))
                    + (string.IsNullOrWhiteSpace(stato.Dettaglio) ? "" : " — " + stato.Dettaglio),
                DailyBackupState.Rinviato => string.Format(loc.Traduci("BackupGiornalieroRinviatoFmt"), stato.Dettaglio),
                DailyBackupState.Errore => string.Format(loc.Traduci("BackupGiornalieroErroreFmt"), stato.Dettaglio),
                _ => ""
            };
        }
    }

    partial void OnManutenzioneStorageInCorsoChanged(bool value) => OnPropertyChanged(nameof(PuoEseguireManutenzioneStorage));
    partial void OnRisoluzioneForkInCorsoChanged(bool value) => OnPropertyChanged(nameof(PuoRisolvereFork));
    partial void OnRipristinoRecoveryInCorsoChanged(bool value) => OnPropertyChanged(nameof(PuoRipristinareRecovery));

    [RelayCommand]
    private Task EseguiManutenzioneStorage() =>
        ComandoAsincrono.EseguiAsync(EseguiManutenzioneStorageCoreAsync, "Manutenzione dello storage", m => MessaggioErrore = m);

    private async Task EseguiManutenzioneStorageCoreAsync()
    {
        if (!PuoEseguireManutenzioneStorage) return;
        ManutenzioneStorageInCorso = true;
        Messaggio = null;
        MessaggioErrore = null;
        try
        {
            // Prima la manutenzione condivisa standard. Questa resta la stessa usata automaticamente e NON
            // esegue VACUUM, quindi non prende un lock esclusivo SQLite durante l'uso normale dell'app.
            var result = await Task.Run(() => _state.ForzaManutenzioneStorage());

            // Il click manuale "pulizia ora" è invece un momento esplicito: qui possiamo recuperare anche lo
            // spazio interno dei DB locali. VACUUM viene comunque eseguito solo se >=20% e >=128 pagine sono
            // libere; altrimenti il servizio fa soltanto checkpoint/optimize.
            var watch = Stopwatch.StartNew();
            var sqlite = await Task.Run(() => LocalSqliteMaintenanceService.OptimizeWorkspace(
                _state.TestCases.LocalWorkspaceDirectory,
                vacuum: true));
            watch.Stop();
            PerformanceDiagnosticsService.Record(
                _state.TestCases.LocalWorkspaceDirectory,
                "storage.sqlite-manual-cleanup",
                watch.Elapsed,
                sqlite.DatabaseOttimizzati,
                $"vacuum={sqlite.DatabaseVacuum}; bytesFreed={sqlite.ByteRecuperati}; errors={sqlite.Errori}");

            OnPropertyChanged(nameof(UltimaManutenzioneStorageTesto));
            var loc = LocalizationService.Instance;
            var baseText = result == null
                ? loc.Traduci("StorageManutenzioneCompletata")
                : string.Format(loc.Traduci("StorageManutenzioneCompletataFmt"), result.TotaleArtefattiEliminati);
            var sqliteText = sqlite.ByteRecuperati > 0
                ? string.Format(loc.Traduci("StorageSqliteRecuperatoFmt"), FormatBytes(sqlite.ByteRecuperati))
                : sqlite.DatabaseVacuum > 0
                    ? loc.Traduci("StorageSqliteCompattato")
                    : loc.Traduci("StorageSqliteOttimizzato");
            Messaggio = baseText + sqliteText + (sqlite.Errori > 0
                ? string.Format(loc.Traduci("StorageSqliteErroriFmt"), sqlite.Errori)
                : "");
            _onCambiato();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Manutenzione storage manuale", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("StorageManutenzioneFallitaFmt"), ex.Message);
        }
        finally { ManutenzioneStorageInCorso = false; }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.0} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.00} GB";
    }

    [RelayCommand]
    private async Task AggiornaForkJournal()
    {
        if (!_state.CartellaValida) return;
        try
        {
            await Task.Run(() => _state.TestCases.RefreshJournalMetadata());
            NotificaForkProperties();
            _onCambiato();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Aggiornamento fork journal", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ForkAggiornamentoFallitoFmt"), ex.Message);
        }
    }

    [RelayCommand]
    private void AggiornaRecoveryBin()
    {
        NotificaRecoveryProperties();
        MessaggioErrore = null;
    }

    [RelayCommand]
    private Task RipristinaRecovery(RecoveryBinRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => RipristinaRecoveryCoreAsync(riga), "Ripristino dal cestino di recupero", m => MessaggioErrore = m);

    private async Task RipristinaRecoveryCoreAsync(RecoveryBinRiga? riga)
    {
        if (riga == null || riga.Ripristinato || !PuoRipristinareRecovery) return;

        var confermato = await ConfirmService.Chiedi(
            LocalizationService.Instance.Traduci("RecoveryRipristinaTitolo"),
            string.Format(LocalizationService.Instance.Traduci("RecoveryRipristinareDomandaFmt"), riga.Categoria, riga.PercorsoOriginale) + "\n\n" +
            LocalizationService.Instance.Traduci("RecoveryNonSovrascriveMsg"),
            LocalizationService.Instance.Traduci("RecoveryRipristinaBtn"),
            LocalizationService.Instance.Traduci("Annulla"));
        if (!confermato) return;

        RipristinoRecoveryInCorso = true;
        Messaggio = null;
        MessaggioErrore = null;
        try
        {
            using var tx = WorkflowTransactionService.Begin(_state.Paths, $"restore-domain-recovery:{riga.RecoveryId:N}");
            var restored = DomainRecoveryService.Restore(
                _state.Paths,
                riga.RecoveryId,
                _state.Config.OperatoreAttivo);
            tx.Commit();
            // Journal per-device, best effort: il ripristino è già committed e un errore del journal non lo annulla.
            DomainRecoveryService.TryRecordRestoreInJournal(_state.RecordJournal, restored, _state.Config.OperatoreAttivo);

            // La source of truth è appena cambiata: riallinea tutti i repository prima di dichiarare il
            // ripristino riuscito. Per un Test ripristinato dal cestino il JSON resta correttamente Eliminato=true
            // e l'utente può poi usare il normale comando Ripristina dell'Archivio.
            _state.RicaricaTutto();
            _state.RegistraAudit(
                "RestoreDomainRecovery",
                restored.Record.OriginalFileName,
                $"recovery={restored.Record.RecoveryId:N}; category={restored.Record.Category}; path={restored.Record.OriginalRelativePath}; alreadyPresent={restored.AlreadyPresent}");
            _state.NotificaDatiRicaricati();
            NotificaRecoveryProperties();
            _onCambiato();
            Messaggio = restored.AlreadyPresent
                ? LocalizationService.Instance.Traduci("RecoveryGiaPresenteMsg")
                : LocalizationService.Instance.Traduci("RecoveryRipristinatoOkMsg");
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Ripristino recovery-bin", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("RecoveryRipristinoNonEseguitoFmt"), ex.Message);
            NotificaRecoveryProperties();
        }
        finally { RipristinoRecoveryInCorso = false; }
    }

    [RelayCommand]
    private Task RisolviForkJournal(ForkJournalRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => RisolviForkJournalCoreAsync(riga), "Risoluzione di un fork del journal", m => MessaggioErrore = m);

    private async Task RisolviForkJournalCoreAsync(ForkJournalRiga? riga)
    {
        if (riga == null || !PuoRisolvereFork) return;
        RisoluzioneForkInCorso = true;
        Messaggio = null;
        MessaggioErrore = null;
        try
        {
            await Task.Run(() => _state.TestCases.RefreshJournalMetadata());
            var fork = _state.TestCases.JournalForks.FirstOrDefault(f => SameFork(f, riga.Fork));
            if (fork == null)
            {
                Messaggio = LocalizationService.Instance.Traduci("ForkGiaRisolto");
                NotificaForkProperties();
                return;
            }

            var copies = _state.TestCases.GetAll(includeEliminati: true).Where(t => t.Uid == fork.EntityUid).ToList();
            var cachedCurrent = copies.Where(t => !t.Eliminato).OrderByDescending(t => t.RevisioneDati).FirstOrDefault()
                                ?? copies.OrderByDescending(t => t.RevisioneDati).FirstOrDefault();
            if (cachedCurrent == null)
            {
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ForkTestNonDisponibileFmt"), riga.TestId);
                return;
            }

            var current = _state.TestCases.LoadCurrent(cachedCurrent);
            if (current == null)
            {
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ForkTestNonLeggibileFmt"), riga.TestId);
                return;
            }
            var recovery = DelayedForkRecoveryService.LoadAllBranches(_state.Paths, fork, current, copies);
            if (!recovery.Complete)
            {
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ForkRecoveryMancanteFmt"), recovery.Missing.Count, riga.TestId);
                return;
            }

            var working = await TestCaseManualForkResolution.RisolviAsync(
                _state.TestCases, fork, current, recovery, _state.Config.OperatoreAttivo, MergeConflittoDialogService.Risolvi);
            if (working == null)
            {
                MessaggioErrore = LocalizationService.Instance.Traduci("ForkRisoluzioneAnnullata");
                return;
            }
            var forkKey = TestCaseForkResolution.Key(fork);

            _state.RegistraAudit(
                "ResolveDelayedFork",
                working.Id,
                $"fork={forkKey}; branches={fork.Branches.Count}; revision={working.RevisioneDati}; uid={working.Uid:N}");

            DelayedForkRecoveryService.CleanupResolvedRecovery(_state.Paths, fork);
            await Task.Run(() => _state.TestCases.RefreshJournalMetadata());
            _state.NotificaDatiRicaricati();
            NotificaForkProperties();
            _onCambiato();
            Messaggio = string.Format(LocalizationService.Instance.Traduci("ForkRisoltoFmt"), working.Id);
        }
        catch (ConflittoRevisioneException)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("ForkCambiatoDuranteRisoluzione");
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Risoluzione fork journal", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ForkRisoluzioneFallitaFmt"), ex.Message);
        }
        finally { RisoluzioneForkInCorso = false; }
    }

    private void NotificaForkProperties()
    {
        OnPropertyChanged(nameof(ForkJournal));
        OnPropertyChanged(nameof(HaForkJournal));
        OnPropertyChanged(nameof(NumeroForkJournal));
        OnPropertyChanged(nameof(PuoRisolvereFork));
    }

    private void NotificaRecoveryProperties()
    {
        OnPropertyChanged(nameof(RecoveryBin));
        OnPropertyChanged(nameof(HaRecoveryBin));
        OnPropertyChanged(nameof(NumeroRecoveryBin));
        OnPropertyChanged(nameof(NumeroRecoveryDisponibili));
        OnPropertyChanged(nameof(PuoRipristinareRecovery));
    }

    private static bool SameFork(JournalForkConflict a, JournalForkConflict b)
    {
        if (a.EntityUid != b.EntityUid || a.BaseRevision != b.BaseRevision ||
            !string.Equals(a.BaseHash, b.BaseHash, StringComparison.OrdinalIgnoreCase)) return false;
        var ah = a.Branches.Select(x => x.NewHash ?? "").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var bh = b.Branches.Select(x => x.NewHash ?? "").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return ah.SequenceEqual(bh, StringComparer.OrdinalIgnoreCase);
    }
}
