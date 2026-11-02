using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Passi del tour in francese: stessa struttura e stessi identificativi di navigazione
/// (NavId / TargetId) dei passi italiani e inglesi, così il tour punta alle stesse sezioni in ogni lingua.</summary>
public static partial class OnboardingService
{
    private static IReadOnlyList<OnboardingStep> CreaPassiFrancesi(RuoloOperatore ruolo)
    {
        var comuni = new List<OnboardingStep>
        {
            new("🚛", "Bienvenue dans TestLoom", "Ce parcours suit vos autorisations. Vous pouvez le relancer à tout moment depuis Visite guidée dans le menu latéral.", "home", "account-menu"),
            new("🏠", "Votre vue d'ensemble opérationnelle", "L'Accueil affiche les travaux à suivre, les activités terminées et les indicateurs. Récents rouvre les tests, les change requests et les rapports ouverts récemment sur ce PC. Personnaliser l'accueil choisit les sections et leur ordre ; ce choix reste sur ce PC.", "home", "home-filter"),
            new("↩️", "Revenir en arrière sans perdre votre place", "← Précédent ou Alt+← revient à la section, au Test, à la change request ou au signalement ouvert précédemment. S'il y a des modifications non enregistrées, TestLoom demande confirmation avant de changer d'écran.", "home", "main-indietro"),
        };

        comuni.AddRange(ruolo switch
        {
            RuoloOperatore.Master => MasterFrancesi(),
            RuoloOperatore.Standard => OperatoreFrancesi(),
            _ => ViewerFrancesi(),
        });
        return comuni;
    }

    private static IEnumerable<OnboardingStep> MasterFrancesi() =>
    [
        new("🧪", "Concevez et pilotez les tests", "Dans Tests, vous pouvez créer, planifier, assigner et réviser les activités. Chaque enregistrement opérationnel conserve une nouvelle Exécution dans l'historique des REV. Dupliquer la dernière opération ajoute une opération indépendante, avec aperçu et annulation.", "test", "test-new"),
        new("⚠️", "Classez les anomalies", "Depuis un Test, chaque signalement se traite en un clic : Créer une NC ou Lier à NC-… ; Tout traiter applique l'action par défaut après confirmation. Dans Anomalies / CR, les vues enregistrées réappliquent les filtres et la recherche en un clic.", "anomalie", "anomaly-workflow"),
        new("📊", "Contrôlez résultats et couverture", "Rapports et Audit transforment les données en exports, matrices Exigence × composant et indicateurs de conflit vérifiables. Les exports longs s'exécutent en arrière-plan et peuvent être annulés.", "report", "report-export"),
        new("🔀", "Résolvez les conflits entre postes", "Si deux PC modifient la même NC, change request ou le même signalement, cet enregistrement reste verrouillé en écriture et TestLoom le signale. Dans Paramètres → Général, dans la carte Conflits entre postes, choisissez Conserver la version actuelle ou Utiliser cette version.", "impostazioni", "settings-conflicts"),
        new("🔩", "Configurez l'environnement", "Dans Paramètres, gérez les Opérateurs, les Types de test, les Outils et leurs champs, les composants, les véhicules, les fonctionnalités, les exigences, les archives et les préférences partagées. La mise en page de l'accueil pour l'équipe se publie depuis Personnaliser l'accueil.", "impostazioni", "settings-sections"),
        new("✅", "Prêt en tant que Master", "Votre rôle peut modifier la configuration et les données partagées : vérifiez la bannière Dossier partagé incertain, les verrous et les avertissements de fichiers illisibles avant d'intervenir.", "home", "global-search"),
    ];

    private static IEnumerable<OnboardingStep> OperatoreFrancesi() =>
    [
        new("🧪", "Exécutez le travail assigné", "Dans Tests, ouvrez les activités, complétez Détails et Opérations, joignez les preuves et enregistrez. Chaque enregistrement opérationnel crée une nouvelle Exécution consultable depuis REV. Pour une répétition, utilisez Répéter ; pour une opération semblable à une déjà saisie, utilisez Dupliquer la dernière opération : c'est une nouvelle opération indépendante, sans résultats ni preuves copiés.", "test", "test-filter"),
        new("📎", "Rendez le résultat vérifiable", "Pour chaque Opération, indiquez l'outil, le résultat et les motifs requis ; joignez les journaux, images ou traces. Le résultat global dépend de l'opération la plus défavorable.", "test", "test-filter"),
        new("⚠️", "Gérez les signalements et les retests", "Une non-conformité ne devient pas automatiquement une anomalie : confirmez-la avec une gravité ou excluez-la en motivant. Depuis le Test, utilisez Créer une NC ou Lier à NC-… en un clic ; les choix prennent effet à l'enregistrement du Test.", "anomalie", "anomaly-workflow"),
        new("📊", "Consultez rapports et audit", "Utilisez Rapports et Audit pour contrôler la couverture, les scores et les conflits. Les actions disponibles restent limitées par les autorisations de votre rôle.", "report", "report-export"),
        new("✅", "Prêt à travailler", "Partez de l'Accueil : Récents rouvre les derniers éléments ouverts. Utilisez Ctrl+K pour rechercher, et enregistrez toujours avant de changer de section ; Aide, notifications et Visite guidée restent dans le menu latéral.", "home", "global-search"),
    ];

    private static IEnumerable<OnboardingStep> ViewerFrancesi() =>
    [
        new("🔎", "Consultez et filtrez les tests", "Dans Tests, vous pouvez rechercher et ouvrir les activités pour reconstituer l'état, les détails, les opérations, les preuves et les révisions, sans modifier les données.", "test", "test-filter"),
        new("⚠️", "Suivez les anomalies", "La vue Anomalies permet de suivre la classification, la gravité, les retests et les clôtures, tout en restant en lecture seule.", "anomalie", "anomaly-workflow"),
        new("📊", "Analysez résultats et traçabilité", "Rapports et Audit permettent de lire la couverture, les scores Exigence × composant et les conflits ; les exports disponibles ne modifient pas les données partagées.", "audit", "audit-export"),
        new("🔒", "Votre accès est en lecture seule", "Vous pouvez explorer les informations autorisées, utiliser les filtres et modifier les préférences locales, mais vous ne pouvez pas créer ni modifier les tests et la configuration partagés.", "impostazioni", "settings-sections"),
        new("✅", "Prêt à consulter", "Partez de l'Accueil et utilisez Aide ou Visite guidée quand vous en avez besoin. Si vous devez intervenir sur les données, demandez à un Master d'adapter votre rôle.", "home", "global-search"),
    ];
}
