using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PreferenzeMatriceAuditTests
{
    private static readonly string[] Componente = { "Componente01", "Componente02", "Componente03", "Componente04" };

    [Fact]
    public void Predefinite_TutteVisibiliOrdineNaturale()
    {
        var p = PreferenzeMatriceAudit.Deserializza(null);
        Assert.Equal(Componente, p.OrdineVisibile(Componente));
        Assert.Empty(p.EcuNascoste);
        Assert.Null(p.LarghezzaRequisito);
        Assert.Equal(Componente, PreferenzeMatriceAudit.Deserializza("  ").OrdineVisibile(Componente));
    }

    [Fact]
    public void SalvaERicarica_ConservaVisibilitaOrdineLarghezzeEAltezze()
    {
        var p = new PreferenzeMatriceAudit { LarghezzaRequisito = 200 };
        p.LarghezzeEcu["Componente02"] = 150;
        p.AltezzeRighe["ST0001"] = 60;
        Assert.True(p.ImpostaVisibile("Componente03", false, Componente));
        p.ImpostaOrdineVisibili(Componente, new[] { "Componente04", "Componente01", "Componente02" });

        var r = PreferenzeMatriceAudit.Deserializza(p.Serializza());
        Assert.Equal(new[] { "Componente04", "Componente01", "Componente02" }, r.OrdineVisibile(Componente));
        Assert.True(r.EcuNascosta("componente03")); // confronto senza distinzione di maiuscole
        Assert.Equal(200, r.LarghezzaRequisito);
        Assert.Equal(150, r.LarghezzeEcu["Componente02"]);
        Assert.Equal(60, r.AltezzeRighe["ST0001"]);
        Assert.Equal(2, r.Versione);
    }

    [Fact]
    public void ColonneSconosciute_VengonoIgnorateSenzaEssereScartate()
    {
        var p = new PreferenzeMatriceAudit();
        p.OrdineEcu.AddRange(new[] { "Componente99", "Componente03", "Componente01" });
        p.EcuNascoste.Add("Componente98");
        p.EcuNascoste.Add("Componente02");
        Assert.Equal(new[] { "Componente03", "Componente01", "Componente04" }, p.OrdineVisibile(Componente));
        var r = PreferenzeMatriceAudit.Deserializza(p.Serializza());
        Assert.True(r.EcuNascosta("Componente98")); // resta nel file: tornera' valida se il componente riappare
        Assert.Equal(new[] { "Componente03", "Componente01", "Componente04" }, r.OrdineVisibile(Componente));
    }

    [Fact]
    public void NonSiNascondeUltimaColonna_ETuttiNascosti_MostraTutto()
    {
        var p = new PreferenzeMatriceAudit();
        Assert.True(p.ImpostaVisibile("Componente01", false, Componente));
        Assert.True(p.ImpostaVisibile("Componente02", false, Componente));
        Assert.True(p.ImpostaVisibile("Componente03", false, Componente));
        Assert.False(p.ImpostaVisibile("Componente04", false, Componente));
        Assert.Equal(new[] { "Componente04" }, p.OrdineVisibile(Componente));

        // Preferenza salvata per un altro sottoveicolo che nasconderebbe tutti i componenti di questo: non lascia la matrice vuota.
        p.EcuNascoste.Add("Componente04");
        Assert.Equal(Componente, p.OrdineVisibile(Componente));
    }

    [Fact]
    public void RiordinoDelleVisibili_MantieneLaPosizioneDelleNascoste()
    {
        var p = new PreferenzeMatriceAudit();
        p.ImpostaVisibile("Componente02", false, Componente);
        p.ImpostaOrdineVisibili(Componente, new[] { "Componente04", "Componente03", "Componente01" });
        Assert.Equal(new[] { "Componente04", "Componente02", "Componente03", "Componente01" }, p.OrdineCompleto(Componente));
        p.ImpostaVisibile("Componente02", true, Componente);
        Assert.Equal(new[] { "Componente04", "Componente02", "Componente03", "Componente01" }, p.OrdineVisibile(Componente));
    }

    [Fact]
    public void Ripristina_TornaAlPredefinito()
    {
        var p = new PreferenzeMatriceAudit { LarghezzaRequisito = 300 };
        p.LarghezzeEcu["Componente01"] = 100;
        p.AltezzeRighe["ST1"] = 50;
        p.ImpostaVisibile("Componente02", false, Componente);
        p.ImpostaOrdineVisibili(Componente, new[] { "Componente04", "Componente01", "Componente03" });
        p.RipristinaPredefinite();
        Assert.Equal(Componente, p.OrdineVisibile(Componente));
        Assert.Null(p.LarghezzaRequisito);
        Assert.Empty(p.LarghezzeEcu);
        Assert.Empty(p.AltezzeRighe);
        Assert.Empty(p.EcuNascoste);
    }

    [Fact]
    public void JsonNonValidoOValoriFuoriLimite_SonoNormalizzati()
    {
        Assert.Equal(Componente, PreferenzeMatriceAudit.Deserializza("{non json").OrdineVisibile(Componente));
        var r = PreferenzeMatriceAudit.Deserializza(
            "{\"Versione\":1,\"LarghezzaRequisito\":9999,\"LarghezzeEcu\":{\"componente01\":1,\"\":50},\"OrdineEcu\":[\"\",\"componente02\",\"componente02\"],\"AltezzeRighe\":{\"A\":5000}}");
        Assert.Equal(PreferenzeMatriceAudit.LarghezzaRequisitoMassima, r.LarghezzaRequisito);
        Assert.Equal(PreferenzeMatriceAudit.LarghezzaEcuMinima, r.LarghezzeEcu["Componente01"]);
        Assert.Single(r.LarghezzeEcu);
        Assert.Equal(new[] { "Componente02" }, r.OrdineEcu);
        Assert.Equal(PreferenzeMatriceAudit.AltezzaRigaMassima, r.AltezzeRighe["A"]);
        Assert.Empty(r.EcuNascoste); // config v1: nessuna componente nascosta
        Assert.Equal(2, r.Versione);
    }
}
