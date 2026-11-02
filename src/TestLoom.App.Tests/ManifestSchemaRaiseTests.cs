using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.5.1: il client alza schemaDatiMassimo del manifest condiviso (monotono, idempotente, non bloccante).</summary>
public sealed class ManifestSchemaRaiseTests
{
    private static readonly Version Client651 = new(6, 5, 1, 0);

    private static (TempDirectory Temp, DataPaths Paths) Crea(int schema, string versioneMinima = "6.1.0")
    {
        var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        paths.EnsureStructure();
        File.WriteAllText(paths.VersionManifestFile,
            "{\n  \"ManifestSchema\": 1,\n  \"VersioneCorrente\": \"6.5.0\",\n  \"VersioneMinima\": \"" + versioneMinima + "\",\n" +
            "  \"SchemaDatiMassimo\": " + schema + ",\n  \"PacchettoRelativo\": \"updates/TestLoom-v6.5.0-win-x64.zip\",\n" +
            "  \"CampoFuturo\": \"resta\",\n  \"PubblicatoIl\": \"2026-10-08T03:13:27+02:00\"\n}");
        return (temp, paths);
    }

    private static int Schema(DataPaths p) =>
        JsonNode.Parse(File.ReadAllText(p.VersionManifestFile))!["SchemaDatiMassimo"]!.GetValue<int>();

    private static EsitoStatoCondivisione Operativa() =>
        new() { Stato = StatoCondivisione.Operativa, Messaggio = "ok", VerificatoIl = DateTime.Now };

    [Fact]
    public void ManifestVecchio_ClientRecente_VieneAlzatoEPreservaGliAltriCampi()
    {
        var (temp, paths) = Crea(8);
        using (temp)
        {
            var esito = CompatibilitaVersioneService.AllineaSchemaDatiManifest(paths, Client651);
            Assert.Equal(CompatibilitaVersioneService.EsitoAllineamentoSchema.Alzato, esito);
            Assert.Equal(TestCase.CurrentSchemaVersion, Schema(paths));
            var json = JsonNode.Parse(File.ReadAllText(paths.VersionManifestFile))!;
            Assert.Equal("6.5.0", json["VersioneCorrente"]!.GetValue<string>());
            Assert.Equal("updates/TestLoom-v6.5.0-win-x64.zip", json["PacchettoRelativo"]!.GetValue<string>());
            Assert.Equal("resta", json["CampoFuturo"]!.GetValue<string>());
            Assert.Empty(Directory.GetFiles(paths.Root, "version-manifest.json.tmp*"));
        }
    }

    [Fact]
    public void ManifestVecchio_ViaApplica_ClientRecente_AlzaERestaOperativo()
    {
        var (temp, paths) = Crea(8);
        using (temp)
        {
            var r = CompatibilitaVersioneService.Applica(paths, Operativa(), Client651);
            Assert.Equal(StatoCondivisione.Operativa, r.Stato);
            Assert.Equal(TestCase.CurrentSchemaVersion, Schema(paths));
        }
    }

    [Fact]
    public void ManifestGiaAlTarget_RestaInvariatoByteAByte()
    {
        var (temp, paths) = Crea(TestCase.CurrentSchemaVersion);
        using (temp)
        {
            var prima = File.ReadAllBytes(paths.VersionManifestFile);
            var esito = CompatibilitaVersioneService.AllineaSchemaDatiManifest(paths, Client651);
            Assert.Equal(CompatibilitaVersioneService.EsitoAllineamentoSchema.GiaAggiornato, esito);
            Assert.Equal(prima, File.ReadAllBytes(paths.VersionManifestFile));
        }
    }

    [Fact]
    public void ManifestPiuNuovo_NonVieneMaiAbbassato()
    {
        var (temp, paths) = Crea(TestCase.CurrentSchemaVersion + 1);
        using (temp)
        {
            var prima = File.ReadAllBytes(paths.VersionManifestFile);
            Assert.Equal(CompatibilitaVersioneService.EsitoAllineamentoSchema.GiaAggiornato,
                CompatibilitaVersioneService.AllineaSchemaDatiManifest(paths, Client651));
            Assert.Equal(prima, File.ReadAllBytes(paths.VersionManifestFile));
            // Via Applica il client resta in sola lettura (schema piu recente dell'app) e non tocca il file.
            Assert.Equal(StatoCondivisione.SolaLettura, CompatibilitaVersioneService.Applica(paths, Operativa(), Client651).Stato);
            Assert.Equal(prima, File.ReadAllBytes(paths.VersionManifestFile));
        }
    }

    [Fact]
    public void ManifestCamelCase_AggiornaLaStessaChiaveSenzaDuplicati()
    {
        var (temp, paths) = Crea(8);
        using (temp)
        {
            File.WriteAllText(paths.VersionManifestFile, "{ \"schemaDatiMassimo\": 8, \"versioneMinima\": \"6.1.0\" }");
            Assert.Equal(CompatibilitaVersioneService.EsitoAllineamentoSchema.Alzato,
                CompatibilitaVersioneService.AllineaSchemaDatiManifest(paths, Client651));
            var json = JsonNode.Parse(File.ReadAllText(paths.VersionManifestFile))!.AsObject();
            Assert.Equal(TestCase.CurrentSchemaVersion, json["schemaDatiMassimo"]!.GetValue<int>());
            Assert.Equal(2, json.Count);
        }
    }

