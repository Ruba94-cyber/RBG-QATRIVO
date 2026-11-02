using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Argomenti di aiuto in francese. Stessi 22 argomenti e stesso ordine di SezioniIt/SezioniEn:
/// le regole di ruolo e di contesto (VisibilePerRuolo, PrioritaContesto) ragionano per indice.</summary>
public partial class HelpViewModel
{
    private static readonly List<HelpSection> SezioniFr = new()
    {
        new HelpSection
        {
            Titolo = "Accès",
            Testo = "Au démarrage, choisissez votre nom d'opérateur et saisissez votre PIN. Pour changer d'opérateur " +
                    "(ou en fin de poste sur un PC partagé), utilisez « Se déconnecter » en bas du menu latéral : vous revenez " +
                    "à l'écran d'accès sans fermer l'application. Si vous avez oublié votre PIN, utilisez la clé de récupération " +
                    "depuis l'écran d'accès ; sinon, un Master peut le régénérer depuis Paramètres → Opérateurs."
        },
        new HelpSection
        {
            Titolo = "Créer un test",
            Testo = "La première étape consiste à choisir le Type de test parmi ceux configurés par un Master " +
                    "(Paramètres → Types de test) : ce n'est pas une liste fixe, chaque Type de test détermine quels champs, " +
                    "sections Détails, Références et outils présélectionnés apparaîtront dans le reste du formulaire. Un nouveau " +
                    "test se remplit ensuite onglet par onglet — Planification, Détails, Épreuves, Pièces jointes, Références — avec " +
                    "Suivant et Précédent pour naviguer dans l'ordre ; Enregistrer n'apparaît que sur le dernier onglet. composant, " +
                    "véhicule ou banc, matricule, assigné et description sont en général obligatoires, mais cela dépend de la configuration " +
                    "de ce Type de test par le Master. Ajoutez ensuite une ou plusieurs Opérations, chacune avec l'outil utilisé et " +
                    "son propre résultat. Lorsque vous rouvrez le test enregistré, tout le formulaire s'affiche de nouveau d'un seul " +
                    "bloc, sans onglets."
        },
        new HelpSection
        {
            Titolo = "Opérations et Exécutions",
            Testo = "Chaque enregistrement des Détails ou des Opérations crée une nouvelle Exécution (révision), consultable " +
                    "depuis le menu REV en haut ; les Exécutions précédentes restent en lecture seule. Une Exécution contient une ou " +
                    "plusieurs Opérations, chacune avec son propre résultat (Conforme, Non conforme, Conforme avec écarts, Non " +
                    "applicable...) : le résultat global de l'Exécution se calcule toujours à partir de l'Opération la plus défavorable."
        },
        new HelpSection
        {
            Titolo = "Champs système et Références",
            Testo = "Les champs qui apparaissent dans le formulaire (composant, Véhicule, Fonctionnalité, Outils utilisés...) proviennent " +
                    "d'un catalogue partagé, géré par un Master dans « Gestion des champs système » (Paramètres → Types de test → " +
                    "Gérer les champs) : un Master peut renommer le libellé d'un champ existant, changer son type (tant qu'il ne " +
                    "contient aucune donnée enregistrée) ou en créer de nouveaux. La carte Références (Exigences, Tests et Tickets " +
                    "liés) s'affiche ou se masque de la même manière selon le Type de test."
        },
        new HelpSection
        {
            Titolo = "Score de conformité et Audit",
            Testo = "Si un test a une ou plusieurs Exigences liées, chaque Exécution affiche pour chacune un score 0-5/N.A. " +
                    "(en général calculé à partir du résultat, et toujours corrigeable à la main en indiquant un motif). Le panneau " +
                    "Audit du menu latéral résume ces scores dans une matrice Exigence × composant par Véhicule, en signalant les éventuels " +
                    "conflits lorsque deux tests en désaccord couvrent la même paire."
        },
        new HelpSection
        {
            Titolo = "Statut du travail et résultat technique",
            Testo = "Le statut du travail (Planifié, En cours, En attente, À retester, Conclu...) est distinct du résultat " +
                    "technique de chaque essai. Conforme, Non conforme, Conforme avec écarts et Non applicable ne modifient pas " +
                    "automatiquement le statut du test. N.A. exige toujours un motif."
        },
        new HelpSection
        {
            Titolo = "Affectation",
            Testo = "La personne qui exécute un test en devient automatiquement l'assigné à chaque nouvelle révision, même si le " +
                    "test était auparavant assigné à quelqu'un d'autre : un Master peut assigner un test à un collègue, mais si un " +
                    "tiers le prend et l'exécute, l'assignation passe à lui. Seul un Master peut assigner manuellement un test à une " +
                    "autre personne ; les autres opérateurs se l'approprient simplement en y travaillant."
        },
        new HelpSection
        {
            Titolo = "Vérifications et épreuves",
            Testo = "Une fonctionnalité contient des vérifications réutilisables. Un test peut comporter plusieurs vérifications, " +
                    "et chaque vérification plusieurs épreuves : par exemple UnitLogger sur véhicule et ValidationTool au banc, avec des résultats " +
                    "distincts. Pour UnitLogger, la version est obligatoire ; pour les outils d'ingénierie, elle est facultative."
        },
        new HelpSection
        {
            Titolo = "Tests associés",
            Testo = "Lorsqu'un second test (par ex. sur véhicule) couvre la même exigence ou fonctionnalité déjà testée ailleurs " +
                    "(par ex. au banc), l'application propose de le lier comme test associé. Un test peut être lié à plusieurs autres tests."
        },
        new HelpSection
        {
            Titolo = "Preuves et Ticketing",
            Testo = "Les images, PDF, fichiers texte/CSV, ZIP et traces de diagnostic sont pris en charge ; les doublons identiques " +
                    "sont reconnus par empreinte (hash). Ticketing reste externe : TestLoom n'utilise ni API ni identifiants. Le lot manuel " +
                    "contient des TXT, des PDF, un manifeste et les preuves ; après l'ouverture dans Ticketing, liez le ticket TICKET et l'URL."
        },
        new HelpSection
        {
            Titolo = "Signalements et anomalies",
            Testo = "Une épreuve Non conforme ou avec écarts crée un signalement à classer, jamais une anomalie définitive " +
                    "automatiquement. Depuis le Test, le signalement se traite en un clic : « Créer une NC » ouvre une nouvelle " +
                    "non-conformité avec la gravité déjà indiquée ; « Lier à NC-… » apparaît lorsqu'une anomalie ouverte est " +
                    "manifestement la même. « Tout traiter » applique l'action par défaut : il relie les signalements à une anomalie " +
                    "suggérée et crée les NC pour ceux qui ont une gravité ; les autres restent à classer. Créer et lier prennent effet " +
                    "à l'enregistrement du Test. Dans le détail d'une non-conformité, après un conflit d'enregistrement, « Recharger " +
                    "la version actuelle » charge la version de l'autre poste : votre saisie reste dans la boîte de dialogue et dans un " +
                    "brouillon local. La clôture dans TestLoom est manuelle et exige un motif ; elle ne ferme jamais Ticketing."
        },
        new HelpSection
        {
            Titolo = "Accueil",
            Testo = "Les tableaux « À faire » et « Terminés » affichent les tests qui nécessitent votre attention et les derniers " +
                    "tests conclus avec succès. Si vous êtes opérateur Standard avec des Gammes de véhicules assignées, vous ne voyez " +
                    "que les tests de ces gammes. Le tableau de bord comporte 5 filtres toujours visibles, plus « Autres filtres » " +
                    "pour le reste, ainsi qu'un panneau à droite pour choisir les graphiques à afficher ensemble. « Personnaliser " +
                    "l'accueil » choisit les sections à afficher (Récapitulatif, Avertissements, Indicateurs, Tableau de bord et " +
                    "graphiques, À faire et terminés, Récents) et leur ordre : ce choix reste sur ce PC. Les Avertissements n'apparaissent " +
                    "que lorsqu'il y a quelque chose à signaler ; Indicateurs inclut les change requests ouvertes. « Récents » liste " +
                    "jusqu'à 15 éléments parmi les tests, change requests et signalements ouverts récemment sur ce PC. Un Master peut " +
                    "publier une mise en page pour l'équipe avec « Publier comme mise en page de l'équipe » : les PC sans mise en page " +
                    "personnelle l'utilisent, ceux qui ont une mise en page personnelle ne changent pas. Chaque opérateur peut " +
                    "l'appliquer avec « Utiliser la mise en page de l'équipe », et « Rétablir par défaut » revient à l'ordre d'origine."
        },
        new HelpSection
        {
            Titolo = "Actions groupées et vues enregistrées",
            Testo = "Dans la liste des Tests, en sélectionnant plusieurs lignes (Ctrl+clic ou Maj+clic), une barre apparaît avec " +
                    "Assigner à…, Changer le statut… et Archiver, pour agir sur toutes les lignes choisies en une seule commande au " +
                    "lieu de les ouvrir une à une. Les vues enregistrées sont disponibles dans la liste des Tests, dans Anomalies / CR " +
                    "et dans WU Status : une combinaison de filtres, de recherche et de tri s'enregistre avec un nom (« 💾 Enregistrer " +
                    "la vue actuelle ») et se réapplique depuis le menu « Vues enregistrées ». Les vues, les largeurs de colonnes et le " +
                    "tri choisi d'un clic sur l'en-tête restent sur ce PC, dans les tableaux qui le permettent ; la liste des Tests " +
                    "conserve un ordre fixe."
        },
        new HelpSection
        {
            Titolo = "Enregistrement",
            Testo = "L'enregistrement est toujours manuel (bouton Enregistrer, ou Ctrl+S au clavier lorsqu'il est disponible). " +
                    "Si vous essayez de quitter un écran — en changeant de section, en revenant à la liste, avec ← Précédent, en " +
                    "fermant la fenêtre ou en choisissant Se déconnecter depuis le menu de l'icône de la zone de notification — alors " +
                    "que des modifications ne sont pas enregistrées, l'application demande confirmation avant de continuer et ne supprime " +
                    "jamais vos modifications sans que vous l'ayez choisi."
        },
        new HelpSection
        {
            Titolo = "Verrou en modification",
            Testo = "Ouvrir un test ou un fichier maître en lecture seule est toujours libre. Le verrou ne s'active que lorsque " +
                    "vous commencez à modifier un champ, et se libère automatiquement après une longue inactivité. Si vous constatez " +
                    "un verrou « orphelin » (par ex. un collègue a fermé son PC sans enregistrer), vous pouvez forcer son déverrouillage : " +
                    "l'application indique toujours qui le détient et depuis quand, et demande confirmation avant d'interrompre le " +
                    "travail d'une autre personne."
        },
        new HelpSection
        {
            Titolo = "Archives",
            Testo = "Archiver un test ou une ligne d'une liste de données maîtres (Exigences, composant, Outils, Types de test...) ne la " +
                    "supprime jamais immédiatement : elle est déplacée dans les Archives (Paramètres), où elle disparaît du tableau de " +
                    "travail d'origine mais reste toujours restaurable. Depuis là, un Master peut aussi la supprimer définitivement, " +
                    "mais uniquement si rien ne la référence plus."
        },
        new HelpSection
        {
            Titolo = "Journaux de diagnostic (en cas de problème)",
            Testo = "TestLoom conserve sur le PC (jamais dans le dossier partagé) trois journaux locaux et circulaires : les erreurs " +
                    "(crash.log), les principales opérations de l'application, y compris lorsqu'elles réussissent (trace.log, utile " +
                    "aussi pour un comportement incorrect sans véritable erreur), et la télémétrie des performances. Dans Paramètres → " +
                    "Paquet de diagnostic, vous trouvez le chemin du dossier avec un lien pour l'ouvrir directement, ainsi qu'un bouton " +
                    "pour exporter les trois journaux dans un seul fichier zip à joindre à un signalement."
        },
        // Indici 17-21: aggiunti in 6.6.0 in coda, così non spostano gli indici usati dalle regole di ruolo.
        new HelpSection
        {
            Titolo = "Raccourcis clavier",
            Testo = "Ctrl+K ouvre la recherche globale sur les Tests, anomalies, CR, signalements, véhicules et outils ; pour les " +
                    "Master, elle inclut aussi les composants, exigences et fonctionnalités. Entrée ouvre le premier résultat. Ctrl+S enregistre, " +
                    "Ctrl+N crée un nouvel élément de la page et Ctrl+F place le curseur sur le filtre principal. Ctrl+1 à 9, y compris " +
                    "avec le pavé numérique, passe à la section correspondante du menu latéral. Alt+← revient à la position précédente, " +
                    "avec la même confirmation que le bouton ← Précédent. Échap ferme la recherche, les notifications ou l'aide ouverts ; " +
                    "sinon il annule la modification en cours, après confirmation."
        },
        new HelpSection
        {
            Titolo = "Conflits entre postes",
            Testo = "Si deux PC enregistrent la même NC, CR ou le même signalement à partir de la même version avec des résultats " +
                    "différents (y compris une modification face à une suppression), TestLoom ne verrouille en écriture que cet enregistrement " +
                    "et le signale aux Master. Le Master le résout dans Paramètres → Général, dans la carte « Conflits entre postes — NC, " +
                    "CR et Signalements » : « Conserver la version actuelle » ou « Utiliser cette version ». Le choix porte sur la version " +
                    "entière, et non sur des champs isolés ; les autres versions restent consultables. Les autres enregistrements restent " +
                    "modifiables. Les catalogues maîtres ont une carte analogue, « Conflits dans les catalogues maîtres ». La détection " +
                    "lit les journaux de synchronisation : une modification présente uniquement sur un PC dont le journal n'a pas encore " +
                    "atteint les autres n'est visible qu'une fois ce journal synchronisé."
        },
        new HelpSection
        {
            Titolo = "Dossier partagé incertain et visualiseur local",
            Testo = "TestLoom évalue l'état du dossier partagé à partir de signaux purement locaux. Si le dossier paraît incertain " +
                    "(par exemple racine lente, fichiers clés illisibles ou copies de conflit récentes), la bannière « Dossier partagé " +
                    "incertain » apparaît. La bannière ne bloque pas les modifications et ne signifie pas que OneDrive est synchronisé : " +
                    "avant d'enregistrer, vérifiez OneDrive/SharePoint, ou activez le visualiseur local. Le visualiseur bloque les " +
                    "nouvelles modifications partagées sur ce PC tant que vous ne le désactivez pas, et ne touche pas aux brouillons " +
                    "locaux. Il s'active depuis la bannière ou depuis Paramètres → Général → Dossier partagé, toujours avec confirmation. " +
                    "Si le dossier n'est pas accessible, TestLoom bloque déjà les écritures et laisse les données consultables."
        },
        new HelpSection
        {
            Titolo = "Brouillons d'urgence et récupération",
            Testo = "Si l'enregistrement d'une Change request, d'une non-conformité ou d'un signalement réseau échoue, TestLoom conserve " +
                    "une copie locale du contenu en dehors de OneDrive, avec l'identifiant et la révision attendue. Les brouillons ne " +
                    "sont jamais publiés automatiquement. Dans Paramètres → Général → Sauvegarde des données, « Examiner les brouillons " +
                    "(lecture seule) » liste les brouillons avec un aperçu, les plus récents en premier, et « Ouvrir les brouillons locaux " +
                    "d'urgence » ouvre le dossier. Depuis l'éditeur, « Récupérer le brouillon local… » compare le brouillon avec la " +
                    "version actuelle et n'importe, après confirmation, que les champs modifiés dans le brouillon et restés inchangés " +
                    "localement ; les champs modifiés des deux côtés ne sont pas sélectionnables. L'enregistrement reste le même, avec " +
                    "ses contrôles habituels."
        },
        new HelpSection
        {
            Titolo = "Notifications et alerte Windows",
            Testo = "Les notifications non lues apparaissent dans le menu de l'opérateur (le bouton avec le nom et 🔔). Dans " +
                    "Paramètres → Général → Démarrage et notifications locales, choisissez les types qui comptent dans le compteur : un " +
                    "type désélectionné reste dans le centre de notifications mais ne compte ni dans le badge ni dans l'alerte Windows, " +
                    "et aucune notification n'est supprimée. Les conflits de synchronisation des données sont toujours comptés. Lorsque " +
                    "« Activer les notifications Windows lorsqu'elles sont prises en charge » est actif, une nouvelle notification reçue " +
                    "pendant que TestLoom est en arrière-plan fait clignoter le bouton dans la barre des tâches, et l'icône de la zone de " +
                    "notification affiche le nombre de notifications non lues."
        },
    };

    /// <summary>Elenco degli argomenti per la lingua indicata: usato da HelpViewModel e dai test di parità per indice.</summary>
    internal static List<HelpSection> SezioniDi(string lingua) => lingua switch
    {
        "fr" => SezioniFr,
        "en" => SezioniEn,
        _ => SezioniIt,
    };

    /// <summary>Titoli degli argomenti nella lingua indicata ("it", "en" o "fr"), nell'ordine dell'indice.</summary>
    internal static IReadOnlyList<string> TitoliArgomenti(string lingua) =>
        SezioniDi(lingua).Select(s => s.Titolo).ToList();
}
