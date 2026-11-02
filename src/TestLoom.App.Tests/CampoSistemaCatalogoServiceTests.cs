using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class CampoSistemaCatalogoServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_campocatalogo_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void CampiLiberiTuttiIGruppi_UniscePianificazioneEDettagli()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        state.Master.CampiSistemaPianificazione.Save(new List<CampoSistemaDefinizione> { new() { Chiave = "custom-p", Etichetta = "Custom P", Ruolo = RuoloCampoSistema.Nessuno } });
        state.Master.CampiSistemaDettagli.Save(new List<CampoSistemaDefinizione> { new() { Chiave = "custom-d", Etichetta = "Custom D", Ruolo = RuoloCampoSistema.Nessuno } });

        var risultato = CampoSistemaCatalogoService.CampiLiberiTuttiIGruppi(state);

        Assert.Contains(risultato, c => c.Chiave == "custom-p");
        Assert.Contains(risultato, c => c.Chiave == "custom-d");
    }

    [Fact]
    public void CampiUtilizzabiliComeFiltro_EscludeICampiNonMarcatiUsabiliComeFiltro()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        // "custom-non-filtrabile" invece di "descrizione": il seed di sistema crea gia' un campo "Descrizione"
        // e le chiavi business sono confrontate case-insensitive, quindi "descrizione" collide con quello.
        state.Master.CampiSistemaDettagli.Save(new List<CampoSistemaDefinizione>
        {
            new() { Chiave = "custom-non-filtrabile", Etichetta = "Custom non filtrabile", Ruolo = RuoloCampoSistema.Nessuno, UsabileComeFiltro = false },
            new() { Chiave = "colore", Etichetta = "Colore", Ruolo = RuoloCampoSistema.Nessuno, UsabileComeFiltro = true },
        });

        var risultato = CampoSistemaCatalogoService.CampiUtilizzabiliComeFiltro(state);

        Assert.DoesNotContain(risultato, c => c.Chiave == "custom-non-filtrabile");
        Assert.Contains(risultato, c => c.Chiave == "colore");
    }

    [Fact]
    public void CampiUtilizzabiliComeFiltro_DefaultDelCampoENonUsabileComeFiltro()
    {
        var nuovo = new CampoSistemaDefinizione();
        Assert.False(nuovo.UsabileComeFiltro);
    }

    [Fact]
    public void GeneraChiaveDaEtichetta_Slugifica_MinuscoloSenzaAccentiConTrattini()
    {
        var chiave = CampoSistemaCatalogoService.GeneraChiaveDaEtichetta("Colore Città", new List<string>());
        Assert.Equal("colore-citta", chiave);
    }

    [Fact]
    public void GeneraChiaveDaEtichetta_CollisioneAggiungeSuffissoNumerico()
    {
        var chiave = CampoSistemaCatalogoService.GeneraChiaveDaEtichetta("Colore", new List<string> { "colore", "colore-2" });
        Assert.Equal("colore-3", chiave);
    }

    [Fact]
    public void GeneraChiaveDaEtichetta_EtichettaSenzaCaratteriValidi_UsaFallback()
    {
        var chiave = CampoSistemaCatalogoService.GeneraChiaveDaEtichetta("!!!", new List<string>());
        Assert.Equal("campo", chiave);
    }

    [Fact]
    public void ValoriDistinti_IgnoraValoriVuotiENonPresenti()
    {
        var tc1 = new TestCase { Id = "A", CampiCustom = { ["colore"] = "Rosso" } };
        var tc2 = new TestCase { Id = "B", CampiCustom = { ["colore"] = "Blu" } };
        var tc3 = new TestCase { Id = "C", CampiCustom = { ["colore"] = "Rosso" } };
        var tc4 = new TestCase { Id = "D", CampiCustom = { ["colore"] = "" } };
        var tc5 = new TestCase { Id = "E" };

        var valori = CampoSistemaCatalogoService.ValoriDistinti(new[] { tc1, tc2, tc3, tc4, tc5 }, "colore");

        Assert.Equal(new[] { "Blu", "Rosso" }, valori);
    }

    [Fact]
    public void OpzioniFiltro_CampoListaOffreLeOpzioniDelCatalogoEPoiIValoriPresenti()
    {
        var opzioni = new List<string> { "USB", "Bluetooth", "Wi-Fi" };

        // "usb" nei Test e' la stessa voce di "USB" nel catalogo: mostrata una volta, con la grafia del catalogo.
        var offerte = CampoSistemaCatalogoService.OpzioniFiltro(TipoCampoStrumento.Lista, opzioni, new[] { "usb", "Zigbee" });
        Assert.Equal(new[] { "USB", "Bluetooth", "Wi-Fi", "Zigbee" }, offerte);

        // Nessun Test usa ancora le opzioni: restano comunque offerte.
        Assert.Equal(new[] { "USB", "Bluetooth", "Wi-Fi" }, CampoSistemaCatalogoService.OpzioniFiltro(TipoCampoStrumento.Lista, opzioni, Array.Empty<string>()));

        // Per gli altri tipi le opzioni del catalogo non entrano: solo i valori presenti.
        Assert.Equal(new[] { "Zigbee" }, CampoSistemaCatalogoService.OpzioniFiltro(TipoCampoStrumento.Testo, opzioni, new[] { "Zigbee" }));
    }
}
