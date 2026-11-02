using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

public sealed class DtcRegistroQueryTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_DtcRegistro_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static Condizione Op(string? requisito, string guasto, PassoDtc passo, Esito esito, string veicolo, string componente = "ECM") => new()
    {
        Guasto = guasto,
        DtcRequisitoId = requisito,
        DtcPasso = passo,
        DtcVeicolo = veicolo,
        Esito = esito,
        Componente = new EcuSelection(new[] { componente })
    };

    private static TestCase Test(string id, int ora, string veicolo, params Condizione[] ops)
    {
        var tc = new TestCase
        {
            Id = id, Uid = Guid.NewGuid(), DataCreazione = Day, UltimaModifica = Day,
            ModificatoDa = "tester", Componente = new EcuSelection(new[] { "ECM" }),
            Veicoli = [veicolo], Veicolo = veicolo
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = Day.AddHours(ora), Operatore = "tester",
            Esito = Esito.Conforme, Condizioni = ops.ToList()
        });
        return tc;
    }

    private LocalTestIndexStore NuovoIndice(params TestCase[] tests)
    {
        var store = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        foreach (var tc in tests) store.Upsert(Path.Combine(_root, tc.Id + ".json"), Day, 100, tc);
        return store;
    }

    [Fact]
    public void Registro_IncludePassiPianificatiENonCollegatiAlRequisito()
    {
        var store = NuovoIndice(
            Test("T-01", 1, "V1",
                Op("R1", "P100", PassoDtc.Validazione, Esito.Conforme, "V1"),
                Op(null, "P200", PassoDtc.Devalidazione, Esito.DaEseguire, "V1"),
                Op("R1", "P300", PassoDtc.Validazione, Esito.NonApplicabile, "V1")));

        var page = new LocalDtcRegistroQueryService(store.DatabasePath).LeggiPagina(new(), 1, 20);

        Assert.Equal(3, page.Totale);
        Assert.Contains(page.Righe, r => r.Guasto == "P200" && r.Requisito.Length == 0 && r.Passo == PassoDtc.Devalidazione);
        Assert.Contains(page.Righe, r => r.Guasto == "P200" && r.Esito == Esito.DaEseguire);
        Assert.Contains(page.Righe, r => r.Guasto == "P300" && r.Esito == Esito.NonApplicabile);
    }

    [Fact]
    public void Registro_ApplicaFiltriVeicoloPassoProblemiSenzaRequisitoETesto()
    {
        var store = NuovoIndice(
            Test("T-V1", 1, "V1",
                Op("R1", "P100", PassoDtc.Validazione, Esito.Conforme, "V1"),
                Op(null, "P200", PassoDtc.Devalidazione, Esito.NonConforme, "V1")),
            Test("T-V2", 2, "V2",
                Op("R2", "P200", PassoDtc.Validazione, Esito.ConformeConDeviazioni, "V2")));
        var query = new LocalDtcRegistroQueryService(store.DatabasePath);

        var problemi = query.LeggiPagina(new(SoloProblemi: true), 1, 20);
        Assert.Equal(2, problemi.Totale);
        Assert.All(problemi.Righe, r => Assert.True(r.HaProblemi));

        var senzaRequisito = query.LeggiPagina(new(SoloSenzaRequisito: true), 1, 20);
        Assert.Single(senzaRequisito.Righe);
        Assert.Equal("P200", senzaRequisito.Righe[0].Guasto);

        var vehicleStep = query.LeggiPagina(new(Veicolo: "V1", Passo: PassoDtc.Devalidazione), 1, 20);
        Assert.Single(vehicleStep.Righe);
        Assert.Equal("T-V1", vehicleStep.Righe[0].TestId);

        var byText = query.LeggiPagina(new(Testo: "ECM"), 1, 20);
        Assert.Equal(3, byText.Totale);
    }

    [Fact]
    public void Registro_PaginaEdEscludeOperazioniNonEffettiveETestEliminati()
    {
        var first = Test("T-01", 1, "V1",
            Op("R1", "P001", PassoDtc.Validazione, Esito.Conforme, "V1"),
            Op("R1", "P002", PassoDtc.Validazione, Esito.Conforme, "V1"));
        first.Eliminato = true;
        var sostituita = Op("R1", "P099", PassoDtc.Validazione, Esito.NonConforme, "V1");
        var corrente = Op("R1", "P100", PassoDtc.Validazione, Esito.Conforme, "V1");
        var uidOperazione = Guid.NewGuid();
        sostituita.OperazioneOrigineUid = uidOperazione;
        corrente.OperazioneOrigineUid = uidOperazione;
        var store = NuovoIndice(
            first,
            Test("T-02", 2, "V1",
                sostituita, corrente,
                Op("R1", "P200", PassoDtc.Validazione, Esito.Conforme, "V1"),
                Op("R1", "P300", PassoDtc.Validazione, Esito.Conforme, "V1")));
        var query = new LocalDtcRegistroQueryService(store.DatabasePath);

        var prima = query.LeggiPagina(new(), 1, 2);
        var seconda = query.LeggiPagina(new(), 2, 2);

        Assert.Equal(3, prima.Totale);
        Assert.Equal(2, prima.Righe.Count);
        Assert.Single(seconda.Righe);
        Assert.DoesNotContain(prima.Righe.Concat(seconda.Righe), r => r.TestId == "T-01");
        Assert.DoesNotContain(prima.Righe.Concat(seconda.Righe), r => r.Guasto == "P099");
        Assert.Equal(3, prima.Righe.Concat(seconda.Righe).Select(r => r.Guasto).Distinct().Count());
    }
}
