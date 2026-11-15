using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Confronto/unione a due versioni sul record reale: nessuna scelta implicita, risultato validato dal modello,
/// campi tecnici dalla versione corrente, changelog unito, valori sempre in forma leggibile (mai JSON).</summary>
public sealed class RecordMergeEngineTests
{
    private static byte[] Bytes(NonConformita nc) => JsonSerializer.SerializeToUtf8Bytes(nc, JsonIo.Options);

    private static (byte[] Mia, byte[] Altra, NonConformita Base) Coppia()
    {
        var b = new NonConformita { Id = "NC-M1", Owner = "Base", Descrizione = "KO base", Uid = Guid.NewGuid(), RevisioneDati = 3 };
        var mia = new NonConformita { Id = "NC-M1", Owner = "Anna", Descrizione = "KO base", Uid = b.Uid, RevisioneDati = 5 };
        var altra = new NonConformita { Id = "NC-M1", Owner = "Bruno", Descrizione = "KO descritto meglio", Uid = b.Uid, RevisioneDati = 4 };
        return (Bytes(mia), Bytes(altra), b);
    }

    [Fact]
    public void Prepare_ElencaSoloICampiDiversi_ConEtichetteLeggibili()
    {
        var (mia, altra, _) = Coppia();

        var prep = RecordMergeEngine.Prepare(RecordMergeKind.NonConformita, mia, altra);

        Assert.True(prep.Confrontabile);
        var diffs = prep.Plan!.Differences.Select(d => d.Name).ToList();
        Assert.Contains("Owner", diffs);
        Assert.Contains("Descrizione", diffs);
        Assert.DoesNotContain("Uid", diffs);
        Assert.DoesNotContain("RevisioneDati", diffs);
        var campo = prep.Plan.Fields.Single(f => f.Name == "Descrizione");
        Assert.Equal("Descrizione", campo.Label);
        Assert.Equal("KO descritto meglio", campo.LoroTesto);
        Assert.DoesNotContain("{", campo.MiaTesto + campo.LoroTesto);
        Assert.Equal("Stato workflow", RecordValueFormatter.Humanize("StatoWorkflow"));
    }

    [Fact]
    public void Build_SenzaScelteEsplicite_Rifiuta()
    {
        var (mia, altra, _) = Coppia();
        var plan = RecordMergeEngine.Prepare(RecordMergeKind.NonConformita, mia, altra).Plan!;

        var ex = Assert.Throws<RecordMergeException>(() => plan.Build(new RecordMergeChoices(), "Op"));
        Assert.Contains("Scegliere", ex.Message);
        Assert.False(plan.IsComplete(new RecordMergeChoices()));
    }

    [Fact]
    public void Build_UnioneCampoPerCampo_TecniciDallaCorrenteEChangelogUnito()
    {
        var (mia, altra, _) = Coppia();
        var plan = RecordMergeEngine.Prepare(RecordMergeKind.NonConformita, mia, altra).Plan!;
        var scelte = new RecordMergeChoices();
        scelte.Scelte["Owner"] = RecordFieldChoice.Mia;
        scelte.Scelte["Descrizione"] = RecordFieldChoice.Loro;

        var res = plan.Build(scelte, "Master");

        var nc = Assert.IsType<NonConformita>(res.Model);
        Assert.Equal("Anna", nc.Owner);
        Assert.Equal("KO descritto meglio", nc.Descrizione);
        Assert.Equal(5, nc.RevisioneDati); // dalla versione corrente
        Assert.Contains(nc.Changelog, c => c.Campo.Contains("risoluzione conflitto") && c.Operatore == "Master");
    }

