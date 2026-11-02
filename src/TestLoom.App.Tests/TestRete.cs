using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Helper dei test della modalità cartella di rete (6.6.1). I test che cambiano stato statico (risolutore delle
/// unità mappate, override di modalità) stanno nella collection "CondivisioneRete" e ripristinano sempre lo stato precedente:
/// nessun test dipende da unità di rete reali o dal PC su cui gira la suite.</summary>
internal static class TestRete
{
    public static string NuovaDirectoria()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_Rete_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>Sostituisce il risolutore delle unità mappate: ogni coppia (lettera, percorso UNC) è un'unità di rete; le
/// altre lettere sono locali. Ripristina il risolutore precedente alla dispose.</summary>
internal sealed class RisolutoreUnitaFinto : IDisposable
{
    private readonly Func<string, DatiUnitaRete?> _precedente;

    public RisolutoreUnitaFinto(params (string Lettera, string? PercorsoUnc)[] unita)
    {
        _precedente = UnitaRete.Risolutore;
        // Esiti memorizzati da un risolutore precedente non devono sopravvivere a questo (P4: memoria per lettera).
        UnitaRete.Reimposta();
        UnitaRete.Risolutore = lettera => unita
            .Where(u => string.Equals(u.Lettera, lettera, StringComparison.OrdinalIgnoreCase))
            .Select(u => (DatiUnitaRete?)new DatiUnitaRete(u.PercorsoUnc))
            .FirstOrDefault();
    }

    public void Dispose()
    {
        UnitaRete.Risolutore = _precedente;
        UnitaRete.Reimposta();
    }
}

/// <summary>Imposta l'override di modalità per la durata del test e ripristina quello precedente alla dispose.</summary>
internal sealed class OverrideModalitaScope : IDisposable
{
    private readonly string? _radice;
    private readonly string _precedente = ModalitaCondivisioneService.OverrideCorrente;

    /// <param name="valore">Modalità da forzare.</param>
    /// <param name="radice">Cartella a cui limitare l'override: obbligatoria nei test in parallelo, così non cambia la modalità
    /// di altre cartelle. Senza radice l'override è globale (solo per test che non girano in parallelo).</param>
    public OverrideModalitaScope(string? valore, string? radice = null)
    {
        _radice = radice;
        if (radice == null) ModalitaCondivisioneService.ImpostaOverride(valore);
        else ModalitaCondivisioneService.ImpostaOverrideRadice(radice, valore);
    }

    public void Dispose()
    {
        if (_radice == null) ModalitaCondivisioneService.ImpostaOverride(_precedente);
        else ModalitaCondivisioneService.ImpostaOverrideRadice(_radice, null);
    }
}
