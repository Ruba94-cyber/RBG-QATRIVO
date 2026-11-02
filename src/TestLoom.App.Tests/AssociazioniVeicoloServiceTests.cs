using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Associazioni viste dal veicolo (Veicoli > "Componente e funzionalità"): montaggio dei componenti sul veicolo e restrizioni
/// delle funzionalità per componente, senza alterare gli altri veicoli. Logica pura, senza file né UI.</summary>
public sealed class AssociazioniVeicoloServiceTests
{
    private const string V1 = "VEI-1";
    private const string V2 = "VEI-2";
    private const string V3 = "VEI-3";

    private static Componente E(string nome, params string[] veicoli) => new() { Nome = nome, VeicoliAssociati = veicoli.ToList() };

    private static Funzionalita F(string nome, bool tutteLeEcu, string[]? ecuAssociate = null, params VeicoliFunzionalitaEcu[] voci) => new()
    {
        Nome = nome,
        TutteLeEcu = tutteLeEcu,
        EcuAssociate = ecuAssociate?.ToList() ?? new List<string>(),
        VeicoliPerEcu = voci.ToList(),
    };

    private static VeicoliFunzionalitaEcu Voce(string componente, params string[] veicoli) => new() { Componente = componente, Veicoli = veicoli.ToList() };

    private static string[]? Ristretti(Funzionalita f, string componente) => f.VeicoliRistretti(componente)?.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();

    private static string[] Insieme(params string[] veicoli) => veicoli.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();

    [Fact]
    public void EcuSelezionabili_EscludeLeArchiviateEOrdinaNaturale()
    {
        var componente = new[] { E("Componente10"), new Componente { Nome = "ECUX", Archiviata = true }, E("Componente2"), E("Componente1") };

        var risultato = AssociazioniVeicoloService.EcuSelezionabili(componente).Select(e => e.Nome).ToArray();

        Assert.Equal(new[] { "Componente1", "Componente2", "Componente10" }, risultato);
    }

    [Fact]
    public void FunzionalitaDellEcu_ContieneTutteLeEcuEAssociate_EscludeLeArchiviate()
    {
        var funzioni = new[]
        {
            F("Tutte", tutteLeEcu: true),
            F("Solo E1", tutteLeEcu: false, ecuAssociate: new[] { "E1" }),
            new Funzionalita { Nome = "Archiviata", TutteLeEcu = true, Archiviata = true },
        };

        Assert.Equal(new[] { "Solo E1", "Tutte" }, AssociazioniVeicoloService.FunzionalitaDellEcu(funzioni, "E1").Select(f => f.Nome));
        Assert.Equal(new[] { "Tutte" }, AssociazioniVeicoloService.FunzionalitaDellEcu(funzioni, "E2").Select(f => f.Nome));
    }

    [Fact]
    public void FunzionalitaAggiungibili_NonContieneLeGiaValideSullEcuNeLeArchiviate()
    {
        var funzioni = new[]
        {
            F("Tutte", tutteLeEcu: true),
            F("Solo E1", tutteLeEcu: false, ecuAssociate: new[] { "E1" }),
            F("Libera", tutteLeEcu: false, ecuAssociate: new[] { "E2" }),
            new Funzionalita { Nome = "Archiviata", TutteLeEcu = false, Archiviata = true },
        };

        Assert.Equal(new[] { "Libera" }, AssociazioniVeicoloService.FunzionalitaAggiungibili(funzioni, "E1").Select(f => f.Nome));
    }

