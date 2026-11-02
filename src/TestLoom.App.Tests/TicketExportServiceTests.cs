using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>GeneraTesto deve riflettere sempre la selezione corrente di Strumenti usati: deselezionare uno
/// strumento non ripulisce mai le sue chiavi da ValoriStrumenti/ValoriStrumentiTest (restano nel JSON per non
/// perdere dati se lo strumento viene riselezionato), ma senza filtro comparivano ancora nell'export come dati
/// attuali (feedback utente 2026-08-19, punto 3).
///
/// Revisione 2026-09-18 (punto 0 del piano, isolamento test): questi tre casi + il costruttore usavano
/// `new AppState()` col config service DI DEFAULT — quello reale del PC (%APPDATA%\TestLoom.App\config.json),
/// non un fixture isolato. Se quel config ha una CartellaCondivisa valida (qui: la cartella dati fittizia
/// usata per i collaudi), ogni esecuzione fa un bootstrap COMPLETO di quella cartella (osservato: 5m36s per
/// 41 test, con scritture di bookkeeping _ditm/transactions sulla cartella) - lento, non deterministico da
/// un PC all'altro, e un contributo concreto ai fallimenti sporadici della suite completa mai spiegati
/// (stesso principio del punto 0 su TestTemplateDisplay: stato globale ambientale invece di un fixture
/// hermetic). GeneraTesto qui non usa mai templates/master data della cartella condivisa (nessuna delle tre
/// sezioni testate tocca TestTemplateDisplay), quindi TestStateFactory.Create() - isolato, senza bootstrap -
/// è equivalente e immediato.</summary>
public sealed class TicketExportServiceTests
{
    [Fact]
    public void PacchettiPolarion_TengonoIlNomeOperatoreSuUnaRigaSeparata()
    {
        using var state = TestStateFactory.Create();
        state.Config.OperatoreAttivo = "Nome Operatore";
        var sezioni = new HashSet<string>();
        var esportazioni = new[]
        {
            TicketExportService.GeneraTesto(new TestCase { Id = "TC-1", Componente = "BCM" }, state, sezioni),
            TicketExportService.GeneraTesto(new ChangeRequest { Id = "CR-1" }, state, sezioni),
            TicketExportService.GeneraTesto(new NonConformita { Id = "NC-1" }, state, sezioni),
        };

        Assert.All(esportazioni, testo =>
        {
            Assert.Contains("\nOperatore: Nome Operatore\n", testo.Replace("\r\n", "\n"));
            Assert.DoesNotContain(" da Nome Operatore", testo);
        });
    }

    [Fact]
    public void GeneraTesto_EscludeCampiRevisioneDiUnoStrumentoNonPiuSelezionato()
    {
        var tc = new TestCase { Id = "TC-1", Componente = "BCM", StrumentiUsati = new List<string> { "ValidationTool" } };
        var esec = new Esecuzione { Iterazione = 1, Data = DateTime.Now, Operatore = "Op" };
        esec.ValoriStrumenti["ValidationTool::Campo1"] = "valore-validationtool";
        esec.ValoriStrumenti["Guasto Injection::Campo2"] = "valore-strumento-deselezionato";
        tc.Esecuzioni.Add(esec);

        using var state = TestStateFactory.Create();
        var testo = TicketExportService.GeneraTesto(tc, state, new HashSet<string> { PolarionSezioni.RevisioneCorrente });

        Assert.Contains("valore-validationtool", testo);
        Assert.DoesNotContain("valore-strumento-deselezionato", testo);
    }

    [Fact]
    public void GeneraTesto_EscludeCampiOperazioneDiUnoStrumentoNonPiuSelezionato()
    {
        var tc = new TestCase { Id = "TC-1", Componente = "BCM", StrumentiUsati = new List<string> { "Guasto Injection" } };
        var esec = new Esecuzione { Iterazione = 1, Data = DateTime.Now, Operatore = "Op" };
        var cond = new Condizione { Esito = Esito.Conforme };
        cond.ValoriStrumenti["Guasto Injection::Codice"] = "P0420";
        cond.ValoriStrumenti["ValidationTool::Vecchio"] = "dato-orfano";
        esec.Condizioni.Add(cond);
        tc.Esecuzioni.Add(esec);

        using var state = TestStateFactory.Create();
        var testo = TicketExportService.GeneraTesto(tc, state, new HashSet<string> { PolarionSezioni.Operazioni });

        Assert.Contains("P0420", testo);
        Assert.DoesNotContain("dato-orfano", testo);
    }

