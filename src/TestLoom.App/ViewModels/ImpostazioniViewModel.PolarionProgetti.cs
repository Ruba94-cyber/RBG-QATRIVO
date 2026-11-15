using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Riga dell'editor dei progetti Ticketing (Impostazioni, scheda Ticketing): ID tecnico, nome e ultimo esito della sonda.</summary>
public sealed partial class PolarionProgettoRiga : ObservableObject
{
    [ObservableProperty] private string id = "";
    [ObservableProperty] private string nome = "";
    [ObservableProperty] private string esitoVerifica = "";
    [ObservableProperty] private bool esitoErrore;
    [ObservableProperty] private bool inVerifica;

    /// <summary>Messaggio di validazione dell'ID (vuoto = valido o riga ancora vuota).</summary>
    public string ErroreId => Id.Trim().Length > 0 && !PolarionReadClient.IsValidProjectId(Id.Trim())
        ? LocalizationService.Instance.Scegli(
            "ID non valido: iniziare con una lettera, poi solo lettere, cifre o _.",
            "Invalid ID: start with a letter, then only letters, digits or _.",
            "ID non valide : commencer par une lettre, puis uniquement lettres, chiffres ou _.")
        : "";
    public bool HaErroreId => ErroreId.Length > 0;
    public bool PuoVerificare => Id.Trim().Length > 0 && !HaErroreId && !InVerifica;

    partial void OnIdChanged(string value)
    {
        EsitoVerifica = ""; EsitoErrore = false;
        OnPropertyChanged(nameof(ErroreId)); OnPropertyChanged(nameof(HaErroreId)); OnPropertyChanged(nameof(PuoVerificare));
    }
    partial void OnInVerificaChanged(bool value) => OnPropertyChanged(nameof(PuoVerificare));
}

public partial class ImpostazioniViewModel
{
    /// <summary>Progetti Ticketing aggiuntivi all'TICKET (es. quelli dei requisiti), modificabili dal solo Master.</summary>
    public ObservableCollection<PolarionProgettoRiga> PolarionProgetti { get; } = new();

    public string PolarionProgettoEstaEtichetta =>
        PolarionText("Progetto TICKET predefinito (Test, NC, CR)", "Default TICKET project (Tests, NCs, CRs)", "Projet TICKET par défaut (tests, NC, CR)") +
        ": " + (string.IsNullOrWhiteSpace(_polarionApiLoaded.ProjectId) ? "TICKET_Tickets" : _polarionApiLoaded.ProjectId);

    public string PolarionProgettiTitolo => PolarionText("Progetti Ticketing per i requisiti", "Ticketing projects for requirements", "Projets Ticketing pour les exigences");
    public string PolarionProgettiNota => PolarionText(
        "Elenco dei progetti Ticketing selezionabili nel collegamento di un requisito. L'TICKET resta il progetto predefinito per Test, NC e CR. La verifica usa la sola lettura.",
        "Ticketing projects selectable when linking a requirement. TICKET remains the default project for Tests, NCs and CRs. The check is read-only.",
        "Projets Ticketing sélectionnables pour lier une exigence. L'TICKET reste le projet par défaut des tests, NC et CR. La vérification est en lecture seule.");
    public string PolarionProgettoAggiungiLabel => PolarionText("Aggiungi progetto", "Add project", "Ajouter un projet");
    public string PolarionProgettoVerificaLabel => PolarionText("Verifica", "Check", "Vérifier");
    public string PolarionProgettoRimuoviLabel => PolarionText("Rimuovi", "Remove", "Retirer");
    public string PolarionProgettoIdPlaceholder => PolarionText("ID progetto (es. REQ_PROJ)", "Project ID (e.g. REQ_PROJ)", "ID du projet (ex. REQ_PROJ)");
    public string PolarionProgettoNomePlaceholder => PolarionText("Nome leggibile", "Readable name", "Nom lisible");

    private void CaricaProgettiPolarion()
    {
        PolarionProgetti.Clear();
        foreach (var p in _polarionApiLoaded.Progetti ?? new List<PolarionProgettoConfig>())
            PolarionProgetti.Add(NuovaRigaProgetto(p.Id, p.Nome));
        OnPropertyChanged(nameof(PolarionProgettoEstaEtichetta));
    }

