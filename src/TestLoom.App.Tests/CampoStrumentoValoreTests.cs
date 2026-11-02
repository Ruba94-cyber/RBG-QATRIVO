using TestLoom.App.Models;

namespace TestLoom.App.Tests;

/// <summary>Copertura del punto 8 (feedback utente 2026-08-24): evidenziazione sull'intero campo
/// (CampoStrumentoValore.Colore, dentro un'operazione) e campo Tipo=ListaTesto (righe con evidenziazione
/// propria, serializzate dentro lo stesso Valore stringa — nessun cambio allo schema del test).</summary>
public class CampoStrumentoValoreTests
{
    private static CampoStrumentoValore NuovoCampo(Dictionary<string, string> valori, string nome = "Trace", TipoCampoStrumento tipo = TipoCampoStrumento.Testo) =>
        new(valori, "ValidationTool", new DefinizioneCampoStrumento { Nome = nome, Tipo = tipo });

    [Fact]
    public void Colore_DiDefault_Nessuno()
    {
        var campo = NuovoCampo(new());
        Assert.Equal(ColoreEvidenziazione.Nessuno, campo.Colore);
    }

    [Fact]
    public void Colore_Impostato_SiPersisteNelDizionarioConChiaveCompagna()
    {
        var valori = new Dictionary<string, string>();
        var campo = NuovoCampo(valori);

        campo.Colore = ColoreEvidenziazione.Rosso;

        Assert.Equal(ColoreEvidenziazione.Rosso, campo.Colore);
        Assert.Equal("Rosso", valori["ValidationTool::Trace#colore"]);
        // il valore vero e proprio del campo non viene toccato dalla chiave colore
        Assert.False(valori.ContainsKey("ValidationTool::Trace"));
    }

    [Fact]
    public void Colore_RiportatoANessuno_RimuoveLaChiaveInveceDiScrivereNessuno()
    {
        var valori = new Dictionary<string, string> { ["ValidationTool::Trace#colore"] = "Giallo" };
        var campo = NuovoCampo(valori);

        campo.Colore = ColoreEvidenziazione.Nessuno;

        Assert.False(valori.ContainsKey("ValidationTool::Trace#colore"));
    }

    [Fact]
    public void CheckboxVuoto_VisualizzaOffSenzaMaterializzareUnValoreFincheNonVieneModificato()
    {
        var valori = new Dictionary<string, string>();
        var campo = NuovoCampo(valori, "Abilitato", TipoCampoStrumento.Checkbox);

        Assert.Null(campo.ValoreCheckbox);
        Assert.False(campo.ValoreCheckboxVisual);
        Assert.Empty(valori);

        campo.ValoreCheckboxVisual = true;
        Assert.Equal("true", valori["ValidationTool::Abilitato"]);

        campo.ValoreCheckboxVisual = false;
        Assert.Equal("false", valori["ValidationTool::Abilitato"]);
    }

    [Fact]
    public void RigheListaTesto_CampoVuoto_ListaVuota()
    {
        var campo = NuovoCampo(new(), tipo: TipoCampoStrumento.ListaTesto);
        Assert.Empty(campo.RigheListaTesto);
        Assert.Equal(0, campo.RigheListaTestoConteggio);
    }

    [Fact]
    public void RigheListaTesto_RoundTripConEvidenziazionePerRiga()
    {
        var valori = new Dictionary<string, string>();
        var campo = NuovoCampo(valori, tipo: TipoCampoStrumento.ListaTesto);

        campo.RigheListaTesto = new List<RigaEvidenziata>
        {
            new() { Testo = "TX > 22 01 07", Colore = ColoreEvidenziazione.Nessuno },
            new() { Testo = "RX < 62 01 07 13 88", Colore = ColoreEvidenziazione.Rosso },
        };

        // la seconda istanza dello stesso campo (stesso dizionario sottostante) deve rileggere le stesse righe
        var campoRiletto = NuovoCampo(valori, tipo: TipoCampoStrumento.ListaTesto);
        Assert.Equal(2, campoRiletto.RigheListaTestoConteggio);
        Assert.Equal("TX > 22 01 07", campoRiletto.RigheListaTesto[0].Testo);
        Assert.Equal(ColoreEvidenziazione.Nessuno, campoRiletto.RigheListaTesto[0].Colore);
        Assert.Equal(ColoreEvidenziazione.Rosso, campoRiletto.RigheListaTesto[1].Colore);
    }

    [Fact]
    public void RigheListaTesto_ColoreSalvatoPerNomeNonPerNumero()
    {
        var campo = NuovoCampo(new Dictionary<string, string>(), tipo: TipoCampoStrumento.ListaTesto);
        campo.RigheListaTesto = new List<RigaEvidenziata> { new() { Testo = "riga", Colore = ColoreEvidenziazione.Giallo } };

        // Il numero dipende dall'ordine dell'enum: salvando il nome il colore resta lo stesso anche se l'ordine cambia.
        Assert.Contains("\"Colore\":\"Giallo\"", campo.Valore);
    }

    [Fact]
    public void RigheListaTesto_LeggeIlColoreNumericoDeiDatiPrecedenti()
    {
        var campo = NuovoCampo(new Dictionary<string, string>(), tipo: TipoCampoStrumento.ListaTesto);
        campo.Valore = "[{\"Testo\":\"TX\",\"Colore\":1},{\"Testo\":\"RX\",\"Colore\":2}]";

        Assert.Equal(new[] { ColoreEvidenziazione.Giallo, ColoreEvidenziazione.Rosso }, campo.RigheListaTesto.Select(r => r.Colore));
    }

    [Fact]
    public void RigheListaTesto_ListaVuotaImpostata_RipulisceIlValore()
    {
        var valori = new Dictionary<string, string>();
        var campo = NuovoCampo(valori, tipo: TipoCampoStrumento.ListaTesto);
        campo.RigheListaTesto = new List<RigaEvidenziata> { new() { Testo = "riga" } };

        campo.RigheListaTesto = new List<RigaEvidenziata>();

        Assert.Null(campo.Valore);
    }

    [Fact]
    public void RigheListaTesto_ValoreNonJsonValido_TornaListaVuotaInveceDiEsplodere()
    {
        // un campo passato da Testo a ListaTesto dopo il fatto porta un Valore che non è JSON
        var valori = new Dictionary<string, string> { ["ValidationTool::Trace"] = "testo semplice, non JSON" };
        var campo = NuovoCampo(valori, tipo: TipoCampoStrumento.ListaTesto);

        Assert.Empty(campo.RigheListaTesto);
    }
}
