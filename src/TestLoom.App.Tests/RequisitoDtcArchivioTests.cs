using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>6.5.0: archivio separato dei guasti dei requisiti (un documento per requisito nel journal Phase D): niente riscrittura
/// degli archivi per salvataggi senza modifiche guasto, solo l'archivio del requisito toccato, lettura lazy, concorrenza per
/// requisito, recovery (archivio mancante/illeggibile), retro-compatibilita del JSON embedded e gate di schema.</summary>
[Collection("Localization")]
public sealed class RequisitoDtcArchivioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_dtc_arch_" + Guid.NewGuid().ToString("N"));
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
            if (!ops.Any(o => o.Nome == "Master Arch"))
            {
                ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Arch", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") }));
                state.Master.Operatori.Save(ops);
            }
            Assert.True(state.ImpostaOperatore("Master Arch", "1234"));
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
        Enumerable.Range(0, n).Select(i => new DtcRequisito { Codice = $"{prefisso}{i:D5}", Componente = i % 2 == 0 ? "ECM" : "BCM" }).ToList();

    /// <summary>Seed nel formato 6.5.0: requisiti con il solo conteggio + un archivio per requisito.</summary>
    private static void Semina(AppState s, params (string Id, int N)[] spec)
    {
        var reqs = spec.Select(x => Extra(x.Id)).ToList();
        var voci = new List<DtcScrittura>();
        for (var i = 0; i < reqs.Count; i++)
            if (spec[i].N > 0) { reqs[i].DtcConteggio = spec[i].N; }
        s.Master.Requisiti.Save(reqs);
        for (var i = 0; i < reqs.Count; i++)
            if (spec[i].N > 0) voci.Add(new DtcScrittura(reqs[i], Voci(spec[i].Id, spec[i].N), null));
        s.Master.RequisitiDtc.Scrivi(voci, "Seed");
    }

    private long Dimensione() => new DirectoryInfo(_dir).EnumerateFiles("*", SearchOption.AllDirectories)
        .Where(f => !f.Name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)).Sum(f => f.Length);

    private static Requisito Req(AppState s, string id) => s.Master.Requisiti.Load().Single(r => r.Id == id);

    [Fact]
    public void Catalogo_NonPortaIDtc_ENelJsonDelRequisitoCeSoloIlConteggio()
    {
        var s = Nuovo();
        Semina(s, ("D1", 300), ("D2", 0));
        var d1 = Req(s, "D1");
        Assert.Null(d1.Guasto);
        Assert.Equal(300, d1.DtcConteggio);
        Assert.Equal(300, d1.DtcTotale);
        var json = JsonSerializer.Serialize(d1, JsonIo.Options);
        Assert.DoesNotContain("\"Guasto\"", json);
        Assert.Contains("\"DtcConteggio\":300", json.Replace(" ", ""));
        Assert.DoesNotContain("DtcConteggio", JsonSerializer.Serialize(Req(s, "D2"), JsonIo.Options)); // additivo: non scritto se vuoto
        Assert.Equal(300, s.Master.RequisitiDtc.Leggi(d1).Voci.Count);
    }

    [Fact]
    public async Task SalvaRequisitoSenzaModificheDtc_NonRiscriveAlcunArchivio()
    {
        var s = Nuovo();
        Semina(s, ("D1", 3000), ("D2", 3000));
        var prima = s.Master.RequisitiDtc.Leggi(Req(s, "D1")).Documento!;
        var dopoSeed = Dimensione();
        using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.Items.Single(x => x.Id == "D1").Testo = "testo cambiato";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);

        var scritto = Dimensione() - dopoSeed;
        Assert.InRange(scritto, 1, 20_000);  // solo la riga del requisito (3000 guasto valgono ~150 KB)
        var dopo = s.Master.RequisitiDtc.Leggi(Req(s, "D1")).Documento!;
        Assert.Equal(prima.UltimaModifica, dopo.UltimaModifica);   // documento invariato
        Assert.Equal(3000, dopo.Voci.Count);
    }

    [Fact]
    public async Task ModificaDtcDiUnRequisito_RiscriveSoloIlSuoArchivio_ETracciaIlChangelog()
    {
        var s = Nuovo();
        Semina(s, ("D1", 3000), ("D2", 3000));
        var d2Prima = s.Master.RequisitiDtc.Leggi(Req(s, "D2")).Documento!;
        var baseSize = Dimensione();
        using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single(x => x.Id == "D1");
        vm.DtcCaricamento.GetAwaiter().GetResult();
        Assert.Equal(3000, vm.DtcVisibili.Count);
        vm.NuovoDtcCodice = "ZZ999"; vm.NuovoDtcEcu = "ECM";
        vm.AggiungiDtcCommand.Execute(null);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);

        var scritto = Dimensione() - baseSize;
        Assert.InRange(scritto, 100_000, 400_000);                  // ~ un archivio da 3000 voci, non due
        var d1 = Req(s, "D1");
        Assert.Equal(3001, d1.DtcConteggio);
        Assert.Contains(d1.Changelog, c => c.Campo == "Guasto" && c.ValoreNuovo == "+1/-0 guasto");
        Assert.Equal(3001, s.Master.RequisitiDtc.Leggi(d1).Voci.Count);
        var d2Dopo = s.Master.RequisitiDtc.Leggi(Req(s, "D2")).Documento!;
        Assert.Equal(d2Prima.UltimaModifica, d2Dopo.UltimaModifica);
        Assert.Equal(3000, d2Dopo.Voci.Count);
    }

    [Fact]
    public async Task RimozioneDiTuttiIDtc_AzzeraIlConteggioELArchivioVuoto()
    {
        var s = Nuovo();
        Semina(s, ("D1", 3));
        using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single();
        foreach (var v in vm.DtcVisibili.ToList()) vm.RimuoviDtcCommand.Execute(v);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);
        var d1 = Req(s, "D1");
        Assert.Null(d1.DtcConteggio);
        Assert.Empty(s.Master.RequisitiDtc.Leggi(d1).Voci);
    }

    [Fact]
    public void LetturaPerRequisitoGrande_SelezioneAsincronaConSegnaposto()
    {
        var s = Nuovo();
        Semina(s, ("D1", 2000));
        using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.ElementoSelezionato = vm.Items.Single();
        vm.DtcCaricamento.GetAwaiter().GetResult();
        Assert.False(vm.DtcInCaricamento);
        Assert.Equal(2000, vm.DtcVisibili.Count);
    }

    [Fact]
    public async Task RetroCompat_JsonConDtcEmbedded_SiLeggeComePrima_EAlSalvataggioMigraNellArchivio()
    {
        var s = Nuovo();
        var d1 = Extra("D1"); d1.Guasto = new List<DtcRequisito> { new() { Codice = "A1", Componente = "ECM" }, new() { Codice = "A2", Componente = "ECM" } };
        s.Master.Requisiti.Save(new List<Requisito> { d1 });
        var letto = Req(s, "D1");
        Assert.Equal(2, letto.DtcVoci.Count);
        Assert.Equal(2, letto.DtcTotale);
        Assert.True(s.Master.RequisitiDtc.Leggi(letto).Embedded);

        using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single();
        Assert.Equal(2, vm.DtcVisibili.Count);
        vm.NuovoDtcCodice = "A3"; vm.NuovoDtcEcu = "BCM";
        vm.AggiungiDtcCommand.Execute(null);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);
        var dopo = Req(s, "D1");
        Assert.Null(dopo.Guasto);
        Assert.Equal(3, dopo.DtcConteggio);
        Assert.Equal(3, s.Master.RequisitiDtc.Leggi(dopo).Voci.Count);
    }

    [Fact]
    public void Concorrenza_RequisitiDiversi_NonConfliggono()
    {
        var a = Nuovo(); var b = Nuovo();
        Semina(a, ("D1", 10), ("D2", 10));
        var d1a = Req(a, "D1"); var d2b = Req(b, "D2");
        var baseA = a.Master.RequisitiDtc.Leggi(d1a); var baseB = b.Master.RequisitiDtc.Leggi(d2b);
        var nuoveA = baseA.Voci.Concat(new[] { new DtcRequisito { Codice = "NEW-A", Componente = "ECM" } }).ToList();
        var nuoveB = baseB.Voci.Concat(new[] { new DtcRequisito { Codice = "NEW-B", Componente = "ECM" } }).ToList();

        a.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(d1a, nuoveA, baseA.Documento) }, "PC-A");
        b.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(d2b, nuoveB, baseB.Documento) }, "PC-B");

        var verifica = Nuovo(false);
        Assert.Contains(verifica.Master.RequisitiDtc.Leggi(Req(verifica, "D1")).Voci, v => v.Codice == "NEW-A");
        Assert.Contains(verifica.Master.RequisitiDtc.Leggi(Req(verifica, "D2")).Voci, v => v.Codice == "NEW-B");
    }

    [Fact]
    public void Concorrenza_StessoRequisito_MergePerVoce_NessunaDelleDueModificheSiPerde()
    {
        var a = Nuovo(); var b = Nuovo();
        Semina(a, ("D1", 10));
        var d1 = Req(a, "D1");
        var baseA = a.Master.RequisitiDtc.Leggi(d1); var baseB = b.Master.RequisitiDtc.Leggi(Req(b, "D1"));
        a.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(d1, baseA.Voci.Concat(new[] { new DtcRequisito { Codice = "DA-A", Componente = "ECM" } }).ToList(), baseA.Documento) }, "PC-A");
        // 6.5.0 (review): il conflitto sullo stesso requisito non perde piu la modifica del secondo PC, si fonde per voce.
        b.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(Req(b, "D1"), baseB.Voci.Concat(new[] { new DtcRequisito { Codice = "DA-B", Componente = "ECM" } }).ToList(), baseB.Documento) }, "PC-B");
        var finale = Nuovo(false).Master.RequisitiDtc.Leggi(Req(a, "D1")).Voci;
        Assert.Contains(finale, v => v.Codice == "DA-A");
        Assert.Contains(finale, v => v.Codice == "DA-B");
        Assert.Equal(12, finale.Count);
    }

    [Fact]
    public void Recovery_ArchivioMancante_RequisitoSiApreSenzaDtcConAvviso_AltriRequisitiIntegri()
    {
        var s = Nuovo();
        var d1 = Extra("D1"); d1.DtcConteggio = 7;          // dichiara 7 guasto ma l'archivio non esiste
        var d2 = Extra("D2"); d2.DtcConteggio = 4;
        s.Master.Requisiti.Save(new List<Requisito> { d1, d2 });
        s.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(Req(s, "D2"), Voci("B", 4), null) }, "Seed");

        var letto = s.Master.RequisitiDtc.Leggi(Req(s, "D1"));
        Assert.Equal(DtcArchivioStato.Assente, letto.Stato);
        Assert.Empty(letto.Voci);
        Assert.Contains("mancante", letto.Avviso);

        using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.ElementoSelezionato = vm.Items.Single(x => x.Id == "D1");
        Assert.Empty(vm.DtcVisibili);
        Assert.Contains("mancante", vm.AvvisoDtcVolume);
        vm.ElementoSelezionato = vm.Items.Single(x => x.Id == "D2");
        Assert.Equal(4, vm.DtcVisibili.Count);
    }

    [Fact]
    public async Task Recovery_ArchivioIllegibile_NessunCrash_RequisitoSenzaDtcConAvviso_ENessunDatoSovrascritto()
    {
        var s = Nuovo();
        Semina(s, ("D1", 5), ("D2", 5));
        var d1 = Req(s, "D1");
        ScriviDocumentoCorrotto(d1);

        var letto = s.Master.RequisitiDtc.Leggi(d1);
        Assert.Equal(DtcArchivioStato.Illeggibile, letto.Stato);
        Assert.Empty(letto.Voci);
        Assert.Contains("non leggibile", letto.Avviso);
        Assert.Equal(5, s.Master.RequisitiDtc.Leggi(Req(s, "D2")).Voci.Count);

        using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single(x => x.Id == "D1");
        Assert.Empty(vm.DtcVisibili);
        Assert.Contains("non leggibile", vm.AvvisoDtcVolume);
        // Salvare senza toccare i guasti non deve riscrivere (ne distruggere) l'archivio illeggibile.
        vm.Items.Single(x => x.Id == "D1").Testo = "modificato";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);
        Assert.Equal("modificato", Req(s, "D1").Testo);
        Assert.Equal(DtcArchivioStato.Illeggibile, s.Master.RequisitiDtc.Leggi(Req(s, "D1")).Stato);
    }

    [Fact]
    public void Recovery_JournalConRigaTroncata_LeggiNonLanciaMai()
    {
        var s = Nuovo();
        Semina(s, ("D1", 5));
        foreach (var f in Directory.EnumerateFiles(_dir, "*.ndjson", SearchOption.AllDirectories))
            File.AppendAllText(f, "{\"BatchId\":\"troncato\",\"Mutations\":[{\"Cat");
        var letto = s.Master.RequisitiDtc.Leggi(Req(Nuovo(false), "D1"));   // non deve lanciare; vale Ok oppure Illeggibile con avviso
        Assert.True(letto.Stato is DtcArchivioStato.Ok or DtcArchivioStato.Illeggibile);
    }

    [Fact]
    public void ConteggiDaArchivi_RicostruisceIlSuggerimento()
    {
        var s = Nuovo();
        Semina(s, ("D1", 12), ("D2", 5));
        var conteggi = s.Master.RequisitiDtc.ConteggiDaArchivi();
        Assert.Equal(12, conteggi[MasterEntityIdentityService.GetRequired(Req(s, "D1"))]);
        Assert.Equal(5, conteggi[MasterEntityIdentityService.GetRequired(Req(s, "D2"))]);
    }

    [Fact]
    public void RiepilogoAudit_UsaIlConteggioSenzaLeggereGliArchivi_EIlDettaglioLiLegge()
    {
        var s = Nuovo();
        Semina(s, ("D1", 50), ("D2", 20));
        var reqs = s.Master.Requisiti.Load().Where(r => r.DtcTotale > 0).ToList();
        var riepilogo = DtcAvanzamentoProvider.CalcolaRiepilogo(s, reqs);
        Assert.Equal(70, riepilogo.Totale.Totale);
        Assert.Equal(new[] { 50, 20 }, riepilogo.PerRequisito.Select(r => r.Totale));
        Assert.Equal(70, riepilogo.Totale.DaFare);
        var dettaglio = DtcAvanzamentoProvider.Calcola(s, reqs);
        Assert.Equal(70, dettaglio.PerDtc.Count);
    }

    [Fact]
    public void Archivio_PreservaIlSottoveicolo_EDistingueLaStessaCoppiaCodiceEcuInSottoveicoliDiversi()
    {
        var s = Nuovo();
        var r = Extra("DS1");
        s.Master.Requisiti.Save(new List<Requisito> { r });
        var req = Req(s, "DS1");
        var voci = new List<DtcRequisito>
        {
            new() { Codice = "P0001", Componente = "ECM", Veicolo = "Daily MY24" },
            new() { Codice = "P0001", Componente = "ECM", Veicolo = "Daily MY25" },
            new() { Codice = "P0001", Componente = "ECM" },
        };
        s.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(req, voci, null) }, "Test");
        var letto = s.Master.RequisitiDtc.Leggi(req).Voci;
        Assert.Equal(3, letto.Count);
        Assert.Equal(new[] { "", "Daily MY24", "Daily MY25" }, letto.Select(v => v.Veicolo ?? "").OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Archivio_ClientCheNonConoscelo_SchemaFuturo_NonScriveGliArchiviDtc()
    {
        // Gate globale del manifest: vale anche per gli archivi master "requisiti-guasto" (journal Phase D -> WorkspaceWritePolicy).
        var s = Nuovo();
        Semina(s, ("DG1", 2));
        var req = Req(s, "DG1");
        var manifest = JsonIo.TryRead<VersionManifest>(s.Paths.VersionManifestFile, out _)!;
        manifest.SchemaDatiMassimo = TestCase.CurrentSchemaVersion + 1;
        JsonIo.WriteAtomic(s.Paths.VersionManifestFile, manifest);
        var prima = s.Master.RequisitiDtc.Leggi(req).Voci.Count;
        Assert.ThrowsAny<Exception>(() => s.Master.RequisitiDtc.Scrivi(new[]
            { new DtcScrittura(req, new List<DtcRequisito> { new() { Codice = "X", Componente = "ECM", Veicolo = "A" } }, s.Master.RequisitiDtc.Leggi(req).Documento) }, "Test"));
        Assert.Equal(prima, s.Master.RequisitiDtc.Leggi(req).Voci.Count);
    }

    [Fact]
    public void GateDiCompatibilita_LoSchemaENoveENonSuperatoDalClientCheNonConosceLArchivio()
    {
        Assert.Equal(11, TestCase.CurrentSchemaVersion);
        // Un client con schema massimo < 9 (build precedente, ignara di Veicolo negli archivi) NON puo scrivere se il manifest dichiara 9.
        var s = Nuovo();
        var manifest = JsonIo.TryRead<VersionManifest>(s.Paths.VersionManifestFile, out _)!;
        Assert.Equal(11, manifest.SchemaDatiMassimo);
        manifest.SchemaDatiMassimo = TestCase.CurrentSchemaVersion + 1;       // simula manifest scritto da build piu recente
        JsonIo.WriteAtomic(s.Paths.VersionManifestFile, manifest);
        var esito = CompatibilitaVersioneService.VerificaSenzaScrivere(s.Paths, new EsitoStatoCondivisione { Stato = StatoCondivisione.Operativa });
        Assert.Equal(StatoCondivisione.SolaLettura, esito.Stato);
    }

    /// <summary>Pubblica nel journal un documento d'archivio valido come envelope ma con payload non deserializzabile.</summary>
    private void ScriviDocumentoCorrotto(Requisito r)
    {
        var uid = MasterEntityIdentityService.GetRequired(r);
        var device = DeviceIdentityService.GetOrCreate();
        var payload = "{\"Id\":\"" + uid.ToString("N") + "\",\"Voci\":\"non-una-lista\",\"_ditmUid\":\"" + uid.ToString("N") + "\"}";
        var mutation = new PhaseDMasterMutation
        {
            MutationId = Guid.NewGuid(), Catalog = RequisitoDtcArchivio.Catalogo, EntityUid = uid,
            Operation = PhaseDMasterMutationOperation.Upsert,
            VersionVector = new Dictionary<string, long> { [device] = 1_000_000 },
            PayloadJson = payload, PayloadSha256 = PhaseDMasterMutation.HashPayload(payload),
            BusinessKey = uid.ToString("N"), TimestampUtc = DateTime.UtcNow
        };
        var batch = new PhaseDMasterMutationBatch
        {
            BatchId = Guid.NewGuid(), DeviceId = device,
            DeviceSequence = PhaseDMasterJournalService.NextDeviceSequence(_dir),
            CreatedUtc = DateTime.UtcNow, Mutations = new List<PhaseDMasterMutation> { mutation }
        };
        batch.Validate();
        PhaseDMasterJournalService.Append(new DataPaths(_dir), batch);
    }
}
