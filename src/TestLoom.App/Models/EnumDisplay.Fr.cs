using TestLoom.App.Services;

namespace TestLoom.App.Models;

/// <summary>Etichette francesi degli enum (lingua "fr"). Italiano e inglese restano nei switch di EnumDisplay
/// (Enums.cs). Se la lingua è il francese e una voce non compare qui, la ricerca ricade sull'inglese.</summary>
public static partial class EnumDisplay
{
    private static readonly Dictionary<Esito, string> FrEsito = new()
    {
        [Esito.DaEseguire] = "À exécuter",
        [Esito.Conforme] = "Conforme",
        [Esito.NonConforme] = "Non conforme",
        [Esito.ConformeConDeviazioni] = "Conforme avec écarts",
        [Esito.NonApplicabile] = "Non applicable",
        [Esito.NonEseguitoNA] = "Non exécuté / N.A.",
        [Esito.DaRivedere] = "À revoir",
    };

    private static readonly Dictionary<PunteggioConformita, string> FrPunteggioConformita = new()
    {
        [PunteggioConformita.NonConforme] = "Non conforme (0)",
        [PunteggioConformita.ScarsamenteConforme] = "Faiblement conforme (1)",
        [PunteggioConformita.ParzialmenteConforme] = "Partiellement conforme (2)",
        [PunteggioConformita.ModeratamenteConforme] = "Modérément conforme (3)",
        [PunteggioConformita.AmpiamenteConforme] = "Largement conforme (4)",
        [PunteggioConformita.Conforme] = "Conforme (5)",
        [PunteggioConformita.NonApplicabile] = "N.A.",
    };

    private static readonly Dictionary<StatoLavoroTest, string> FrStatoLavoroTest = new()
    {
        [StatoLavoroTest.Pianificato] = "Planifié",
        [StatoLavoroTest.InCorso] = "En cours",
        [StatoLavoroTest.DaRivedere] = "À revoir",
        [StatoLavoroTest.InAttesa] = "En attente",
        [StatoLavoroTest.DaRitestare] = "À retester",
        [StatoLavoroTest.Concluso] = "Conclu",
        [StatoLavoroTest.Annullato] = "Annulé",
    };

    private static readonly Dictionary<AmbienteProva, string> FrAmbienteProva = new()
    {
        [AmbienteProva.Banco] = "Banc",
        [AmbienteProva.Veicolo] = "Véhicule",
    };

    private static readonly Dictionary<TipoCampoStrumento, string> FrTipoCampoStrumento = new()
    {
        [TipoCampoStrumento.Testo] = "Texte libre",
        [TipoCampoStrumento.Numero] = "Nombre",
        [TipoCampoStrumento.Lista] = "Liste déroulante",
        [TipoCampoStrumento.Checkbox] = "Case à cocher",
        [TipoCampoStrumento.File] = "Fichier lié",
        [TipoCampoStrumento.Data] = "Date",
        [TipoCampoStrumento.ListaTesto] = "Liste de lignes",
    };

    private static readonly Dictionary<StatoManualeWorkingUnit, string> FrStatoManualeWorkingUnit = new()
    {
        [StatoManualeWorkingUnit.Nessuno] = "Automatique",
        [StatoManualeWorkingUnit.Ok] = "OK (manuel)",
        [StatoManualeWorkingUnit.Problemi] = "Problèmes (manuel)",
    };

    private static readonly Dictionary<ColoreEvidenziazione, string> FrColoreEvidenziazione = new()
    {
        [ColoreEvidenziazione.Nessuno] = "Aucune mise en évidence",
        [ColoreEvidenziazione.Giallo] = "Jaune",
        [ColoreEvidenziazione.Rosso] = "Rouge",
    };

    private static readonly Dictionary<TipoAmbitoCampo, string> FrTipoAmbitoCampo = new()
    {
        [TipoAmbitoCampo.PerOperazione] = "Par opération",
        [TipoAmbitoCampo.PerRevisione] = "Stable par révision",
        [TipoAmbitoCampo.PerTest] = "Constant pour tout le test",
    };