    [Fact]
    public void GeneraTesto_EscludeCampiTestDiUnoStrumentoNonPiuSelezionato()
    {
        var tc = new TestCase { Id = "TC-1", Componente = "BCM", StrumentiUsati = new List<string> { "UnitLogger" } };
        tc.ValoriStrumentiTest["UnitLogger::Licenza"] = "ABC123";
        tc.ValoriStrumentiTest["ValidationTool::Vecchio"] = "dato-orfano-test";
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = DateTime.Now, Operatore = "Op" });

        using var state = TestStateFactory.Create();
        var testo = TicketExportService.GeneraTesto(tc, state, new HashSet<string> { PolarionSezioni.CampiStrumento });

        Assert.Contains("ABC123", testo);
        Assert.DoesNotContain("dato-orfano-test", testo);
    }

    /// <summary>Bug (anteprima e pacchetto CR): la sezione "Campi CR" stampava la chiave GUID del campo al posto del suo nome
    /// ("a0c890fa-...: Diagnostica componente"). Ora l'etichetta viene dai campi del template CR.</summary>
    [Fact]
    public void PacchettoCr_StampaIlNomeDelCampoNonLaChiaveGuid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_PacchettoCr_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var state = TestStateFactory.Create();
            Directory.CreateDirectory(dir); // Il bootstrap non ricrea una cartella condivisa assente.
            state.InizializzaCartella(dir);
            var campoSoftware = new CampoTemplate { Tipo = TipoCampoTemplate.CampoLibero, Nome = "Componente software" };
            var campoVersione = new CampoTemplate { Tipo = TipoCampoTemplate.CampoLibero, Nome = "Versione prevista" };
            var sezione = new SezioneTemplate { Nome = "Dettagli", Campi = new() { campoVersione } };
            var template = new TestTemplate { Nome = "Change Request", IsChangeRequest = true, CampiPianificazione = new() { campoSoftware }, Sezioni = new() { sezione } };
            state.Master.TestTemplate.Save(new List<TestTemplate> { template });

            var cr = new ChangeRequest { Id = "CR-2026-0001", TemplateUid = template.Uid };
            cr.CampiCustom[campoSoftware.Uid.ToString()] = "Diagnostica componente";
            cr.CampiCustom[campoVersione.Uid.ToString()] = "26.3.0";

            var testo = TicketExportService.GeneraTesto(cr, state, new HashSet<string> { CrSezioni.CampiCr });

            Assert.Contains("Componente software: Diagnostica componente", testo);
            Assert.Contains("Versione prevista: 26.3.0", testo);
            Assert.DoesNotContain(campoSoftware.Uid.ToString(), testo);
            Assert.DoesNotContain(campoVersione.Uid.ToString(), testo);
        }
        finally { if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } } }
    }

    /// <summary>Una chiave che non corrisponde piu' a nessun campo (campo rimosso dal template, o template non disponibile)
    /// resta visibile con la nota, invece di un GUID senza significato.</summary>
    [Fact]
    public void PacchettoCr_ChiaveNonPiuConfigurataMostraLaChiaveConLaNota()
    {
        using var state = TestStateFactory.Create();
        var chiaveOrfana = Guid.NewGuid().ToString();
        var cr = new ChangeRequest { Id = "CR-2026-0002", TemplateUid = Guid.NewGuid() };
        cr.CampiCustom[chiaveOrfana] = "valore residuo";

        var testo = TicketExportService.GeneraTesto(cr, state, new HashSet<string> { CrSezioni.CampiCr });

        Assert.Contains($"{chiaveOrfana} (campo non più configurato): valore residuo", testo);
    }

    /// <summary>Il catalogo dei campi di sistema fornisce l'etichetta quando la chiave non e' un campo libero del template.</summary>
    [Fact]
    public void EtichettaChiaveCampoCr_UsaIlCatalogoDeiCampiDiSistema()
    {
        var catalogo = new List<CampoSistemaDefinizione> { new() { Chiave = "scadenza-cr", Etichetta = "Scadenza" } };

        Assert.Equal("Scadenza", TicketExportService.EtichettaChiaveCampoCr("scadenza-cr", new Dictionary<string, string>(), catalogo));
    }

    /// <summary>Il pacchetto dei Test non scrive mai l'Uid di un campo libero senza nome: la nota prende il posto del GUID.</summary>
    [Fact]
    public void PacchettoTest_CampoLiberoSenzaNomeNonMostraIlGuid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_PacchettoTestSenzaNome_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var state = TestStateFactory.Create();
            Directory.CreateDirectory(dir);
            state.InizializzaCartella(dir);
            var campo = new CampoTemplate { Tipo = TipoCampoTemplate.CampoLibero, Nome = "" };
            var sezione = new SezioneTemplate { Nome = "Prove ambientali", Campi = new() { campo } };
            var template = new TestTemplate { Nome = "Template Prova", Sezioni = new() { sezione } };
            state.Master.TestTemplate.Save(new List<TestTemplate> { template });

            var tc = new TestCase { Id = "TC-2", Componente = "BCM", TestTemplateUid = template.Uid };
            tc.CampiCustom[campo.Uid.ToString()] = "SN-9";

            var testo = TicketExportService.GeneraTesto(tc, state, new HashSet<string> { PolarionSezioni.ChiaveSezioneDinamica(sezione) });

            Assert.Contains("(campo senza nome): SN-9", testo);
            Assert.DoesNotContain(campo.Uid.ToString(), testo);
        }
        finally { if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } } }
    }

    /// <summary>La Segnalazione scrive "Strumento - Campo", come il pacchetto dei Test: la chiave tecnica "Strumento::Campo"
    /// non compare mai nel testo.</summary>
    [Fact]
    public void PacchettoSegnalazione_ValoreStrumentoSenzaChiaveTecnica()
    {
        using var state = TestStateFactory.Create();
        var nc = new NonConformita { Id = "NC-2026-0001" };
        nc.ValoriStrumentoSegnalato["Asist::Licenza"] = "LIC-1";

        var testo = TicketExportService.GeneraTesto(nc, state, new HashSet<string> { NcSezioni.CampiStrumento });

        Assert.Contains("Asist - Licenza: LIC-1", testo);
        Assert.DoesNotContain("Asist::Licenza", testo);
    }

    /// <summary>Copre il feedback utente 2026-08-21 punto 10: ogni sezione custom del template deve comparire
    /// come voce a sé nel pacchetto Ticketing, con etichetta leggibile (non l'Uid del campo) e con contenuto
    /// filtrato dalla selezione — non più un unico bucket generico "Campi custom".</summary>
    [Fact]
    public void GeneraTesto_SezioneCustomDelTemplateCompareConEtichettaLeggibileSoloSeSelezionata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ExportSezioniCustom_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var state = TestStateFactory.Create();
            Directory.CreateDirectory(dir); // Il bootstrap non ricrea una cartella condivisa assente.
            state.InizializzaCartella(dir);
            var campo = new CampoTemplate { Tipo = TipoCampoTemplate.CampoLibero, Nome = "Numero seriale banco" };
            var sezione = new SezioneTemplate { Nome = "Prove ambientali", Campi = new() { campo } };
            var template = new TestTemplate { Nome = "Template Prova", Sezioni = new() { sezione } };
            state.Master.TestTemplate.Save(new List<TestTemplate> { template });

            var tc = new TestCase { Id = "TC-1", Componente = "BCM", TestTemplateUid = template.Uid };
            tc.CampiCustom[campo.Uid.ToString()] = "SN-4471";

            var chiaveSezione = PolarionSezioni.ChiaveSezioneDinamica(sezione);
            var testoConSezione = TicketExportService.GeneraTesto(tc, state, new HashSet<string> { chiaveSezione });
            var testoSenzaSezione = TicketExportService.GeneraTesto(tc, state, new HashSet<string>());

            Assert.Contains("Prove ambientali", testoConSezione);
            Assert.Contains("Numero seriale banco", testoConSezione);
            Assert.Contains("SN-4471", testoConSezione);
            Assert.DoesNotContain(campo.Uid.ToString(), testoConSezione);
            Assert.DoesNotContain("SN-4471", testoSenzaSezione);
        }
        finally { if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } } }
    }
}