    [Fact]
    public void ManifestSenzaChiaveSchema_AggiungeLaChiaveLettaDallApp()
    {
        var (temp, paths) = Crea(8);
        using (temp)
        {
            File.WriteAllText(paths.VersionManifestFile, "{ \"VersioneMinima\": \"6.1.0\" }");
            CompatibilitaVersioneService.AllineaSchemaDatiManifest(paths, Client651);
            Assert.Equal(TestCase.CurrentSchemaVersion, Schema(paths));
        }
    }

    [Fact]
    public void ClientPrecedenteA651_NonToccaIlManifest()
    {
        var (temp, paths) = Crea(8);
        using (temp)
        {
            var prima = File.ReadAllBytes(paths.VersionManifestFile);
            Assert.Equal(CompatibilitaVersioneService.EsitoAllineamentoSchema.ClientTroppoVecchio,
                CompatibilitaVersioneService.AllineaSchemaDatiManifest(paths, new Version(6, 5, 0, 0)));
            CompatibilitaVersioneService.Applica(paths, Operativa(), new Version(6, 5, 0, 0));
            Assert.Equal(prima, File.ReadAllBytes(paths.VersionManifestFile));
        }
    }

    [Fact]
    public void ManifestNonScrivibile_NonSollevaENonBloccaIlWorkspace()
    {
        var (temp, paths) = Crea(8);
        using (temp)
        {
            File.SetAttributes(paths.VersionManifestFile, FileAttributes.ReadOnly);
            try
            {
                var esito = CompatibilitaVersioneService.AllineaSchemaDatiManifest(paths, Client651);
                Assert.Equal(CompatibilitaVersioneService.EsitoAllineamentoSchema.ScritturaFallita, esito);
                Assert.Equal(8, Schema(paths));
                Assert.Equal(StatoCondivisione.Operativa,
                    CompatibilitaVersioneService.Applica(paths, Operativa(), Client651).Stato);
            }
            finally { File.SetAttributes(paths.VersionManifestFile, FileAttributes.Normal); }
        }
    }

    [Fact]
    public void ManifestNonLeggibile_NonVieneSovrascritto()
    {
        var (temp, paths) = Crea(8);
        using (temp)
        {
            File.WriteAllText(paths.VersionManifestFile, "{{corrotto");
            Assert.Equal(CompatibilitaVersioneService.EsitoAllineamentoSchema.ScritturaFallita,
                CompatibilitaVersioneService.AllineaSchemaDatiManifest(paths, Client651));
            Assert.Equal("{{corrotto", File.ReadAllText(paths.VersionManifestFile));
        }
    }

    [Fact]
    public void ClientInSolaLettura_NonScriveIlManifest()
    {
        var (temp, paths) = Crea(8);
        using (temp)
        {
            var prima = File.ReadAllBytes(paths.VersionManifestFile);
            var sola = new EsitoStatoCondivisione { Stato = StatoCondivisione.SolaLettura, Messaggio = "x", VerificatoIl = DateTime.Now };
            Assert.Equal(StatoCondivisione.SolaLettura, CompatibilitaVersioneService.Applica(paths, sola, Client651).Stato);
            // Il preflight non mutante non scrive mai.
            CompatibilitaVersioneService.VerificaSenzaScrivere(paths, Operativa());
            Assert.Equal(prima, File.ReadAllBytes(paths.VersionManifestFile));
        }
    }

    [Fact]
    public void ScrittureConcorrenti_DuePcSimultanei_ConvergonoSenzaCorruzione()
    {
        var (temp, paths) = Crea(8);
        using (temp)
        {
            var esiti = new ConcurrentBag<CompatibilitaVersioneService.EsitoAllineamentoSchema>();
            var errori = new ConcurrentBag<Exception>();
            using var via = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            {
                try { via.Wait(); esiti.Add(CompatibilitaVersioneService.AllineaSchemaDatiManifest(paths, Client651)); }
                catch (Exception ex) { errori.Add(ex); }
            })).ToArray();
            via.Set();
            Task.WaitAll(tasks);

            Assert.Empty(errori);
            Assert.Equal(TestCase.CurrentSchemaVersion, Schema(paths));
            Assert.Equal("resta", JsonNode.Parse(File.ReadAllText(paths.VersionManifestFile))!["CampoFuturo"]!.GetValue<string>());
            Assert.Empty(Directory.GetFiles(paths.Root, "version-manifest.json.tmp*"));
            // Idempotenza dopo la convergenza.
            Assert.Equal(CompatibilitaVersioneService.EsitoAllineamentoSchema.GiaAggiornato,
                CompatibilitaVersioneService.AllineaSchemaDatiManifest(paths, Client651));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "testloom-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
