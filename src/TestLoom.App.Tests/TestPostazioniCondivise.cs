using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Apre una "postazione" di test (AppState con config locale isolato) sulla stessa cartella condivisa:
/// più postazioni sulla stessa cartella simulano più PC con indici locali indipendenti.</summary>
internal static class TestPostazioniCondivise
{
    public static AppState Apri(string cartellaCondivisa, string cartellaLocale)
    {
        Directory.CreateDirectory(cartellaCondivisa);
        var configService = new AppConfigService(Path.Combine(cartellaLocale, "config.json"));
        configService.Save(new AppConfig { CartellaCondivisa = cartellaCondivisa, Lingua = "it", OperatoreAttivo = "Master Prova" });
        var stato = new AppState(configService, runBackgroundMaintenance: false);
        stato.InizializzaCartella(cartellaCondivisa);

        // La prima postazione crea gli operatori; le successive trovano quelli già presenti nella cartella condivisa.
        var operatori = stato.Master.Operatori.Load().ToList();
        if (!operatori.Any(o => o.Nome == "Master Prova"))
        {
            operatori.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") }));
            stato.Master.Operatori.Save(operatori);
        }
        Assert.True(stato.ImpostaOperatore("Master Prova", "1234"));
        return stato;
    }
}
