using System.Diagnostics;
using System.Text.Json.Nodes;

namespace TestLoom.App.Tests;

/// <summary>
/// FASE 4 (multi-PC 6.4.2, correzione orchestratore "nessun limite di budget, sincronizzazione verificata e
/// definitiva"): harness a due processi REALI. DeviceIdentityService/WorkspaceLocalPaths sono statici per
/// processo (vedi WorkspaceLocalPaths.LocalApplicationDataOverrideForTests): due "PC" nello stesso processo
/// .NET condividerebbero device-id e indici locali, che è esattamente il vincolo che rende un singolo processo
/// inadatto a validare la sincronizzazione multi-PC. Qui ogni "PC" è un processo Windows realmente separato
/// (TestLoom.App.MultiPcHost.exe), con la propria cartella "%LOCALAPPDATA%" e quindi il proprio device-id e i
/// propri indici SQLite locali, che legge/scrive sulla STESSA cartella condivisa reale sul filesystem.
///
/// LIMITE DICHIARATO (aggiornato, revisione successiva): qui la cartella condivisa è ancora il filesystem reale
/// senza simulazione di OneDrive/SharePoint — resta il harness più semplice/veloce per verificare visibilità e
/// modifiche di base. Il simulatore dedicato (ritardi/riordino, scritture a metà, lock temporanei, cancellazioni
/// ritardate, copie di conflitto) è stato costruito in <see cref="OneDriveFolderSyncSimulator"/> e usato dagli
/// scenari end-to-end a due processi in <see cref="MultiPcSyncScenarios"/>, che sostituisce l'annotazione
/// precedente ("non costruito in questa sessione") per quella parte. Vedi MultiPcSyncScenarios per la copertura
/// dichiarata sui 9 scenari richiesti e per cosa resta lavoro residuo.
/// </summary>
[Trait("Category", "Lento")]
public sealed class MultiPcSyncTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "TestLoom_MultiPC_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _localA = Path.Combine(Path.GetTempPath(), "TestLoom_MultiPC_PcA_" + Guid.NewGuid().ToString("N"));
    private readonly string _localB = Path.Combine(Path.GetTempPath(), "TestLoom_MultiPC_PcB_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var dir in new[] { _shared, _localA, _localB })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void PcA_CreaTest_PcB_LoVedeELoModifica_PcA_VedeLaModifica_NessunFileConProblemi()
    {
        Directory.CreateDirectory(_shared);
        using var pcA = new HostProcess(_shared, _localA);
        using var pcB = new HostProcess(_shared, _localB);

        // Due processi OS distinti sulla stessa cartella condivisa devono avere identità diverse: è la prova
        // diretta che questo harness isola davvero due "PC", non una finzione nello stesso processo.
        Assert.NotEqual(pcA.DeviceId, pcB.DeviceId);

        // PC A crea un Test nuovo.
        pcA.Send(new JsonObject
        {
            ["cmd"] = "createTest",
            ["id"] = "T-MP-0001",
            ["componente"] = "ECM",
            ["veicolo"] = "Daily",
            ["descrizione"] = "creato da PC A"
        });

        // PC B legge dal disco condiviso e deve vederlo, senza alcun file illeggibile/strano.
        pcB.Send(new JsonObject { ["cmd"] = "refresh" });
        var vistoDaB = pcB.Send(new JsonObject { ["cmd"] = "getTest", ["id"] = "T-MP-0001" });
        Assert.True(vistoDaB["trovato"]!.GetValue<bool>(), "PC B non vede il Test creato da PC A dopo il refresh.");
        Assert.Equal("creato da PC A", vistoDaB["descrizione"]!.GetValue<string>());
        AssertNessunProblema(pcB, "PC B dopo aver visto il Test creato da A");

        // PC B lo modifica tranquillamente.
        pcB.Send(new JsonObject { ["cmd"] = "editTest", ["id"] = "T-MP-0001", ["descrizione"] = "modificato da PC B" });

        // PC A rilegge e deve vedere la modifica di B, senza alcun file con problemi.
        pcA.Send(new JsonObject { ["cmd"] = "refresh" });
        var vistoDaA = pcA.Send(new JsonObject { ["cmd"] = "getTest", ["id"] = "T-MP-0001" });
        Assert.True(vistoDaA["trovato"]!.GetValue<bool>());
        Assert.Equal("modificato da PC B", vistoDaA["descrizione"]!.GetValue<string>());
        AssertNessunProblema(pcA, "PC A dopo aver visto la modifica di B");
        AssertNessunProblema(pcB, "PC B a fine scenario");
    }

    [Fact]
    public void PcA_EliminaTest_PcB_NonLoVedePiuTraAttiviDopoIlRefresh()
    {
        Directory.CreateDirectory(_shared);
        using var pcA = new HostProcess(_shared, _localA);
        using var pcB = new HostProcess(_shared, _localB);

        pcA.Send(new JsonObject
        {
            ["cmd"] = "createTest",
            ["id"] = "T-MP-0002",
            ["componente"] = "ECM",
            ["veicolo"] = "Daily",
            ["descrizione"] = "da eliminare"
        });
        pcB.Send(new JsonObject { ["cmd"] = "refresh" });
        Assert.True(pcB.Send(new JsonObject { ["cmd"] = "getTest", ["id"] = "T-MP-0002" })["trovato"]!.GetValue<bool>());

        pcA.Send(new JsonObject { ["cmd"] = "deleteTest", ["id"] = "T-MP-0002", ["operatore"] = "PC-A" });

        pcB.Send(new JsonObject { ["cmd"] = "refresh" });
        var elencoB = pcB.Send(new JsonObject { ["cmd"] = "listTests" })["tests"]!.AsArray();
        Assert.DoesNotContain(elencoB, t => t!["id"]!.GetValue<string>() == "T-MP-0002");
        AssertNessunProblema(pcB, "PC B dopo aver visto l'eliminazione da parte di A");
    }

    private static void AssertNessunProblema(HostProcess pc, string contesto)
    {
        var esito = pc.Send(new JsonObject { ["cmd"] = "listProblemi" });
        var fileConProblemi = esito["fileConProblemi"]!.AsArray();
        var copieDiConflitto = esito["copieDiConflitto"]!.AsArray();
        Assert.True(fileConProblemi.Count == 0,
            $"{contesto}: {fileConProblemi.Count} 'File con problemi' inattesi: " +
            string.Join(" | ", fileConProblemi.Select(p => p!["errore"]!.GetValue<string>())));
        Assert.True(copieDiConflitto.Count == 0,
            $"{contesto}: {copieDiConflitto.Count} 'Copie di conflitto' inattese: " +
            string.Join(" | ", copieDiConflitto.Select(p => p!["errore"]!.GetValue<string>())));
    }

    /// <summary>Un processo TestLoom.App.MultiPcHost.exe reale, pilotato via stdin/stdout JSON-per-riga.</summary>
    private sealed class HostProcess : IDisposable
    {
        private readonly Process _process;
        public string DeviceId { get; }

        public HostProcess(string shared, string local)
        {
            Directory.CreateDirectory(local);
            var exe = LocateHostExecutable();
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--shared");
            psi.ArgumentList.Add(shared);
            psi.ArgumentList.Add("--local");
            psi.ArgumentList.Add(local);
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Impossibile avviare TestLoom.App.MultiPcHost.");

            var readyLine = ReadLineWithTimeout(TimeSpan.FromSeconds(30))
                ?? throw new TimeoutException("TestLoom.App.MultiPcHost non ha risposto 'ready' entro il timeout." + LeggiStderr());
            var ready = JsonNode.Parse(readyLine)!.AsObject();
            if (ready["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Avvio host fallito: " + readyLine);
            DeviceId = ready["deviceId"]!.GetValue<string>();
        }

        public JsonObject Send(JsonObject comando)
        {
            _process.StandardInput.WriteLine(comando.ToJsonString());
            _process.StandardInput.Flush();
            var riga = ReadLineWithTimeout(TimeSpan.FromSeconds(30))
                ?? throw new TimeoutException($"Nessuna risposta dall'host per il comando '{comando["cmd"]}'." + LeggiStderr());
            var risposta = JsonNode.Parse(riga)!.AsObject();
            if (risposta["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"Comando '{comando["cmd"]}' fallito: {riga}");
            return risposta;
        }

        private string? ReadLineWithTimeout(TimeSpan timeout)
        {
            var task = _process.StandardOutput.ReadLineAsync();
            return task.Wait(timeout) ? task.Result : null;
        }

        private string LeggiStderr()
        {
            try
            {
                var err = _process.StandardError.ReadToEnd();
                return string.IsNullOrWhiteSpace(err) ? "" : $" stderr: {err}";
            }
            catch { return ""; }
        }

        private static string LocateHostExecutable()
        {
            var nomeEseguibile = OperatingSystem.IsWindows() ? "TestLoom.App.MultiPcHost.exe" : "TestLoom.App.MultiPcHost";
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            // Risale da .../src/TestLoom.App.Tests/bin/<config>/net10.0/ fino a trovare la cartella "src",
            // poi cerca l'eseguibile del progetto sibling qualunque sia la Configuration/RID di build.
            var cursore = dir;
            while (cursore != null && !string.Equals(cursore.Name, "src", StringComparison.OrdinalIgnoreCase))
                cursore = cursore.Parent;
            if (cursore == null)
                throw new InvalidOperationException($"Impossibile risalire alla cartella 'src' da {AppContext.BaseDirectory}.");

            var hostDir = Path.Combine(cursore.FullName, "TestLoom.App.MultiPcHost");
            var trovato = Directory.Exists(hostDir)
                ? Directory.EnumerateFiles(hostDir, nomeEseguibile, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            return trovato ?? throw new InvalidOperationException(
                $"TestLoom.App.MultiPcHost non trovato sotto {hostDir}. Compila TestLoom.slnx (include il progetto host) prima di eseguire MultiPcSyncTests.");
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
                }
            }
            catch { }
            _process.Dispose();
        }
    }
}
