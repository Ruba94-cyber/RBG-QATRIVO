using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

public class CampoSistemaDefinizioneTests
{
    [Fact]
    public void Default_RuoloNessuno_TipoTesto_GruppoDettagli()
    {
        var c = new CampoSistemaDefinizione();
        Assert.Equal(RuoloCampoSistema.Nessuno, c.Ruolo);
        Assert.Equal(TipoCampoStrumento.Testo, c.Tipo);
        Assert.Equal(GruppoCampoSistema.Dettagli, c.Gruppo);
        Assert.False(c.Archiviata);
    }

    /// <summary>Il payload del journal Phase D è JsonSerializer.Serialize(item, JsonIo.CompactOptions) e torna con
    /// JsonIo.Options: un campo Lista deve conservare le sue opzioni attraverso questo giro.</summary>
    [Fact]
    public void TipoListaConOpzioni_SopravvivePayloadPhaseD()
    {
        var originale = new CampoSistemaDefinizione
        {
            Chiave = "vci-connection", Etichetta = "VCI connection", Gruppo = GruppoCampoSistema.Dettagli,
            Tipo = TipoCampoStrumento.Lista, OpzioniLista = new List<string> { "USB", "Ethernet" }, UsabileComeFiltro = true,
        };

        var payload = JsonSerializer.Serialize(originale, JsonIo.CompactOptions);
        var letto = JsonSerializer.Deserialize<CampoSistemaDefinizione>(payload, JsonIo.Options)!;

        Assert.Contains("\"OpzioniLista\"", payload);
        Assert.Equal(TipoCampoStrumento.Lista, letto.Tipo);
        Assert.Equal(new[] { "USB", "Ethernet" }, letto.OpzioniLista);
    }

    /// <summary>Salvataggio e ricaricamento dal file del catalogo (percorso legacy, senza journal Phase D).</summary>
    [Fact]
    public void TipoListaConOpzioni_SalvaECaricaDalFileDelCatalogo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_campolista_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "campi-sistema-dettagli.json");
            new MasterDataRepository<CampoSistemaDefinizione>(file).Save(new List<CampoSistemaDefinizione>
            {
                new()
                {
                    Chiave = "vci-connection", Etichetta = "VCI connection", Gruppo = GruppoCampoSistema.Dettagli,
                    Tipo = TipoCampoStrumento.Lista, OpzioniLista = new List<string> { "USB", "Ethernet" },
                },
            });

            var letto = Assert.Single(new MasterDataRepository<CampoSistemaDefinizione>(file).Load());
            Assert.Equal(TipoCampoStrumento.Lista, letto.Tipo);
            Assert.Equal(new[] { "USB", "Ethernet" }, letto.OpzioniLista);
        }
        finally
        {
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    /// <summary>Un valore salvato su un Test che non compare più tra le opzioni (rinominata o rimossa) resta tra le voci del menu, così non
    /// sparisce dal campo; le opzioni ancora presenti restano nell'ordine della definizione.</summary>
    [Fact]
    public void OpzioniVisualizzate_ValoreFuoriDalloElencoRestaVisibile()
    {
        var valori = new Dictionary<string, string> { ["vci-connection"] = "VCI RED USB" };
        var fuori = new CampoStrumentoValore(valori, "vci-connection", "VCI connection", TipoCampoStrumento.Lista, new List<string> { "VCI USB", "VCI LAN" }, null);
        Assert.Equal(new[] { "VCI USB", "VCI LAN", "VCI RED USB" }, fuori.OpzioniVisualizzate);

        valori["vci-connection"] = "VCI LAN";
        var dentro = new CampoStrumentoValore(valori, "vci-connection", "VCI connection", TipoCampoStrumento.Lista, new List<string> { "VCI USB", "VCI LAN" }, null);
        Assert.Equal(new[] { "VCI USB", "VCI LAN" }, dentro.OpzioniVisualizzate);
    }

    /// <summary>Quando cambiare il Tipo apre l'editor delle opzioni: solo passando a Lista un campo che non ne ha ancora, da un tipo noto.</summary>
    [Theory]
    [InlineData(TipoCampoStrumento.Testo, TipoCampoStrumento.Lista, 0, true)]
    [InlineData(TipoCampoStrumento.Testo, TipoCampoStrumento.Lista, 3, false)]
    [InlineData(TipoCampoStrumento.Lista, TipoCampoStrumento.Lista, 0, false)]
    [InlineData(TipoCampoStrumento.Lista, TipoCampoStrumento.Numero, 0, false)]
    public void DeveProporre_SoloPassandoAUnaListaSenzaOpzioni(TipoCampoStrumento precedente, TipoCampoStrumento nuovo, int opzioni, bool attesa) =>
        Assert.Equal(attesa, ListaOpzioniEditorWindow.DeveProporre(precedente, nuovo, opzioni));

    [Fact]
    public void DeveProporre_TipoPrecedenteSconosciutoNonApreNulla() =>
        Assert.False(ListaOpzioniEditorWindow.DeveProporre(null, TipoCampoStrumento.Lista, 0));

    /// <summary>L'unità di misura di un campo Numero sopravvive al payload Phase D come le opzioni.</summary>
    [Fact]
    public void UnitaMisura_DelCampoNumeroSopravvivePayloadPhaseD()
    {
        var originale = new CampoSistemaDefinizione { Chiave = "temperatura", Etichetta = "Temperatura", Tipo = TipoCampoStrumento.Numero, UnitaMisura = "°C" };

        var letto = JsonSerializer.Deserialize<CampoSistemaDefinizione>(JsonSerializer.Serialize(originale, JsonIo.CompactOptions), JsonIo.Options)!;

        Assert.Equal("°C", letto.UnitaMisura);
    }
}
