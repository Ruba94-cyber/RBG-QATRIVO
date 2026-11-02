using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// 6.7.1: la radice condivisa va ricontrollata periodicamente, e il banner "cartella non disponibile" segue l'esito della
/// sonda (non la cache che nasconde il banner proprio quando la cartella manca). Sonda finta: una sequenza di esiti.
/// </summary>
public class MonitorRaggiungibilitaRadiceTests
{
    private static readonly DateTime Base = new(2026, 10, 13, 9, 0, 0);
    private const StatoCondivisione Op = StatoCondivisione.Operativa;
    private const StatoCondivisione NonDisp = StatoCondivisione.NonDisponibile;

    /// <summary>Applica un passo come fa AppState: se la radice non risponde lo stato diventa non disponibile.</summary>
    private static (PassoSonda Passo, StatoCondivisione Stato) Applica(MonitorRaggiungibilitaRadice m, RadiceCondivisaSonda.Esito esito,
        StatoCondivisione statoCorrente, DateTime ora, bool forza = false)
    {
        var passo = m.Passo(esito, statoCorrente, forza, ora);
        var stato = passo.NuovoStato?.Stato ?? statoCorrente;
        return (passo, stato);
    }

    [Theory]
    [InlineData(RadiceCondivisaSonda.Esito.Presente, true)]
    [InlineData(RadiceCondivisaSonda.Esito.Offline, true)]
    [InlineData(RadiceCondivisaSonda.Esito.Assente, false)]
    [InlineData(RadiceCondivisaSonda.Esito.Inaccessibile, false)]
    [InlineData(RadiceCondivisaSonda.Esito.Lenta, false)]
    public void EUtilizzabile_RispettaLaSemanticaDiPresenteUi(RadiceCondivisaSonda.Esito esito, bool atteso)
    {
        Assert.Equal(atteso, MonitorRaggiungibilitaRadice.EUtilizzabile(esito));
    }

    [Fact]
    public void CartellaSparita_ImpostaNonDisponibileConMessaggioENonRilegge()
    {
        var m = new MonitorRaggiungibilitaRadice();

        var (passo, stato) = Applica(m, RadiceCondivisaSonda.Esito.Assente, Op, Base);

        Assert.False(passo.Utilizzabile);
        Assert.False(passo.Ricalcola);
        Assert.Equal(NonDisp, stato);
        Assert.False(string.IsNullOrWhiteSpace(passo.NuovoStato!.Messaggio));
        Assert.False(m.UltimaUtilizzabile);
    }

    [Fact]
    public void Banner_SegueLoStatoNonLaCacheECompareSubitoDopoLaPerdita()
    {
        var m = new MonitorRaggiungibilitaRadice();
        var (_, stato) = Applica(m, RadiceCondivisaSonda.Esito.Assente, Op, Base);

        // Prima (cache che nasconderebbe il banner): ora il banner dipende solo da workspace inizializzato e stato.
        Assert.True(MonitorRaggiungibilitaRadice.MostraBanner(workspaceInizializzato: true, stato));
        Assert.False(MonitorRaggiungibilitaRadice.MostraBanner(workspaceInizializzato: false, stato));
    }

    [Fact]
    public void CartellaTornaRaggiungibile_ChiedeUnaSolaRileturaDiRecupero()
    {
        var m = new MonitorRaggiungibilitaRadice();
        var (_, stato) = Applica(m, RadiceCondivisaSonda.Esito.Assente, Op, Base);

        var (passo, statoDopo) = Applica(m, RadiceCondivisaSonda.Esito.Presente, stato, Base.AddSeconds(15));
        Assert.True(passo.Utilizzabile);
        Assert.True(passo.Ricalcola, "dopo la perdita, il ritorno della radice deve innescare la rilettura di recupero");
        Assert.Null(passo.NuovoStato);
        Assert.Equal(NonDisp, statoDopo); // lo stato lo chiude la rilettura (controllo di scrittura), non la sonda

        // Il giro successivo, con la radice ancora presente, non rilegge di nuovo a ogni 15 s.
        var (passo2, _) = Applica(m, RadiceCondivisaSonda.Esito.Presente, statoDopo, Base.AddSeconds(30));
        Assert.False(passo2.Ricalcola);
    }

    [Fact]
    public void RadicePresenteSempreOperativa_NonRileggeNeiGiriPeriodici()
    {
        var m = new MonitorRaggiungibilitaRadice();
        for (var i = 0; i < 10; i++)
        {
            var (passo, _) = Applica(m, RadiceCondivisaSonda.Esito.Presente, Op, Base.AddSeconds(15 * i));
            Assert.False(passo.Ricalcola);
        }
    }