    [Fact]
    public void Build_ValoreManuale_ValidatoEUsato_ENonValidoRifiutato()
    {
        var (mia, altra, _) = Coppia();
        var plan = RecordMergeEngine.Prepare(RecordMergeKind.NonConformita, mia, altra).Plan!;
        var scelte = new RecordMergeChoices();
        scelte.Scelte["Owner"] = RecordFieldChoice.Manuale;
        scelte.ValoriManuali["Owner"] = "Carla";
        scelte.Scelte["Descrizione"] = RecordFieldChoice.Mia;

        Assert.Equal("Carla", ((NonConformita)plan.Build(scelte, "Op").Model!).Owner);
        var anteprima = plan.Preview(scelte);
        Assert.Equal("Manuale", anteprima.Single(r => r.Name == "Owner").Origine);
        Assert.Equal("Carla", anteprima.Single(r => r.Name == "Owner").Testo);

        // Un enum non e' modificabile a mano: solo scelta fra le due versioni.
        var enumField = plan.Fields.FirstOrDefault(f => f.EditKind == RecordEditKind.Nessuno);
        Assert.NotNull(enumField);
    }

    [Fact]
    public void Build_ManualeSuCampoNonModificabile_Errore()
    {
        var (mia, altra, _) = Coppia();
        altra = Bytes(JsonSerializer.Deserialize<NonConformita>(mia, JsonIo.Options)!.Also(n => n.Severita = SeveritaAnomalia.High));
        var plan = RecordMergeEngine.Prepare(RecordMergeKind.NonConformita, mia, altra).Plan!;
        var campo = plan.Differences.Single(d => d.Name == "Severita");
        Assert.Equal(RecordEditKind.Nessuno, campo.EditKind);
        var scelte = new RecordMergeChoices();
        scelte.Scelte["Severita"] = RecordFieldChoice.Manuale;
        scelte.ValoriManuali["Severita"] = "Inventata";

        Assert.Throws<RecordMergeException>(() => plan.Build(scelte, "Op"));
        Assert.False(plan.IsComplete(scelte));
    }

    [Fact]
    public void Prepare_NonConfrontabile_FileCorrottoUidDiversoTombstoneOTipoErrato()
    {
        var (mia, altra, _) = Coppia();

        var corrotto = RecordMergeEngine.Prepare(RecordMergeKind.NonConformita, mia, Encoding.UTF8.GetBytes("{ troncato"));
        Assert.False(corrotto.Confrontabile);
        Assert.Contains("non e' leggibile", corrotto.MotivoNonConfrontabile);

        var altroUid = Bytes(new NonConformita { Id = "NC-M1", Uid = Guid.NewGuid() });
        Assert.Contains("record diversi", RecordMergeEngine.Prepare(RecordMergeKind.NonConformita, mia, altroUid).MotivoNonConfrontabile);

        var tomb = Encoding.UTF8.GetBytes("{ \"" + WorkflowTransactionVisibilityService.DeletedProperty + "\": true, \"Uid\": \"" +
                                          JsonDocument.Parse(mia).RootElement.GetProperty("Uid").GetString() + "\" }");
        Assert.Contains("eliminata", RecordMergeEngine.Prepare(RecordMergeKind.NonConformita, mia, tomb).MotivoNonConfrontabile);

        var tipoErrato = Encoding.UTF8.GetBytes("{ \"Id\": \"NC-M1\", \"Severita\": \"NonEsiste\", \"Uid\": \"" +
                                                JsonDocument.Parse(mia).RootElement.GetProperty("Uid").GetString() + "\" }");
        Assert.False(RecordMergeEngine.Prepare(RecordMergeKind.NonConformita, mia, tipoErrato).Confrontabile);
        Assert.NotNull(altra);
    }

    [Fact]
    public void Problema_IndicaCampoEValoreNonValido_InItalianoChiaro()
    {
        var uid = Guid.NewGuid();
        var rotto = Encoding.UTF8.GetBytes("{ \"Id\": \"NC-P1\", \"Uid\": \"" + uid + "\", \"Priorita\": \"High\" }");

        var problema = RecordMergeEngine.Problema(RecordMergeKind.NonConformita, rotto);

        Assert.NotNull(problema);
        Assert.Contains("Priorità interna", problema);
        Assert.Contains("High", problema);
        Assert.Contains("valore non valido", problema);
        Assert.DoesNotContain("tipo di record (", problema);
        var prep = RecordMergeEngine.Prepare(RecordMergeKind.NonConformita, Bytes(new NonConformita { Id = "NC-P1", Uid = uid }), rotto);
        Assert.False(prep.Confrontabile);
        Assert.Contains("Priorità interna", prep.MotivoNonConfrontabile);
        Assert.Null(RecordMergeEngine.Problema(RecordMergeKind.NonConformita, Bytes(new NonConformita { Id = "NC-P1", Uid = uid })));
    }

