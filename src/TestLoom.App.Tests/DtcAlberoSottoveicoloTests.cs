using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>6.5.1 fase 2: albero Requisito Extra -> Veicolo -> componente -> guasto. Costruzione/validazione pure, editor, import con colonna
/// opzionale Veicolo, avanzamento con lo stesso guasto in due sottoveicoli, indice SQLite v10 -> v11 (backfill/ricostruzione/versione
/// superiore), archivio concorrente tra due PC e segnalazione "non allineata".</summary>
[Collection("Localization")]
public sealed class DtcAlberoSottoveicoloTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_dtc_albero_" + Guid.NewGuid().ToString("N"));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_DtcAlbero_" + Guid.NewGuid().ToString("N"));
    private readonly List<AppState> _stati = new();
    private static readonly DateTime Day = new(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc);

    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public DtcAlberoSottoveicoloTests(Xunit.Abstractions.ITestOutputHelper o) { _out = o; }

    public void Dispose()
    {
        foreach (var s in _stati) { try { s.Dispose(); } catch { } }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
        try { Directory.Delete(_root, true); } catch { }
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
            if (!ops.Any(o => o.Nome == "Master Albero"))
            {
                ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Albero", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") }));
                state.Master.Operatori.Save(ops);
            }
            Assert.True(state.ImpostaOperatore("Master Albero", "1234"));
        }
        return state;
    }

    private static Requisito Extra(string id, params string[] sottoveicoli)
    {
        var r = new Requisito { Id = id, Testo = "t" };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        if (sottoveicoli.Length > 0) RequisitoApplicabilita.ImpostaSottoveicoli(r, sottoveicoli);
        return r;
    }

    private static DtcRequisito V(string codice, string componente, string? sv = null) => new() { Codice = codice, Componente = componente, Veicolo = sv };
    private static Requisito Req(AppState s, string id) => s.Master.Requisiti.Load().Single(r => r.Id == id);

    private static void SeminaEcu(AppState s, params string[] nomi) =>
        s.Master.Componente.Save(nomi.Select(n => new Componente { Nome = n }).ToList());

    private static void SeminaRequisito(AppState s, Requisito r, List<DtcRequisito> voci)
    {
        r.DtcConteggio = voci.Count;
        s.Master.Requisiti.Save(new List<Requisito> { r });
        s.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(Req(s, r.Id), voci, null) }, "Seed");
    }

    private static RequisitiViewModel Apri(AppState s)
    {
        var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single();
        return vm;
    }

    private static void Aggiungi(RequisitiViewModel vm, string sv, string componente, string codice)
    {
        vm.NuovoDtcVeicolo = sv; vm.NuovoDtcEcu = componente; vm.NuovoDtcCodice = codice;
        vm.AggiungiDtcCommand.Execute(null);
    }

    // === Costruzione dell'albero (pura) ==================================================================

    [Fact]
    public void Costruisci_RaggruppaPerSottoveicoloPoiEcu_SenzaVeicoloPerPrimo()
    {
        var voci = new List<DtcRequisito> { V("P2", "ECM", "B"), V("P1", "BCM", "A"), V("P1", "ECM", "A"), V("P9", "ECM"), V("P1", "ECM", "B") };
        var righe = DtcAlberoRegole.Costruisci(voci, new[] { "A", "B" }, null, _ => true);

        Assert.Equal(new[]
        {
            "Sv:", "Componente:ECM", "Guasto:P9",
            "Sv:A", "Componente:BCM", "Guasto:P1", "Componente:ECM", "Guasto:P1",
            "Sv:B", "Componente:ECM", "Guasto:P1", "Guasto:P2"
        }, righe.Select(r => r.Livello switch { LivelloDtcAlbero.Veicolo => "Sv:" + r.Veicolo, LivelloDtcAlbero.Componente => "Componente:" + r.Componente, _ => "Guasto:" + r.Testo }));
        Assert.Equal(DtcRequisito.SenzaVeicolo, righe[0].Testo);
        Assert.Equal(5, righe.Where(r => r.Livello == LivelloDtcAlbero.Veicolo).Sum(r => r.Conteggio));
        Assert.Equal(2, righe.Single(r => r.Livello == LivelloDtcAlbero.Veicolo && r.Veicolo == "A").Conteggio);
        Assert.All(righe, r => Assert.False(r.NonAllineata));
        // Il codice P1 compare sotto A/ECM e B/ECM con AutomationId distinti; senza veicolo l'id resta quello del 6.5.0.
        var ids = righe.Where(r => r.EFoglia).Select(r => r.AutomationId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains("Requis_Guasto_Voce_P9", ids);
        Assert.Contains("Requis_Guasto_Voce_A_ECM_P1", ids);
        Assert.Contains("Requis_Guasto_Veicolo_senza", righe.Select(r => r.AutomationId));
    }

    [Fact]
    public void Costruisci_SottoveicoloNonDichiarato_ESegnalatoComeNonAllineata_SenzaScartareLeVoci()
    {
        var voci = new List<DtcRequisito> { V("P1", "ECM", "Vecchio"), V("P2", "ECM", "Ok"), V("P3", "ECM") };
        var righe = DtcAlberoRegole.Costruisci(voci, new[] { "Ok" }, null, _ => true);
        Assert.Equal(3, righe.Count(r => r.EFoglia));
        Assert.All(righe.Where(r => r.Veicolo == "Vecchio"), r => Assert.True(r.NonAllineata));
        Assert.All(righe.Where(r => r.Veicolo != "Vecchio"), r => Assert.False(r.NonAllineata));   // "(senza veicolo)" e valido
    }

    [Fact]
    public void Costruisci_GruppiChiusi_NonEmettonoIFigli_ENodiVuotiCompaiono()
    {
        var voci = new List<DtcRequisito> { V("P1", "ECM", "A"), V("P2", "BCM", "A") };
        var chiuso = DtcAlberoRegole.Costruisci(voci, new[] { "A", "B" }, new[] { ("B", "") }, _ => false);
        Assert.Equal(new[] { "A", "B" }, chiuso.Select(r => r.Veicolo));
        Assert.Equal(0, chiuso[1].Conteggio);
        var soloSv = DtcAlberoRegole.Costruisci(voci, new[] { "A", "B" }, new[] { ("B", "ECM") }, k => k is "A" or "B" or "B|ECM");
        Assert.Contains(soloSv, r => r.Livello == LivelloDtcAlbero.Componente && r.Veicolo == "B" && r.Componente == "ECM" && r.Conteggio == 0);
        var aperto = DtcAlberoRegole.Costruisci(voci, new[] { "A" }, null, k => k == "A");
        Assert.Equal(new[] { "A", "BCM", "ECM" }, aperto.Select(r => r.Testo));   // componente chiuse: niente guasto
    }

    [Fact]
    public void Costruisci_100kVoci_ChiusoEEspanso_EntroSoglia_ESenzaPerdereVoci()
    {
        var voci = Enumerable.Range(0, 100_000).Select(i => V($"P{i:D6}", $"Componente{i % 10:D2}", $"SV{i % 3}")).ToList();
        var dichiarati = new[] { "SV0", "SV1", "SV2" };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var chiuso = DtcAlberoRegole.Costruisci(voci, dichiarati, null, _ => false);
        var tChiuso = sw.ElapsedMilliseconds;
        sw.Restart();
        var aperto = DtcAlberoRegole.Costruisci(voci, dichiarati, null, _ => true);
        var tAperto = sw.ElapsedMilliseconds;
        _out.WriteLine($"albero 100k voci: chiuso {tChiuso} ms ({chiuso.Count} righe), espanso {tAperto} ms ({aperto.Count} righe)");
        Assert.Equal(3, chiuso.Count);
        Assert.Equal(100_000, aperto.Count(r => r.EFoglia));
        Assert.Equal(100_000, chiuso.Sum(r => r.Conteggio));
        Assert.InRange(tChiuso, 0, 10_000);
        Assert.InRange(tAperto, 0, 15_000);
    }

    // === Validazione in scrittura (pura) =================================================================

    [Fact]
    public void ValidaNuovaVoce_Regole()
    {
        var chiavi = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DtcRequisito.Chiave("A", "ECM", "P0001"), DtcRequisito.Chiave(null, "ECM", "P0001") };
        var dichiarati = new[] { "A", "B" };
        var componente = new[] { "ECM", "BCM" };
        Assert.Null(DtcAlberoRegole.ValidaNuovaVoce("B", "ECM", "P0001", dichiarati, componente, chiavi));                  // stesso codice, altro veicolo
        Assert.Null(DtcAlberoRegole.ValidaNuovaVoce("b", "bcm", "P0001", dichiarati, componente, chiavi));                  // case-insensitive
        Assert.Equal("DtcDuplicato", DtcAlberoRegole.ValidaNuovaVoce("a", "ECM", "p0001", dichiarati, componente, chiavi)); // chiave a 3 campi
        Assert.Equal("DtcDuplicato", DtcAlberoRegole.ValidaNuovaVoce("", "ECM", "P0001", dichiarati, componente, chiavi));  // anche senza veicolo
        Assert.Equal("DtcVeicoloNonDichiarato", DtcAlberoRegole.ValidaNuovaVoce("C", "ECM", "P0002", dichiarati, componente, chiavi));
        Assert.Equal("DtcVeicoloNonDichiarato", DtcAlberoRegole.ValidaNuovaVoce("A", "ECM", "P0002", Array.Empty<string>(), componente, chiavi));
        Assert.Equal("DtcEcuNonInAnagrafica", DtcAlberoRegole.ValidaNuovaVoce("A", "XYZ", "P0002", dichiarati, componente, chiavi));
        Assert.Equal("DtcEcuMancante", DtcAlberoRegole.ValidaNuovaVoce("A", " ", "P0002", dichiarati, componente, chiavi));
        Assert.Equal("DtcCodiceNonValido", DtcAlberoRegole.ValidaNuovaVoce("A", "ECM", "P", dichiarati, componente, chiavi));
        Assert.Equal("DtcCodiceNonValido", DtcAlberoRegole.ValidaNuovaVoce("A", "ECM", "P 01", dichiarati, componente, chiavi));
        Assert.Null(DtcAlberoRegole.ValidaNuovaVoce("", "QUALUNQUE", "U0100-00", dichiarati, null, chiavi));         // anagrafica non disponibile: nessun vincolo componente
    }

    // === Editor =========================================================================================

    [Fact]
    public async Task Editor_AggiungiRimuovi_AlberoPerSottoveicoloEcu_ESalvaconIlSottoveicolo()
    {
        var s = Nuovo();
        SeminaEcu(s, "ECM", "BCM");
        SeminaRequisito(s, Extra("D1", "Daily MY24", "Daily MY25"), new List<DtcRequisito> { V("OLD1", "ECM") });
        using var vm = Apri(s);

        Assert.Equal(new[] { "Daily MY24", "Daily MY25" }, vm.VeicoliDichiarati);
        Assert.Equal(new[] { DtcRequisito.SenzaVeicolo, "Daily MY24", "Daily MY25" }, vm.VeicoliScegliibili);
        Assert.Equal(new[] { "BCM", "ECM" }, vm.EcuDisponibili);

        Aggiungi(vm, "Daily MY24", "ECM", "P0001");
        Assert.Null(vm.MessaggioErrore);
        Aggiungi(vm, "Daily MY25", "ECM", "P0001");           // stesso codice, altro veicolo: ammesso
        Assert.Null(vm.MessaggioErrore);
        Aggiungi(vm, "Daily MY24", "ECM", "P0001");           // duplicato sulla chiave a 3 campi
        Assert.Equal(LocalizationService.Instance.Traduci("DtcDuplicato"), vm.MessaggioErrore);
        Aggiungi(vm, "Daily MY99", "ECM", "P0002");           // non dichiarato
        Assert.Equal(LocalizationService.Instance.Traduci("DtcVeicoloNonDichiarato"), vm.MessaggioErrore);
        Aggiungi(vm, "Daily MY24", "NOPE", "P0002");          // componente non in anagrafica
        Assert.Equal(LocalizationService.Instance.Traduci("DtcEcuNonInAnagrafica"), vm.MessaggioErrore);
        Aggiungi(vm, "Daily MY24", "BCM", "X");               // codice non valido
        Assert.Equal(LocalizationService.Instance.Traduci("DtcCodiceNonValido"), vm.MessaggioErrore);
        Assert.Equal(3, vm.DtcVisibili.Count);

        // Albero: (senza veicolo) prima, poi i sottoveicoli, ciascuno con il nodo componente e le foglie.
        var foglie = vm.AlberoDtc.Where(n => n.EFoglia).Select(n => $"{n.Veicolo}/{n.Componente}/{n.Testo}").ToList();
        Assert.Equal(new[] { "/ECM/OLD1", "Daily MY24/ECM/P0001", "Daily MY25/ECM/P0001" }, foglie);
        Assert.Contains(vm.AlberoDtc, n => n.AutomationId == "Requis_Guasto_Voce_Daily_MY24_ECM_P0001");

        vm.RimuoviDtcCommand.Execute(vm.AlberoDtc.Single(n => n.Veicolo == "Daily MY25" && n.EFoglia).Voce);
        Assert.DoesNotContain(vm.AlberoDtc, n => n.Veicolo == "Daily MY25");

        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);
        var salvate = s.Master.RequisitiDtc.Leggi(Req(s, "D1")).Voci;
        Assert.Equal(new[] { "/OLD1", "Daily MY24/P0001" }, salvate.Select(v => $"{v.Veicolo}/{v.Codice}").OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(2, Req(s, "D1").DtcConteggio);
    }

    [Fact]
    public void Editor_NodiVuoti_SottoveicoloEEcu_SonoSoloDiSessione_ELaSelezioneDelNodoPrecompilaIMenu()
    {
        var s = Nuovo();
        SeminaEcu(s, "ECM", "BCM");
        SeminaRequisito(s, Extra("D1", "A", "B"), new List<DtcRequisito> { V("P1", "ECM", "A") });
        using var vm = Apri(s);

        vm.NuovoDtcVeicolo = "B";
        vm.AggiungiNodoVeicoloCommand.Execute(null);
        Assert.Contains(vm.AlberoDtc, n => n.Livello == LivelloDtcAlbero.Veicolo && n.Veicolo == "B" && n.Conteggio == 0);
        vm.NuovoDtcEcu = "BCM";
        vm.AggiungiNodoEcuCommand.Execute(null);
        Assert.Contains(vm.AlberoDtc, n => n.Livello == LivelloDtcAlbero.Componente && n.Veicolo == "B" && n.Componente == "BCM");
        vm.NuovoDtcVeicolo = "ZZ";
        vm.AggiungiNodoVeicoloCommand.Execute(null);
        Assert.Equal(LocalizationService.Instance.Traduci("DtcVeicoloNonDichiarato"), vm.MessaggioErrore);
        Assert.Single(vm.DtcVisibili);                                   // nessuna voce creata: i nodi vuoti non sono dati

        vm.NodoDtcSelezionato = vm.AlberoDtc.Single(n => n.Livello == LivelloDtcAlbero.Componente && n.Veicolo == "B");
        Assert.Equal("B", vm.NuovoDtcVeicolo);
        Assert.Equal("BCM", vm.NuovoDtcEcu);
        vm.NuovoDtcCodice = "P7"; vm.AggiungiDtcCommand.Execute(null);
        Assert.Contains(vm.AlberoDtc, n => n.EFoglia && n.Veicolo == "B" && n.Componente == "BCM" && n.Testo == "P7");

        vm.AnnullaCommand.Execute(null);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single();
        Assert.Equal(new[] { "A" }, vm.AlberoDtc.Where(n => n.Livello == LivelloDtcAlbero.Veicolo).Select(n => n.Veicolo)); // nulla persistito
    }

    [Fact]
    public void Editor_ToggleNodo_ChiudeEApreIFigli()
    {
        var s = Nuovo();
        SeminaEcu(s, "ECM");
        SeminaRequisito(s, Extra("D1", "A"), new List<DtcRequisito> { V("P1", "ECM", "A"), V("P2", "ECM", "A") });
        using var vm = Apri(s);
        Assert.Equal(2, vm.AlberoDtc.Count(n => n.EFoglia));
        vm.ToggleNodoDtcCommand.Execute(vm.AlberoDtc.Single(n => n.Livello == LivelloDtcAlbero.Componente));
        Assert.Equal(0, vm.AlberoDtc.Count(n => n.EFoglia));
        vm.ToggleNodoDtcCommand.Execute(vm.AlberoDtc.Single(n => n.Livello == LivelloDtcAlbero.Componente));
        Assert.Equal(2, vm.AlberoDtc.Count(n => n.EFoglia));
    }

    [Fact]
    public async Task Editor_SottoveicoloRimossoDalRequisito_VoceNonAllineataMaSalvabile_ENonBloccaIlSalvataggio()
    {
        var s = Nuovo();
        SeminaEcu(s, "ECM");
        SeminaRequisito(s, Extra("D1", "A", "B"), new List<DtcRequisito> { V("P1", "ECM", "A"), V("P2", "ECM", "B") });
        using var vm = Apri(s);
        Assert.All(vm.AlberoDtc, n => Assert.False(n.NonAllineata));

        RequisitoApplicabilita.ImpostaSottoveicoli(vm.ElementoSelezionato!, new[] { "A" });   // B tolto dal requisito
        vm.NotificaSottoveicoliCambiati();
        Assert.All(vm.AlberoDtc.Where(n => n.Veicolo == "B"), n => Assert.True(n.NonAllineata));
        Assert.All(vm.AlberoDtc.Where(n => n.Veicolo == "A"), n => Assert.False(n.NonAllineata));
        Assert.Equal(new[] { "A" }, vm.VeicoliDichiarati);

        Aggiungi(vm, "B", "ECM", "P3");                                                        // ma nuove voci su B sono rifiutate
        Assert.Equal(LocalizationService.Instance.Traduci("DtcVeicoloNonDichiarato"), vm.MessaggioErrore);
        vm.MessaggioErrore = null;
        vm.ElementoSelezionato!.Testo = "modificato";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);                                       // non blocca
        Assert.Equal(2, s.Master.RequisitiDtc.Leggi(Req(s, "D1")).Voci.Count);                 // la voce B resta
    }

    // === Aggiunta in blocco al nodo (Veicolo + componente) =======================================================

    [Fact]
    public void Blocco_Tokenizza_ACapoVirgolaPuntoEVirgolaESpazio()
    {
        Assert.Equal(new[] { "P1", "P2", "P3", "P4", "P5" }, DtcAlberoRegole.Tokenizza(" P1\r\nP2,P3 ;  P4\tP5\n\n"));
        Assert.Empty(DtcAlberoRegole.Tokenizza("  \n ,; "));
        Assert.Empty(DtcAlberoRegole.Tokenizza(null));
    }

    [Fact]
    public void Blocco_ValidaBlocco_DedupSuChiaveACampi_NonValidiEDuplicatiSeparati()
    {
        var esistenti = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DtcRequisito.Chiave("A", "ECM", "P1") };
        var r = DtcAlberoRegole.ValidaBlocco("a", "ECM", "P1 P2\np2, P3 X P 4 U0100-00", new[] { "A", "B" }, new[] { "ECM" }, esistenti);
        Assert.Null(r.ErroreNodo);
        Assert.Equal(new[] { "P2", "P3", "U0100-00" }, r.Nuove.Select(v => v.Codice));
        Assert.All(r.Nuove, v => { Assert.Equal("A", v.Veicolo); Assert.Equal("ECM", v.Componente); });   // veicolo canonico del requisito
        Assert.Equal(new[] { "P1", "p2" }, r.Duplicati);
        Assert.Equal(new[] { "X", "P", "4" }, r.NonValidi);       // "P 4" sono due token
        // stesso codice su un altro veicolo: nodo diverso, nessun duplicato
        Assert.Single(DtcAlberoRegole.ValidaBlocco("B", "ECM", "P1", new[] { "A", "B" }, new[] { "ECM" }, esistenti).Nuove);
        Assert.Equal("DtcNessunCodice", DtcAlberoRegole.ValidaBlocco("A", "ECM", " ", new[] { "A" }, null, esistenti).ErroreNodo);
        Assert.Equal("DtcVeicoloNonDichiarato", DtcAlberoRegole.ValidaBlocco("Z", "ECM", "P9", new[] { "A" }, null, esistenti).ErroreNodo);
        Assert.Equal("DtcEcuNonInAnagrafica", DtcAlberoRegole.ValidaBlocco("A", "NOPE", "P9", new[] { "A" }, new[] { "ECM" }, esistenti).ErroreNodo);
        Assert.Equal("DtcEcuMancante", DtcAlberoRegole.ValidaBlocco("A", "", "P9", new[] { "A" }, null, esistenti).ErroreNodo);
    }

    [Fact]
    public async Task Editor_AggiungiInBlocco_AlNodo_SenzaRipetereVeicoloEEcu_ERiepilogo()
    {
        var s = Nuovo();
        SeminaEcu(s, "ECM", "BCM");
        SeminaRequisito(s, Extra("D1", "A", "B"), new List<DtcRequisito>());
        using var vm = Apri(s);
        var loc = LocalizationService.Instance;

        vm.NuovoDtcVeicolo = "A"; vm.NuovoDtcEcu = "ECM";
        vm.NuovoDtcCodici = "P0001\nP0002, P0003;P0004 P0001 ?? P0005";
        vm.AggiungiDtcCommand.Execute(null);
        Assert.Equal(5, vm.DtcVisibili.Count);
        Assert.Equal(string.Format(loc.Traduci("DtcBloccoEsitoFmt"), 5, "A / ECM"), vm.Messaggio);
        Assert.Contains("P0001", vm.MessaggioErrore);               // duplicato nel blocco
        Assert.Contains("??", vm.MessaggioErrore);                  // codice non valido
        Assert.Equal("??", vm.NuovoDtcCodici);                      // resta solo il da correggere

        // Altri guasto sullo stesso nodo (veicolo e componente gia scelti), uno gia presente
        vm.NuovoDtcCodici = "P0005 P0006\nP0007";
        vm.AggiungiDtcCommand.Execute(null);
        Assert.Equal(7, vm.DtcVisibili.Count);
        Assert.Equal("", vm.NuovoDtcCodici);
        Assert.Contains("P0005", vm.MessaggioErrore);

        // Stessi codici su un altro veicolo/componente: nodi distinti
        vm.NuovoDtcVeicolo = "B"; vm.NuovoDtcEcu = "BCM"; vm.NuovoDtcCodici = "P0001 P0002";
        vm.AggiungiDtcCommand.Execute(null);
        Assert.Null(vm.MessaggioErrore);
        Assert.Equal(9, vm.DtcVisibili.Count);
        Assert.Equal(new[] { "A/ECM", "B/BCM" }, vm.AlberoDtc.Where(n => n.Livello == LivelloDtcAlbero.Componente).Select(n => $"{n.Veicolo}/{n.Componente}"));

        // Nodo invalido: nulla viene aggiunto e il testo resta
        vm.NuovoDtcVeicolo = "ZZ"; vm.NuovoDtcCodici = "P9999";
        vm.AggiungiDtcCommand.Execute(null);
        Assert.Equal(loc.Traduci("DtcVeicoloNonDichiarato"), vm.MessaggioErrore);
        Assert.Equal(9, vm.DtcVisibili.Count);
        Assert.Equal("P9999", vm.NuovoDtcCodici);
        vm.NuovoDtcVeicolo = "A"; vm.NuovoDtcCodici = "  ";
        vm.AggiungiDtcCommand.Execute(null);
        Assert.Equal(loc.Traduci("DtcNessunCodice"), vm.MessaggioErrore);

        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);
        Assert.Equal(9, s.Master.RequisitiDtc.Leggi(Req(s, "D1")).Voci.Count);
    }

    [Fact]
    public void Import_ColonnaVeicolo_AccettaVeicoloESottoveicoloComeAlias()
    {
        foreach (var intest in new[] { "Veicolo", "Sottoveicolo" })
        {
            var r = DtcImportService.Interpreta(Righe($"Guasto;componente;{intest}", "P1;ECM;a"), Array.Empty<DtcRequisito>(), null, new[] { "A" });
            Assert.Equal("A", r.Nuove.Single().Veicolo);
        }
    }

    // === Import ==========================================================================================

    private static List<List<string>> Righe(params string[] righe) => righe.Select(r => r.Split(';').ToList()).ToList();

    [Fact]
    public void Import_Semplice_ColonnaSottoveicoloOpzionale()
    {
        var senza = DtcImportService.Interpreta(Righe("Guasto;componente", "P1;ECM", "P2;BCM"), Array.Empty<DtcRequisito>());
        Assert.Equal(2, senza.Nuove.Count);
        Assert.All(senza.Nuove, v => Assert.Null(v.Veicolo));

        var con = DtcImportService.Interpreta(Righe("Guasto;componente;Veicolo", "P1;ECM;daily my24", "P1;ECM;Daily MY25", "P1;ECM;daily my24", "P2;BCM;"),
            Array.Empty<DtcRequisito>(), null, new[] { "Daily MY24", "Daily MY25" });
        Assert.Empty(con.Errori);
        Assert.Equal(1, con.Duplicate);                                       // stessa chiave a 3 campi
        Assert.Equal(new[] { "Daily MY24", "Daily MY25", null }, con.Nuove.Select(v => v.Veicolo));   // nome canonico del requisito

        var nonDichiarato = DtcImportService.Interpreta(Righe("Guasto;componente;Veicolo", "P1;ECM;Altro"), Array.Empty<DtcRequisito>(), null, new[] { "Daily MY24" });
        Assert.Single(nonDichiarato.Errori);
        Assert.Contains("Altro", nonDichiarato.Errori[0]);
        Assert.Empty(nonDichiarato.Nuove);

        // Colonna veicolo senza vincolo (nessun elenco passato): comportamento storico, nessun controllo.
        var libero = DtcImportService.Interpreta(Righe("Componente;guasto;Veicolo", "ECM;P1;Qualunque"), Array.Empty<DtcRequisito>());
        Assert.Equal("Qualunque", libero.Nuove.Single().Veicolo);
        // Una voce esistente senza veicolo non e duplicata di una con veicolo.
        var altroSv = DtcImportService.Interpreta(Righe("Guasto;componente;Veicolo", "P1;ECM;A"), new[] { V("P1", "ECM") });
        Assert.Single(altroSv.Nuove);
    }

    [Fact]
    public void Import_MultiRequisito_ColonnaSottoveicolo_ScartaIlNonDichiaratoPerRequisito()
    {
        var dichiarati = new Dictionary<string, string[]> { ["R1"] = new[] { "A" }, ["R2"] = new[] { "B" } };
        IReadOnlyList<DtcRequisito>? Esistenti(string id) => dichiarati.ContainsKey(id) ? Array.Empty<DtcRequisito>() : null;
        var report = DtcImportRequisitoService.Interpreta(
            Righe("Requisito;guasto;componente;Veicolo", "R1;P1;ECM;a", "R2;P1;ECM;A", "R2;P1;ECM;B", "R1;P1;ECM;A", "R1;P5;ECM;"),
            Esistenti, null, null, null, default, id => dichiarati.TryGetValue(id, out var d) ? d : null);
        Assert.False(report.Rifiutato);
        Assert.Equal(3, report.Importate.Count);
        Assert.Equal(1, report.Duplicate);
        Assert.Single(report.Scartate);
        Assert.Contains("riga 3", report.Scartate[0]);
        Assert.Contains("veicolo 'A'", report.Scartate[0]);
        Assert.Equal(new[] { "R1:A", "R2:B", "R1:" }, report.Importate.Select(v => $"{v.RequisitoId}:{v.Voce.Veicolo}"));
    }

    [Fact]
    public void Import_MultiRequisito_SenzaColonna_ComportamentoInvariato()
    {
        var report = DtcImportRequisitoService.Interpreta(Righe("Requisito;guasto;componente", "R1;P1;ECM", "R1;P1;ECM"),
            id => id == "R1" ? Array.Empty<DtcRequisito>() : null);
        Assert.Single(report.Importate);
        Assert.Null(report.Importate[0].Voce.Veicolo);
        Assert.Equal(1, report.Duplicate);
    }

    [Fact]
    public async Task Import_Editor_ApplicaIlSottoveicoloENonLoSalvaSeNonDichiarato()
    {
        var s = Nuovo();
        SeminaEcu(s, "ECM");
        SeminaRequisito(s, Extra("D1", "A"), new List<DtcRequisito>());
        using var vm = Apri(s);
        await vm.ImportaDtcDaRighe(Righe("Guasto;componente;Veicolo", "P1;ECM;A", "P2;ECM;Altro", "P3;ECM;"));
        Assert.Equal(new[] { "/P3", "A/P1" }, vm.DtcVisibili.Select(v => $"{v.Veicolo}/{v.Codice}").OrderBy(x => x, StringComparer.Ordinal));
        Assert.NotNull(vm.UltimoReportDtc);
        Assert.Single(vm.UltimoReportDtc!.Scartate);
    }

    // === Avanzamento =====================================================================================

    private static Condizione Op(string req, string guasto, PassoDtc passo, Esito esito, string componente = "ECM", string? sv = null) => new()
    {
        Guasto = guasto, DtcRequisitoId = req, DtcPasso = passo, Esito = esito, DtcVeicolo = sv, Componente = new EcuSelection(new[] { componente })
    };

    private static TestCase Test(string id, int ora, string[] veicoli, params Condizione[] ops)
    {
        var tc = new TestCase
        {
            Id = id, Uid = Guid.NewGuid(), DataCreazione = Day, UltimaModifica = Day, ModificatoDa = "x",
            Componente = new EcuSelection(new[] { "ECM" }), Veicoli = veicoli.ToList(), Veicolo = veicoli.FirstOrDefault() ?? ""
        };
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = Day.AddHours(ora), Operatore = "op", Esito = Esito.Conforme, Condizioni = ops.ToList() });
        return tc;
    }

    private static Requisito ReqCalcolo(string id, string[] dichiarati, params DtcRequisito[] voci)
    {
        var r = Extra(id, dichiarati);
        r.Guasto = voci.ToList();
        return r;
    }

    private static string Fp(DtcAvanzamentoRisultato r) =>
        string.Join("|", r.PerDtc.Select(d => $"{d.Chiave}:{d.Stato}:{d.NonAllineata}")) + $"#{r.Totale.Testo}/{r.CelleSenzaAnagrafica}";

    private static List<TestCase> DatasetDueSottoveicoli() => new()
    {
        // MY24: validazione+devalidazione conformi => Completo. MY25: solo validazione => Validato. Stesso guasto P1/ECM.
        Test("T1", 1, new[] { "MY24" }, Op("R1", "P1", PassoDtc.Validazione, Esito.Conforme), Op("R1", "P1", PassoDtc.Devalidazione, Esito.Conforme)),
        Test("T2", 2, new[] { "MY25" }, Op("R1", "P1", PassoDtc.Validazione, Esito.Conforme)),
        // Test con due veicoli: il veicolo e scelto sull'operazione.
        Test("T3", 3, new[] { "MY24", "MY25" }, Op("R1", "P2", PassoDtc.Validazione, Esito.NonConforme, "ECM", "MY25")),
    };

    [Fact]
    public void Avanzamento_StessoDtcInDueSottoveicoli_SonoCelleDistinte()
    {
        var reqs = new[] { ReqCalcolo("R1", new[] { "MY24", "MY25" }, V("P1", "ECM", "MY24"), V("P1", "ECM", "MY25"), V("P2", "ECM", "MY24"), V("P2", "ECM", "MY25")) };
        var r = DtcAvanzamentoService.Calcola(reqs, DatasetDueSottoveicoli());
        Assert.Equal(4, r.PerDtc.Count);
        Assert.Equal(0, r.CelleSenzaAnagrafica);
        StatoDtc Stato(string guasto, string sv) => r.PerDtc.Single(d => d.Guasto == guasto && d.Veicolo == sv).Stato;
        Assert.Equal(StatoDtc.Completo, Stato("P1", "MY24"));
        Assert.Equal(StatoDtc.Validato, Stato("P1", "MY25"));
        Assert.Equal(StatoDtc.DaFare, Stato("P2", "MY24"));          // l'esito non conforme dell'operazione e di MY25
        Assert.Equal(StatoDtc.NonConforme, Stato("P2", "MY25"));
        Assert.Equal(1, r.Totale.Completi);
        Assert.Equal(4, r.PerDtc.Select(d => d.Chiave).Distinct().Count());
        Assert.Equal(new[] { "MY24", "MY24", "MY25", "MY25" }, r.PerDtc.Select(d => d.Veicolo));   // ordinamento per veicolo
    }

    [Fact]
    public void Avanzamento_RaccogliEsiti_SottoveicoloDellOperazioneOAltrimentiDelTest()
    {
        var esiti = DtcAvanzamentoService.RaccogliEsiti(DatasetDueSottoveicoli());
        Assert.Equal(new[] { "MY24", "MY24", "MY25", "MY25" }, esiti.Select(e => e.Veicolo));   // T1 (x2), T2 dal test; T3 dall'operazione
        // Test con due veicoli e operazione senza veicolo: nessun veicolo (non si indovina).
        var ambiguo = DtcAvanzamentoService.RaccogliEsiti(new[] { Test("T9", 1, new[] { "A", "B" }, Op("R1", "P1", PassoDtc.Validazione, Esito.Conforme)) });
        Assert.Equal("", ambiguo.Single().Veicolo);
    }

    [Fact]
    public void Avanzamento_Abbinamento_VoceSenzaVeicolo_EUnicaCella_EAmbiguita()
    {
        // 1) voce senza veicolo (dati 6.5.0): vale per qualunque veicolo dell'operazione.
        var legacy = new[] { ReqCalcolo("R1", Array.Empty<string>(), V("P1", "ECM")) };
        var r1 = DtcAvanzamentoService.Calcola(legacy, DatasetDueSottoveicoli().Take(1));
        Assert.Equal(StatoDtc.Completo, r1.PerDtc.Single().Stato);
        Assert.Equal(0, r1.CelleSenzaAnagrafica);

        // 2) operazione senza veicolo (test con 2 veicoli) e UNICA cella con veicolo: si abbina.
        var unica = new[] { ReqCalcolo("R1", new[] { "MY24" }, V("P1", "ECM", "MY24")) };
        var senzaSv = Test("T9", 1, new[] { "A", "B" }, Op("R1", "P1", PassoDtc.Validazione, Esito.Conforme));
        Assert.Equal(StatoDtc.Validato, DtcAvanzamentoService.Calcola(unica, new[] { senzaSv }).PerDtc.Single().Stato);

        // 3) piu celle e operazione senza veicolo: ambiguo, non si abbina (celle senza anagrafica), nessuna cella cambia stato.
        var due = new[] { ReqCalcolo("R1", new[] { "MY24", "MY25" }, V("P1", "ECM", "MY24"), V("P1", "ECM", "MY25")) };
        var r3 = DtcAvanzamentoService.Calcola(due, new[] { senzaSv });
        Assert.Equal(1, r3.CelleSenzaAnagrafica);
        Assert.All(r3.PerDtc, d => Assert.Equal(StatoDtc.DaFare, d.Stato));

        // 4) veicolo dell'operazione inesistente nel requisito (e nessuna voce senza veicolo): senza anagrafica.
        var altro = Test("T8", 1, new[] { "MY99" }, Op("R1", "P1", PassoDtc.Validazione, Esito.Conforme));
        Assert.Equal(1, DtcAvanzamentoService.Calcola(due, new[] { altro }).CelleSenzaAnagrafica);
    }

    [Fact]
    public void Avanzamento_VoceConSottoveicoloNonPiuDichiarato_ENonAllineataMaConta()
    {
        var reqs = new[] { ReqCalcolo("R1", new[] { "MY24" }, V("P1", "ECM", "MY24"), V("P1", "ECM", "Vecchio"), V("P2", "ECM")) };
        var r = DtcAvanzamentoService.Calcola(reqs, Array.Empty<EsitoDtcRilevato>());
        Assert.Equal(3, r.Totale.Totale);
        Assert.Equal(new[] { "Vecchio" }, r.PerDtc.Where(d => d.NonAllineata).Select(d => d.Veicolo));
        var riga = new DtcDettaglioRiga(r.PerDtc.Single(d => d.NonAllineata));
        Assert.True(riga.NonAllineata);
        Assert.Contains("Vecchio", riga.RigaCsv());
        Assert.Contains(";Vecchio;non allineata;", riga.RigaCsv());
        Assert.Contains(riga.SignificatoStato, riga.RigaCsv());
        Assert.StartsWith("R1;P1;ECM;DaFare", riga.RigaCsv());     // colonne storiche invariate
        Assert.Equal("Requisito;guasto;componente;Stato;Conflitto;Passo mancante;Ultimo test;Ultima data;Veicolo;Allineamento;Significato stato", DtcDettaglioRiga.IntestazioneCsv);
    }

    [Fact]
    public void Avanzamento_ChiaveCella_SenzaVeicoloResta650()
    {
        Assert.Equal("R1|ECM|P1", DtcAvanzamentoService.ChiaveCella("R1", "P1", "ECM"));
        Assert.Equal("R1|ECM|P1", DtcAvanzamentoService.ChiaveCella("R1", "P1", "ECM", "  "));
        Assert.Equal("R1|ECM|P1|MY24", DtcAvanzamentoService.ChiaveCella("r1", "p1", "ecm", "my24"));
    }

    [Fact]
    public void Avanzamento_DtcLiberoSenzaRequisito_NonEntraInAuditENonCambiaNulla()
    {
        var reqs = new[] { ReqCalcolo("R1", new[] { "MY24" }, V("P1", "ECM", "MY24")) };
        var libero = Test("TL", 1, new[] { "MY24" }, Op("", "P1", PassoDtc.Validazione, Esito.NonConforme), Op("", "ZZZ9", PassoDtc.Validazione, Esito.Conforme));
        var senza = DtcAvanzamentoService.Calcola(reqs, Array.Empty<TestCase>());
        var con = DtcAvanzamentoService.Calcola(reqs, new[] { libero });
        Assert.Equal(senza.Totale, con.Totale);                                // gli esiti liberi non alterano nessun totale
        Assert.Single(con.PerDtc);
        Assert.Equal(StatoDtc.DaFare, con.PerDtc[0].Stato);
        Assert.False(con.PerDtc[0].NonAllineata);
        Assert.Equal(1, con.Totale.Totale);
    }

    // === Indice SQLite v11 ===============================================================================

    private LocalTestIndexStore NuovoStore(IEnumerable<TestCase> tests)
    {
        var store = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        foreach (var tc in tests) store.Upsert(Path.Combine(_root, tc.Id + ".json"), Day, 100, tc);
        return store;
    }

    private void Sql(string db, string sql)
    {
        SqliteConnection.ClearAllPools();
        using var c = new SqliteConnection($"Data Source={db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private object? Scalar(string db, string sql)
    {
        SqliteConnection.ClearAllPools();
        using var c = new SqliteConnection($"Data Source={db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        SqliteConnection.ClearAllPools();
        return v;
    }

    private static Requisito[] ReqDueSv() => new[] { ReqCalcolo("R1", new[] { "MY24", "MY25" }, V("P1", "ECM", "MY24"), V("P1", "ECM", "MY25"), V("P2", "ECM", "MY24"), V("P2", "ECM", "MY25")) };

    [Fact]
    public void Indice_V11_StreamingConSottoveicolo_ParitaConCalcoloInMemoria()
    {
        var tests = DatasetDueSottoveicoli();
        var store = NuovoStore(tests);
        var atteso = Fp(DtcAvanzamentoService.Calcola(ReqDueSv(), tests));
        var q = new LocalDtcQueryService(store.DatabasePath);
        Assert.True(q.Disponibile());
        Assert.Equal(atteso, Fp(DtcAvanzamentoService.Calcola(ReqDueSv(), q.StreamEsiti())));
        Assert.Equal(new[] { "MY24", "MY24", "MY25", "MY25" }, q.StreamEsiti().Select(e => e.Veicolo).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(LocalTestIndexStore.CurrentSchemaVersion.ToString(), Scalar(store.DatabasePath, "SELECT value FROM meta WHERE key='schema_version';"));
    }

    [Fact]
    public void Indice_MigrazioneV10ToV11_AggiungeLaColonnaERicostruisceLaProiezione_Idempotente()
    {
        var tests = DatasetDueSottoveicoli();
        var store = NuovoStore(tests);
        var atteso = Fp(DtcAvanzamentoService.Calcola(ReqDueSv(), tests));
        var db = store.DatabasePath;

        // Indice v10 reale: tabella senza la colonna veicolo e versione 10.
        Sql(db, "ALTER TABLE test_dtc_operation DROP COLUMN veicolo; UPDATE meta SET value = '10' WHERE key = 'schema_version';");
        Assert.False(new LocalDtcQueryService(db).Disponibile());           // v10 non migrato: il chiamante ripiega sul calcolo in memoria

        var riaperto = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        Assert.Equal(LocalTestIndexStore.CurrentSchemaVersion.ToString(), Scalar(db, "SELECT value FROM meta WHERE key='schema_version';"));
        Assert.Equal(1L, Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('test_dtc_operation') WHERE name='veicolo';")));
        var q = new LocalDtcQueryService(riaperto.DatabasePath);
        Assert.True(q.Disponibile());
        Assert.Equal(atteso, Fp(DtcAvanzamentoService.Calcola(ReqDueSv(), q.StreamEsiti())));

        // Idempotenza: forzare di nuovo la migrazione non duplica ne perde righe.
        var righe = Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM test_dtc_operation;"));
        Sql(db, "UPDATE meta SET value = '10' WHERE key = 'schema_version';");
        var terzo = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        Assert.Equal(righe, Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM test_dtc_operation;")));
        Assert.Equal(atteso, Fp(DtcAvanzamentoService.Calcola(ReqDueSv(), new LocalDtcQueryService(terzo.DatabasePath).StreamEsiti())));
    }

    [Fact]
    public void Indice_Ricostruzione_ProiezioneSvuotataOSottoveicoliAzzerati_SiRipristinaDalCacheJson()
    {
        var tests = DatasetDueSottoveicoli();
        var store = NuovoStore(tests);
        var atteso = Fp(DtcAvanzamentoService.Calcola(ReqDueSv(), tests));
        var db = store.DatabasePath;

        Sql(db, "UPDATE test_dtc_operation SET veicolo = ''; UPDATE meta SET value = '10' WHERE key = 'schema_version';");   // dati stantii
        var r1 = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        Assert.Equal(atteso, Fp(DtcAvanzamentoService.Calcola(ReqDueSv(), new LocalDtcQueryService(r1.DatabasePath).StreamEsiti())));

        Sql(db, "DELETE FROM test_dtc_operation; UPDATE meta SET value = '8' WHERE key = 'schema_version';");
        var r2 = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        Assert.Equal(atteso, Fp(DtcAvanzamentoService.Calcola(ReqDueSv(), new LocalDtcQueryService(r2.DatabasePath).StreamEsiti())));
        Assert.Equal(LocalTestIndexStore.CurrentSchemaVersion.ToString(), Scalar(db, "SELECT value FROM meta WHERE key='schema_version';"));
    }

    [Fact]
    public void Indice_V10ToV11_FallitaFaRollback_VersioneNonAvanzata_RipartePulita()
    {
        var tests = DatasetDueSottoveicoli();
        var store = NuovoStore(tests);
        var atteso = Fp(DtcAvanzamentoService.Calcola(ReqDueSv(), tests));
        var db = store.DatabasePath;
        Sql(db, """
            ALTER TABLE test_dtc_operation DROP COLUMN veicolo;
            UPDATE meta SET value = '10' WHERE key = 'schema_version';
            CREATE TRIGGER fail_dtc_backfill BEFORE INSERT ON test_dtc_operation
            BEGIN SELECT RAISE(ABORT, 'injected guasto migration failure'); END;
            """);
        Assert.Throws<SqliteException>(() => new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache")));
        Assert.Equal("10", Scalar(db, "SELECT value FROM meta WHERE key='schema_version';"));                         // versione NON avanzata
        Assert.True(Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM test_dtc_operation;")) > 0);                    // proiezione non svuotata
        Assert.Equal(0L, Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('test_dtc_operation') WHERE name='veicolo';"))); // ALTER annullato

        Sql(db, "DROP TRIGGER fail_dtc_backfill;");
        var riaperto = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        Assert.Equal(atteso, Fp(DtcAvanzamentoService.Calcola(ReqDueSv(), new LocalDtcQueryService(riaperto.DatabasePath).StreamEsiti())));
    }

    [Fact]
    public void Indice_VersioneSuperiore_NonVaInErrore_LaCacheLocaleSiRicostruisce()
    {
        var tests = DatasetDueSottoveicoli();
        var store = NuovoStore(tests);
        var atteso = Fp(DtcAvanzamentoService.Calcola(ReqDueSv(), tests));
        var db = store.DatabasePath;
        Sql(db, "UPDATE meta SET value = '12' WHERE key = 'schema_version';");        // indice scritto da una build piu recente

        // Il tipo dedicato non e corruzione ma, per un indice che e solo cache ricostruibile, autorizza quarantena + ricostruzione.
        var ex = new LocalIndexSchemaIncompatibleException("12");
        Assert.Equal(LocalIndexFaultKind.SchemaIncompatible, LocalIndexFaultClassifier.Classify(ex));
        Assert.False(LocalIndexFaultClassifier.IsCorruption(ex));

        var riaperto = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));   // nessuna eccezione
        Assert.Equal(LocalTestIndexStore.CurrentSchemaVersion.ToString(), Scalar(riaperto.DatabasePath, "SELECT value FROM meta WHERE key='schema_version';"));
        Assert.True(new LocalDtcQueryService(riaperto.DatabasePath).Disponibile());
        foreach (var tc in tests) riaperto.Upsert(Path.Combine(_root, tc.Id + ".json"), Day, 100, tc);       // il refresh ripopola la proiezione
        Assert.Equal(atteso, Fp(DtcAvanzamentoService.Calcola(ReqDueSv(), new LocalDtcQueryService(riaperto.DatabasePath).StreamEsiti())));
    }

    // === Archivio concorrente tra due PC =================================================================

    [Fact]
    public void Archivio_DuePc_StessoRequisito_SottoveicoliDiversi_MergePerChiave_NessunaModificaPersa()
    {
        var a = Nuovo(); var b = Nuovo();
        SeminaRequisito(a, Extra("D1", "A", "B"), new List<DtcRequisito> { V("P1", "ECM", "A") });
        var d1 = Req(a, "D1");
        var baseA = a.Master.RequisitiDtc.Leggi(d1); var baseB = b.Master.RequisitiDtc.Leggi(Req(b, "D1"));

        // PC-A aggiunge lo stesso codice su B; PC-B aggiunge P2 su A e un'altra voce senza veicolo.
        a.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(d1, baseA.Voci.Concat(new[] { V("P1", "ECM", "B") }).ToList(), baseA.Documento) }, "PC-A");
        b.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(Req(b, "D1"), baseB.Voci.Concat(new[] { V("P2", "ECM", "A"), V("P1", "ECM") }).ToList(), baseB.Documento) }, "PC-B");

        var finale = Nuovo(false).Master.RequisitiDtc.Leggi(Req(a, "D1")).Voci;
        Assert.Equal(new[] { "/P1", "A/P1", "A/P2", "B/P1" }, finale.Select(v => $"{v.Veicolo}/{v.Codice}").OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(finale.Count, finale.Select(DtcRequisito.Chiave).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Archivio_DuePc_StessaVoceAggiuntaInEntrambi_UnaSolaVolta_EStessoCodiceAltroSottoveicoloResta()
    {
        var a = Nuovo(); var b = Nuovo();
        SeminaRequisito(a, Extra("D1", "A", "B"), new List<DtcRequisito> { V("P1", "ECM", "A") });
        var baseA = a.Master.RequisitiDtc.Leggi(Req(a, "D1")); var baseB = b.Master.RequisitiDtc.Leggi(Req(b, "D1"));
        a.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(Req(a, "D1"), baseA.Voci.Concat(new[] { V("P9", "ECM", "A") }).ToList(), baseA.Documento) }, "PC-A");
        b.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(Req(b, "D1"), baseB.Voci.Concat(new[] { V("P9", "ECM", "A"), V("P9", "ECM", "B") }).ToList(), baseB.Documento) }, "PC-B");
        var finale = Nuovo(false).Master.RequisitiDtc.Leggi(Req(a, "D1")).Voci;
        Assert.Equal(new[] { "A/P1", "A/P9", "B/P9" }, finale.Select(v => $"{v.Veicolo}/{v.Codice}").OrderBy(x => x, StringComparer.Ordinal));
    }

    // === Audit / provider ================================================================================

    [Fact]
    public void Provider_Calcola_SegnalaNonAllineata_QuandoIlSottoveicoloNonEPiuDichiarato()
    {
        var s = Nuovo();
        SeminaRequisito(s, Extra("D1", "A", "B"), new List<DtcRequisito> { V("P1", "ECM", "A"), V("P1", "ECM", "B") });
        Assert.All(DtcAvanzamentoProvider.Calcola(s, new[] { Req(s, "D1") }).PerDtc, d => Assert.False(d.NonAllineata));

        var req = Req(s, "D1");
        RequisitoApplicabilita.ImpostaSottoveicoli(req, new[] { "A" });                 // B rimosso/rinominato dal requisito
        s.Master.Requisiti.Save(new List<Requisito> { req });
        var r = DtcAvanzamentoProvider.Calcola(s, new[] { Req(s, "D1") });
        Assert.Equal(2, r.Totale.Totale);                                               // la voce conta ancora
        Assert.Equal(new[] { "B" }, r.PerDtc.Where(d => d.NonAllineata).Select(d => d.Veicolo));
    }
}
