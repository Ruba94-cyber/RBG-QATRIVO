using System.Linq;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class CampoSistemaDefinizioneSeedTests
{
    [Fact]
    public void SeedPianificazione_UnaVocePerOgniChiaveEsistente()
    {
        var voci = CampoSistemaDefinizioneSeed.SeedPianificazione();
        Assert.Equal(CampiSistema.ChiaviPianificazione.Count, voci.Count);
        Assert.All(voci, v => Assert.Equal(GruppoCampoSistema.Pianificazione, v.Gruppo));
    }

    [Fact]
    public void SeedDettagli_UnaVocePerOgniChiaveEsistentePiuGammaECategoria()
    {
        var voci = CampoSistemaDefinizioneSeed.SeedDettagli();
        Assert.Equal(CampiSistema.Chiavi.Count + 2, voci.Count); // +2: Gamma, Categoria
        Assert.All(voci, v => Assert.Equal(GruppoCampoSistema.Dettagli, v.Gruppo));
    }

    [Fact]
    public void SeedDettagli_EcuHaRuoloEcu()
    {
        var voce = CampoSistemaDefinizioneSeed.SeedDettagli().Single(v => v.Chiave == CampiSistema.Componente);
        Assert.Equal(RuoloCampoSistema.Componente, voce.Ruolo);
    }

    [Fact]
    public void SeedPianificazione_ScadenzaHaTipoData()
    {
        var voce = CampoSistemaDefinizioneSeed.SeedPianificazione().Single(v => v.Chiave == CampiSistema.Scadenza);
        Assert.Equal(TipoCampoStrumento.Data, voce.Tipo);
    }

    [Fact]
    public void SeedDettagli_DescrizioneBreve_RuoloNessuno()
    {
        var voce = CampoSistemaDefinizioneSeed.SeedDettagli().Single(v => v.Chiave == CampiSistema.DescrizioneBreve);
        Assert.Equal(RuoloCampoSistema.Nessuno, voce.Ruolo);
    }

    [Fact]
    public void SeedDettagli_ContieneGammaECategoriaConIRuoliCorretti()
    {
        var voci = CampoSistemaDefinizioneSeed.SeedDettagli();
        Assert.Contains(voci, v => v.Chiave == "Gamma" && v.Ruolo == RuoloCampoSistema.Gamma);
        Assert.Contains(voci, v => v.Chiave == "Categoria" && v.Ruolo == RuoloCampoSistema.Categoria);
    }

    [Fact]
    public void SeedDettagli_NessunaChiaveDuplicata()
    {
        var chiavi = CampoSistemaDefinizioneSeed.SeedDettagli().Select(v => v.Chiave).ToList();
        Assert.Equal(chiavi.Distinct().Count(), chiavi.Count);
    }

    [Fact]
    public void AppState_PrimaAperturaCartellaSeedaEntrambiICataloghiCampiSistema()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_campisistema_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var state = TestStateFactory.Create();
            state.InizializzaCartella(root);

            Assert.Equal(CampiSistema.ChiaviPianificazione.Count, state.Master.CampiSistemaPianificazione.Load().Count);
            Assert.Equal(CampiSistema.Chiavi.Count + 2, state.Master.CampiSistemaDettagli.Load().Count); // +2: Gamma, Categoria
            Assert.Equal(CampiSistema.Chiavi.Count + CampiSistema.ChiaviPianificazione.Count + 2, state.CatalogoCampiSistemaCompleto().Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
