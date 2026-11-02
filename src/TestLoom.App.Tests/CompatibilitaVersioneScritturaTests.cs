using System.Text.Json.Nodes;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Gate di compatibilità del manifest condiviso (P2.11, P2.12). P2.11: una sonda su rete abbandonata dal tetto di tempo
/// non scrive mai il manifest. P2.12: un alzamento dello schema non riuscito resta visibile in sospeso e viene ritentato
/// nel corso della sessione, come il marcatore del catalogo; il workspace resta operativo (contratto non bloccante).
/// </summary>
[Collection("CondivisioneRete")]
public sealed class CompatibilitaVersioneScritturaTests : IDisposable
{
    private static readonly Version Client673 = new(6, 7, 3, 0);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_CompatScrittura_" + Guid.NewGuid().ToString("N"));

    public CompatibilitaVersioneScritturaTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        CompatibilitaVersioneService.PausaSondaPerTest = null;
        CompatibilitaVersioneService.TimeoutSondaOverride = null;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void SondaAbbandonataDalTetto_NonScriveMaiIlManifest()
    {
        var paths = new DataPaths(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.VersionManifestFile)!);
        using var modalita = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreRete, _root);
        var sondaPartita = new ManualResetEventSlim(false);
        var rilascio = new ManualResetEventSlim(false);
        CompatibilitaVersioneService.PausaSondaPerTest = () =>
        {
            sondaPartita.Set();
            rilascio.Wait(TimeSpan.FromSeconds(30));
        };
        CompatibilitaVersioneService.TimeoutSondaOverride = TimeSpan.FromMilliseconds(300);
        try
        {
            var esito = CompatibilitaVersioneService.Applica(paths, Operativa());

            Assert.Equal(StatoCondivisione.SolaLettura, esito.Stato);
            Assert.True(sondaPartita.IsSet, "la sonda deve essere partita nel worker con tetto di tempo");
            // La sonda abbandonata riprende e termina: non deve scrivere nulla dopo lo scadere del tempo.
            rilascio.Set();
            Thread.Sleep(1000);
            Assert.False(File.Exists(paths.VersionManifestFile), "una sonda abbandonata dal tetto non deve mai scrivere il manifest");
        }
        finally
        {
            rilascio.Set();
        }
    }

    [Fact]
    public void ManifestMinimo611_BloccaIlClientPrecedenteSenzaModificareIlManifest()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        using var modalita = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreOneDrive, _root);
        File.WriteAllText(paths.VersionManifestFile, """
            {
              "ManifestSchema": 1,
              "VersioneCorrente": "6.7.11",
              "VersioneMinima": "6.7.11",
              "SchemaDatiMassimo": 10,
              "PacchettoRelativo": "updates/TestLoom-v6.7.11-win-x64.zip",
              "PubblicatoIl": "2026-10-22T15:48:08+02:00"
            }
            """);
        var manifestIniziale = File.ReadAllText(paths.VersionManifestFile);

        var esito = CompatibilitaVersioneService.Applica(paths, Operativa(), new Version(6, 7, 10));

        Assert.Equal(StatoCondivisione.SolaLettura, esito.Stato);
        Assert.Contains("Aggiornamento richiesto: versione minima 6.7.11", esito.Messaggio);
        Assert.Equal(manifestIniziale, File.ReadAllText(paths.VersionManifestFile));
    }

    [Fact]
    public void AlzamentoSchemaFallito_RestaInSospesoEVieneRitentatoQuandoLaCartellaRiprende()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        File.WriteAllText(paths.VersionManifestFile,
            "{\n  \"ManifestSchema\": 1,\n  \"VersioneCorrente\": \"6.5.0\",\n  \"VersioneMinima\": \"6.1.0\",\n" +
            "  \"SchemaDatiMassimo\": 8,\n  \"PacchettoRelativo\": \"updates/TestLoom-latest.zip\",\n" +
            "  \"PubblicatoIl\": \"2026-10-08T03:13:27+02:00\"\n}");
        File.SetAttributes(paths.VersionManifestFile, FileAttributes.ReadOnly);
        try
        {
            var esito = CompatibilitaVersioneService.AllineaSchemaDatiManifest(paths, Client673);

            Assert.Equal(CompatibilitaVersioneService.EsitoAllineamentoSchema.ScritturaFallita, esito);
            Assert.True(CompatibilitaVersioneService.AlzamentoSchemaInSospeso(paths), "il fallimento deve restare visibile in sospeso");
        }
        finally
        {
            File.SetAttributes(paths.VersionManifestFile, FileAttributes.Normal);
        }

        // La cartella riprende a scrivere: il nuovo tentativo alza lo schema e chiude la sospensione.
        Assert.True(CompatibilitaVersioneService.RiprovaAlzamentoSchema(paths));
        Assert.False(CompatibilitaVersioneService.AlzamentoSchemaInSospeso(paths));
        Assert.Equal(TestCase.CurrentSchemaVersion,
            JsonNode.Parse(File.ReadAllText(paths.VersionManifestFile))!["SchemaDatiMassimo"]!.GetValue<int>());
    }

    private static EsitoStatoCondivisione Operativa() =>
        new() { Stato = StatoCondivisione.Operativa, Messaggio = "ok", VerificatoIl = DateTime.Now };
}
