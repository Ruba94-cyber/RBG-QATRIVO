using System.Reflection;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Correzioni review 6.5.0 sull'archivio guasto dei requisiti: salvataggio requisiti fallito DOPO la scrittura dell'archivio
/// (retry coerente, legacy incluso), Unisci/Dividi/Rinomina/Elimina requisiti, conteggio stantio = ignoto, merge per voce tra due PC
/// sullo stesso requisito e caso misto embedded + conteggio stantio.</summary>
[Collection("Localization")]
public sealed class RequisitoDtcArchivioCoerenzaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_dtc_coer_" + Guid.NewGuid().ToString("N"));
    private readonly List<AppState> _stati = new();

    public void Dispose()
    {
        foreach (var s in _stati) { try { s.Dispose(); } catch { } }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AppState Nuovo(bool master = true)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        _stati.Add(state);
        if (master)
        {
            var ops = state.Master.Operatori.Load().ToList();
            if (!ops.Any(o => o.Nome == "Master Coer"))
            {
                ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Coer", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") }));
                state.Master.Operatori.Save(ops);
            }
            Assert.True(state.ImpostaOperatore("Master Coer", "1234"));
        }
        return state;
    }

    private static Requisito Extra(string id)
    {
        var r = new Requisito { Id = id, Testo = "t" };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    private static List<DtcRequisito> Voci(string prefisso, int n) =>
        Enumerable.Range(0, n).Select(i => new DtcRequisito { Codice = $"{prefisso}{i:D3}", Componente = "ECM" }).ToList();

    private static Requisito Req(AppState s, string id) => s.Master.Requisiti.Load().Single(r => r.Id == id);

    /// <summary>Seed formato 6.5.0: requisito con conteggio + archivio. <paramref name="conteggio"/> permette di simulare un suggerimento stantio.</summary>
    private static void Semina(AppState s, string id, List<DtcRequisito> voci, int? conteggio = -1)
    {
        var r = Extra(id);
        r.DtcConteggio = conteggio == -1 ? voci.Count : conteggio;
        s.Master.Requisiti.Save(new List<Requisito> { r });
        s.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(Req(s, id), voci, null) }, "Seed");
    }

    private static HashSet<string> Codici(IEnumerable<DtcRequisito> v) => v.Select(x => x.Codice).ToHashSet();

    /// <summary>Repository che fallisce al primo Save con l'errore dato e poi delega al repository vero (simula conflitto/IO dopo la
    /// scrittura degli archivi).</summary>
    private sealed class RepoCheFallisceUnaVolta : MasterDataRepository<Requisito>
    {
        private readonly MasterDataRepository<Requisito> _vero;
        private Exception? _errore;
        public int Chiamate { get; private set; }
        public RepoCheFallisceUnaVolta(string path, MasterDataRepository<Requisito> vero, Exception errore) : base(path) { _vero = vero; _errore = errore; }
        public override void Save(List<Requisito> items, IReadOnlyList<Requisito>? expectedSnapshot, bool allowSilentRemoteMerge = true, string? baselineSourceFingerprint = null)
        {
            Chiamate++;
            if (_errore is { } e) { _errore = null; throw e; }
            _vero.Save(items, expectedSnapshot, allowSilentRemoteMerge, baselineSourceFingerprint);
        }
    }

    private static RepoCheFallisceUnaVolta Inietta(RequisitiViewModel vm, AppState s, Exception errore)
    {
        var repo = new RepoCheFallisceUnaVolta(s.Paths.RequisitiFile, s.Master.Requisiti, errore);
        typeof(MasterDataEditorViewModelBase<Requisito>).GetField("_repo", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, repo);
        return repo;
    }

    // === HIGH-1 ============================================================================================

    public static IEnumerable<object[]> Errori() => new[]
    {
        new object[] { new MasterDataConflictException("requisiti") },
        new object[] { new IOException("disco non raggiungibile") },
    };

    [Theory]
    [MemberData(nameof(Errori))]
    public async Task SalvataggioRequisitiFallitoDopoArchivio_Legacy_RetryAllineaConteggioEDtcNull(Exception errore)
    {
        var s = Nuovo();
        var d1 = Extra("D1"); d1.Guasto = new List<DtcRequisito> { new() { Codice = "A1", Componente = "ECM" }, new() { Codice = "A2", Componente = "ECM" } };
        s.Master.Requisiti.Save(new List<Requisito> { d1 });

        using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single();
        vm.NuovoDtcCodice = "A3"; vm.NuovoDtcEcu = "BCM";
        vm.AggiungiDtcCommand.Execute(null);
        var repo = Inietta(vm, s, errore);

        await vm.SalvaCommand.ExecuteAsync(null);                       // archivio scritto, commit requisiti fallito
        Assert.True(vm.InModifica);
        Assert.NotNull(vm.MessaggioErrore);
        Assert.Equal(1, repo.Chiamate);
        // requisito su disco ancora legacy (embedded a 2) ma l'archivio ha gia 3 voci
        Assert.Equal(2, Req(s, "D1").Guasto!.Count);

        await vm.SalvaCommand.ExecuteAsync(null);                       // retry: stesso stato di lavoro, nessuna modifica nuova
        Assert.False(vm.InModifica, vm.MessaggioErrore);
        var dopo = Req(s, "D1");
        Assert.Null(dopo.Guasto);
        Assert.Equal(3, dopo.DtcConteggio);
        Assert.Equal(new[] { "A1", "A2", "A3" }, Codici(s.Master.RequisitiDtc.Leggi(dopo).Voci).OrderBy(x => x));
    }

    [Theory]
    [MemberData(nameof(Errori))]
    public async Task SalvataggioRequisitiFallitoDopoArchivio_Archiviato_RetryAggiornaConteggio(Exception errore)
    {
        var s = Nuovo();
        Semina(s, "D1", Voci("B", 4));
        using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single();
        vm.NuovoDtcCodice = "NEW"; vm.NuovoDtcEcu = "BCM";
        vm.AggiungiDtcCommand.Execute(null);
        Inietta(vm, s, errore);

        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);
        Assert.Equal(4, Req(s, "D1").DtcConteggio);          // commit non avvenuto
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);
        Assert.Equal(5, Req(s, "D1").DtcConteggio);
        Assert.Equal(5, s.Master.RequisitiDtc.Leggi(Req(s, "D1")).Voci.Count);
    }

    // === HIGH-2 ============================================================================================

    [Fact]
    public void Unisci_PortaIDtcDellOrigineNellaDestinazione_SenzaDuplicati_EOrigineSenzaArchivioOrfano()
    {
        var s = Nuovo();
        Semina(s, "D1", Voci("A", 3));                       // destinazione A000..A002
        var d2 = Extra("D2"); d2.DtcConteggio = 4; var lista = s.Master.Requisiti.Load().ToList(); lista.Add(d2); s.Master.Requisiti.Save(lista);
        var origine = Voci("A", 2);                          // A000, A001 (duplicati) + B000, B001
        origine.AddRange(Voci("B", 2));
        s.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(Req(s, "D2"), origine, null) }, "Seed");
        var uidOrigine = MasterEntityIdentityService.GetRequired(Req(s, "D2"));

        MasterUnioneService.Unisci(s, s.Master.Requisiti, "Requisito", "D2", "D1", r => r.Id, new List<string>());

        var d1 = Req(s, "D1");
        Assert.DoesNotContain(s.Master.Requisiti.Load(), r => r.Id == "D2");
        Assert.Equal(new[] { "A000", "A001", "A002", "B000", "B001" }, Codici(s.Master.RequisitiDtc.Leggi(d1).Voci).OrderBy(x => x));
        Assert.Equal(5, d1.DtcConteggio);
        Assert.False(s.Master.RequisitiDtc.ConteggiLeggeri().ContainsKey(uidOrigine));   // archivio origine tombstonato
    }

    [Fact]
    public void Unisci_DestinazioneLegacyEmbedded_MigraNellArchivioEPortaLeVociDellOrigine()
    {
        var s = Nuovo();
        var d1 = Extra("D1"); d1.Guasto = Voci("L", 2);
        var d2 = Extra("D2"); d2.Guasto = Voci("M", 2);
        s.Master.Requisiti.Save(new List<Requisito> { d1, d2 });

        MasterUnioneService.Unisci(s, s.Master.Requisiti, "Requisito", "D2", "D1", r => r.Id, new List<string>());

        var dopo = Req(s, "D1");
        Assert.Null(dopo.Guasto);
        Assert.Equal(4, dopo.DtcConteggio);
        Assert.Equal(4, s.Master.RequisitiDtc.Leggi(dopo).Voci.Count);
    }

    [Fact]
    public void Dividi_LaCopiaNasceSenzaDtc_LOriginaleLiMantiene()
    {
        var s = Nuovo();
        Semina(s, "D1", Voci("A", 3));
        MasterUnioneService.Dividi(s, s.Master.Requisiti, "Requisito", "D1", new[] { "D1-bis" }, r => r.Id, new List<string>());

        var originale = Req(s, "D1");
        var copia = Req(s, "D1-bis");
        Assert.Equal(3, s.Master.RequisitiDtc.Leggi(originale).Voci.Count);
        Assert.Empty(s.Master.RequisitiDtc.Leggi(copia).Voci);
        Assert.Equal(0, copia.DtcTotale);
        Assert.NotEqual(MasterEntityIdentityService.GetRequired(originale), MasterEntityIdentityService.GetRequired(copia));
    }

    [Fact]
    public void Rinomina_ConservaUidEDtc()
    {
        var s = Nuovo();
        Semina(s, "D1", Voci("A", 3));
        var uid = MasterEntityIdentityService.GetRequired(Req(s, "D1"));
        MasterUnioneService.Rinomina(s, s.Master.Requisiti, "Requisito", "D1", "D1-nuovo", r => r.Id, new List<string>());
        var r = Req(s, "D1-nuovo");
        Assert.Equal(uid, MasterEntityIdentityService.GetRequired(r));
        Assert.Equal(3, s.Master.RequisitiDtc.Leggi(r).Voci.Count);
    }

    [Fact]
    public async Task EliminazioneRequisito_TombstonaLArchivioDtc()
    {
        var s = Nuovo();
        Semina(s, "D1", Voci("A", 3));
        Semina2(s, "D2", Voci("B", 2));
        var uid1 = MasterEntityIdentityService.GetRequired(Req(s, "D1"));
        var uid2 = MasterEntityIdentityService.GetRequired(Req(s, "D2"));
        using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.Items.Remove(vm.Items.Single(r => r.Id == "D1"));
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);
        var conteggi = s.Master.RequisitiDtc.ConteggiLeggeri();
        Assert.False(conteggi.ContainsKey(uid1));
        Assert.Equal(2, conteggi[uid2]);
    }

    private static void Semina2(AppState s, string id, List<DtcRequisito> voci)
    {
        var lista = s.Master.Requisiti.Load().ToList();
        var r = Extra(id); r.DtcConteggio = voci.Count; lista.Add(r);
        s.Master.Requisiti.Save(lista);
        s.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(Req(s, id), voci, null) }, "Seed");
    }

    // === MEDIUM-3 ==========================================================================================

    [Fact]
    public void ConteggioStantioZero_ConArchivioPieno_SelettoreERiepilogoCorretti()
    {
        var s = Nuovo();
        Semina(s, "D1", Voci("A", 12), conteggio: null);     // suggerimento stantio/assente, archivio con 12 guasto
        var r = Req(s, "D1");
        Assert.Equal(0, r.DtcTotale);                        // prima della verifica: ignoto
        s.Master.RequisitiDtc.Risolvi(new[] { r });
        Assert.Equal(12, r.DtcTotale);

        var riepilogo = DtcAvanzamentoProvider.CalcolaRiepilogo(s, new[] { Req(s, "D1") });
        Assert.Equal(12, riepilogo.Totale.Totale);

        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(new[] { "ECM" }), Veicolo = "V", Veicoli = { "V" }, RequisitoId = { "D1" } };
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.DaEseguire, Matricola = "x", Condizioni = { new Condizione { Esito = Esito.DaEseguire } } });
        s.TestCases.Save(tc, true);
        var vm = new TestCaseEditorViewModel(s, "T-1", () => { }, _ => { });
        Assert.True(vm.MostraDtcOperazione);
        Assert.True(vm.DtcUsaPicker);                        // 12 > soglia picker
    }

    [Fact]
    public void ConteggioStantioZero_ConteggiLeggeriNonMaterializzaPayload_EVaInvalidatoDallaScrittura()
    {
        var s = Nuovo();
        Semina(s, "D1", Voci("A", 5));
        var uid = MasterEntityIdentityService.GetRequired(Req(s, "D1"));
        Assert.Equal(5, s.Master.RequisitiDtc.ConteggiLeggeri()[uid]);
        var r = Req(s, "D1");
        var b = s.Master.RequisitiDtc.Leggi(r);
        s.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(r, Voci("A", 7), b.Documento) }, "t");
        Assert.Equal(7, s.Master.RequisitiDtc.ConteggiLeggeri()[uid]);
    }

    // === MEDIUM-4 ==========================================================================================

    [Fact]
    public void DuePc_StessoRequisito_MergePerVoce_NessunaPerditaDiAggiunteERimozioni()
    {
        var a = Nuovo(); var b = Nuovo();
        Semina(a, "D1", Voci("X", 4));                       // X000..X003
        var ra = Req(a, "D1"); var rb = Req(b, "D1");
        var baseA = a.Master.RequisitiDtc.Leggi(ra); var baseB = b.Master.RequisitiDtc.Leggi(rb);

        // PC-A: aggiunge A1, rimuove X000. PC-B (baseline vecchia): aggiunge B1, rimuove X001.
        var voceA = baseA.Voci.Where(v => v.Codice != "X000").Append(new DtcRequisito { Codice = "A1", Componente = "ECM" }).ToList();
        a.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(ra, voceA, baseA.Documento) }, "PC-A");
        var voceB = baseB.Voci.Where(v => v.Codice != "X001").Append(new DtcRequisito { Codice = "B1", Componente = "ECM" }).ToList();
        var scritti = b.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(rb, voceB, baseB.Documento) }, "PC-B");

        var finale = Codici(Nuovo(false).Master.RequisitiDtc.Leggi(Req(a, "D1")).Voci);
        Assert.Equal(new[] { "A1", "B1", "X002", "X003" }, finale.OrderBy(x => x));
        Assert.Equal(4, scritti.Values.Single().Voci.Count);  // la baseline restituita contiene il risultato fuso
    }

    [Fact]
    public void DuePc_CreazioneConcorrente_BaselineNullaConDocumentoGiaPresente_Unione()
    {
        var a = Nuovo(); var b = Nuovo();
        var r = Extra("D1");
        a.Master.Requisiti.Save(new List<Requisito> { r });
        var ra = Req(a, "D1"); var rb = Req(b, "D1");
        a.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(ra, new List<DtcRequisito> { new() { Codice = "A1", Componente = "ECM" } }, null) }, "PC-A");
        b.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(rb, new List<DtcRequisito> { new() { Codice = "B1", Componente = "ECM" } }, null) }, "PC-B");
        Assert.Equal(new[] { "A1", "B1" }, Codici(Nuovo(false).Master.RequisitiDtc.Leggi(Req(a, "D1")).Voci).OrderBy(x => x));
    }

    [Fact]
    public async Task DuePc_EditorSalva_AdottaIlRisultatoFusoEConteggioCoerente()
    {
        var a = Nuovo(); var b = Nuovo();
        Semina(a, "D1", Voci("X", 3));
        using var vm = new RequisitiViewModel(b, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single();
        vm.NuovoDtcCodice = "B1"; vm.NuovoDtcEcu = "ECM";
        vm.AggiungiDtcCommand.Execute(null);
        // nel frattempo l'altro PC scrive lo stesso requisito
        var ra = Req(a, "D1"); var ba = a.Master.RequisitiDtc.Leggi(ra);
        a.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(ra, ba.Voci.Append(new DtcRequisito { Codice = "A1", Componente = "ECM" }).ToList(), ba.Documento) }, "PC-A");
        // forza l'assenza della precondizione di catalogo (la policy Master e coperta da altri test) per esercitare il solo archivio
        typeof(MasterDataEditorViewModelBase<Requisito>).GetField("_modificaSeguitaAForzatura", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, true);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);
        var dopo = Req(b, "D1");
        Assert.Equal(5, dopo.DtcConteggio);
        Assert.Equal(new[] { "A1", "B1", "X000", "X001", "X002" }, Codici(b.Master.RequisitiDtc.Leggi(dopo).Voci).OrderBy(x => x));
    }

    // === MEDIUM-5 ==========================================================================================

    [Fact]
    public async Task LegacyMisto_EmbeddedConConteggioStantio_DopoSalvataggioCoerente()
    {
        var s = Nuovo();
        var d1 = Extra("D1"); d1.Guasto = Voci("L", 2); d1.DtcConteggio = 9;   // ambiguo: embedded 2, suggerimento 9
        s.Master.Requisiti.Save(new List<Requisito> { d1 });
        var letto = Req(s, "D1");
        Assert.Equal(2, letto.DtcTotale);                    // l'embedded ha la precedenza finche esiste

        using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single();
        vm.NuovoDtcCodice = "L9"; vm.NuovoDtcEcu = "ECM";
        vm.AggiungiDtcCommand.Execute(null);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);
        var dopo = Req(s, "D1");
        Assert.Null(dopo.Guasto);
        Assert.Equal(3, dopo.DtcConteggio);
        Assert.Equal(3, dopo.DtcTotale);
        Assert.Equal(3, s.Master.RequisitiDtc.Leggi(dopo).Voci.Count);
    }

    // === Review 6.5.0 (round 2): M1 Annulla dopo Salva fallito, M2 unione tra tipi, L1 elimina da Archivio, L2 Uid ricreato ===

    private static RequisitiViewModel ApriConDtcModificato(AppState s, string codiceNuovo, bool rimuoviPrimo = false)
    {
        var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single();
        vm.NuovoDtcCodice = codiceNuovo; vm.NuovoDtcEcu = "BCM";
        vm.AggiungiDtcCommand.Execute(null);
        if (rimuoviPrimo) vm.RimuoviDtcCommand.Execute(vm.DtcVisibili.First());
        return vm;
    }

    [Fact]
    public async Task AnnullaDopoSalvaFallito_Archiviato_ArchivioTornaIdenticoAllOriginale_NessunaResurrezione()
    {
        var s = Nuovo();
        Semina(s, "D1", Voci("B", 4));
        using (var vm = ApriConDtcModificato(s, "NEW", rimuoviPrimo: true))
        {
            Inietta(vm, s, new IOException("disco"));
            await vm.SalvaCommand.ExecuteAsync(null);
            Assert.True(vm.InModifica);
            Assert.Equal(new[] { "B001", "B002", "B003", "NEW" }, Codici(s.Master.RequisitiDtc.Leggi(Req(s, "D1")).Voci).OrderBy(x => x)); // archivio gia scritto
            vm.AnnullaCommand.Execute(null);
            Assert.False(vm.InModifica, vm.MessaggioErrore);
        }
        var r = Req(s, "D1");
        Assert.Equal(new[] { "B000", "B001", "B002", "B003" }, Codici(s.Master.RequisitiDtc.Leggi(r).Voci).OrderBy(x => x));
        Assert.Equal(4, r.DtcConteggio);

        // nuova sessione: aggiunge solo Z; i guasti annullati (NEW) non devono ricomparire
        using var vm2 = ApriConDtcModificato(s, "ZZ");
        await vm2.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm2.InModifica, vm2.MessaggioErrore);
        Assert.Equal(new[] { "B000", "B001", "B002", "B003", "ZZ" }, Codici(s.Master.RequisitiDtc.Leggi(Req(s, "D1")).Voci).OrderBy(x => x));
    }

    [Fact]
    public async Task AnnullaDopoSalvaFallito_Legacy_ArchivioNonEsistePiu_NessunaResurrezioneAlSalvataggioSuccessivo()
    {
        var s = Nuovo();
        var d1 = Extra("D1"); d1.Guasto = new List<DtcRequisito> { new() { Codice = "A1", Componente = "ECM" }, new() { Codice = "A2", Componente = "ECM" } };
        s.Master.Requisiti.Save(new List<Requisito> { d1 });
        var uid = MasterEntityIdentityService.GetRequired(Req(s, "D1"));
        using (var vm = ApriConDtcModificato(s, "A3"))
        {
            Inietta(vm, s, new IOException("disco"));
            await vm.SalvaCommand.ExecuteAsync(null);
            Assert.True(s.Master.RequisitiDtc.ConteggiLeggeri().ContainsKey(uid));   // archivio scritto dal Salva fallito
            vm.AnnullaCommand.Execute(null);
            Assert.False(vm.InModifica, vm.MessaggioErrore);
        }
        // l'archivio torna alle voci ORIGINALI (A1,A2 = quelle embedded) e non contiene la voce annullata A3
        Assert.Equal(2, s.Master.RequisitiDtc.ConteggiLeggeri().GetValueOrDefault(uid));
        var senzaEmbedded = Req(s, "D1"); senzaEmbedded.Guasto = null;
        Assert.DoesNotContain("A3", Codici(s.Master.RequisitiDtc.Leggi(senzaEmbedded).Voci));
        Assert.Equal(2, Req(s, "D1").Guasto!.Count);

        using var vm2 = ApriConDtcModificato(s, "ZZ");                                // l'archivio puo' esistere vuoto
        await vm2.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm2.InModifica, vm2.MessaggioErrore);
        Assert.Equal(new[] { "A1", "A2", "ZZ" }, Codici(s.Master.RequisitiDtc.Leggi(Req(s, "D1")).Voci).OrderBy(x => x));
    }

    [Fact]
    public async Task AnnullaDopoSalvaFallito_Legacy_AltroPcAggiungeDtcNelFrattempo_NonVieneCancellato()
    {
        var s = Nuovo();
        var d1 = Extra("D1"); d1.Guasto = new List<DtcRequisito> { new() { Codice = "A1", Componente = "ECM" }, new() { Codice = "A2", Componente = "ECM" } };
        s.Master.Requisiti.Save(new List<Requisito> { d1 });
        var altro = Nuovo(false);                                                    // secondo PC sulla stessa cartella
        var uid = MasterEntityIdentityService.GetRequired(Req(s, "D1"));
        using (var vm = ApriConDtcModificato(s, "A3"))
        {
            Inietta(vm, s, new IOException("disco"));
            await vm.SalvaCommand.ExecuteAsync(null);                                           // archivio scritto: A1,A2,A3
            // l'altro PC aggiunge ALTRO allo stesso archivio mentre il Salva e' fallito
            var rAltro = Req(altro, "D1"); rAltro.Guasto = null;
            var corrente = altro.Master.RequisitiDtc.Leggi(rAltro);
            altro.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(rAltro,
                corrente.Voci.Append(new DtcRequisito { Codice = "ALTRO", Componente = "ECM" }).ToList(), corrente.Documento) }, "PC-B");
            vm.AnnullaCommand.Execute(null);
            Assert.False(vm.InModifica, vm.MessaggioErrore);
        }
        var rv = Req(s, "D1"); rv.Guasto = null;
        var voci = Codici(s.Master.RequisitiDtc.Leggi(rv).Voci).ToList();
        Assert.Contains("ALTRO", voci);                                              // dati altrui preservati
        Assert.DoesNotContain("A3", voci);                                           // la nostra aggiunta annullata e' sparita
        Assert.True(s.Master.RequisitiDtc.ConteggiLeggeri().GetValueOrDefault(uid) >= 1);
    }

    [Fact]
    public async Task AnnullaDopoSalvaFallito_RiscritturaFallita_EditingResteAperto_ERiprovaOk()
    {
        var s = Nuovo();
        Semina(s, "D1", Voci("B", 3));
        using var vm = ApriConDtcModificato(s, "NEW");
        Inietta(vm, s, new IOException("disco"));
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);

        var campo = typeof(RequisitoDtcArchivio).GetField("_paths", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var originali = campo.GetValue(s.Master.RequisitiDtc);
        campo.SetValue(s.Master.RequisitiDtc, new DataPaths(Path.Combine(_dir, "inesistente")));   // archivio non scrivibile
        vm.AnnullaCommand.Execute(null);
        Assert.True(vm.InModifica);                                                  // editing NON chiuso
        Assert.False(string.IsNullOrEmpty(vm.MessaggioErrore));
        Assert.Contains("NEW", vm.DtcVisibili.Select(v => v.Codice));                // copia di lavoro intatta

        campo.SetValue(s.Master.RequisitiDtc, originali);
        vm.AnnullaCommand.Execute(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);
        Assert.Equal(new[] { "B000", "B001", "B002" }, Codici(s.Master.RequisitiDtc.Leggi(Req(s, "D1")).Voci).OrderBy(x => x));
    }

    [Fact]
    public void Unisci_TipiDiversiConDtcNellOrigine_Rifiutata_SenzaScrivereNulla()
    {
        var s = Nuovo();
        Semina(s, "D1", Voci("A", 3));                       // origine Extra con 3 guasto
        var std = new Requisito { Id = "S1", Testo = "t" };
        var lista = s.Master.Requisiti.Load().ToList(); lista.Add(std); s.Master.Requisiti.Save(lista);
        var uid = MasterEntityIdentityService.GetRequired(Req(s, "D1"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MasterUnioneService.Unisci(s, s.Master.Requisiti, "Requisito", "D1", "S1", r => r.Id, new List<string>()));
        Assert.Contains("D1", ex.Message);
        Assert.Contains(s.Master.Requisiti.Load(), r => r.Id == "D1");
        Assert.Equal(3, s.Master.RequisitiDtc.ConteggiLeggeri()[uid]);               // archivio origine intatto
        Assert.Equal(3, s.Master.RequisitiDtc.Leggi(Req(s, "D1")).Voci.Count);
    }

    [Fact]
    public void Unisci_TipiDiversiSenzaDtcNellOrigine_Consentita()
    {
        var s = Nuovo();
        var d1 = Extra("D1");
        var std = new Requisito { Id = "S1", Testo = "t" };
        s.Master.Requisiti.Save(new List<Requisito> { d1, std });
        MasterUnioneService.Unisci(s, s.Master.Requisiti, "Requisito", "D1", "S1", r => r.Id, new List<string>());
        Assert.DoesNotContain(s.Master.Requisiti.Load(), r => r.Id == "D1");
    }

    [Fact]
    public async Task EliminaDefinitivamenteDaArchivio_TombstonaLArchivioDtc()
    {
        var s = Nuovo();
        Semina(s, "D1", Voci("A", 3));
        var uid = MasterEntityIdentityService.GetRequired(Req(s, "D1"));
        var l = s.Master.Requisiti.Load().ToList(); l.Single().Archiviata = true; s.Master.Requisiti.Save(l);
        using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        Assert.True(s.Master.RequisitiDtc.ConteggiLeggeri().ContainsKey(uid));
        var errore = await vm.EliminaDefinitivamenteDaArchivio("D1");
        Assert.Null(errore);
        Assert.DoesNotContain(s.Master.Requisiti.Load(), r => r.Id == "D1");
        Assert.False(s.Master.RequisitiDtc.ConteggiLeggeri().ContainsKey(uid));
    }

    [Fact]
    public void UidRicreatoDopoTombstone_ScriviConBaselineNull_RicreaLArchivio()
    {
        var s = Nuovo();
        Semina(s, "D1", Voci("A", 3));
        var r = Req(s, "D1");
        var uid = MasterEntityIdentityService.GetRequired(r);
        s.Master.RequisitiDtc.Rimuovi(r, "t");
        Assert.False(s.Master.RequisitiDtc.ConteggiLeggeri().ContainsKey(uid));
        var scritti = s.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(r, Voci("N", 2), null) }, "t");
        Assert.Equal(2, scritti[uid].Voci.Count);
        Assert.Equal(new[] { "N000", "N001" }, Codici(s.Master.RequisitiDtc.Leggi(r).Voci).OrderBy(x => x));
        Assert.Equal(2, s.Master.RequisitiDtc.ConteggiLeggeri()[uid]);
    }

    [Fact]
    public void Risolvi_OggettoViveGiaRisolto_SiAggiornaQuandoUnAltroPcCambiaIDtc()
    {
        var s = Nuovo(); var altro = Nuovo();
        Semina(s, "D1", Voci("A", 5), conteggio: null);
        var live = Req(s, "D1");
        s.Master.RequisitiDtc.Risolvi(new[] { live });
        Assert.Equal(5, live.DtcTotale);

        var r2 = Req(altro, "D1"); var b = altro.Master.RequisitiDtc.Leggi(r2);
        altro.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(r2, Voci("A", 7), b.Documento) }, "AltroPC");

        s.Master.RequisitiDtc.Risolvi(new[] { live });                                // stesso oggetto, impronta cambiata
        Assert.Equal(7, live.DtcTotale);
    }
}
