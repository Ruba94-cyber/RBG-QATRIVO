using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class RequirementD4DTests
{
    [Fact]
    public void LegacyRequirement_DefaultsToStandard()
    {
        var requisito = new Requisito { Id = "STD-1" };
        Assert.Equal(TipoRequisito.Standard, RequisitoApplicabilita.TipoDi(requisito));
        Assert.True(RequisitoApplicabilita.SiApplicaAlSottoveicolo(requisito, "Daily MY26"));
    }

    [Fact]
    public void Extra_AppliesOnlyToConfiguredSubvehicles()
    {
        var requisito = new Requisito { Id = "Extra-1" };
        RequisitoApplicabilita.ImpostaTipo(requisito, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(requisito, new[] { "Daily MY26", "eDaily MY26" });

        Assert.True(RequisitoApplicabilita.SiApplicaAlSottoveicolo(requisito, "Daily MY26"));
        Assert.True(RequisitoApplicabilita.SiApplicaAlSottoveicolo(requisito, "eDaily MY26"));
        Assert.False(RequisitoApplicabilita.SiApplicaAlSottoveicolo(requisito, "Eurocargo MY26"));
    }

    [Fact]
    public void Extra_SenzaSottoveicoli_ApplicaATutti_EUnaSolaUnitaDiCopertura()
    {
        var requisito = new Requisito { Id = "Extra-LIBERO", ApplicabileVeicolo = true };
        RequisitoApplicabilita.ImpostaTipo(requisito, TipoRequisito.Extra);
        Assert.True(RequisitoApplicabilita.SiApplicaAlSottoveicolo(requisito, "Qualsiasi"));
        Assert.True(RequisitoApplicabilita.SiApplicaAlTest(requisito, Test("T-1", "Z", requisito.Id, Esito.Conforme)));

        var bucket = CoverageService.PerRequisito(new[] { requisito },
            new[] { Test("T-1", "Z", requisito.Id, Esito.Conforme) }, false, false);
        Assert.Equal(1, bucket.Totale);
        Assert.Equal(1, bucket.Conformi);
    }

    [Fact]
    public void Extra_SenzaSottoveicoli_ESalvabileSuMasterData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "d4d_facoltativi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var repo = new MasterDataRepository<Requisito>(Path.Combine(dir, "requisiti.json"));
            var r = new Requisito { Id = "Extra-X" };
            RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
            repo.Save(new List<Requisito> { r });
            Assert.Single(repo.Load());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Coverage_D4DCountsOneUnitPerTargetSubvehicle()
    {
        var requisito = new Requisito { Id = "Extra-2", ApplicabileVeicolo = true };
        RequisitoApplicabilita.ImpostaTipo(requisito, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(requisito, new[] { "A", "B" });

        var testA = Test("T-A", "A", requisito.Id, Esito.Conforme);
        var testAltro = Test("T-X", "X", requisito.Id, Esito.NonConforme);

        var bucket = CoverageService.PerRequisito(new[] { requisito }, new[] { testA, testAltro }, false, false);
        Assert.Equal(2, bucket.Totale);
        Assert.Equal(1, bucket.Conformi);
        Assert.Equal(1, bucket.NonTestati);
        Assert.Equal(0, bucket.NonConformi);
    }

    [Fact]
    public void Audit_D4DIsAbsentOutsideItsSubvehicleAndIgnoresTestsFromOtherTargets()
    {
        var requisito = new Requisito { Id = "Extra-3" };
        RequisitoApplicabilita.ImpostaTipo(requisito, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(requisito, new[] { "A", "B" });
        var componente = new Componente { Nome = "Componente1", VeicoliAssociati = new() { "A", "B", "C" } };

        var testA = Test("T-A", "A", requisito.Id, Esito.Conforme);
        testA.Componente = new EcuSelection(new[] { "Componente1" });
        testA.Esecuzioni[0].PunteggiRequisiti.Add(new PunteggioRequisito { RequisitoId = requisito.Id, Valore = PunteggioConformita.Conforme });

        var testB = Test("T-B", "B", requisito.Id, Esito.NonConforme);
        testB.Componente = new EcuSelection(new[] { "Componente1" });
        testB.Esecuzioni[0].PunteggiRequisiti.Add(new PunteggioRequisito { RequisitoId = requisito.Id, Valore = PunteggioConformita.NonConforme });

        var a = AuditMatriceService.Matrice("A", new[] { componente }, new[] { requisito }, new[] { testA, testB });
        var cellA = Assert.Single(a);
        Assert.Equal(StatoCellaAudit.Valutato, cellA.Stato);
        Assert.Equal(PunteggioConformita.Conforme, cellA.Punteggio);

        var c = AuditMatriceService.Matrice("C", new[] { componente }, new[] { requisito }, new[] { testA, testB });
        Assert.Empty(c);
    }

    private static TestCase Test(string id, string vehicle, string requirement, Esito esito)
    {
        return new TestCase
        {
            Id = id,
            Uid = Guid.NewGuid(),
            Veicoli = new() { vehicle },
            Veicolo = vehicle,
            RequisitoId = new() { requirement },
            Esecuzioni = new()
            {
                new Esecuzione
                {
                    Iterazione = 1,
                    Data = DateTime.Now,
                    Operatore = "op",
                    Esito = esito,
                    EsitoSovrascritto = true
                }
            }
        };
    }
}
