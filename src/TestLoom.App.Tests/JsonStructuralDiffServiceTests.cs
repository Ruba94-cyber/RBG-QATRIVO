using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class JsonStructuralDiffServiceTests
{
    private static JsonStructuralDiffService.Report Compare(string a, string b,
        int maxDifferences = 100, int maxDepth = 32)
    {
        using var left = JsonDocument.Parse(a);
        using var right = JsonDocument.Parse(b);
        return JsonStructuralDiffService.Compare(left.RootElement, right.RootElement,
            maxDifferences, maxDepth);
    }

    [Fact]
    public void IdenticoNonProduceDifferenze()
    {
        var r = Compare("""{"Id":"CR-1","Uid":"a","Flags":[true,false]}""",
            """{"Id":"CR-1","Uid":"a","Flags":[true,false]}""");
        Assert.Empty(r.Items);
        Assert.False(r.Truncated);
        Assert.Contains("Nessuna differenza", r.Details);
    }

    [Fact]
    public void ModificaCampoDescrizioneMostraSoloIlNomeNonIlContenuto()
    {
        var r = Compare("""{"Descrizione":"dato riservato OLD","Id":"A"}""",
            """{"Descrizione":"dato riservato NEW","Id":"A"}""");
        var change = Assert.Single(r.Items);
        Assert.Equal("/Descrizione", change.Path);
        Assert.Equal("Valore modificato", change.Kind);
        Assert.DoesNotContain("riservato", r.Details);
        Assert.DoesNotContain("OLD", r.Details);
        Assert.DoesNotContain("NEW", r.Details);
    }

    [Fact]
    public void OggettoAnnidatoEArraySegnalanoPathPrecisi()
    {
        var r = Compare("""{"Ticket":{"Codice":"A"},"Prove":[{"Esito":"OK"}]}""",
            """{"Ticket":{"Codice":"B"},"Prove":[{"Esito":"KO"}]}""");
        Assert.Contains(r.Items, x => x.Path == "/Ticket/Codice");
        Assert.Contains(r.Items, x => x.Path == "/Prove/0/Esito");
        Assert.Equal(2, r.Items.Count);
    }

    [Fact]
    public void OggettiConCampiAggiuntiORimossiDistinguonoIlLato()
    {
        var r = Compare("""{"SoloBozza":1}""", """{"SoloLocale":2}""");
        Assert.Contains(r.Items, x => x.Path == "/SoloBozza" && x.Kind == "Solo bozza");
        Assert.Contains(r.Items, x => x.Path == "/SoloLocale" && x.Kind == "Solo documento locale");
    }

    [Fact]
    public void JsonPointerEscape_NonInterpretaCaratteriSpecialiComePercorso()
    {
        var r = Compare("""{"a/b~c":1}""", """{"a/b~c":2}""");
        Assert.Equal("/a~1b~0c", Assert.Single(r.Items).Path);
    }

    [Fact]
    public void LimiteDifferenzeRiduceOutputEIndicaTroncamento()
    {
        var r = Compare("""[0,0,0,0,0,0]""", """[1,1,1,1,1,1]""",
            maxDifferences: 3);
        Assert.Equal(3, r.Items.Count);
        Assert.True(r.Truncated);
        Assert.Contains("troncato", r.Details);
    }

    [Fact]
    public void LimiteProfonditaEvitaRicorsioneArbitraria()
    {
        var r = Compare("""{"A":{"B":{"C":1}}}""",
            """{"A":{"B":{"C":2}}}""", maxDepth: 2);
        Assert.Equal("Profondità massima", Assert.Single(r.Items).Kind);
        Assert.Equal("/A/B", r.Items[0].Path);
    }

    [Fact]
    public void ArrayIncompletiNonVengonoConfusiConModificaEsito()
    {
        var r = Compare("""{"Prove":[1,2]}""", """{"Prove":[1,2,3]}""");
        Assert.Equal("/Prove/2", Assert.Single(r.Items).Path);
        Assert.Equal("Solo documento locale", r.Items[0].Kind);
    }

    [Fact]
    public void TipiDifferentiSonoUnaSingolaDifferenza()
    {
        var r = Compare("""{"Priorita":"Alta"}""", """{"Priorita":5}""");
        Assert.Equal("Tipo modificato", Assert.Single(r.Items).Kind);
    }

    [Fact]
    public void IdenticoProfondoNonVieneSegnalatoComeDifferente()
    {
        var json = """{"A":{"B":{"C":{"D":9}}}}""";
        var report = Compare(json, json, maxDepth: 2);
        Assert.False(report.HasDifferences);
        Assert.False(report.Truncated);
    }

    [Fact]
    public void ChiaviJsonDuplicateSonoSegnalateSenzaLancioEccezioni()
    {
        var report = Compare("""{"Titolo":"Uno","Titolo":"Due"}""",
            """{"Titolo":"Uno"}""");
        Assert.Contains(report.Items, x => x.Kind == "Chiavi JSON duplicate");
    }

    [Fact]
    public void ChiaveMoltoLungaTroncaSoloLaDiagnostica()
    {
        var key = new string('X', 500);
        var left = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "prima" });
        var right = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "dopo" });

        var report = Compare(left, right);

        var row = Assert.Single(report.Items);
        Assert.True(row.Path.Length < 300);
        Assert.Contains("percorso troncato", row.Path);
        Assert.Equal("Valore modificato", row.Kind);
    }

    [Fact]
    public void LimitiInvalidiVengonoRifiutati()
    {
        using var a = JsonDocument.Parse("{}");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JsonStructuralDiffService.Compare(a.RootElement, a.RootElement, maxDifferences: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JsonStructuralDiffService.Compare(a.RootElement, a.RootElement, maxDepth: 0));
    }
}
