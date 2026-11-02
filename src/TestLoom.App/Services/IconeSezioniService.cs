using System.Globalization;
using TestLoom.App.Models;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Services;

/// <summary>Icone personalizzabili delle voci di navigazione (barra principale e sotto-sezioni di Impostazioni).
/// Personali PER PC: l'override vive solo in AppConfig.IconeSezioni (config.json locale), mai nei dati
/// condivisi, quindi nessun impatto su sync/multi-PC. Il default è l'icona con cui la voce è costruita
/// (NavItem.Icona); l'override ha chiave "ambito:idVoce" e un valore non valido (config modificata a mano)
/// viene ignorato, non propagato alla UI. Pura: chi la chiama si occupa di salvare AppConfig.</summary>
public static class IconeSezioniService
{
    public const string AmbitoPrincipale = "main";
    public const string AmbitoImpostazioni = "impostazioni";

    /// <summary>Massimo di caratteri UTF-16 accettati per un'icona (copre sequenze emoji con ZWJ).</summary>
    public const int LunghezzaMassima = 16;

    /// <summary>Set curato proposto nella galleria di Impostazioni (emoji comuni a Windows, nessun font extra).</summary>
    public static readonly IReadOnlyList<string> Galleria =
    [
        "🏠", "🧪", "⚠️", "📶", "📊", "🛡️", "🔩", "📦", "👤", "📋", "🧩", "⚙️", "🖥️", "🚚", "🧰", "🛟",
        "📁", "📂", "📝", "📌", "📎", "🔍", "🔧", "🛠️", "🔔", "🔒", "🔑", "⭐", "❤️", "✅", "❌", "❗",
        "🚗", "🚛", "🔋", "⚡", "🌐", "📡", "💾", "🗂️", "📈", "📉", "🧭", "🎯", "🧠", "💡", "🔬", "🏷️"
    ];

    public static string Chiave(string ambito, string id) => $"{ambito}:{id}";

    /// <summary>Vero se il valore è una sola "icona" (un grafema, es. emoji o simbolo), senza spazi o
    /// caratteri di controllo e di lunghezza ragionevole.</summary>
    public static bool Valida(string? glifo)
    {
        if (string.IsNullOrWhiteSpace(glifo)) return false;
        var valore = glifo.Trim();
        if (valore.Length > LunghezzaMassima) return false;
        foreach (var c in valore)
            if (char.IsControl(c) || char.IsWhiteSpace(c)) return false;
        return new StringInfo(valore).LengthInTextElements == 1;
    }

    /// <summary>Assegna l'ambito alle voci e applica gli override validi presenti in config (voci senza
    /// override, o con override non valido, restano sull'icona predefinita).</summary>
    public static void Applica(IEnumerable<NavItem> voci, string ambito, AppConfig config)
    {
        foreach (var voce in voci)
        {
            voce.Ambito = ambito;
            string? override_ = null;
            if (config.IconeSezioni != null
                && config.IconeSezioni.TryGetValue(voce.ChiaveIcona, out var salvato)
                && Valida(salvato))
                override_ = salvato.Trim();
            voce.IconaPersonalizzata = override_ == voce.Icona ? null : override_;
        }
    }

    /// <summary>Imposta l'icona della voce. Ritorna false (senza toccare nulla) se il valore non è valido.
    /// Scegliere l'icona predefinita equivale a ripristinarla (nessuna voce ridondante in config).</summary>
    public static bool Imposta(AppConfig config, NavItem voce, string? glifo)
    {
        if (!Valida(glifo)) return false;
        var valore = glifo!.Trim();
        if (valore == voce.Icona) { Ripristina(config, voce); return true; }
        config.IconeSezioni ??= new Dictionary<string, string>(StringComparer.Ordinal);
        config.IconeSezioni[voce.ChiaveIcona] = valore;
        voce.IconaPersonalizzata = valore;
        return true;
    }

    public static void Ripristina(AppConfig config, NavItem voce)
    {
        if (config.IconeSezioni != null)
        {
            config.IconeSezioni.Remove(voce.ChiaveIcona);
            if (config.IconeSezioni.Count == 0) config.IconeSezioni = null;
        }
        voce.IconaPersonalizzata = null;
    }

    public static void RipristinaTutte(AppConfig config, IEnumerable<NavItem> voci)
    {
        config.IconeSezioni = null;
        foreach (var voce in voci) voce.IconaPersonalizzata = null;
    }
}
