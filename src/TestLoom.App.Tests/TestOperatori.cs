using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Helper condiviso per creare operatori di test con il ruolo correttamente sigillato (piano
/// 6.4.2, §4b: il ruolo effettivo di un operatore senza sigillo valido ricade su Viewer —
/// <see cref="RuoloSigilloService.RuoloEffettivo"/>). Una fixture che scrive un operatore Master/Standard
/// senza passare da <see cref="RuoloSigilloService.Sigilla"/> crea, di fatto, un Viewer: questo helper
/// evita di ripetere quella dimenticanza in ogni classe di test.
/// Non usare questo helper nei test che verificano DELIBERATAMENTE il comportamento con un sigillo
/// assente/manomesso (es. AccessoRuoloSigillatoTests, OperatoriSicurezzaTests): in quei casi il sigillo
/// mancante è il punto del test.</summary>
internal static class TestOperatori
{
    /// <summary>Sigilla e ritorna lo stesso operatore, per poterlo usare in linea (es. dentro un
    /// inizializzatore di lista) senza spezzare la costruzione su più istruzioni.</summary>
    public static Operatore Sigilla(Operatore operatore)
    {
        RuoloSigilloService.Sigilla(operatore);
        return operatore;
    }

    /// <summary>Crea un operatore di test già sigillato con il ruolo richiesto.</summary>
    public static Operatore Crea(string nome, RuoloOperatore ruolo, string pin = "1234", string modificatoDa = "Test")
    {
        var op = new Operatore { Nome = nome, Ruolo = ruolo, PinHash = PinService.Hash(pin), PinChiaro = pin, ModificatoDa = modificatoDa };
        RuoloSigilloService.Sigilla(op);
        return op;
    }
}
