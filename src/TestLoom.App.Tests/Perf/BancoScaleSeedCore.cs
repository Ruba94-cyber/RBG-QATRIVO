using TestLoom.App.Models;
using TestLoom.App.Services;
using Xunit.Abstractions;

namespace TestLoom.App.Tests.Perf;

/// <summary>
/// Nucleo riusabile estratto da <see cref="DemoDataScaleSeederTests"/> (Piano 6.4.0, banco di prova a piena
/// scala 2026-09-25): porta i cataloghi di sistema di un workspace già aperto (<see cref="AppState"/>) a
/// ~1000 voci ciascuno e il catalogo RevisioniUdt a 100k voci, usando esclusivamente i repository reali
/// (AppState.Master.*, stesso percorso di codice della UI). Comportamento identico a quello storico di
/// DemoDataScaleSeederTests: idempotente (non aggiunge oltre il target), righe aggiunte etichettate "-Scale-".
/// Usato sia da DemoDataScaleSeederTests (banco DemoData esistente) sia da
/// <see cref="BancoPerf100kGeneratoreTests"/> (banco sintetico nuovo a piena scala).
/// </summary>
public static class BancoScaleSeedCore
{
    public const int TargetCatalogoDefault = 1000;
    public const int TargetRevisioniUdtDefault = 100_000;

    public static void SeedCataloghiEWorkingUnit(AppState state, string cartella, ITestOutputHelper output,
        int targetCatalogo = TargetCatalogoDefault, int targetRevisioniUdt = TargetRevisioniUdtDefault)
    {
        SeedNomeSemplice(state.Master.Componente, "Componente-Scale", targetCatalogo, n => new Componente { Nome = n }, output);
        SeedNomeSemplice(state.Master.Gamma, "Gamma-Scale", targetCatalogo, n => new Gamma { Nome = n }, output);
        SeedNomeSemplice(state.Master.Categorie, "Categoria-Scale", targetCatalogo, n => new Categoria { Nome = n }, output);
        var gammaFallback = state.Master.Gamma.Load().Select(g => g.Nome).FirstOrDefault() ?? "";
        SeedNomeSemplice(state.Master.Veicoli, "Veicolo-Scale", targetCatalogo,
            n => new Veicolo { Nome = n, GammaNome = gammaFallback }, output);
        SeedNomeSemplice(state.Master.Strumenti, "Strumento-Scale", targetCatalogo,
            n => new StrumentoDiagnostico { Nome = n }, output);
        SeedNomeSemplice(state.Master.Operatori, "Operatore-Scale", targetCatalogo, n => new Operatore
        {
            Nome = n,
            // Iniziali intenzionalmente vuote: PhaseDMasterBusinessKeyService usa Iniziali come chiave business
            // dell'Operatore quando valorizzata (fallback su Nome altrimenti) — un'unica sigla costante su
            // 1000 righe produrrebbe PhaseDMasterBusinessKeyConflictException al primo Save (bug scoperto dal
            // primo run reale a piena scala di questo seeder, mai eseguito prima a 1000 voci). Nome è già
            // unico per costruzione (prefisso+indice), quindi il fallback basta.
            Ruolo = RuoloOperatore.Viewer,
            PinHash = PinService.Hash("0000"),
            RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()),
            ModificatoDa = "ScaleSeeder",
        }, output);
        SeedIndice(state.Master.Requisiti, "Requisito-Scale", targetCatalogo, i => new Requisito
        {
            Id = $"REQ-SCALE-{i:D5}",
            Testo = $"Requisito fittizio di scala #{i}: descrizione sintetica generata dal seeder di collaudo prestazioni.",
            ApplicabileBanco = true,
            ApplicabileVeicolo = true,
        }, output);
        SeedNomeSemplice(state.Master.Funzionalita, "Funzionalita-Scale", targetCatalogo,
            n => new Funzionalita { Nome = n, TutteLeEcu = true }, output);
        SeedIndice(state.Master.CampiSistemaPianificazione, "CampoPianificazione-Scale", targetCatalogo, i => new CampoSistemaDefinizione
        {
            Chiave = $"scale-pian-{i:D5}",
            Etichetta = $"Campo pianificazione scala #{i}",
            Gruppo = GruppoCampoSistema.Pianificazione,
            Tipo = TipoCampoStrumento.Testo,
        }, output);
        SeedIndice(state.Master.CampiSistemaDettagli, "CampoDettagli-Scale", targetCatalogo, i => new CampoSistemaDefinizione
        {
            Chiave = $"scale-dett-{i:D5}",
            Etichetta = $"Campo dettagli scala #{i}",
            Gruppo = GruppoCampoSistema.Dettagli,
            Tipo = TipoCampoStrumento.Testo,
        }, output);
        SeedIndice(state.Master.TestTemplate, "Template-Scale", targetCatalogo, i => new TestTemplate
        {
            Nome = $"Template-Scale-{i:D5}",
            Ordine = 1000 + i,
        }, output);

