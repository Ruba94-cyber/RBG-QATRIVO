using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class TestRevisionSafetyTests
{
    [Fact]
    public void RimozioneDiTutteLeRevisioni_VieneBloccata()
    {
        var persisted = TestConDueCampiBase();
        var candidate = Clone(persisted);
        candidate.Esecuzioni.Clear();

        Assert.Throws<InvalidDataException>(() =>
            TestRevisionSafety.PreserveImmutableHistory(persisted, candidate, DateTime.Now, "Tester"));
        Assert.Single(persisted.Esecuzioni);
    }

    [Fact]
    public void ModificaEcuOperazione_NonSovrascriveRevCorrente_MaNeCreaUnaNuova()
    {
        var persisted = TestConDueCampiBase();
        var candidate = Clone(persisted);
        candidate.Esecuzioni[0].Condizioni[0].Componente = new EcuSelection(new[] { "ECM" });

        var appended = TestRevisionSafety.PreserveImmutableHistory(persisted, candidate,
            new DateTime(2026, 9, 4, 10, 0, 0), "Tester");

        Assert.True(appended);
        Assert.Equal(2, candidate.Esecuzioni.Count);
        Assert.Equal("BCM", candidate.Esecuzioni[0].Condizioni[0].Componente.Single());
        Assert.Equal("ECM", candidate.Esecuzioni[1].Condizioni[0].Componente.Single());
        Assert.Equal(2, candidate.Esecuzioni[1].Iterazione);
    }

    [Fact]
    public void ModificaNoteOPunteggio_CreaNuovaRev()
    {
        var persisted = TestConDueCampiBase();
        persisted.Esecuzioni[0].PunteggiRequisiti.Add(new PunteggioRequisito { RequisitoId = "REQ-1", Valore = PunteggioConformita.Conforme });
        var candidate = Clone(persisted);
        candidate.Esecuzioni[0].Note = "Nuova nota tecnica";
        candidate.Esecuzioni[0].PunteggiRequisiti[0].Valore = PunteggioConformita.ParzialmenteConforme;

        Assert.True(TestRevisionSafety.PreserveImmutableHistory(persisted, candidate, DateTime.Now, "Tester"));
        Assert.Equal(2, candidate.Esecuzioni.Count);
        Assert.Equal("", candidate.Esecuzioni[0].Note);
        Assert.Equal("Nuova nota tecnica", candidate.Esecuzioni[1].Note);
    }

    [Fact]
    public void ModificaRevGiaStorica_VieneBloccata()
    {
        var persisted = TestConDueCampiBase();
        persisted.Esecuzioni.Add(Clone(persisted.Esecuzioni[0]));
        persisted.Esecuzioni[1].Iterazione = 2;
        var candidate = Clone(persisted);
        candidate.Esecuzioni[0].Note = "tentativo di riscrivere la storia";

        var ex = Assert.Throws<InvalidDataException>(() =>
            TestRevisionSafety.PreserveImmutableHistory(persisted, candidate, DateTime.Now, "Tester"));

        Assert.Contains("storica", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NuovaRevGiaCreataDalViewModel_NonVieneDuplicata()
    {
        var persisted = TestConDueCampiBase();
        var candidate = Clone(persisted);
        var nuova = Clone(candidate.Esecuzioni[0]);
        nuova.Iterazione = 2;
        nuova.Note = "nuova revisione già preparata";
        candidate.Esecuzioni.Add(nuova);

        var appended = TestRevisionSafety.PreserveImmutableHistory(persisted, candidate, DateTime.Now, "Tester");

        Assert.False(appended);
        Assert.Equal(2, candidate.Esecuzioni.Count);
    }

    private static TestCase TestConDueCampiBase() => new()
    {
        Id = "TC-REV-SAFE",
        Componente = new EcuSelection(new[] { "BCM" }),
        Veicolo = "Daily",
        Funzionalita = new List<string> { "Diagnosi" },
        Descrizione = "Base",
        Esecuzioni = new List<Esecuzione>
        {
            new()
            {
                Iterazione = 1,
                Operatore = "Tester",
                Data = new DateTime(2026, 9, 1),
                Condizioni = new List<Condizione>
                {
                    new() { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Diagnosi", Esito = Esito.Conforme }
                }
            }
        }
    };

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonIo.Options), JsonIo.Options)!;

    [Fact]
    public void AmmettiRiscritturaRiferimentiCatalogo_UnicoChiamanteInSrc_EMasterUnioneService()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "TestLoom.App", "Services"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var chiamanti = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "TestLoom.App"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("AmmettiRiscritturaRiferimentiCatalogo()"))
            .Select(Path.GetFileName).OrderBy(n => n).ToList();
        // Definizione + unico chiamante: l'eccezione alla guardia delle REV non deve diffondersi.
        Assert.Equal(new[] { "MasterUnioneService.cs", "TestRevisionSafety.cs" }, chiamanti);
    }
}