    [Fact]
    public void AggiornaOra_RileggeSempreSeLaRadiceRisponde()
    {
        var m = new MonitorRaggiungibilitaRadice();
        var (passo, _) = Applica(m, RadiceCondivisaSonda.Esito.Presente, Op, Base, forza: true);
        Assert.True(passo.Ricalcola);
    }

    [Fact]
    public void RecuperoFallitoConRadicePresente_RitentaSoloDopoIlRitardo()
    {
        var m = new MonitorRaggiungibilitaRadice();
        // Radice presente ma stato ancora non disponibile (per esempio prova di scrittura fallita).
        var primo = m.Passo(RadiceCondivisaSonda.Esito.Presente, NonDisp, false, Base);
        Assert.True(primo.Ricalcola);

        var troppoPresto = m.Passo(RadiceCondivisaSonda.Esito.Presente, NonDisp, false, Base.AddSeconds(15));
        Assert.False(troppoPresto.Ricalcola);

        var dopoRitardo = m.Passo(RadiceCondivisaSonda.Esito.Presente, NonDisp, false,
            Base.Add(MonitorRaggiungibilitaRadice.RitardoRiprovaRecupero));
        Assert.True(dopoRitardo.Ricalcola);
    }

    [Fact]
    public void SequenzaSonda_PresenteAssenteAssentePresente_BannerSiAccendeESiSpegne()
    {
        var m = new MonitorRaggiungibilitaRadice();
        var sequenza = new[]
        {
            RadiceCondivisaSonda.Esito.Presente,
            RadiceCondivisaSonda.Esito.Assente,
            RadiceCondivisaSonda.Esito.Assente,
            RadiceCondivisaSonda.Esito.Presente,
        };
        var stato = Op;
        var banner = new List<bool>();
        var ora = Base;
        foreach (var esito in sequenza)
        {
            var (_, nuovo) = Applica(m, esito, stato, ora);
            stato = nuovo;
            banner.Add(MonitorRaggiungibilitaRadice.MostraBanner(true, stato));
            ora = ora.AddSeconds(15);
        }
        // Il ritorno della radice non chiude il banner da solo: lo chiude la rilettura di recupero (qui simulata).
        Assert.Equal(new[] { false, true, true, true }, banner);
        stato = Op; // rilettura riuscita
        Assert.False(MonitorRaggiungibilitaRadice.MostraBanner(true, stato));
    }

    [Fact]
    public void MostraBanner_NonVisibileSeLoWorkspaceNonERiinizializzato()
    {
        Assert.False(MonitorRaggiungibilitaRadice.MostraBanner(false, NonDisp));
        Assert.False(MonitorRaggiungibilitaRadice.MostraBanner(false, StatoCondivisione.SolaLettura));
        Assert.True(MonitorRaggiungibilitaRadice.MostraBanner(true, NonDisp));
        Assert.True(MonitorRaggiungibilitaRadice.MostraBanner(true, StatoCondivisione.SolaLettura));
        Assert.False(MonitorRaggiungibilitaRadice.MostraBanner(true, Op));
    }

    [Fact]
    public void Lenta_ConsideraLaRadiceNonRaggiungibile()
    {
        var m = new MonitorRaggiungibilitaRadice();
        var (passo, stato) = Applica(m, RadiceCondivisaSonda.Esito.Lenta, Op, Base);
        Assert.False(passo.Utilizzabile);
        Assert.Equal(NonDisp, stato);
        Assert.Contains("non risponde", passo.NuovoStato!.Messaggio);
    }

    [Fact]
    public void IntestazioneCartella_DichiaraCartellaEModalitaInUso()
    {
        var cartella = Path.Combine(Path.GetTempPath(), "testloom-intestazione-" + Guid.NewGuid().ToString("N"));
        var righe = WorkspaceDiagnosticSnapshotService.IntestazioneCartella(cartella);

        Assert.Equal(3, righe.Count);
        Assert.Contains(cartella, righe[0], StringComparison.Ordinal);
        // Testo indipendente dalla lingua corrente: il prefisso viene dalla stessa chiave usata dalla finestra.
        var prefissoModalita = string.Format(LocalizationService.Instance.Traduci("WsDiagModalitaFmt"), "");
        Assert.StartsWith(prefissoModalita, righe[1], StringComparison.Ordinal);
    }
}
