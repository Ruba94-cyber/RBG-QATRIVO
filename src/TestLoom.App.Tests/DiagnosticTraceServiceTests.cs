using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>DiagnosticTraceService (feedback utente 2026-09-17: "non solo per crash proprio per bug in
/// generale") — traccia circolare delle operazioni applicative principali, non solo delle eccezioni.</summary>
[Collection("TracciaStatica")]
public class DiagnosticTraceServiceTests : IDisposable
{
    // Ogni test scrive in un trace.log proprio in una cartella temporanea (vedi DiagnosticTraceService.PercorsoPerTest):
    // il trace.log reale in %APPDATA% e' condiviso con altri processi di test e con l'app.
    private readonly string _cartellaTraccia = Path.Combine(Path.GetTempPath(), "TestLoom_DiagTrace_" + Guid.NewGuid().ToString("N"));

    public DiagnosticTraceServiceTests()
    {
        Directory.CreateDirectory(_cartellaTraccia);
        DiagnosticTraceService.PercorsoPerTest = Path.Combine(_cartellaTraccia, "trace.log");
    }

    public void Dispose()
    {
        DiagnosticTraceService.PercorsoPerTest = null;
        try { Directory.Delete(_cartellaTraccia, recursive: true); } catch { }
    }

    [Fact]
    public void Registra_ScriveLaVoceNelFileESiTrovaTraIFilePresenti()
    {
        var marcatore = "MARCATORE_TEST_" + Guid.NewGuid().ToString("N");
        DiagnosticTraceService.Registra("TestCategoria", marcatore);

        // File di log globale e condiviso tra run (stesso schema di CrashLogService): non assumere che sia
        // l'unico presente, solo che la voce appena scritta si trovi da qualche parte.
        var file = Assert.Single(DiagnosticTraceService.FilePresenti(), f => File.ReadAllText(f).Contains(marcatore));
        var testo = File.ReadAllText(file);
        Assert.Contains(marcatore, testo);
        Assert.Contains("[TestCategoria]", testo);
    }

    [Fact]
    public void Registra_NonLancia_AncheConCategoriaOMessaggioVuoti()
    {
        DiagnosticTraceService.Registra("", "");
        DiagnosticTraceService.Registra("X", null!);
    }

    /// <summary>Copertura del bug segnalato 2026-09-18: "il logging deve comprendere veramente tutte le
    /// operazioni... non si vede quando archivio ecc". Prima di questa correzione ChangeRequestRepository e
    /// SegnalazioneRepository non chiamavano MAI Registra: un'archiviazione CR era invisibile nella traccia
    /// diagnostica. Verifica ogni categoria una per una sulle entita' reali, non solo che il servizio non
    /// lanci.</summary>
    [Collection("TracciaStatica")]
    public sealed class OperazioniDiDominioTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_DiagTrace_Dominio_" + Guid.NewGuid().ToString("N"));
        private readonly string _cartellaTraccia = Path.Combine(Path.GetTempPath(), "TestLoom_DiagTrace_Traccia_" + Guid.NewGuid().ToString("N"));

        public OperazioniDiDominioTests()
        {
            Directory.CreateDirectory(_cartellaTraccia);
            DiagnosticTraceService.PercorsoPerTest = Path.Combine(_cartellaTraccia, "trace.log");
        }

        public void Dispose()
        {
            DiagnosticTraceService.PercorsoPerTest = null;
            try { Directory.Delete(_dir, recursive: true); } catch { }
            try { Directory.Delete(_cartellaTraccia, recursive: true); } catch { }
        }

        private static bool TracciaContiene(string categoria, string testo)
        {
            var atteso = $"[{categoria}] {testo}";
            return DiagnosticTraceService.FilePresenti().Any(f => File.ReadAllText(f).Contains(atteso));
        }

        [Fact]
        public void ChangeRequest_SaveArchiviaRipristinaEliminaDefinitivamente_RegistranoOgnunoLaPropriaCategoria()
        {
            var state = TestStateFactory.Create();
            Directory.CreateDirectory(_dir);
            state.InizializzaCartella(_dir);
            var marcatore = "CR-TRACE-" + Guid.NewGuid().ToString("N")[..8];
            var cr = new ChangeRequest { Id = marcatore, Titolo = "t", CreatoDa = "Operatore Prova A" };

            state.ChangeRequests.Save(cr, null);
            Assert.True(TracciaContiene("ChangeRequest.Save", $"{marcatore} revisione=1"));

            state.ChangeRequests.Archivia(cr, "Operatore Prova A");
            Assert.True(TracciaContiene("ChangeRequest.Archivia", $"{marcatore} autore=Operatore Prova A"));

            state.ChangeRequests.Ripristina(cr, "Operatore Prova A");
            Assert.True(TracciaContiene("ChangeRequest.Ripristina", $"{marcatore} autore=Operatore Prova A"));

            state.ChangeRequests.EliminaDefinitivamente(cr);
            Assert.True(TracciaContiene("ChangeRequest.EliminaDefinitivamente", marcatore));
        }

        [Fact]
        public void Segnalazione_Save_RegistraLaPropriaCategoria()
        {
            var state = TestStateFactory.Create();
            Directory.CreateDirectory(_dir);
            state.InizializzaCartella(_dir);
            var item = new SegnalazioneDaClassificare();

            state.Segnalazioni.Save(item);
            Assert.True(TracciaContiene("Segnalazione.Save", $"{item.Uid:N} revisione=1"));
        }

        [Fact]
        public void Operatore_AccessoELogout_RegistranoOgnunoLaPropriaCategoria()
        {
            var state = TestStateFactory.Create();
            Directory.CreateDirectory(_dir);
            state.InizializzaCartella(_dir);
            var nome = "Operatore Trace " + Guid.NewGuid().ToString("N")[..8];
            state.Master.Operatori.Save(new List<Operatore> { TestOperatori.Sigilla(new() { Nome = nome, Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("1234") }) });

            Assert.True(state.ImpostaOperatore(nome, "1234"));
            Assert.True(TracciaContiene("Operatore.Accesso", nome));

            state.Logout();
            Assert.True(TracciaContiene("Operatore.Logout", nome));
        }
    }
}