    [Fact]
    public void ImpostaEcuMontata_Montare_AggiungeSenzaDuplicatiIgnorandoMaiuscole()
    {
        var componente = E("E1", V1);

        AssociazioniVeicoloService.ImpostaEcuMontata(componente, V2, true, Array.Empty<Funzionalita>());
        AssociazioniVeicoloService.ImpostaEcuMontata(componente, V2, true, Array.Empty<Funzionalita>());
        AssociazioniVeicoloService.ImpostaEcuMontata(componente, V1.ToLowerInvariant(), true, Array.Empty<Funzionalita>());

        Assert.Equal(Insieme(V1, V2), componente.VeicoliAssociati.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void ImpostaEcuMontata_Smontare_ToglieIlVeicoloSoloDalleRestrizioniDiQuellEcu()
    {
        var componente = E("E1", V1, V2);
        var tutte = F("Tutte", tutteLeEcu: true);
        var ristretta = F("Ristretta", tutteLeEcu: true, voci: Voce("E1", V1));
        var altraEcu = F("Altra", tutteLeEcu: true, voci: Voce("E9", V1));
        var funzioni = new[] { tutte, ristretta, altraEcu };

        AssociazioniVeicoloService.ImpostaEcuMontata(componente, V1, false, funzioni);

        Assert.Equal(new[] { V2 }, componente.VeicoliAssociati);
        Assert.Equal(Array.Empty<string>(), Ristretti(ristretta, "E1"));    // voce svuotata: nessun veicolo su E1
        Assert.False(ristretta.ValeSu("E1", V2));
        Assert.True(tutte.ValeSu("E1", V2));                                 // senza voce vale ancora sul veicolo rimasto
        Assert.Equal(Insieme(V1), Ristretti(altraEcu, "E9"));                 // le altre componente restano intatte
    }

    [Fact]
    public void ImpostaFunzioneSulVeicolo_Disattivare_SenzaVoce_CreaVoceConGliAltriVeicoliDellEcu()
    {
        var componente = E("E1", V1, V2, V3);
        var f = F("Funzione", tutteLeEcu: true);

        AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(f, componente, V2, applicata: false);

        Assert.Equal(Insieme(V1, V3), Ristretti(f, "E1"));
        Assert.False(f.ValeSu("E1", V2));
        Assert.True(f.ValeSu("E1", V1));
        Assert.True(f.ValeSu("E1", V3));
    }

    [Fact]
    public void ImpostaFunzioneSulVeicolo_Riattivare_QuandoLaVoceTornaTuttiIVeicoli_RimuoveLaVoce()
    {
        var componente = E("E1", V1, V2);
        var f = F("Funzione", tutteLeEcu: true, voci: Voce("E1", V1));

        AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(f, componente, V2, applicata: true);

        Assert.Empty(f.VeicoliPerEcu);
        Assert.Null(f.VeicoliRistretti("E1"));
        Assert.True(f.ValeSu("E1", V1));
        Assert.True(f.ValeSu("E1", V2));
    }

    [Fact]
    public void ImpostaFunzioneSulVeicolo_UltimoVeicoloDisattivato_LasciaVoceVuotaNessunVeicolo()
    {
        var componente = E("E1", V1);
        var f = F("Funzione", tutteLeEcu: true);

        AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(f, componente, V1, applicata: false);

        Assert.NotNull(f.VeicoliRistretti("E1"));
        Assert.Empty(f.VeicoliRistretti("E1")!);
        Assert.False(f.ValeSu("E1", V1));
    }

    [Fact]
    public void ImpostaFunzioneSulVeicolo_RiattivareInVoceEsistente_AggiungeSoloQuelVeicolo()
    {
        var componente = E("E1", V1, V2, V3);
        var f = F("Funzione", tutteLeEcu: true, voci: Voce("E1", V1));

        AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(f, componente, V3, applicata: true);

        Assert.Equal(Insieme(V1, V3), Ristretti(f, "E1"));
        Assert.False(f.ValeSu("E1", V2));
    }

    [Fact]
    public void ImpostaFunzioneSulVeicolo_DisattivareERiattivare_TornaAlloStessoStatoIniziale()
    {
        var componente = E("E1", V1, V2);
        var f = F("Funzione", tutteLeEcu: true);

        AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(f, componente, V2, applicata: false);
        AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(f, componente, V2, applicata: true);

        Assert.Empty(f.VeicoliPerEcu);
        Assert.True(f.ValeSu("E1", V1));
        Assert.True(f.ValeSu("E1", V2));
    }

    [Fact]
    public void ImpostaFunzioneSulVeicolo_NonValeSullEcu_NonCambiaNulla()
    {
        var componente = E("E1", V1);
        var f = F("Solo E2", tutteLeEcu: false, ecuAssociate: new[] { "E2" });

        AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(f, componente, V1, applicata: false);

        Assert.Empty(f.VeicoliPerEcu);
        Assert.Equal(new[] { "E2" }, f.EcuAssociate);
    }

    [Fact]
    public void ImpostaFunzioneSulVeicolo_NonTocaLeVociDelleAltreEcu()
    {
        var e1 = E("E1", V1, V2);
        var f = F("Funzione", tutteLeEcu: true, voci: new[] { Voce("E2", V2), Voce("E1", V1, V2) });

        AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(f, e1, V1, applicata: false);

        Assert.Equal(Insieme(V2), Ristretti(f, "E1"));
        Assert.Equal(Insieme(V2), Ristretti(f, "E2"));
    }

    [Fact]
    public void ImpostaFunzioneSulVeicolo_NonRiordinaLeAltreVoci()
    {
        var componente = E("E1", V1, V2);
        var f = F("Funzione", tutteLeEcu: true, voci: new[] { Voce("E0", V1), Voce("E1", V1, V2), Voce("E9", V2) });

        AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(f, componente, V1, applicata: false);

        Assert.Equal(new[] { "E0", "E1", "E9" }, f.VeicoliPerEcu.Select(v => v.Componente));   // stessa posizione della voce di E1
        Assert.Equal(Insieme(V2), Ristretti(f, "E1"));
        Assert.Equal(Insieme(V1), Ristretti(f, "E0"));
    }

    [Fact]
    public void AggiungiFunzioneAdEcu_SoloQuestoVeicolo_RestringeAlVeicoloLasciandoGliAltri()
    {
        var componente = E("E1", V1, V2);
        var f = F("Nuova", tutteLeEcu: false);

        AssociazioniVeicoloService.AggiungiFunzioneAdEcu(f, componente, V1, soloQuestoVeicolo: true);

        Assert.Contains("E1", f.EcuAssociate);
        Assert.True(f.ValeSu("E1", V1));
        Assert.False(f.ValeSu("E1", V2));
        Assert.Equal(new[] { "E1" }, f.VeicoliPerEcu.Select(v => v.Componente));
    }

    [Fact]
    public void AggiungiFunzioneAdEcu_TuttiIVeicoli_NonCreaRestrizione()
    {
        var componente = E("E1", V1, V2);
        var f = F("Nuova", tutteLeEcu: false);

        AssociazioniVeicoloService.AggiungiFunzioneAdEcu(f, componente, V1, soloQuestoVeicolo: false);

        Assert.Contains("E1", f.EcuAssociate);
        Assert.True(f.ValeSu("E1", V1));
        Assert.True(f.ValeSu("E1", V2));
        Assert.Empty(f.VeicoliPerEcu);
    }

    [Fact]
    public void AggiungiFunzioneAdEcu_EcuConUnSoloVeicolo_NonRichiedeRestrizione()
    {
        var componente = E("E1", V1);
        var f = F("Nuova", tutteLeEcu: false);

        AssociazioniVeicoloService.AggiungiFunzioneAdEcu(f, componente, V1, soloQuestoVeicolo: true);

        Assert.Contains("E1", f.EcuAssociate);
        Assert.True(f.ValeSu("E1", V1));
        Assert.Empty(f.VeicoliPerEcu);
    }

    [Fact]
    public void AggiungiFunzioneAdEcu_ScartaLeVociResiduDiUnEcuNonAssociata()
    {
        var componente = E("E1", V1, V2);
        var f = F("Nuova", tutteLeEcu: false, voci: Voce("E1", V2));

        AssociazioniVeicoloService.AggiungiFunzioneAdEcu(f, componente, V1, soloQuestoVeicolo: true);

        Assert.Equal(new[] { "E1" }, f.VeicoliPerEcu.Select(v => v.Componente));
        Assert.Equal(Insieme(V1), Ristretti(f, "E1"));
    }

    [Fact]
    public void AggiungiFunzioneAdEcu_FunzioneGiaTutteLeEcu_NonCambiaNulla()
    {
        var componente = E("E1", V1, V2);
        var f = F("Tutte", tutteLeEcu: true);

        AssociazioniVeicoloService.AggiungiFunzioneAdEcu(f, componente, V1, soloQuestoVeicolo: true);

        Assert.Empty(f.EcuAssociate);
        Assert.Empty(f.VeicoliPerEcu);
    }

    // ---------- Lettura intersecata con il montaggio e checklist dei veicoli (pagina Funzionalità) ----------

    private const string VeicoloResiduo = "VEI-9"; // tolto dal componente senza riscrivere la funzionalità: la voce è inerte

    [Fact]
    public void VeicoliAmmessi_SenzaRestrizione_SonoTuttiIMontati()
    {
        var componente = E("E1", V1, V2);
        var f = F("Funzione", tutteLeEcu: true);

        Assert.Equal(new[] { V1, V2 }, AssociazioniVeicoloService.VeicoliAmmessi(f, componente));
    }

    [Fact]
    public void VeicoliAmmessi_IntersecaLaRestrizioneConIMontati_IgnoraIVeicoliNonPiuMontati()
    {
        var componente = E("E1", V1, V2);
        var f = F("Funzione", tutteLeEcu: true, voci: Voce("E1", V1, V3));

        Assert.Equal(new[] { V1 }, AssociazioniVeicoloService.VeicoliAmmessi(f, componente));
    }

    [Fact]
    public void RiepilogoVeicoli_TuttiOSottoinsieme_ContaSoloIMontati()
    {
        var componente = E("E1", V1, V2);

        var tutti = AssociazioniVeicoloService.RiepilogoVeicoli(F("Tutti", tutteLeEcu: true), componente);
        Assert.True(tutti.TuttiVeicoli);
        Assert.Equal((2, 2), (tutti.Ammessi, tutti.Montati));

        var parziale = AssociazioniVeicoloService.RiepilogoVeicoli(F("Parziale", tutteLeEcu: true, voci: Voce("E1", V1)), componente);
        Assert.False(parziale.TuttiVeicoli);
        Assert.Equal((1, 2), (parziale.Ammessi, parziale.Montati));

        // Una restrizione fatta solo di veicoli non più montati non ammette nessun veicolo.
        var residua = AssociazioniVeicoloService.RiepilogoVeicoli(F("Residua", tutteLeEcu: true, voci: Voce("E1", V3)), componente);
        Assert.Equal((0, 2), (residua.Ammessi, residua.Montati));
    }

    [Fact]
    public void ValeSuVeicoloMontato_VoceResiduaDiVeicoloNonMontato_NonVale()
    {
        var componente = E("E1", V1, V2);
        var f = F("Funzione", tutteLeEcu: true, voci: Voce("E1", V1, V3));

        Assert.True(AssociazioniVeicoloService.ValeSuVeicoloMontato(f, componente, V1));
        Assert.False(AssociazioniVeicoloService.ValeSuVeicoloMontato(f, componente, V2));
        Assert.False(AssociazioniVeicoloService.ValeSuVeicoloMontato(f, componente, V3));
    }

    [Fact]
    public void ValeSuVeicoloMontato_SenzaRestrizione_RestaComeValeSu()
    {
        // Come prima: senza restrizione nessun controllo di montaggio, quindi anche un veicolo non montato vale.
        var componente = E("E1", V1, V2);
        var f = F("Funzione", tutteLeEcu: true);

        Assert.Equal(f.ValeSu("E1", V3), AssociazioniVeicoloService.ValeSuVeicoloMontato(f, componente, V3));
        Assert.True(AssociazioniVeicoloService.ValeSuVeicoloMontato(f, componente, V3));
    }

    [Fact]
    public void ImpostaVeicoliFunzione_TuttiIVeicoliMontati_RimuoveLaVoce()
    {
        var componente = E("E1", V1, V2, V3);
        var f = F("Funzione", tutteLeEcu: true, voci: Voce("E1", V1));

        AssociazioniVeicoloService.ImpostaVeicoliFunzione(f, componente, new[] { V3, V1, V2 });

        Assert.Empty(f.VeicoliPerEcu);
        Assert.Null(f.VeicoliRistretti("E1"));
    }

    [Fact]
    public void ImpostaVeicoliFunzione_Sottoinsieme_ScriveSoloIMontati_SenzaRiordinareLeAltreVoci()
    {
        var componente1 = E("E1", V1, V2, V3);
        var f = F("Funzione", tutteLeEcu: true, voci: new[] { Voce("E2", V1), Voce("E1", V1, VeicoloResiduo) });

        AssociazioniVeicoloService.ImpostaVeicoliFunzione(f, componente1, new[] { V2, VeicoloResiduo });

        Assert.Equal(new[] { "E2", "E1" }, f.VeicoliPerEcu.Select(v => v.Componente).ToArray());
        Assert.Equal(new[] { V1 }, Ristretti(f, "E2"));
        Assert.Equal(new[] { V2 }, Ristretti(f, "E1"));
    }

    [Fact]
    public void ImpostaVeicoliFunzione_NessunVeicoloScelto_LasciaUnaVoceSenzaVeicoli()
    {
        var componente = E("E1", V1, V2);
        var f = F("Funzione", tutteLeEcu: true);

        AssociazioniVeicoloService.ImpostaVeicoliFunzione(f, componente, Array.Empty<string>());

        Assert.Equal(new[] { "E1" }, f.VeicoliPerEcu.Select(v => v.Componente).ToArray());
        Assert.False(f.ValeSu("E1", V1));
        Assert.False(f.ValeSu("E1", V2));
    }

    [Fact]
    public void ImpostaVeicoliFunzione_FunzioneNonValeSullEcu_NonCambiaNulla()
    {
        var componente = E("E1", V1, V2);
        var f = F("SoloE2", tutteLeEcu: false, ecuAssociate: new[] { "E2" });

        AssociazioniVeicoloService.ImpostaVeicoliFunzione(f, componente, new[] { V1 });

        Assert.Empty(f.VeicoliPerEcu);
    }

    [Fact]
    public void ImpostaFunzioneSulVeicolo_RiscriveSoloIMontati_LasciandoCadereIResidui()
    {
        // Attivare V2 con la restrizione [V1, residuo]: si riscrivono solo i montati, che ora sono tutti, quindi la voce cade.
        var componente = E("E1", V1, V2);
        var f = F("Funzione", tutteLeEcu: true, voci: Voce("E1", V1, VeicoloResiduo));

        AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(f, componente, V2, applicata: true);

        Assert.Empty(f.VeicoliPerEcu);
    }
}
