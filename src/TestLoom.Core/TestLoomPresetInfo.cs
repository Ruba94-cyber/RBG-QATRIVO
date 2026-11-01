namespace TestLoom.Core;

/// <summary>What the first-run preset selector shows for one preset in one language.</summary>
public sealed record TestLoomPresetCard(
    string Id,
    string DisplayName,
    string Description,
    string SubjectLabel,
    string ComponentLabel,
    string PlanLabel,
    IReadOnlyList<string> TestTemplates);

/// <summary>Localized presentation of the neutral presets (the presets themselves stay language-free identifiers).</summary>
public static class TestLoomPresetInfo
{
    public static readonly IReadOnlyList<string> Languages = new[] { "en", "it" };

    private static readonly Dictionary<string, (string NameIt, string DescEn, string DescIt, string SubjectIt, string ComponentIt, string PlanIt)> Table =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["software-qa"] = ("Qualità software",
                "Applications, modules and test suites: smoke, regression and compatibility checks.",
                "Applicazioni, moduli e suite di test: controlli smoke, di regressione e di compatibilità.",
                "Applicazione", "Modulo", "Suite di test"),
            ["hardware"] = ("Ingegneria hardware",
                "Devices and components: functional verification, endurance and safety checks.",
                "Dispositivi e componenti: verifica funzionale, prove di durata e controlli di sicurezza.",
                "Dispositivo", "Componente", "Piano di test"),
            ["manufacturing"] = ("Qualità di produzione",
                "Products and assemblies: incoming, in-process and final inspection plans.",
                "Prodotti e assemblaggi: piani di ispezione in ingresso, in processo e finale.",
                "Prodotto", "Assemblaggio", "Piano di ispezione"),
            ["laboratory"] = ("Laboratorio di ricerca",
                "Samples and parameters: procedure verification, measurement series and repeatability.",
                "Campioni e parametri: verifica delle procedure, serie di misure e ripetibilità.",
                "Campione", "Parametro", "Protocollo di prova"),
            ["blank"] = ("Area di lavoro vuota",
                "Start from nothing and define your own templates and vocabulary.",
                "Parti da zero e definisci modelli e vocabolario a tuo piacimento.",
                "Elemento", "Componente", "Piano di test"),
        };

    public static string NormalizeLanguage(string? language) =>
        string.Equals(language, "it", StringComparison.OrdinalIgnoreCase) ? "it" : "en";

    public static IReadOnlyList<TestLoomPresetCard> Cards(string language) =>
        TestLoomWorkspacePresets.All.Select(p => Card(p.Id, language)).ToArray();

    public static TestLoomPresetCard Card(string presetId, string language)
    {
        var preset = TestLoomWorkspacePresets.Find(presetId) ?? throw new ArgumentException("Unknown workspace preset.", nameof(presetId));
        var it = NormalizeLanguage(language) == "it";
        var t = Table[preset.Id];
        return new TestLoomPresetCard(preset.Id,
            it ? t.NameIt : preset.DisplayName,
            it ? t.DescIt : t.DescEn,
            it ? t.SubjectIt : preset.SubjectLabel,
            it ? t.ComponentIt : preset.ComponentLabel,
            it ? t.PlanIt : preset.PlanLabel,
            preset.SuggestedTestTemplates.Select(n => it ? WorkspaceLocalization.T(n, "it") : n).ToArray());
    }
}