    private PolarionProgettoRiga NuovaRigaProgetto(string id, string nome)
    {
        var riga = new PolarionProgettoRiga { Id = id, Nome = nome };
        // Digitare nell'editor aggiorna subito lo stato "modifiche non salvate".
        riga.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PolarionProgettoRiga.Id) or nameof(PolarionProgettoRiga.Nome))
                OnPropertyChanged(nameof(PolarionApiPendingStatus));
        };
        return riga;
    }

    /// <summary>Elenco normalizzato delle righe dell'editor (righe vuote scartate). Lancia InvalidOperationException se non valido.</summary>
    internal List<PolarionProgettoConfig>? ProgettiPolarionDaSalvare() =>
        PolarionApiSettingsService.NormalizzaProgetti(
            PolarionProgetti.Select(r => new PolarionProgettoConfig { Id = r.Id, Nome = r.Nome }), _polarionApiLoaded.ProjectId);

    private bool ProgettiPolarionModificati()
    {
        List<PolarionProgettoConfig>? attuale;
        try { attuale = ProgettiPolarionDaSalvare(); }
        catch (InvalidOperationException) { return true; }   // un valore non valido e comunque una modifica non salvata
        var salvato = _polarionApiLoaded.Progetti ?? new List<PolarionProgettoConfig>();
        var a = attuale ?? new List<PolarionProgettoConfig>();
        return a.Count != salvato.Count || a.Where((p, i) =>
            !string.Equals(p.Id, salvato[i].Id, StringComparison.Ordinal) || !string.Equals(p.Nome, salvato[i].Nome, StringComparison.Ordinal)).Any();
    }

    [RelayCommand]
    private void AggiungiProgettoPolarion()
    {
        if (!EMaster) return;
        if (PolarionProgetti.Count >= PolarionApiSettings.MaxProgetti) return;
        PolarionProgetti.Add(NuovaRigaProgetto("", ""));
        OnPropertyChanged(nameof(PolarionApiPendingStatus));
    }

    [RelayCommand]
    private void RimuoviProgettoPolarion(PolarionProgettoRiga? riga)
    {
        if (!EMaster || riga is null) return;
        PolarionProgetti.Remove(riga);
        OnPropertyChanged(nameof(PolarionApiPendingStatus));
    }

    /// <summary>Sonda di raggiungibilita in sola lettura (ProbeProjectAsync) del progetto indicato nella riga, con le credenziali SALVATE.</summary>
    [RelayCommand]
    private async Task VerificaProgettoPolarionAsync(PolarionProgettoRiga? riga)
    {
        if (riga is null || riga.InVerifica || PolarionRefreshInCorso || !_state.CartellaValida) return;
        var progetto = riga.Id.Trim();
        if (!PolarionReadClient.IsValidProjectId(progetto)) return;
        void Esito(string testo, bool errore) { riga.EsitoVerifica = testo; riga.EsitoErrore = errore; }

        PolarionApiSettings settings;
        try { settings = PolarionApiSettingsService.Load(_state.Paths); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Esito(PolarionText("Configurazione non leggibile.", "Configuration cannot be read.", "Configuration illisible."), true);
            return;
        }
        if (settings.ErroreCaricamento is not null || !settings.LetturaAbilitata || !settings.HaToken ||
            !string.Equals(PolarionApiRoot.Trim(), settings.ApiRoot, StringComparison.Ordinal))
        {
            Esito(PolarionText("Salva prima una configurazione API valida con la lettura abilitata.",
                "Save a valid API configuration with reading enabled first.",
                "Enregistrez d'abord une configuration API valide avec la lecture activée."), true);
            return;
        }
        riga.InVerifica = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            using var http = PolarionHttpFactory();
            var client = new PolarionReadClient(http, new Uri(settings.ApiRoot, UriKind.Absolute), settings.LeggiToken());
            await client.ProbeProjectAsync(progetto, cts.Token);
            Esito(PolarionText("Progetto raggiungibile.", "Project reachable.", "Projet joignable."), false);
        }
        catch (HttpRequestException ex) { Esito(PolarionHttpError(ex.StatusCode), true); }
        catch (OperationCanceledException) { Esito(PolarionText("Tempo di connessione scaduto.", "Connection timed out.", "Délai de connexion dépassé."), true); }
        catch (Exception) { Esito(PolarionText("Risposta non valida o connessione non disponibile.", "Invalid response or connection unavailable.", "Réponse invalide ou connexion indisponible."), true); }
        finally { riga.InVerifica = false; }
    }
}