        SeedRevisioniUdt(state, cartella, output, targetRevisioniUdt);
    }

    public static void SeedNomeSemplice<T>(MasterDataRepository<T> repo, string prefisso, int target, Func<string, T> crea,
        ITestOutputHelper output)
        where T : MasterEntity
    {
        var lista = repo.Load();
        var esistenti = lista.Count;
        if (esistenti >= target)
        {
            output.WriteLine($"{typeof(T).Name}: già {esistenti} >= {target}, nessuna aggiunta.");
            return;
        }
        var daAggiungere = target - esistenti;
        for (var i = 1; i <= daAggiungere; i++)
        {
            var nome = $"{prefisso}-{i:D5}";
            var entita = crea(nome);
            entita.ModificatoDa = "ScaleSeeder";
            entita.UltimaModifica = DateTime.UtcNow;
            lista.Add(entita);
        }
        repo.Save(lista);
        output.WriteLine($"{typeof(T).Name}: {esistenti} -> {lista.Count} (target {target}).");
    }

    public static void SeedIndice<T>(MasterDataRepository<T> repo, string prefisso, int target, Func<int, T> crea,
        ITestOutputHelper output)
        where T : MasterEntity
    {
        var lista = repo.Load();
        var esistenti = lista.Count;
        if (esistenti >= target)
        {
            output.WriteLine($"{typeof(T).Name}: già {esistenti} >= {target}, nessuna aggiunta.");
            return;
        }
        var daAggiungere = target - esistenti;
        for (var i = 1; i <= daAggiungere; i++)
        {
            var entita = crea(i);
            entita.ModificatoDa = "ScaleSeeder";
            entita.UltimaModifica = DateTime.UtcNow;
            lista.Add(entita);
        }
        repo.Save(lista);
        output.WriteLine($"{typeof(T).Name}: {esistenti} -> {lista.Count} (target {target}).");
    }

    /// <summary>RevisioneUdt normalmente nasce da sola quando un test con un valore Pacchetto viene salvato
    /// (RevisioneUdtService.Sincronizza) — qui la scriviamo direttamente col repository reale (stessa API usata
    /// da quella sincronizzazione) per raggiungere il target senza dover generare N salvataggi di Test. Chiave
    /// nello stesso formato "Nome - Rev. Versione" atteso da RevisioneUdtService/WuStatusView.</summary>
    public static void SeedRevisioniUdt(AppState state, string cartella, ITestOutputHelper output, int targetRevisioniUdt)
    {
        var repo = state.Master.RevisioniUdt;
        var lista = repo.Load();
        var esistenti = lista.Count;
        if (esistenti >= targetRevisioniUdt)
        {
            output.WriteLine($"RevisioneUdt: già {esistenti} >= {targetRevisioniUdt}, nessuna aggiunta.");
            return;
        }
        var daAggiungere = targetRevisioniUdt - esistenti;
        var esistentiChiavi = new HashSet<string>(lista.Select(r => r.WorkingUnitRev), StringComparer.OrdinalIgnoreCase);
        var scritte = 0;
        for (var i = 1; i <= daAggiungere; i++)
        {
            if (i % 20000 == 0) VerificaSpazioLibero(cartella);
            var chiave = ChiaveRevisioneUdtScale(i);
            if (!esistentiChiavi.Add(chiave)) continue;
            lista.Add(new RevisioneUdt
            {
                WorkingUnitRev = chiave,
                StatoManuale = (i % 37 == 0) ? StatoManualeWorkingUnit.Problemi
                    : (i % 11 == 0) ? StatoManualeWorkingUnit.Ok : StatoManualeWorkingUnit.Nessuno,
                NotaManuale = "",
                ModificatoDa = "ScaleSeeder",
                UltimaModifica = DateTime.UtcNow,
            });
            scritte++;
        }
        repo.Save(lista);
        output.WriteLine($"RevisioneUdt: {esistenti} -> {lista.Count} (+{scritte}, target {targetRevisioniUdt}).");
    }

    /// <summary>Stesso formato usato da SeedRevisioniUdt sopra e dal generatore di Test a piena scala, per
    /// tenere Test.ValoriStrumenti (campi "UnitLogger::Pacchetto"/"UnitLogger::Versione Pacchetto") e catalogo
    /// RevisioniUdt coerenti tra loro senza duplicare la formula in due punti.</summary>
    public static string NomeWorkingUnitScale(int i) => $"WU-Scale-{i:D6}";
    public static string VersioneWorkingUnitScale(int i) => ((i % 500) + 1).ToString();
    public static string ChiaveRevisioneUdtScale(int i) => $"{NomeWorkingUnitScale(i)} - Rev. {VersioneWorkingUnitScale(i)}";

    public static void VerificaSpazioLibero(string cartella, long sogliaByte = 6L * 1024 * 1024 * 1024)
    {
        var radice = Path.GetPathRoot(Path.GetFullPath(cartella));
        if (string.IsNullOrEmpty(radice)) return;
        var drive = new DriveInfo(radice);
        Assert.True(drive.AvailableFreeSpace >= sogliaByte,
            $"Spazio libero su {radice} sceso a {drive.AvailableFreeSpace / 1024 / 1024} MB (soglia {sogliaByte / 1024 / 1024} MB): seed interrotto per sicurezza.");
    }
}