    private static readonly Dictionary<StatoAnomalia, string> FrStatoAnomalia = new()
    {
        [StatoAnomalia.InVerifica] = "En vérification",
        [StatoAnomalia.Confermata] = "Confirmée",
        [StatoAnomalia.EvidenzeDaCompletare] = "Preuves à compléter",
        [StatoAnomalia.ProntaPerEsta] = "Prête pour TICKET",
        [StatoAnomalia.EstaAperto] = "TICKET ouvert",
        [StatoAnomalia.InAttesa] = "En attente",
        [StatoAnomalia.UlterioriTestRichiesti] = "Tests supplémentaires requis",
        [StatoAnomalia.ProntaPerRetest] = "Prête pour le retest",
        [StatoAnomalia.RetestFallito] = "Retest échoué",
        [StatoAnomalia.RetestSuperato] = "Retest réussi",
        [StatoAnomalia.Conclusa] = "Clôturée",
    };

    private static readonly Dictionary<PrioritaOperativa, string> FrPrioritaOperativa = new()
    {
        [PrioritaOperativa.Enhancement] = "Amélioration",
        [PrioritaOperativa.Trivial] = "Trivial",
        [PrioritaOperativa.Minor] = "Mineure",
        [PrioritaOperativa.Normal] = "Normale",
        [PrioritaOperativa.Major] = "Majeure",
        [PrioritaOperativa.Critical] = "Critique",
    };

    private static readonly Dictionary<SeveritaAnomalia, string> FrSeveritaAnomalia = new()
    {
        [SeveritaAnomalia.NA] = "N.D.",
        [SeveritaAnomalia.Lowest] = "Minimale",
        [SeveritaAnomalia.Low] = "Faible",
        [SeveritaAnomalia.Medium] = "Moyenne",
        [SeveritaAnomalia.High] = "Élevée",
        [SeveritaAnomalia.Highest] = "Maximale",
    };

    private static readonly Dictionary<StatoNonConformita, string> FrStatoNonConformita = new()
    {
        [StatoNonConformita.Aperta] = "Ouverte",
        [StatoNonConformita.Chiusa] = "Clôturée",
    };

    private static readonly Dictionary<StatoDettaglioNc, string> FrStatoDettaglioNc = new()
    {
        [StatoDettaglioNc.InAnalisi] = "En analyse",
        [StatoDettaglioNc.InFix] = "En correction",
        [StatoDettaglioNc.BugFixedInRetest] = "Bug corrigé – en retest",
        [StatoDettaglioNc.NoSolutionAccettato] = "Sans solution (accepté)",
    };

    private static readonly Dictionary<DestinatarioSegnalazione, string> FrDestinatarioSegnalazione = new()
    {
        [DestinatarioSegnalazione.Diag] = "Diag",
        [DestinatarioSegnalazione.TD] = "T&D",
        [DestinatarioSegnalazione.Supplier] = "Fournisseur",
        [DestinatarioSegnalazione.Altro] = "Autre",
    };

    private static readonly Dictionary<CausaProbabileUdt, string> FrCausaProbabileUdt = new()
    {
        [CausaProbabileUdt.Veicolo] = "Véhicule",
        [CausaProbabileUdt.Componente] = "Composant",
        [CausaProbabileUdt.UnitLogger] = "UnitLogger",
        [CausaProbabileUdt.Altro] = "Autre",
    };

    private static readonly Dictionary<RuoloOperatore, string> FrRuoloOperatore = new()
    {
        [RuoloOperatore.Master] = "Master",
        [RuoloOperatore.Standard] = "Opérateur",
        [RuoloOperatore.Viewer] = "Viewer (lecture seule)",
    };

    /// <summary>Etichetta francese della voce, o null se la lingua non è il francese o la voce non ha traduzione.</summary>
    private static string? FrOppure<T>(IReadOnlyDictionary<T, string> dizionario, T valore) where T : notnull =>
        LocalizationService.Instance.Lingua == "fr" && dizionario.TryGetValue(valore, out var testo) ? testo : null;
}
