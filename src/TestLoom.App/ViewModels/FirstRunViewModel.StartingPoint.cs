using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.Setup;

namespace TestLoom.App.ViewModels;

/// <summary>
/// First run, empty data folder: choose the kind of testing and get its first test templates. It only adds templates to the
/// folder that was chosen, exactly like the sample-data buttons next to it, and never touches an existing workspace.
/// </summary>
public partial class FirstRunViewModel
{
    public sealed record PuntoDiPartenza(string Id, string Nome, string Descrizione)
    {
        public override string ToString() => Nome;
    }

    private static string Testo(string it, string en, string fr) => LocalizationService.Instance.Lingua switch
    {
        "it" => it,
        "fr" => fr,
        _ => en,
    };

    private static PuntoDiPartenza Descrivi(TestLoomWorkspacePreset preset) => preset.Id switch
    {
        "software-qa" => new(preset.Id, Testo("Qualità software", "Software QA", "Qualité logicielle"),
            Testo("Applicazioni e moduli: prove di fumo, regressione e compatibilità.",
                  "Applications and modules: smoke, regression and compatibility tests.",
                  "Applications et modules : tests de fumée, de régression et de compatibilité.")),
        "hardware" => new(preset.Id, Testo("Ingegneria hardware", "Hardware engineering", "Ingénierie matérielle"),
            Testo("Dispositivi e componenti: verifica funzionale, durata e sicurezza.",
                  "Devices and components: functional verification, endurance and safety.",
                  "Appareils et composants : vérification fonctionnelle, endurance et sécurité.")),
        "manufacturing" => new(preset.Id, Testo("Qualità di produzione", "Manufacturing quality", "Qualité de production"),
            Testo("Prodotti e assemblaggi: controllo in ingresso, in corso d'opera e finale.",
                  "Products and assemblies: incoming, in-process and final inspection.",
                  "Produits et assemblages : contrôle à la réception, en cours et final.")),
        "laboratory" => new(preset.Id, Testo("Laboratorio di ricerca", "Research laboratory", "Laboratoire de recherche"),
            Testo("Campioni e parametri: verifica di procedura, serie di misure e ripetibilità.",
                  "Samples and parameters: procedure checks, measurement series and repeatability.",
                  "Échantillons et paramètres : vérification de procédure, séries de mesures et répétabilité.")),
        _ => new(preset.Id, Testo("Area vuota", "Blank workspace", "Espace vide"),
            Testo("Un solo modello di test generico da personalizzare.",
                  "A single generic test template to customise.",
                  "Un seul modèle de test générique à personnaliser.")),
    };

    public IReadOnlyList<PuntoDiPartenza> PuntiDiPartenza { get; } = TestLoomWorkspacePresets.All.Select(Descrivi).ToList();

    [ObservableProperty] private PuntoDiPartenza? puntoDiPartenzaSelezionato;

    partial void OnPuntoDiPartenzaSelezionatoChanged(PuntoDiPartenza? value) => OnPropertyChanged(nameof(DescrizionePuntoDiPartenza));

    public string TitoloPuntoDiPartenza => Testo("Punto di partenza per il tipo di test", "Starting point for your kind of testing", "Point de départ selon le type de test");

    public string TestoPuntoDiPartenza => Testo(
        "Scegli il tipo di test: ti prepariamo i primi modelli di test. Si possono modificare, archiviare o sostituire in seguito.",
        "Choose your kind of testing and we prepare the first test templates. You can edit, archive or replace them later.",
        "Choisissez le type de test : nous préparons les premiers modèles de test. Vous pourrez les modifier, les archiver ou les remplacer.");

    public string DescrizionePuntoDiPartenza => PuntoDiPartenzaSelezionato?.Descrizione ?? "";

    public string TestoBottonePuntoDiPartenza => Testo("Crea i modelli di test", "Create the test templates", "Créer les modèles de test");

    [RelayCommand]
    private async Task CreaModelliIniziali()
    {
        if (!PuoInizializzareDemo) return;
        var scelto = PuntoDiPartenzaSelezionato;
        if (scelto is null)
        {
            MessaggioErrore = Testo("Scegli prima un punto di partenza.", "Choose a starting point first.", "Choisissez d'abord un point de départ.");
            return;
        }

        OperazioneDemoInCorso = true;
        MessaggioErrore = null;
        MessaggioDemo = null;
        try
        {
            var cartella = Cartella;
            var creati = await Task.Run(() =>
            {
                if (!Directory.Exists(cartella))
                    throw new DirectoryNotFoundException(Testo("La cartella selezionata non esiste.", "The selected folder does not exist.", "Le dossier sélectionné n'existe pas."));
                if (Directory.EnumerateFileSystemEntries(cartella).Any())
                    throw new InvalidOperationException(Testo("I modelli iniziali si creano solo in una cartella completamente vuota.",
                        "The starting templates can only be created in a completely empty folder.",
                        "Les modèles de départ ne se créent que dans un dossier complètement vide."));
                var modelli = TestLoomTemplateFactory.Create(scelto.Id).ToList();
                new MasterDataRepository<TestTemplate>(new DataPaths(cartella).TestTemplateFile).Save(modelli);
                return modelli.Count;
            });
            MessaggioDemo = string.Format(
                Testo("Creati {0} modelli di test dal punto di partenza «{1}».", "Created {0} test templates from the starting point \"{1}\".",
                      "{0} modèles de test créés à partir du point de départ « {1} »."), creati, scelto.Nome);
        }
        catch (Exception ex)
        {
            MessaggioErrore = ex.Message;
        }
        finally
        {
            OperazioneDemoInCorso = false;
            AggiornaStatoCartella();
        }
    }
}
