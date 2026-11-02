using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Esito di un passo della sonda periodica della radice condivisa (6.7.1). Il passo dice se la radice risponde, se serve
/// una rilettura di recupero del workspace e, quando la radice non risponde, quale stato di condivisione mostrare.
/// </summary>
public sealed record PassoSonda(bool Utilizzabile, bool Ricalcola, EsitoStatoCondivisione? NuovoStato);

/// <summary>
/// Sonda periodica della cartella dati (6.7.1). Il banner "cartella non disponibile" prima dipendeva dall'ultimo esito
/// letto dalla cache (<see cref="RadiceCondivisaSonda.PresenteUi"/>) e veniva nascosto proprio quando la cartella
/// spariva; nessun giro la ricalcolava dopo la perdita. Questa classe decide, senza I/O, cosa fare a ogni esito della
/// sonda: la sonda vera (con tetto di tempo) viene passata da fuori e gira sempre fuori dal thread UI.
/// </summary>
public sealed class MonitorRaggiungibilitaRadice
{
    /// <summary>Intervallo del giro periodico della sonda. Il costo di un giro è una verifica di esistenza della radice.</summary>
    public static readonly TimeSpan IntervalloSonda = TimeSpan.FromSeconds(15);

    /// <summary>Attesa minima tra due riletture di recupero quando la radice risponde ma lo stato resta non disponibile
    /// (per esempio cartella presente ma non scrivibile): evita di riprovare la prova di scrittura ad ogni giro.</summary>
    public static readonly TimeSpan RitardoRiprovaRecupero = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private bool _utilizzabilePrecedente = true;
    private DateTime _ultimaRilettura = DateTime.MinValue;

    /// <summary>Vero se la radice risulta utilizzabile all'ultimo passo (Presente o Offline).</summary>
    public bool UltimaUtilizzabile
    {
        get { lock (_gate) return _utilizzabilePrecedente; }
    }

    /// <summary>Vero se l'esito della sonda indica una radice utilizzabile (Presente o Offline, come PresenteUi).</summary>
    public static bool EUtilizzabile(RadiceCondivisaSonda.Esito esito) => esito is RadiceCondivisaSonda.Esito.Presente or RadiceCondivisaSonda.Esito.Offline;

    /// <summary>
    /// Messaggio per lo stato "non disponibile" della radice. Testo in italiano, come gli altri messaggi di
    /// CondivisioneHealthService, perché il banner lo compone con il prefisso già localizzato.
    /// </summary>
    public static string MessaggioNonRaggiungibile(RadiceCondivisaSonda.Esito esito) => esito switch
    {
        RadiceCondivisaSonda.Esito.Lenta => "la cartella dati non risponde entro il tempo previsto; nessuna modifica viene salvata sul condiviso.",
        RadiceCondivisaSonda.Esito.Inaccessibile => "la cartella dati non è leggibile; nessuna modifica viene salvata sul condiviso.",
        _ => "la cartella dati non è più raggiungibile; nessuna modifica viene salvata sul condiviso.",
    };

    /// <summary>
    /// Un passo della sonda. <paramref name="forza"/> vale per "Aggiorna ora": con la radice raggiungibile produce sempre una
    /// rilettura, anche se lo stato è già operativo. Chiamato solo fuori dal thread UI (la sonda può attendere il tetto di tempo).
    /// </summary>
    public PassoSonda Passo(RadiceCondivisaSonda.Esito esito, Models.StatoCondivisione statoCorrente, bool forza, DateTime ora)
    {
        lock (_gate)
        {
            if (!EUtilizzabile(esito))
            {
                _utilizzabilePrecedente = false;
                return new PassoSonda(false, false, new EsitoStatoCondivisione
                {
                    Stato = Models.StatoCondivisione.NonDisponibile,
                    Messaggio = MessaggioNonRaggiungibile(esito),
                    VerificatoIl = ora,
                });
            }

            var transizione = !_utilizzabilePrecedente;
            _utilizzabilePrecedente = true;
            var recuperoPendente = statoCorrente == Models.StatoCondivisione.NonDisponibile &&
                                   ora - _ultimaRilettura >= RitardoRiprovaRecupero;
            var ricalcola = forza || transizione || recuperoPendente;
            if (ricalcola) _ultimaRilettura = ora;
            return new PassoSonda(true, ricalcola, null);
        }
    }

    /// <summary>
    /// Vero se il banner di stato va mostrato: il workspace è inizializzato e lo stato non è operativo. Non dipende più da
    /// <see cref="AppState.CartellaValida"/>, che è falso proprio quando la cartella non c'è.
    /// </summary>
    public static bool MostraBanner(bool workspaceInizializzato, Models.StatoCondivisione stato) =>
        workspaceInizializzato && stato != Models.StatoCondivisione.Operativa;
}
