using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Feedback utente 2026-09-22: "qualsiasi NC che è stata creata tramite TestLoom e non legata a test non
/// si vedrebbe se filtrata... componente e veicolo se presenti devo poterle filtrare". I filtri componente/Veicolo/Gamma
/// della pagina Anomalie leggevano l'ambito esclusivamente dal Test collegato e scartavano ogni NC con
/// tc == null: una segnalazione di rete, che il proprio ambito lo porta su EcuSegnalate/VeicoliSegnalati,
/// spariva dall'elenco appena si selezionava una centralina — pur essendo l'unica pagina da cui la si può
/// cercare. Qui l'ambito proprio della NC concorre al filtro esattamente come quello del Test.</summary>
public sealed class NonConformitaFiltroAmbitoSenzaTestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NcAmbito_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStatoConCataloghi()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Gamma.Save(new List<Gamma> { new() { Nome = "Light" }, new() { Nome = "Heavy" } });
        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = "Daily MY2019", GammaNome = "Light" },
            new() { Nome = "Stralis MY2019", GammaNome = "Heavy" },
        });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "VMU" } });
        return state;
    }

    private static NonConformita SegnalazioneRete(string id, string componente, string veicolo) => new()
    {
        Id = id,
        TestCaseId = "",
        DaSegnalazioneRete = true,
        DataApertura = DateTime.Now,
        Severita = SeveritaAnomalia.Medium,
        EcuSegnalate = new EcuSelection(new[] { componente }),
        VeicoliSegnalati = new List<string> { veicolo },
        DescrizioneBreveSegnalata = "Sintesi",
        Descrizione = "Dettaglio",
    };

    private static void Seleziona(FiltroMultiSelect filtro, string valore)
    {
        var opzione = filtro.Opzioni.FirstOrDefault(o => o.Valore == valore);
        Assert.True(opzione != null, $"La tendina deve offrire '{valore}': trovati [{string.Join(", ", filtro.Opzioni.Select(o => o.Valore))}].");
        opzione!.Selezionato = true;
    }

    [Fact]
    public void FiltroEcu_TrovaLaSegnalazioneDiReteSenzaTestCollegato()
    {
        var state = CreaStatoConCataloghi();
        state.NonConformita.Save(SegnalazioneRete("NC-0001", "BCM", "Daily MY2019"));

        var vm = new NonConformitaViewModel(state, _ => { });
        Seleziona(vm.FiltroEcu, "BCM");

        var riga = Assert.Single(vm.Righe);
        Assert.Equal("NC-0001", riga.Nc.Id);
    }

    [Fact]
    public void FiltroVeicolo_TrovaLaSegnalazioneDiReteSenzaTestCollegato()
    {
        var state = CreaStatoConCataloghi();
        state.NonConformita.Save(SegnalazioneRete("NC-0001", "BCM", "Daily MY2019"));

        var vm = new NonConformitaViewModel(state, _ => { });
        Seleziona(vm.FiltroVeicolo, "Daily MY2019");

        var riga = Assert.Single(vm.Righe);
        Assert.Equal("NC-0001", riga.Nc.Id);
    }

    /// <summary>La Gamma non è un campo della NC: si risolve attraverso il veicolo, come già avviene per i
    /// Test — quindi deve risolversi anche attraverso il veicolo segnalato.</summary>
    [Fact]
    public void FiltroGamma_RisolveTramiteIlVeicoloSegnalato()
    {
        var state = CreaStatoConCataloghi();
        state.NonConformita.Save(SegnalazioneRete("NC-0001", "BCM", "Daily MY2019"));

        var vm = new NonConformitaViewModel(state, _ => { });
        Seleziona(vm.FiltroGamma, "Light");

        var riga = Assert.Single(vm.Righe);
        Assert.Equal("NC-0001", riga.Nc.Id);
    }

    /// <summary>Il filtro deve restare un filtro: allargarlo all'ambito proprio della NC non deve trasformarsi
    /// in "mostra tutto quando non c'è un Test".</summary>
    [Fact]
    public void FiltroEcu_NonRestituisceLaSegnalazioneConUnAltraEcu()
    {
        var state = CreaStatoConCataloghi();
        state.NonConformita.Save(SegnalazioneRete("NC-0001", "BCM", "Daily MY2019"));

        var vm = new NonConformitaViewModel(state, _ => { });
        Seleziona(vm.FiltroEcu, "VMU");

        Assert.Empty(vm.Righe);
    }

    /// <summary>Una NC senza test e senza ambito proprio non ha nulla da confrontare: deve continuare a essere
    /// esclusa da un filtro per componente, non a comparire ovunque.</summary>
    [Fact]
    public void FiltroEcu_EscludeLaNcSenzaTestESenzaAmbitoProprio()
    {
        var state = CreaStatoConCataloghi();
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-0002", TestCaseId = "", DataApertura = DateTime.Now, Severita = SeveritaAnomalia.Medium,
        });

        var vm = new NonConformitaViewModel(state, _ => { });
        Seleziona(vm.FiltroEcu, "BCM");

        Assert.Empty(vm.Righe);
    }

    /// <summary>Feedback utente 2026-09-22: "cercare anche per quello se dovesse servire". componente e veicolo
    /// restano nei loro campi strutturati e non vengono duplicati nel testo della descrizione, quindi è la
    /// ricerca libera a doverli guardare.</summary>
    [Fact]
    public void RicercaLibera_TrovaLaSegnalazionePerNomeEcu()
    {
        var state = CreaStatoConCataloghi();
        state.NonConformita.Save(SegnalazioneRete("NC-0001", "BCM", "Daily MY2019"));
        state.NonConformita.Save(SegnalazioneRete("NC-0002", "VMU", "Stralis MY2019"));

        var vm = new NonConformitaViewModel(state, _ => { }) { FiltroTesto = "BCM" };

        var riga = Assert.Single(vm.Righe);
        Assert.Equal("NC-0001", riga.Nc.Id);
    }

    [Fact]
    public void RicercaLibera_TrovaLaSegnalazionePerNomeVeicolo()
    {
        var state = CreaStatoConCataloghi();
        state.NonConformita.Save(SegnalazioneRete("NC-0001", "BCM", "Daily MY2019"));
        state.NonConformita.Save(SegnalazioneRete("NC-0002", "VMU", "Stralis MY2019"));

        var vm = new NonConformitaViewModel(state, _ => { }) { FiltroTesto = "Stralis" };

        var riga = Assert.Single(vm.Righe);
        Assert.Equal("NC-0002", riga.Nc.Id);
    }

    /// <summary>La sintesi del problema è il campo su cui l'utente cerca per primo e prima non era guardata
    /// da nessuna delle due varianti della ricerca.</summary>
    [Fact]
    public void RicercaLibera_TrovaLaSegnalazionePerSintesiDelProblema()
    {
        var state = CreaStatoConCataloghi();
        var nc = SegnalazioneRete("NC-0001", "BCM", "Daily MY2019");
        nc.DescrizioneBreveSegnalata = "Rear camera non disponibile";
        nc.Descrizione = "Dettaglio non correlato";
        state.NonConformita.Save(nc);

        var vm = new NonConformitaViewModel(state, _ => { }) { FiltroTesto = "rear camera" };

        var riga = Assert.Single(vm.Righe);
        Assert.Equal("NC-0001", riga.Nc.Id);
    }

    /// <summary>La ricerca libera deve restare coerente anche quando è attivo un filtro d'ambito: i due
    /// percorsi di Ricarica() sono distinti e prima duplicavano l'elenco dei campi cercati.</summary>
    [Fact]
    public void RicercaLibera_RestaCoerenteConUnFiltroAmbitoAttivo()
    {
        var state = CreaStatoConCataloghi();
        state.NonConformita.Save(SegnalazioneRete("NC-0001", "BCM", "Daily MY2019"));
        state.NonConformita.Save(SegnalazioneRete("NC-0002", "BCM", "Stralis MY2019"));

        var vm = new NonConformitaViewModel(state, _ => { }) { FiltroTesto = "Stralis" };
        Seleziona(vm.FiltroEcu, "BCM");

        var riga = Assert.Single(vm.Righe);
        Assert.Equal("NC-0002", riga.Nc.Id);
    }

    /// <summary>Domanda utente 2026-09-22: "pero' cerca tra tutte le Nc non solo quelle della pagina giusto?".
    /// Filtro e ricerca devono applicarsi all'intero archivio e la paginazione deve restare l'ultimo passo:
    /// qui la NC cercata sta deliberatamente oltre la prima pagina per ordinamento (e' la piu' vecchia di 120,
    /// con pagina da 250), quindi un filtro applicato dopo il taglio della pagina non la troverebbe.</summary>
    [Fact]
    [Trait("Category", "Lento")]
    public void RicercaLibera_CercaSuTuttoLArchivioNonSoloSullaPaginaCorrente()
    {
        var state = CreaStatoConCataloghi();
        for (var i = 0; i < 300; i++)
        {
            var rumore = SegnalazioneRete($"NC-{i:D4}", "VMU", "Stralis MY2019");
            rumore.DataApertura = DateTime.Now.AddDays(-i);
            state.NonConformita.Save(rumore);
        }
        var cercata = SegnalazioneRete("NC-9999", "BCM", "Daily MY2019");
        cercata.DescrizioneBreveSegnalata = "Ago nel pagliaio";
        cercata.DataApertura = DateTime.Now.AddDays(-500);
        state.NonConformita.Save(cercata);

        var vm = new NonConformitaViewModel(state, _ => { });
        Assert.Equal(301L, vm.TotaleRighe);
        Assert.True(vm.Righe.Count < 301, "La pagina deve contenere meno righe dell'archivio, altrimenti il test non prova nulla.");
        Assert.DoesNotContain(vm.Righe, r => r.Nc.Id == "NC-9999");

        vm.FiltroTesto = "Ago nel pagliaio";

        var riga = Assert.Single(vm.Righe);
        Assert.Equal("NC-9999", riga.Nc.Id);
        Assert.Equal(1L, vm.TotaleRighe);
    }

    /// <summary>Stessa garanzia sul percorso con filtro d'ambito attivo, che in Ricarica() e' un ramo di
    /// codice distinto da quello della sola ricerca libera.</summary>
    [Fact]
    [Trait("Category", "Lento")]
    public void FiltroEcu_CercaSuTuttoLArchivioNonSoloSullaPaginaCorrente()
    {
        var state = CreaStatoConCataloghi();
        for (var i = 0; i < 300; i++)
        {
            var rumore = SegnalazioneRete($"NC-{i:D4}", "VMU", "Stralis MY2019");
            rumore.DataApertura = DateTime.Now.AddDays(-i);
            state.NonConformita.Save(rumore);
        }
        var cercata = SegnalazioneRete("NC-9999", "BCM", "Daily MY2019");
        cercata.DataApertura = DateTime.Now.AddDays(-500);
        state.NonConformita.Save(cercata);

        var vm = new NonConformitaViewModel(state, _ => { });
        Seleziona(vm.FiltroEcu, "BCM");

        var riga = Assert.Single(vm.Righe);
        Assert.Equal("NC-9999", riga.Nc.Id);
        Assert.Equal(1L, vm.TotaleRighe);
    }

    /// <summary>Regressione: per una NC nata da un Test l'ambito resta quello del Test.</summary>
    [Fact]
    public void FiltroEcu_ContinuaAFiltrareTramiteIlTestCollegato()
    {
        var state = CreaStatoConCataloghi();
        var test = new TestCase
        {
            Id = "TC-1", Componente = "BCM", Veicoli = new List<string> { "Daily MY2019" },
            DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
        };
        state.TestCases.Save(test, isNew: true);
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-0003", TestCaseId = "TC-1", TestUid = test.Uid, DataApertura = DateTime.Now, Severita = SeveritaAnomalia.Medium,
        });

        var vm = new NonConformitaViewModel(state, _ => { });
        Seleziona(vm.FiltroEcu, "BCM");

        var riga = Assert.Single(vm.Righe);
        Assert.Equal("NC-0003", riga.Nc.Id);
    }

    /// <summary>Il caso che giustifica l'unione invece di un if/else come in AmbitoDi(): una segnalazione di
    /// rete collegata a un Test in un secondo momento conserva l'ambito che la rete aveva segnalato, che puo'
    /// divergere da quello effettivamente provato. Deve essere raggiungibile da entrambi. Senza questo test
    /// una futura "semplificazione" a if/else passerebbe l'intera suite.</summary>
    [Fact]
    public void FiltroEcu_TrovaSiaLAmbitoDelTestSiaQuelloSegnalatoQuandoDivergono()
    {
        var state = CreaStatoConCataloghi();
        var testVmu = new TestCase
        {
            Id = "TC-1", Componente = "VMU", Veicoli = new List<string> { "Stralis MY2019" },
            DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
        };
        state.TestCases.Save(testVmu, isNew: true);
        var nc = SegnalazioneRete("NC-0001", "BCM", "Daily MY2019");
        nc.TestCaseId = "TC-1";
        nc.TestUid = testVmu.Uid;
        state.NonConformita.Save(nc);

        var perSegnalata = new NonConformitaViewModel(state, _ => { });
        Seleziona(perSegnalata.FiltroEcu, "BCM");
        Assert.Equal("NC-0001", Assert.Single(perSegnalata.Righe).Nc.Id);

        var perProvata = new NonConformitaViewModel(state, _ => { });
        Seleziona(perProvata.FiltroEcu, "VMU");
        Assert.Equal("NC-0001", Assert.Single(perProvata.Righe).Nc.Id);
    }

    /// <summary>Le tendine dei filtri offrono il nome di catalogo: l'ambito scritto sulla NC puo' avere una
    /// grafia diversa e deve corrispondere lo stesso.</summary>
    [Fact]
    public void FiltroEcu_ConfrontaSenzaDistinguereMaiuscoleEMinuscole()
    {
        var state = CreaStatoConCataloghi();
        state.NonConformita.Save(SegnalazioneRete("NC-0001", "bcm", "daily my2019"));

        var vm = new NonConformitaViewModel(state, _ => { });
        Seleziona(vm.FiltroEcu, "BCM");

        Assert.Equal("NC-0001", Assert.Single(vm.Righe).Nc.Id);
    }

    /// <summary>Due filtri d'ambito attivi insieme si combinano per dimensione, non per coppia: la NC passa
    /// se soddisfa il componente da una fonte e il veicolo dall'altra. E' la stessa semantica che vale da sempre per
    /// un Test multi-componente/multi-veicolo; qui la si fissa perche' ora le fonti sono due.</summary>
    [Fact]
    public void FiltroEcuEVeicoloInsieme_SiCombinanoPerDimensioneNonPerCoppia()
    {
        var state = CreaStatoConCataloghi();
        var testVmu = new TestCase
        {
            Id = "TC-1", Componente = "VMU", Veicoli = new List<string> { "Stralis MY2019" },
            DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
        };
        state.TestCases.Save(testVmu, isNew: true);
        var nc = SegnalazioneRete("NC-0001", "BCM", "Daily MY2019");
        nc.TestCaseId = "TC-1";
        nc.TestUid = testVmu.Uid;
        state.NonConformita.Save(nc);

        var vm = new NonConformitaViewModel(state, _ => { });
        Seleziona(vm.FiltroEcu, "VMU");          // soddisfatto dal Test
        Seleziona(vm.FiltroVeicolo, "Daily MY2019"); // soddisfatto dalla segnalazione

        Assert.Equal("NC-0001", Assert.Single(vm.Righe).Nc.Id);
    }

    /// <summary>Piu' valori selezionati dentro lo stesso filtro sono in OR fra loro.</summary>
    [Fact]
    public void FiltroEcu_ConPiuValoriSelezionati_RestituisceEntrambe()
    {
        var state = CreaStatoConCataloghi();
        state.NonConformita.Save(SegnalazioneRete("NC-0001", "BCM", "Daily MY2019"));
        state.NonConformita.Save(SegnalazioneRete("NC-0002", "VMU", "Stralis MY2019"));

        var vm = new NonConformitaViewModel(state, _ => { });
        Seleziona(vm.FiltroEcu, "BCM");
        Seleziona(vm.FiltroEcu, "VMU");

        Assert.Equal(2, vm.Righe.Count);
    }

    /// <summary>Fissa l'asimmetria dichiarata nel codice: la ricerca libera guarda l'ambito PROPRIO della NC
    /// ma non quello del Test collegato, perche' risolverlo costringerebbe a caricare i Test di tutte le NC
    /// candidate invece delle sole righe di pagina. Per quelle NC il componente resta raggiungibile dal filtro a
    /// tendina. Se un domani si decidesse diversamente, questo test va aggiornato consapevolmente.</summary>
    [Fact]
    public void RicercaLibera_NonGuardaLAmbitoDelTestCollegato()
    {
        var state = CreaStatoConCataloghi();
        var testBcm = new TestCase
        {
            Id = "TC-1", Componente = "BCM", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
        };
        state.TestCases.Save(testBcm, isNew: true);
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-0003", TestCaseId = "TC-1", TestUid = testBcm.Uid, DataApertura = DateTime.Now, Severita = SeveritaAnomalia.Medium,
            Descrizione = "Nessun riferimento alla centralina nel testo",
        });

        var vm = new NonConformitaViewModel(state, _ => { }) { FiltroTesto = "BCM" };

        Assert.Empty(vm.Righe);
    }

    /// <summary>Una NC che punta a un Test inesistente (cancellato) non deve sparire dal filtro: l'ambito
    /// proprio resta l'unica fonte disponibile e va usato.</summary>
    [Fact]
    public void FiltroEcu_ConTestCollegatoInesistente_RipiegaSullAmbitoProprio()
    {
        var state = CreaStatoConCataloghi();
        var nc = SegnalazioneRete("NC-0001", "BCM", "Daily MY2019");
        nc.TestCaseId = "TC-MAI-ESISTITO";
        state.NonConformita.Save(nc);

        var vm = new NonConformitaViewModel(state, _ => { });
        Seleziona(vm.FiltroEcu, "BCM");

        Assert.Equal("NC-0001", Assert.Single(vm.Righe).Nc.Id);
    }

    /// <summary>Con un filtro d'ambito attivo la paginazione deve restare coerente: conteggio totale sul
    /// filtrato, pagine calcolate su quello, e la seconda pagina effettivamente popolata.</summary>
    [Fact]
    [Trait("Category", "Lento")]
    public void FiltroEcu_ImpaginaSulRisultatoFiltratoNonSullArchivio()
    {
        var state = CreaStatoConCataloghi();
        for (var i = 0; i < 300; i++)
            state.NonConformita.Save(SegnalazioneRete($"NC-A{i:D4}", "BCM", "Daily MY2019"));
        for (var i = 0; i < 100; i++)
            state.NonConformita.Save(SegnalazioneRete($"NC-B{i:D4}", "VMU", "Stralis MY2019"));

        var vm = new NonConformitaViewModel(state, _ => { });
        Seleziona(vm.FiltroEcu, "BCM");

        Assert.Equal(300L, vm.TotaleRighe);
        Assert.Equal(2L, vm.PagineTotaliRighe);
        Assert.Equal(250, vm.Righe.Count);

        vm.PaginaRigheSuccessivaCommand.Execute(null);
        Assert.Equal(2L, vm.PaginaRighe);
        Assert.Equal(50, vm.Righe.Count);
        Assert.All(vm.Righe, r => Assert.StartsWith("NC-A", r.Nc.Id));
    }

}