    [Fact]
    public void Etichette_UsanoQuelleItalianeDegliEditorConAccenti()
    {
        Assert.Equal("Priorità interna", RecordValueFormatter.Label("Priorita"));
        Assert.Contains("Severità", RecordValueFormatter.Label("Severita"));
        Assert.Equal("Data di apertura", RecordValueFormatter.Label("DataApertura"));
        Assert.Equal("Campo sconosciuto xyz", RecordValueFormatter.Label("CampoSconosciutoXyz"));
    }

    [Fact]
    public void Formatter_ListeEOggetti_SonoTestoLeggibileNonJson()
    {
        var node = JsonSerializer.SerializeToNode(new { Elenco = new[] { "uno", "due" }, Nested = new { A = 1, Flag = true } })!;
        var t1 = RecordValueFormatter.Format(node["Elenco"]);
        var t2 = RecordValueFormatter.Format(node["Nested"]);

        Assert.Equal("• uno\n• due", t1);
        Assert.Contains("A: 1", t2);
        Assert.Contains("Flag: Sì", t2);
        Assert.DoesNotContain("\"", t1 + t2);
    }

    [Fact]
    public void Generico_SenzaModello_UnioneDeiCampi()
    {
        var a = Encoding.UTF8.GetBytes("{\"Uid\":\"11111111-1111-1111-1111-111111111111\",\"Nome\":\"X\",\"Attivo\":true,\"Note\":\"a\"}");
        var b = Encoding.UTF8.GetBytes("{\"Uid\":\"11111111-1111-1111-1111-111111111111\",\"Nome\":\"X\",\"Attivo\":false,\"Note\":\"b\"}");
        var plan = RecordMergeEngine.Prepare(RecordMergeKind.Generico, a, b).Plan!;
        Assert.Equal(RecordEditKind.Booleano, plan.Fields.Single(f => f.Name == "Attivo").EditKind);
        var scelte = new RecordMergeChoices();
        scelte.Scelte["Attivo"] = RecordFieldChoice.Loro;
        scelte.Scelte["Note"] = RecordFieldChoice.Manuale;
        scelte.ValoriManuali["Note"] = "ab";

        var json = plan.Build(scelte, "Op").Json;

        Assert.False(json["Attivo"]!.GetValue<bool>());
        Assert.Equal("ab", json["Note"]!.GetValue<string>());
    }

    [Fact]
    public void ChiaveDiBusiness_NonPuoEssereMescolataOScrittaAMano()
    {
        MasterEntityIdentity();
        var uid = Guid.NewGuid();
        PhaseDMasterMutation Testa(string payload, string key) => new()
        {
            Catalog = "componente", EntityUid = uid, Operation = PhaseDMasterMutationOperation.Upsert, PayloadJson = payload, BusinessKey = key
        };
        var a = Testa("{\"Nome\":\"componente-A\",\"Note\":\"1\"}", "Componente-A");
        var b = Testa("{\"Nome\":\"componente-B\",\"Note\":\"2\"}", "Componente-B");

        var tenutaA = System.Text.Json.Nodes.JsonNode.Parse("{\"Nome\":\"componente-A\",\"Note\":\"2\"}")!.AsObject();
        Assert.Equal("Componente-A", PhaseDMasterConflictResolver.ScegliChiaveDiBusiness(new[] { a, b }, tenutaA));

        var scritta = System.Text.Json.Nodes.JsonNode.Parse("{\"Nome\":\"componente-ZZ\",\"Note\":\"2\"}")!.AsObject();
        Assert.Throws<InvalidOperationException>(() => PhaseDMasterConflictResolver.ScegliChiaveDiBusiness(new[] { a, b }, scritta));
    }

    private static void MasterEntityIdentity() { }
}

internal static class AlsoExt
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
