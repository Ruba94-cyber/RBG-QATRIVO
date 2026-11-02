using System.Text.Json;
using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Restrizione Funzionalita.VeicoliPerEcu (dato reale: una funzionalità su un componente vale solo per alcuni veicoli): modello e
/// predicati, normalizzazione, testo Excel, import e merge dei duplicati, propagazione di Unisci/Rinomina/Dividi su componente, Veicolo e
/// Funzionalità, protezione dall'eliminazione, filtri dei picker Test e Segnalazioni rete, fingerprint di salvataggio.</summary>
[Collection("Localization")]
public sealed class FunzionalitaVeicoliPerEcuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_FunzVeicoliPerEcu_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStatoMaster()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    private static Funzionalita F(string nome, bool tutteLeEcu, string[]? ecuAssociate, params VeicoliFunzionalitaEcu[] voci) => new()
    {
        Nome = nome,
        TutteLeEcu = tutteLeEcu,
        EcuAssociate = ecuAssociate?.ToList() ?? new List<string>(),
        VeicoliPerEcu = voci.ToList(),
    };

    private static VeicoliFunzionalitaEcu Voce(string componente, params string[] veicoli) => new() { Componente = componente, Veicoli = veicoli.ToList() };

    /// <summary>File Excel d'import delle Funzionalità: colonne A-C come nel modulo; colonna D (veicoli per componente) solo se richiesta.</summary>
    private string CreaFileImport(bool conQuartaColonna, params (string Nome, string Componente, string? VeicoliPerEcu)[] righe)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "Nome";
        ws.Cell(1, 2).Value = "Categorie";
        ws.Cell(1, 3).Value = "Componente";
        if (conQuartaColonna) ws.Cell(1, 4).Value = "Veicoli per componente";
        for (var r = 0; r < righe.Length; r++)
        {
            ws.Cell(r + 2, 1).Value = righe[r].Nome;
            ws.Cell(r + 2, 2).Value = "";
            ws.Cell(r + 2, 3).Value = righe[r].Componente;
            if (conQuartaColonna && righe[r].VeicoliPerEcu != null) ws.Cell(r + 2, 4).Value = righe[r].VeicoliPerEcu;
        }
        var percorso = Path.Combine(_dir, "import_" + Guid.NewGuid().ToString("N") + ".xlsx");
        wb.SaveAs(percorso);
        return percorso;
    }

    private static FunzionalitaViewModel ApriImport(AppState state, string percorso)
    {
        var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));
        return vm;
    }

    // ---------- Modello, JSON e predicati ----------

    [Fact]
    public void Json_SenzaCampoVeicoliPerEcu_LeggeNessunaRestrizione()
    {
        var f = JsonSerializer.Deserialize<Funzionalita>("""{"Nome":"Diagnosi","TutteLeEcu":false,"EcuAssociate":["BCM"]}""", JsonIo.Options)!;

        Assert.Empty(f.VeicoliPerEcu);
        Assert.True(f.ValeSu("BCM", "V1"));
        Assert.True(f.ValeSu("BCM", "V2"));
    }

    [Fact]
    public void Json_VeicoliPerEcuNullOVeicoliNull_SiLeggonoComeVuoti()
    {
        var senzaVoci = JsonSerializer.Deserialize<Funzionalita>("""{"Nome":"X","VeicoliPerEcu":null}""", JsonIo.Options)!;
        Assert.Empty(senzaVoci.VeicoliPerEcu);

        var senzaVeicoli = JsonSerializer.Deserialize<Funzionalita>("""{"Nome":"X","VeicoliPerEcu":[{"Componente":"BCM","Veicoli":null}]}""", JsonIo.Options)!;
        var voce = Assert.Single(senzaVeicoli.VeicoliPerEcu);
        Assert.Empty(voce.Veicoli);
    }

    [Fact]
    public void Json_VeicoliPerEcu_RoundTripConservaLeVoci()
    {
        var originale = F("Diagnosi", false, new[] { "BCM", "SGW" }, Voce("BCM", "V1", "V2"), Voce("SGW"));

        var json = JsonSerializer.Serialize(originale, JsonIo.Options);
        var letta = JsonSerializer.Deserialize<Funzionalita>(json, JsonIo.Options)!;

        Assert.Contains("\"VeicoliPerEcu\"", json);
        Assert.Equal(originale.VeicoliPerEcuTesto, letta.VeicoliPerEcuTesto);
        Assert.Equal(new[] { "V1", "V2" }, letta.VeicoliPerEcu[0].Veicoli);
    }

    /// <summary>Il fingerprint di salvataggio e l'hash dell'indice locale leggono l'intero JSON: una restrizione cambia il contenuto
    /// e quindi viene rilevata come modifica tra PC.</summary>
    [Fact]
    public void Fingerprint_CambiaSoloConLeRestrizioni()
    {
        var uid = Guid.Parse("6b2f1c1e-6f4a-4f3b-9c55-2b6d8e1a7f10");
        var senza = F("Diagnosi", false, new[] { "BCM" });
        senza.Uid = uid;
        var con = F("Diagnosi", false, new[] { "BCM" }, Voce("BCM", "V1"));
        con.Uid = uid;

        Assert.NotEqual(EntityStateFingerprint.Compute(senza), EntityStateFingerprint.Compute(con));
    }

    [Fact]
    public void ValeSu_SenzaVoce_ValeSuTuttiIVeicoliDellEcuAssociata()
    {
        var f = F("F", false, new[] { "E1" });

        Assert.True(f.ValeSuEcu("E1"));
        Assert.True(f.ValeSu("E1", "V1"));
        Assert.True(f.ValeSu("E1", "V2"));
        Assert.False(f.ValeSuEcu("E2"));
        Assert.False(f.ValeSu("E2", "V1"));
    }

    [Fact]
    public void ValeSu_ConVoce_ValeSoloSuiVeicoliElencati()
    {
        var f = F("F", false, new[] { "E1" }, Voce("E1", "V1"));

        Assert.True(f.ValeSu("E1", "V1"));
        Assert.False(f.ValeSu("E1", "V2"));
        Assert.True(f.ValeSu("E1", ""));   // solo verifica sul componente
        Assert.True(f.ValeSu("E1", null));
    }

    [Fact]
    public void ValeSu_VoceSenzaVeicoli_NonValeSuNessunVeicoloDellEcu()
    {
        var f = F("F", true, null, Voce("E1"));

        Assert.True(f.ValeSuEcu("E1"));
        Assert.False(f.ValeSu("E1", "V1"));
        Assert.True(f.ValeSu("E2", "V1")); // le altre componente restano su tutti i veicoli (TutteLeEcu)
    }

    [Fact]
    public void ValeSu_NomiSenzaDistinzioneDiMaiuscole()
    {
        var f = F("F", false, new[] { "bcm" }, Voce("BCM", "v1"));

        Assert.True(f.ValeSu("Bcm", "V1"));
        Assert.False(f.ValeSu("BCM", "V2"));
    }

    // ---------- Normalizzazione (invariante al salvataggio) ----------

    [Fact]
    public void Normalizza_RimuoveLeVociDiEcuNonPiuApplicabili()
    {
        var f = F("F", false, new[] { "E1" }, Voce("E1", "V1"), Voce("E9", "V2"));

        Assert.True(f.NormalizzaVeicoliPerEcu());
        Assert.Equal(new[] { "E1" }, f.VeicoliPerEcu.Select(v => v.Componente));
        Assert.False(f.NormalizzaVeicoliPerEcu()); // idempotente
    }

    [Fact]
    public void Normalizza_FondeLeVociDuplicateSenzaDistinguereLeMaiuscole()
    {
        var f = F("F", false, new[] { "E1" }, Voce("E1", "V1"), Voce("e1", "V2", "V1"));

        f.NormalizzaVeicoliPerEcu();

        var voce = Assert.Single(f.VeicoliPerEcu);
        Assert.Equal("E1", voce.Componente);
        Assert.Equal(new[] { "V1", "V2" }, voce.Veicoli);
    }

    [Fact]
    public void Normalizza_ScartaVeicoliVuotiEDuplicati()
    {
        var f = F("F", false, new[] { "E1" }, Voce("E1", "V1", " ", "v1", "V2", ""));

        f.NormalizzaVeicoliPerEcu();

        Assert.Equal(new[] { "V1", "V2" }, f.VeicoliPerEcu[0].Veicoli);
    }

    // ---------- Testo Excel (VeicoliPerEcuTesto) ----------

    [Fact]
    public void VeicoliPerEcuTesto_RoundTripOrdinatoEStabile()
    {
        var f = new Funzionalita { Nome = "F" };

        f.VeicoliPerEcuTesto = "E2 => V3; E1 => V2, V1; E3 =>";

        Assert.Equal("E1 => V1, V2; E2 => V3; E3 =>", f.VeicoliPerEcuTesto);
        Assert.Empty(f.VeicoliPerEcuTestoNonValidi);
        Assert.Equal(3, f.VeicoliPerEcu.Count);
        Assert.Empty(f.VeicoliPerEcu.Single(v => v.Componente == "E3").Veicoli);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("   ")]
    public void VeicoliPerEcuTesto_VuotoOAsterisco_NessunaRestrizione(string testo)
    {
        var f = F("F", false, new[] { "E1" }, Voce("E1", "V1"));

        f.VeicoliPerEcuTesto = testo;

        Assert.Empty(f.VeicoliPerEcu);
        Assert.Equal("", f.VeicoliPerEcuTesto);
    }

    [Theory]
    [InlineData("E1 V1; E2 => V3", "E1 V1")]
    [InlineData("=> V1; E2 => V3", "=> V1")]
    public void VeicoliPerEcuTesto_SegmentoNonValido_RestaSegnalatoSenzaPerdereLeAltreVoci(string testo, string segnalato)
    {
        var f = new Funzionalita { Nome = "F" };

        f.VeicoliPerEcuTesto = testo;

        Assert.Equal(new[] { segnalato }, f.VeicoliPerEcuTestoNonValidi);
        Assert.Equal("E2 => V3", f.VeicoliPerEcuTesto);
    }

    // ---------- Propagazione Unisci / Rinomina / Dividi (componente, Veicolo, Funzionalità) ----------

    [Fact]
    public void UnisciEcu_UnisceLeRestrizioniDelleDueEcu()
    {
        var state = CreaStatoMaster();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1"] }, new() { Nome = "ECM", VeicoliAssociati = ["V2"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", false, new[] { "BCM", "ECM" }, Voce("BCM", "V1"), Voce("ECM", "V2")) });

        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, new List<string>());

        var f = state.Master.Funzionalita.Load().Single();
        Assert.Equal(new[] { "ECM" }, f.EcuAssociate);
        var voce = Assert.Single(f.VeicoliPerEcu);
        Assert.Equal("ECM", voce.Componente);
        Assert.Equal(new[] { "V1", "V2" }, voce.Veicoli);
    }

    /// <summary>Regola decisa: se una delle due componente unite non ha voce (vale su tutti i suoi veicoli), il risultato non ha voce: vale su tutti
    /// i veicoli della nuova componente.</summary>
    [Fact]
    public void UnisciEcu_SeUnaDelleDueEcuNonHaVoce_LaFunzionalitaTornaSuTutti()
    {
        var state = CreaStatoMaster();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1"] }, new() { Nome = "ECM", VeicoliAssociati = ["V2"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", false, new[] { "BCM", "ECM" }, Voce("BCM", "V1")) });

        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, new List<string>());

        var f = state.Master.Funzionalita.Load().Single();
        Assert.Equal(new[] { "ECM" }, f.EcuAssociate);
        Assert.Empty(f.VeicoliPerEcu);
    }

    [Fact]
    public void RinominaEcu_SpostaLaRestrizioneSullaNuovaEcu()
    {
        var state = CreaStatoMaster();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", false, new[] { "BCM" }, Voce("BCM", "V1")) });

        MasterUnioneService.Rinomina(state, state.Master.Componente, "Componente", "BCM", "BCM2", e => e.Nome, new List<string>());

        var f = state.Master.Funzionalita.Load().Single();
        Assert.Equal(new[] { "BCM2" }, f.EcuAssociate);
        var voce = Assert.Single(f.VeicoliPerEcu);
        Assert.Equal("BCM2", voce.Componente);
        Assert.Equal(new[] { "V1" }, voce.Veicoli);
    }

    [Fact]
    public void DividiEcu_CopiaLaRestrizioneSulleNuoveEcu()
    {
        var state = CreaStatoMaster();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1", "V2"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", false, new[] { "BCM" }, Voce("BCM", "V1")) });

        MasterUnioneService.Dividi(state, state.Master.Componente, "Componente", "BCM", new[] { "BCM-B" }, e => e.Nome, new List<string>());

        var f = state.Master.Funzionalita.Load().Single();
        Assert.Equal(new[] { "BCM", "BCM-B" }, f.EcuAssociate);
        Assert.Equal(new[] { "BCM", "BCM-B" }, f.VeicoliPerEcu.Select(v => v.Componente));
        Assert.All(f.VeicoliPerEcu, v => Assert.Equal(new[] { "V1" }, v.Veicoli));
    }

    [Fact]
    public void RinominaEcu_ConTutteLeEcu_SpostaLaRestrizioneSullaNuovaEcu()
    {
        var state = CreaStatoMaster();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", true, null, Voce("BCM", "V1")) });

        MasterUnioneService.Rinomina(state, state.Master.Componente, "Componente", "BCM", "BCM2", e => e.Nome, new List<string>());

        // Con "Tutti i componenti" la nuova componente non ha una voce propria: deve ereditare la restrizione del solo nome vecchio.
        var f = state.Master.Funzionalita.Load().Single();
        Assert.True(f.TutteLeEcu);
        var voce = Assert.Single(f.VeicoliPerEcu);
        Assert.Equal("BCM2", voce.Componente);
        Assert.Equal(new[] { "V1" }, voce.Veicoli);
        Assert.True(f.ValeSu("BCM2", "V1"));
        Assert.False(f.ValeSu("BCM2", "V2"));
    }

    [Fact]
    public void DividiEcu_ConTutteLeEcu_CopiaLaRestrizioneSulleNuoveEcu()
    {
        var state = CreaStatoMaster();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1", "V2"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", true, null, Voce("BCM", "V1")) });

        MasterUnioneService.Dividi(state, state.Master.Componente, "Componente", "BCM", new[] { "BCM-B" }, e => e.Nome, new List<string>());

        var f = state.Master.Funzionalita.Load().Single();
        Assert.True(f.TutteLeEcu);
        Assert.Equal(new[] { "BCM", "BCM-B" }, f.VeicoliPerEcu.Select(v => v.Componente));
        Assert.All(f.VeicoliPerEcu, v => Assert.Equal(new[] { "V1" }, v.Veicoli));
        Assert.False(f.ValeSu("BCM-B", "V2"));
    }

    [Fact]
    public void RinominaVeicolo_AggiornaLeVociDelleRestrizioni()
    {
        var state = CreaStatoMaster();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" }, new() { Nome = "V2" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1", "V2"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", false, new[] { "BCM" }, Voce("BCM", "V1", "V2")) });

        MasterUnioneService.Rinomina(state, state.Master.Veicoli, "Veicolo", "V1", "V1b", v => v.Nome, new List<string>());

        var voce = Assert.Single(state.Master.Funzionalita.Load().Single().VeicoliPerEcu);
        Assert.Equal(new[] { "V1b", "V2" }, voce.Veicoli);
    }

    [Fact]
    public void UnisciVeicolo_SostituisceNelleVociSenzaDuplicati()
    {
        var state = CreaStatoMaster();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" }, new() { Nome = "V2" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1", "V2"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", false, new[] { "BCM" }, Voce("BCM", "V1", "V2")) });

        MasterUnioneService.Unisci(state, state.Master.Veicoli, "Veicolo", "V1", "V2", v => v.Nome, new List<string>());

        var voce = Assert.Single(state.Master.Funzionalita.Load().Single().VeicoliPerEcu);
        Assert.Equal(new[] { "V2" }, voce.Veicoli);
    }

    [Fact]
    public void DividiVeicolo_AggiungeIlNuovoVeicoloAlleRestrizioniDellOriginale()
    {
        var state = CreaStatoMaster();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", false, new[] { "BCM" }, Voce("BCM", "V1")) });

        MasterUnioneService.Dividi(state, state.Master.Veicoli, "Veicolo", "V1", new[] { "V1b" }, v => v.Nome, new List<string>());

        var voce = Assert.Single(state.Master.Funzionalita.Load().Single().VeicoliPerEcu);
        Assert.Equal(new[] { "V1", "V1b" }, voce.Veicoli);
    }

    [Fact]
    public void UnisciFunzionalita_UnisceLeRestrizioniDelleDueFunzionalita()
    {
        var state = CreaStatoMaster();
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            F("Diagnosi", false, new[] { "E1" }, Voce("E1", "V1")),
            F("Diagnosi UnitLogger", false, new[] { "E2" }, Voce("E2", "V2")),
        });

        MasterUnioneService.Unisci(state, state.Master.Funzionalita, "Funzionalita", "Diagnosi UnitLogger", "Diagnosi", f => f.Nome, new List<string>());

        var f = state.Master.Funzionalita.Load().Single();
        Assert.Equal(new[] { "E1", "E2" }, f.EcuAssociate);
        Assert.Equal(new[] { "E1", "E2" }, f.VeicoliPerEcu.Select(v => v.Componente));
        Assert.Equal(new[] { "V1" }, f.VeicoliPerEcu[0].Veicoli);
        Assert.Equal(new[] { "V2" }, f.VeicoliPerEcu[1].Veicoli);
    }

    [Fact]
    public void DividiFunzionalita_CopiaLeRestrizioniSullaNuovaVoce()
    {
        var state = CreaStatoMaster();
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", false, new[] { "E1" }, Voce("E1", "V1")) });

        MasterUnioneService.Dividi(state, state.Master.Funzionalita, "Funzionalita", "Diagnosi", new[] { "Diagnosi-B" }, f => f.Nome, new List<string>());

        var catalogo = state.Master.Funzionalita.Load();
        Assert.Equal(2, catalogo.Count);
        Assert.All(catalogo, f =>
        {
            var voce = Assert.Single(f.VeicoliPerEcu);
            Assert.Equal("E1", voce.Componente);
            Assert.Equal(new[] { "V1" }, voce.Veicoli);
        });
    }

    /// <summary>Un veicolo citato in una restrizione non si elimina definitivamente, come già per Componente.VeicoliAssociati (altrimenti la
    /// restrizione punterebbe a un nome inesistente).</summary>
    [Fact]
    public void VeicoliViewModel_BloccaEliminazioneSeCitatoInUnaRestrizione()
    {
        var state = CreaStatoMaster();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1", Archiviata = true } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", false, new[] { "BCM" }, Voce("BCM", "V1")) });
        var vm = new VeicoliViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(v => v.Nome == "V1");

        vm.EliminaDefinitivamenteCommand.Execute(riga);

        Assert.Contains(riga, vm.Items);
        Assert.True(vm.HaMessaggioErrore);
    }

    // ---------- Import Excel (colonna D) ----------

    [Fact]
    public void ImportExcel_QuartaColonna_ImportaLeRestrizioniPerEcu()
    {
        var state = CreaStatoMaster();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1", "V2"] } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" }, new() { Nome = "V2" } });
        var percorso = CreaFileImport(true, ("Diagnosi", "BCM", "BCM => V1"));

        var vm = ApriImport(state, percorso);

        Assert.False(vm.HaMessaggioErrore);
        var importata = vm.Items.Single(f => f.Nome == "Diagnosi");
        Assert.Equal(new[] { "BCM" }, importata.EcuAssociate);
        Assert.Equal("BCM => V1", importata.VeicoliPerEcuTesto);
    }

    /// <summary>Un file 6.7.1 (tre colonne) non tocca le restrizioni: la colonna D è semplicemente assente.</summary>
    [Fact]
    public void ImportExcel_TreColonne_LasciaVuoteLeRestrizioni()
    {
        var state = CreaStatoMaster();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1"] } });
        var percorso = CreaFileImport(false, ("Diagnosi", "BCM", null));

        var vm = ApriImport(state, percorso);

        Assert.False(vm.HaMessaggioErrore);
        Assert.Empty(vm.Items.Single(f => f.Nome == "Diagnosi").VeicoliPerEcu);
    }

    /// <summary>Errori di formato, veicoli o componente non presenti nel master e restrizioni su componente non associate: l'import si blocca per intero.</summary>
    [Theory]
    [InlineData("BCM => V9")]    // veicolo non presente nel master
    [InlineData("SGW => V1")]    // componente presente nel master ma non associata alla funzionalita'
    [InlineData("BCM V1")]       // segmento senza "=>"
    [InlineData("XYZ => V1")]    // componente non presente nel master
    public void ImportExcel_RestrizioneNonValida_BloccaImportAtomico(string veicoliPerEcu)
    {
        var state = CreaStatoMaster();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1"] }, new() { Nome = "SGW", VeicoliAssociati = ["V1"] } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" } });
        var percorso = CreaFileImport(true, ("Diagnosi", "BCM", veicoliPerEcu));

        var vm = ApriImport(state, percorso);

        Assert.True(vm.HaMessaggioErrore);
        Assert.DoesNotContain(vm.Items, f => f.Nome == "Diagnosi");
    }

    /// <summary>Duplicato di import: le restrizioni si fondono per unione (come i componenti associate), senza perdere quelle già presenti.</summary>
    [Fact]
    public void ImportExcel_Duplicato_UnisceLeRestrizioni()
    {
        var state = CreaStatoMaster();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1", "V2"] } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" }, new() { Nome = "V2" } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", false, new[] { "BCM" }, Voce("BCM", "V1")) });
        var percorso = CreaFileImport(true, ("Diagnosi", "BCM", "BCM => V2"));

        var vm = ApriImport(state, percorso);

        Assert.False(vm.HaMessaggioErrore);
        Assert.Equal("BCM => V1, V2", vm.Items.Single(f => f.Nome == "Diagnosi").VeicoliPerEcuTesto);
    }

    /// <summary>Un file senza colonna D (o con la cella vuota) non allarga le restrizioni già presenti sui componenti della riga: la regola
    /// "più ampio vince" si applica solo a una riga che dichiara le proprie restrizioni.</summary>
    [Fact]
    public void ImportExcel_Duplicato_SenzaColonnaD_NonAllargaLeRestrizioni()
    {
        var state = CreaStatoMaster();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1", "V2"] } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" }, new() { Nome = "V2" } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Diagnosi", false, new[] { "BCM" }, Voce("BCM", "V1")) });
        var percorso = CreaFileImport(false, ("Diagnosi", "BCM", null));

        var vm = ApriImport(state, percorso);

        Assert.False(vm.HaMessaggioErrore);
        Assert.Equal("BCM => V1", vm.Items.Single(f => f.Nome == "Diagnosi").VeicoliPerEcuTesto);
    }

    [Fact]
    public void VeicoliPerEcuTesto_RoundTripAttraversoImport()
    {
        var state = CreaStatoMaster();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1", "V2"] } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" }, new() { Nome = "V2" } });
        var originale = F("Diagnosi", false, new[] { "BCM" }, Voce("BCM", "V2", "V1"));
        var testoEsportato = originale.VeicoliPerEcuTesto;
        var percorso = CreaFileImport(true, ("Diagnosi reimportata", "BCM", testoEsportato));

        var vm = ApriImport(state, percorso);

        var reimportata = vm.Items.Single(f => f.Nome == "Diagnosi reimportata");
        Assert.Equal(testoEsportato, reimportata.VeicoliPerEcuTesto);
        Assert.Equal(new[] { "V1", "V2" }, reimportata.VeicoliPerEcu[0].Veicoli);
    }

    // ---------- Filtri dei picker (Test e Segnalazioni rete) ----------

    [Fact]
    public void TestCaseEditor_FunzionalitaDisponibili_ConVeicoliScelti_RispettaLaRestrizione()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            F("Solo-V1", false, new[] { "BCM" }, Voce("BCM", "V1")),
            F("Generica", true, null),
        });
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.AggiungiEcuCommand.Execute("BCM");

        vm.TestCase.Veicoli = new List<string> { "V2" };
        Assert.DoesNotContain("Solo-V1", vm.FunzionalitaDisponibili);
        Assert.Contains("Generica", vm.FunzionalitaDisponibili);

        vm.TestCase.Veicoli = new List<string> { "V1" };
        Assert.Contains("Solo-V1", vm.FunzionalitaDisponibili);
    }

    [Fact]
    public void SegnalazioneRete_FunzionalitaDisponibili_ConVeicoliScelti_RispettaLaRestrizione()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1", "V2"] } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" }, new() { Nome = "V2" } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { F("Solo-V1", false, new[] { "BCM" }, Voce("BCM", "V1")) });
        var operatori = state.Master.Operatori.Load();
        operatori.Add(TestOperatori.Sigilla(new Operatore { Nome = "Operatore Rete", Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Operatore Rete", "1234"));
        var vm = new SegnalazioneReteEditorViewModel(state, id: null, torna: () => { });
        vm.AggiungiEcuCommand.Execute("BCM");

        vm.AggiungiVeicoloCommand.Execute("V2");
        Assert.DoesNotContain("Solo-V1", vm.FunzionalitaDisponibili);

        vm.AggiungiVeicoloCommand.Execute("V1");
        Assert.Contains("Solo-V1", vm.FunzionalitaDisponibili);
    }

    [Fact]
    public void TestCaseEditor_FunzionalitaDisponibili_VoceResiduaDiVeicoloTolto_NonFaComparireLaFunzione()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        // BCM è montata solo su V1: la restrizione su V2 è una voce residua (V2 tolto dalla pagina componente), quindi inerte.
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            F("Solo-V2", false, new[] { "BCM" }, Voce("BCM", "V2")),
            F("Solo-V1", false, new[] { "BCM" }, Voce("BCM", "V1")),
        });
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.AggiungiEcuCommand.Execute("BCM");

        vm.TestCase.Veicoli = new List<string> { "V1" };
        Assert.Contains("Solo-V1", vm.FunzionalitaDisponibili); // controllo: il veicolo montato ammette la funzione
        vm.TestCase.Veicoli = new List<string> { "V2" };
        Assert.DoesNotContain("Solo-V2", vm.FunzionalitaDisponibili);
    }

    [Fact]
    public void SegnalazioneRete_FunzionalitaDisponibili_VoceResiduaDiVeicoloTolto_NonFaComparireLaFunzione()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1"] } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" }, new() { Nome = "V2" } });
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            F("Solo-V2", false, new[] { "BCM" }, Voce("BCM", "V2")),
            F("Solo-V1", false, new[] { "BCM" }, Voce("BCM", "V1")),
        });
        var operatori = state.Master.Operatori.Load();
        operatori.Add(TestOperatori.Sigilla(new Operatore { Nome = "Operatore Rete", Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Operatore Rete", "1234"));
        var vm = new SegnalazioneReteEditorViewModel(state, id: null, torna: () => { });
        vm.AggiungiEcuCommand.Execute("BCM");

        vm.Veicoli.Add("V1");
        Assert.Contains("Solo-V1", vm.FunzionalitaDisponibili); // controllo: il veicolo montato ammette la funzione
        vm.Veicoli.Clear();
        vm.Veicoli.Add("V2");
        Assert.DoesNotContain("Solo-V2", vm.FunzionalitaDisponibili);
    }
}
