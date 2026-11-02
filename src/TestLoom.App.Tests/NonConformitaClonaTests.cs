using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>NonConformita.Clona() (feedback utente 2026-09-17, volumi 100k: NonConformitaRepository.GetAll()
/// costava diversi secondi su un archivio di 100k NC perché clonava ogni record con un giro JSON completo).
/// Clona() deve produrre lo stesso risultato — stesso contenuto, isolamento completo — ma senza serializzare.</summary>
public class NonConformitaClonaTests
{
    private static NonConformita CreaNcCompleta() => new()
    {
        Id = "NC-CLONA-1",
        Uid = Guid.NewGuid(),
        RevisioneDati = 3,
        TestCaseId = "TC-1",
        DataApertura = new DateTime(2026, 1, 10),
        DataChiusura = new DateTime(2026, 2, 1),
        Owner = "Mario",
        StatoWorkflow = StatoAnomalia.Confermata,
        Severita = SeveritaAnomalia.High,
        Priorita = PrioritaOperativa.Major,
        Destinatario = DestinatarioSegnalazione.TD,
        StatoDettaglio = StatoDettaglioNc.InFix,
        DataFix = new DateTime(2026, 3, 1),
        CausaProbabile = CausaProbabileUdt.UnitLogger,
        CausaProbabileAltro = "altro",
        MotivoChiusura = "risolto",
        Descrizione = "descrizione",
        Azione = "azione",
        CondizioneOrigineUid = Guid.NewGuid(),
        ChangeRequestIds = new List<string> { "CR-1", "CR-2" },
        DaSegnalazioneRete = true,
        EcuSegnalate = new EcuSelection(new[] { "BCM", "SGW" }),
        VeicoliSegnalati = new List<string> { "VeicoloX" },
        FunzionalitaSegnalate = new List<string> { "FunzA", "FunzB" },
        StrumentoSegnalato = "Asist",
        ValoriStrumentoSegnalato = new Dictionary<string, string> { ["Asist::Licenza"] = "LIC-1" },
        DescrizioneBreveSegnalata = "sintesi",
        NumeriPraticaRete = new List<string> { "PRATICA-1" },
        VinCliente = new List<string> { "Matricola-1" },
        Licenze = new List<string> { "LIC-1", "LIC-2" },
        ConcessionariaOfficina = "Officina Rossi",
        GruppoSegnalazioneUid = Guid.NewGuid(),
        ProveCollegate = new List<RiferimentoProva> { new() { TestId = "TC-1", EsecuzioneIterazione = 1, CondizioneUid = Guid.NewGuid(), EvidenzaComparativa = true } },
        CollegamentiPolarion = new List<CollegamentoPolarion> { new() { Codice = "TICKET-1", Url = "https://x", Note = "nota" } },
        Allegati = new List<Allegato> { new() { NomeFile = "f.pdf", DimensioneByte = 10, Sha256 = "abc", Descrizione = "d" } },
        Changelog = new List<ChangelogEntry> { new() { Campo = "Owner", ValorePrecedente = "A", ValoreNuovo = "Mario" } },
    };

    [Fact]
    public void Clona_ProduceLoStessoContenutoDiUnGiroJsonCompleto()
    {
        var originale = CreaNcCompleta();
        var viaClona = originale.Clona();
        var viaJson = EntityStateFingerprint.Clone(originale);

        Assert.Equal(
            JsonSerializer.Serialize(viaJson, JsonIo.Options),
            JsonSerializer.Serialize(viaClona, JsonIo.Options));
    }

    [Fact]
    public void Clona_ERealmenteIndipendente_MutareLaCopiaNonToccaLOriginale()
    {
        var originale = CreaNcCompleta();
        var copia = originale.Clona();

        copia.Owner = "Cambiato";
        copia.ProveCollegate.Add(new RiferimentoProva { TestId = "TC-2" });
        copia.CollegamentiPolarion[0].Codice = "TICKET-CAMBIATO";
        copia.Licenze.Add("LIC-3");
        copia.ValoriStrumentoSegnalato["Nuovo"] = "valore";
        copia.Allegati[0].Descrizione = "cambiata";
        copia.Changelog.Add(new ChangelogEntry { Campo = "X" });

        Assert.Equal("Mario", originale.Owner);
        Assert.Single(originale.ProveCollegate);
        Assert.Equal("TICKET-1", originale.CollegamentiPolarion[0].Codice);
        Assert.Equal(2, originale.Licenze.Count);
        Assert.DoesNotContain("Nuovo", originale.ValoriStrumentoSegnalato.Keys);
        Assert.Equal("d", originale.Allegati[0].Descrizione);
        Assert.Single(originale.Changelog);
    }

    [Fact]
    public void Clona_ConEstensioneDatiIgnotiELiPreserva()
    {
        var originale = CreaNcCompleta();
        using var documento = JsonDocument.Parse(JsonSerializer.Serialize(originale, JsonIo.Options));
        var conCampoIgnoto = new Dictionary<string, JsonElement>();
        foreach (var proprieta in documento.RootElement.EnumerateObject()) conCampoIgnoto[proprieta.Name] = proprieta.Value.Clone();
        conCampoIgnoto["CampoFuturoIgnoto"] = JsonSerializer.SerializeToElement("valore-futuro");
        var jsonConEstensione = JsonSerializer.Serialize(conCampoIgnoto);

        var conEstensione = JsonSerializer.Deserialize<NonConformita>(jsonConEstensione, JsonIo.Options)!;
        Assert.NotNull(conEstensione.ExtensionData);
        Assert.True(conEstensione.ExtensionData!.ContainsKey("CampoFuturoIgnoto"));

        var copia = conEstensione.Clona();
        Assert.NotNull(copia.ExtensionData);
        Assert.NotSame(conEstensione.ExtensionData, copia.ExtensionData);
        Assert.True(copia.ExtensionData!.ContainsKey("CampoFuturoIgnoto"));
    }
}
