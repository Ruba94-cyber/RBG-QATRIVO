using System.Security.Cryptography;
using System.Text;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Sigillo del ruolo operatore e della modalità di accesso (piano 6.4.2, §4b, decisione utente
/// 2026-09-28). Stessa categoria di protezione di <see cref="PinService.Cifra"/>: una chiave incorporata
/// nell'app, NON un vero segreto — chiunque decompili l'app la trova. Serve solo a rendere inefficace una
/// modifica a mano di operatori.json/accesso-config.json (es. promuoversi Master editando il JSON), non a
/// resistere a un attaccante determinato che ha accesso al binario. Un sigillo assente o non valido fa
/// ricadere il valore protetto al più restrittivo (Viewer per il ruolo, "con PIN" per la modalità di
/// accesso) — MAI al valore più permissivo: un file corrotto/manomesso deve fallire chiuso.</summary>
public static class RuoloSigilloService
{
    /// <summary>Chiave incorporata dedicata (diversa da PinService.ChiaveCifratura, stesso livello di
    /// protezione): un HMAC, non una cifratura reversibile, quindi la chiave non ha bisogno di essere la
    /// stessa — usarne una diversa evita solo che un'eventuale compromissione dell'una implichi l'altra.</summary>
    private static readonly byte[] Chiave = Convert.FromHexString("6b1f0c9a3d7e4521b8f60a2c9d3e17f45a8b2c6d0e4f1a7b3c9d5e2f10a4b7c3");

    private static string CalcolaHmac(string testo)
    {
        using var hmac = new HMACSHA256(Chiave);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(testo));
        return Convert.ToHexString(hash);
    }

    /// <summary>Testo canonico su cui è calcolato il sigillo del ruolo. Include Uid oltre a Nome e Ruolo
    /// (revisione piano 6.4.2, §4b: "il sigillo leghi il ruolo all'identità dell'operatore, così non si può
    /// copiare il sigillo fra operatori diversi") — senza Uid, copiare l'intera riga SigilloRuolo da un
    /// operatore Master a un altro operatore con lo stesso Nome/Ruolo (es. un omonimo creato ad arte) avrebbe
    /// prodotto un sigillo valido. Nome resta comunque nel testo (oltre a Uid) perché il Nome è la chiave con
    /// cui AppState.ImpostaOperatore/RiallineaOperatoreCorrente identificano l'operatore attivo: legare anche
    /// al Nome invalida il sigillo se l'operatore viene rinominato fuori dall'app.</summary>
    private static string TestoRuolo(Guid uid, string nome, RuoloOperatore ruolo) => $"Operatore.Ruolo|{uid}|{nome}|{ruolo}";

    /// <summary>Ricalcola e assegna il sigillo di <see cref="Operatore.Ruolo"/> in base al Nome e al ruolo
    /// CORRENTI dell'oggetto. Va richiamato SOLO dai punti autorizzati a cambiare un ruolo: il salvataggio
    /// dell'editor Operatori (un Master dall'app) e il bootstrap del primo operatore (FirstRun) — mai da un
    /// caricamento/lettura, altrimenti un ruolo manomesso a mano verrebbe risigillato invece di ricadere su
    /// Viewer.</summary>
    /// <remarks>L'identità è l'Uid tecnico Phase D già esistente (<see cref="MasterEntityIdentityService"/>,
    /// estensione <c>_ditmUid</c>), non una proprietà nuova sull'Operatore: una proprietà nativa <c>Uid</c>
    /// sostituirebbe quell'identità e renderebbe illeggibile il catalogo operatori già nel journal master.
    /// Se l'operatore non ha ancora un Uid tecnico, <see cref="Sigilla"/> lo assegna (viene salvato con la
    /// riga); in verifica un Uid mancante rende il sigillo non valido.</remarks>
    public static void Sigilla(Operatore operatore) =>
        operatore.SigilloRuolo = CalcolaHmac(TestoRuolo(
            MasterEntityIdentityService.GetOrCreate(operatore), operatore.Nome, operatore.Ruolo));

    public static bool SigilloRuoloValido(Operatore operatore) =>
        !string.IsNullOrEmpty(operatore.SigilloRuolo) &&
        MasterEntityIdentityService.TryGet(operatore, out var uid) && uid != Guid.Empty &&
        FixedTimeEquals(operatore.SigilloRuolo, CalcolaHmac(TestoRuolo(uid, operatore.Nome, operatore.Ruolo)));

    /// <summary>PUNTO UNICO di calcolo del ruolo effettivo (piano 6.4.2, §4b: "dove si calcola il ruolo
    /// effettivo, usa un punto unico, non spargere controlli"). Sigillo assente o non valido (operatore
    /// esistente prima di questa funzionalità, oppure Ruolo modificato a mano nel JSON) → Viewer, MAI un
    /// ruolo più alto di quello effettivamente sigillato.</summary>
    public static RuoloOperatore RuoloEffettivo(Operatore operatore) =>
        SigilloRuoloValido(operatore) ? operatore.Ruolo : RuoloOperatore.Viewer;

    private static string TestoModalita(bool senzaPin) => $"ModalitaAccessoWorkspace|{senzaPin}";

    public static void SigillaModalitaAccesso(AccessModeConfig config) =>
        config.Sigillo = CalcolaHmac(TestoModalita(config.SenzaPin));

    public static bool SigilloModalitaAccessoValido(AccessModeConfig config) =>
        !string.IsNullOrEmpty(config.Sigillo) &&
        FixedTimeEquals(config.Sigillo, CalcolaHmac(TestoModalita(config.SenzaPin)));

    /// <summary>Modalità di accesso effettiva: sigillo assente/non valido → "con PIN" (il default più
    /// restrittivo), mai "senza PIN" per un file manomesso.</summary>
    public static bool SenzaPinEffettivo(AccessModeConfig config) =>
        SigilloModalitaAccessoValido(config) && config.SenzaPin;

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    /// <summary>Harness di correzione della cartella dati (piano 6.4.2, §4b, punto 3: "la cartella dati della
    /// release verrà sigillata da un harness"): sigilla i ruoli ATTUALI così come sono (nessuna modifica di
    /// merito, nessuna promozione/retrocessione), usato da tools/data-fix-6.4.2 su una AppState già
    /// inizializzata su una copia di lavoro della cartella dati — mai sulla cartella sorgente. Ritorna il
    /// numero di operatori risigillati (per il report dell'harness); idempotente — richiamato su una cartella
    /// già sigillata non cambia nulla e ritorna 0. Non tocca l'impostazione di modalità di accesso: un file
    /// assente ricade già in sicurezza su "con PIN" (SenzaPinEffettivo sopra), quindi non richiede correzione.</summary>
    public static int SigillaRuoliEsistenti(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var repo = state.Master.Operatori;
        var lista = repo.Load();
        var expectedSnapshot = EntityStateFingerprint.Clone(lista);
        var risigillati = 0;
        foreach (var operatore in lista)
        {
            if (SigilloRuoloValido(operatore)) continue;
            Sigilla(operatore);
            risigillati++;
        }
        if (risigillati > 0) repo.Save(lista, expectedSnapshot);
        return risigillati;
    }
}
