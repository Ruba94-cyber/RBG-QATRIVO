using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Review indipendente 2026-09-24 [MEDIUM]: copertura di Unisci/Dividi (MasterUnioneService, motore
/// generico di MasterRiferimentiService.ApplicaSuCataloghiIncrociati) sul nuovo catalogo
/// ValutazioneRequisitoEcuVeicolo, e della regola per il caso in cui un'unione fa collassare due righe
/// diverse sulla stessa chiave di cella (RequisitoId|Componente|Veicolo).
///
/// Regola effettiva (review indipendente 2026-09-24, follow-up: il fail-closed puro bloccava un'unione
/// altrimenti legittima dell'utente): se Unisci/Rinomina riscrivendo Componente/Veicolo/RequisitoId farebbe
/// collassare due righe di ValutazioneRequisitoEcuVeicolo sulla stessa chiave di cella
/// (RequisitoId|Componente|Veicolo), MasterRiferimentiService.ApplicaSuCataloghiIncrociati consolida la collisione
/// PRIMA di salvare (ValutazioneRequisitoEcuVeicoloService.ConsolidaCollisioni): vince la riga con
/// UltimaModifica più recente, l'altra viene rimossa dalla lista ma la sua informazione confluisce nella nota
/// e nel Changelog della vincente — mai persa in silenzio. L'operazione riesce, una sola riga resta per cella.
/// Il guard generico PhaseDMasterRepositoryEngine.ValidateBusinessKeys resta comunque attivo come rete di
/// sicurezza su un Save che aggirasse il consolidamento (verificato qui direttamente).</summary>
public sealed class ValutazioneRequisitoEcuVeicoloUnisciDividiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ValReqEcuVeicolo_UnisciDividi_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } } }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    [Fact]
    public void Unisci_DueRigheCollidonoSullaStessaCella_OperazioneRiesceEConsolidaSenzaPerdereLInformazione()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM-OLD" }, new() { Nome = "BCM" } });
        var righeIniziali = new List<ValutazioneRequisitoEcuVeicolo>
        {
            new()
            {
                RequisitoId = "R1", Componente = "BCM-OLD", Veicolo = "My24", Punteggio = PunteggioConformita.NonConforme,
                Origine = OrigineValutazioneRequisito.Manuale, Nota = "nota vecchia (BCM-OLD)", Operatore = "AB",
                UltimaModifica = new DateTime(2026, 1, 1),
            },
            new()
            {
                RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
                Origine = OrigineValutazioneRequisito.Manuale, Nota = "nota nuova (già su BCM)", Operatore = "CD",
                UltimaModifica = new DateTime(2026, 2, 1),
            },
        };
        state.Master.ValutazioniRequisitoEcuVeicolo.Save(righeIniziali);

        // Anteprima: prima di confermare, l'app deve poter mostrare quante collisioni verranno consolidate.
        var collisioniPreviste = MasterRiferimentiService.ContaCollisioniValutazioni(state.Master, "Componente", "BCM-OLD", "BCM");
        Assert.Equal(1, collisioniPreviste);

        var avvisi = new List<string>();
        // Unire BCM-OLD in BCM riscrive la prima riga su Componente=BCM, collidendo con la seconda sulla stessa
        // chiave di cella (R1|BCM|My24): non fallisce più, viene consolidata prima del salvataggio.
        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM-OLD", "BCM", e => e.Nome, avvisi);

        Assert.Equal(new[] { "BCM" }, state.Master.Componente.Load().Select(e => e.Nome)); // unione riuscita normalmente
        var righeDopo = state.Master.ValutazioniRequisitoEcuVeicolo.Load();
        var vincitrice = Assert.Single(righeDopo); // una sola riga per la cella, non due
        Assert.Equal("BCM", vincitrice.Componente);
        // Vince la più recente (CD, nota "già su BCM"); l'informazione della perdente (AB, "nota vecchia") non
        // è persa: confluisce nella Nota della vincente.
        Assert.Contains("nota nuova (già su BCM)", vincitrice.Nota);
        Assert.Contains("AB", vincitrice.Nota);
        Assert.Contains("nota vecchia (BCM-OLD)", vincitrice.Nota);
        Assert.Equal(PunteggioConformita.Conforme, vincitrice.Punteggio); // il punteggio della vincente resta quello, non fuso
        Assert.Contains(vincitrice.Changelog, c => c.Campo == "(consolidamento collisione cella)");
        Assert.Contains(avvisi, a => a.Contains("consolidat") && a.Contains("1"));
    }

    [Fact]
    public void ValidateBusinessKeys_ReteDiSicurezza_RestaAttivaSuUnSaveCheAggiraIlConsolidamento()
    {
        // Il guard generico deve restare attivo per chi salva direttamente (bypassando
        // ApplicaSuCataloghiIncrociati/ConsolidaCollisioni), es. un import o un bug futuro in un altro punto:
        // due righe con la STESSA chiave di cella nello stesso Save devono continuare a essere rifiutate.
        var state = CreaStato();
        var righe = new List<ValutazioneRequisitoEcuVeicolo>
        {
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme },
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.NonConforme },
        };
        Assert.Throws<PhaseDMasterBusinessKeyConflictException>(() => state.Master.ValutazioniRequisitoEcuVeicolo.Save(righe));
    }

    [Fact]
    public void Unisci_DueRigheSuCelleDiverse_NonCollidono_UnioneRiesceEVaARicoprireEntrambeLeChiaviAggiornate()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM-OLD" }, new() { Nome = "BCM" } });
        state.Master.ValutazioniRequisitoEcuVeicolo.Save(new List<ValutazioneRequisitoEcuVeicolo>
        {
            new() { RequisitoId = "R1", Componente = "BCM-OLD", Veicolo = "My24", Punteggio = PunteggioConformita.NonConforme, Nota = "nota A", Operatore = "AB" },
            new() { RequisitoId = "R2", Componente = "BCM", Veicolo = "My26", Punteggio = PunteggioConformita.Conforme, Nota = "nota B", Operatore = "CD" },
        });

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM-OLD", "BCM", e => e.Nome, avvisi);

        var righe = state.Master.ValutazioniRequisitoEcuVeicolo.Load();
        Assert.Equal(2, righe.Count);
        Assert.All(righe, r => Assert.Equal("BCM", r.Componente)); // chiavi diverse (R1/My24 vs R2/My26): nessuna collisione, unione riuscita
        Assert.Contains(righe, r => r.Nota == "nota A" && r.RequisitoId == "R1" && r.Veicolo == "My24");
        Assert.Contains(righe, r => r.Nota == "nota B" && r.RequisitoId == "R2" && r.Veicolo == "My26");
    }

    [Fact]
    public void Unisci_RinominaLaSorgenteDiUnaPropagazione_SenzaCollisione()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM-OLD" }, new() { Nome = "BCM" } });
        state.Master.ValutazioniRequisitoEcuVeicolo.Save(new List<ValutazioneRequisitoEcuVeicolo>
        {
            new()
            {
                RequisitoId = "R1", Componente = "ECM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
                Origine = OrigineValutazioneRequisito.Propagata, SorgenteVeicolo = "My24", SorgenteTestId = "T1",
                Nota = "propagata", Operatore = "AB", UltimaModifica = new DateTime(2026, 1, 1),
            },
        });
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente { Nome = "ECM" }).ToList());

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM-OLD", "BCM", e => e.Nome, avvisi);

        var riga = Assert.Single(state.Master.ValutazioniRequisitoEcuVeicolo.Load());
        Assert.Equal("ECM", riga.Componente); // non toccata: non referenziava BCM-OLD
    }

    [Fact]
    public void Dividi_CampoEcuSingoloRestaSull_OriginaleSenzaAvvisi()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" } });
        state.Master.ValutazioniRequisitoEcuVeicolo.Save(new List<ValutazioneRequisitoEcuVeicolo>
        {
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme, Nota = "nota", Operatore = "AB" },
        });

        var avvisi = new List<string>();
        MasterUnioneService.Dividi(state, state.Master.Componente, "Componente", "BCM", new[] { "BCM-A", "BCM-B" }, e => e.Nome, avvisi);

        var riga = Assert.Single(state.Master.ValutazioniRequisitoEcuVeicolo.Load());
        Assert.Equal("BCM", riga.Componente); // il campo singolo resta sull'originale (Dividi non rimuove mai A)
        Assert.Equal("nota", riga.Nota); // nessuna perdita del resto della riga
        Assert.DoesNotContain(avvisi, a => a.Contains("limitato al primo nuovo valore"));
    }

    [Fact]
    public void Dividi_CampoVeicoloSingoloRestaSull_OriginaleSenzaAvvisi()
    {
        var state = CreaStato();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "My24" } });
        state.Master.ValutazioniRequisitoEcuVeicolo.Save(new List<ValutazioneRequisitoEcuVeicolo>
        {
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme, Nota = "nota", Operatore = "AB" },
        });

        var avvisi = new List<string>();
        MasterUnioneService.Dividi(state, state.Master.Veicoli, "Veicolo", "My24", new[] { "My24-A", "My24-B" }, v => v.Nome, avvisi);

        var riga = Assert.Single(state.Master.ValutazioniRequisitoEcuVeicolo.Load());
        Assert.Equal("My24", riga.Veicolo);
        Assert.DoesNotContain(avvisi, a => a.Contains("limitato al primo nuovo valore"));
    }

    // --- Review 2026-09-24 [LOW]: conflitto multi-PC su Propaga/ImpostaManuale ---

    [Fact]
    public void MultiPc_SnapshotStaleDopoScritturaConcorrente_FallisceChiuso_NessunaSovrascrittura()
    {
        var dataRoot = Path.Combine(_dir, "data-multipc");
        var paths = new DataPaths(dataRoot);
        paths.EnsureStructure();
        var repoPc1 = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(paths.ValutazioniRequisitoEcuVeicoloFile);
        var repoPc2 = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(paths.ValutazioniRequisitoEcuVeicoloFile);

        // PC1 apre la finestra Load (come fa ValutazioneRequisitoEcuVeicoloService.ProvaAggiorna prima di
        // mutare) e cattura lo snapshot atteso.
        var listaPc1 = repoPc1.Load();
        var attesoPc1 = EntityStateFingerprint.Clone(listaPc1);

        // Nel frattempo PC2, sulla stessa cartella, scrive una valutazione attraverso il servizio pubblico.
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repoPc2, "R1", "BCM", new[] { "My24" }, PunteggioConformita.Conforme, "nota di PC2", "PC2", null, out var erroreImpostazionePc2));
        Assert.Null(erroreImpostazionePc2);

        // PC1 prova a salvare sullo snapshot ormai stale: la finestra Load/Save si è sovrapposta a quella di
        // PC2. Deve fallire chiuso (MasterDataConflictException), non sovrascrivere silenziosamente.
        listaPc1.Add(new ValutazioneRequisitoEcuVeicolo
        {
            RequisitoId = "R2", Componente = "ECM", Veicolo = "My26", Punteggio = PunteggioConformita.NonConforme,
            Origine = OrigineValutazioneRequisito.Manuale, Nota = "nota di PC1", Operatore = "PC1",
        });
        Assert.Throws<MasterDataConflictException>(() => repoPc1.Save(listaPc1, attesoPc1));

        // Nessuna sovrascrittura silenziosa: il catalogo condiviso contiene SOLO la riga di PC2, con la sua
        // nota intatta — non quella (mai confermata) di PC1.
        var stato = repoPc1.Load();
        var riga = Assert.Single(stato);
        Assert.Equal("BCM", riga.Componente);
        Assert.Equal("nota di PC2", riga.Nota);

        // Lo stesso Propaga/ImpostaManuale di PC1, richiamato di nuovo (Load fresco a ogni chiamata, stesso
        // principio di AuditViewModel.ProvaAggiornaRisoluzioni), riesce senza perdere il lavoro di PC2: il
        // conflitto rilevato non lascia il workspace bloccato.
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repoPc1, "R2", "ECM", new[] { "My26" }, PunteggioConformita.NonConforme, "nota di PC1 dopo retry", "PC1", null, out var erroreRetryPc1));
        Assert.Null(erroreRetryPc1);
        var statoFinale = repoPc1.Load();
        Assert.Equal(2, statoFinale.Count);
        Assert.Contains(statoFinale, r => r.Nota == "nota di PC2");
        Assert.Contains(statoFinale, r => r.Nota == "nota di PC1 dopo retry");
    }

    [Fact]
    public void MultiPc_FinestraSovrappostaSuUnaRigaPropagata_FallisceChiuso_NessunaSovrascrittura()
    {
        // Stessa dimostrazione della prova sopra, ma sulla mutazione che produrrebbe Propaga (Origine=
        // Propagata con Sorgente*) invece di ImpostaManuale: il fail-closed di repo.Save e' lo stesso
        // meccanismo per entrambe (ProvaAggiorna e' condiviso), qui verificato esplicitamente anche sul
        // "sapore" Propagata per non lasciarlo solo implicito.
        var dataRoot = Path.Combine(_dir, "data-multipc-propaga");
        var paths = new DataPaths(dataRoot);
        paths.EnsureStructure();
        var repoPc1 = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(paths.ValutazioniRequisitoEcuVeicoloFile);
        var repoPc2 = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(paths.ValutazioniRequisitoEcuVeicoloFile);

        var listaPc1 = repoPc1.Load();
        var attesoPc1 = EntityStateFingerprint.Clone(listaPc1);

        // PC2 scrive nel frattempo, sulla stessa cartella, attraverso Propaga.
        var sorgente = new CandidatoPropagazioneRequisito("My26", "T1", PunteggioConformita.Conforme, DateTime.Now);
        Assert.True(ValutazioneRequisitoEcuVeicoloService.Propaga(repoPc2, "R9", "TCU", new[] { ("My99", sorgente) }, "propagazione di PC2", "PC2", out var erroreProgagazionePc2));
        Assert.Null(erroreProgagazionePc2);

        // PC1, sullo snapshot stale catturato prima della scrittura di PC2, prova a salvare la propria riga
        // Propagata: la finestra Load/Save si e' sovrapposta a quella di PC2, deve fallire chiuso.
        listaPc1.Add(new ValutazioneRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
            Origine = OrigineValutazioneRequisito.Propagata, SorgenteVeicolo = "My26", SorgenteTestId = "T1",
            SorgentePunteggio = PunteggioConformita.Conforme, Nota = "propagazione di PC1", Operatore = "PC1",
        });
        Assert.Throws<MasterDataConflictException>(() => repoPc1.Save(listaPc1, attesoPc1));

        var stato = repoPc1.Load();
        var riga = Assert.Single(stato);
        Assert.Equal("propagazione di PC2", riga.Nota); // nessuna sovrascrittura silenziosa della riga di PC2
        Assert.DoesNotContain(stato, r => r.Nota == "propagazione di PC1"); // la riga mai confermata di PC1 non è entrata

        // Il retry con Load fresco (stesso comportamento di Propaga chiamato di nuovo da PC1) riesce e
        // convive con la riga di PC2, senza perderla.
        Assert.True(ValutazioneRequisitoEcuVeicoloService.Propaga(repoPc1, "R1", "BCM", new[] { ("My24", sorgente) }, "propagazione di PC1 dopo retry", "PC1", out var erroreRetry));
        Assert.Null(erroreRetry);
        var statoFinale = repoPc1.Load();
        Assert.Equal(2, statoFinale.Count);
        Assert.Contains(statoFinale, r => r.Nota == "propagazione di PC2");
        Assert.Contains(statoFinale, r => r.Nota == "propagazione di PC1 dopo retry");
    }
}
