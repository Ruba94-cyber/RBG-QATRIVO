using Avalonia.Controls;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
public sealed class RequisitiVeicoliEcuHeadlessTests
{
    [Fact]
    public void PickerOffersOnlyMountedEcusAndKeepsLegacyAssociationsRemovable()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_D4dPicker_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var state = TestStateFactory.Create();
        try
        {
            state.InizializzaCartella(root);
            Assert.True(state.InizializzazioneWorkspaceCompletata);
            state.Master.Veicoli.Save([new Veicolo { Nome = "V1" }, new Veicolo { Nome = "V2" }]);
            state.Master.Componente.Save([
                new Componente { Nome = "V1-componente", VeicoliAssociati = ["v1"] },
                new Componente { Nome = "V2-componente", VeicoliAssociati = ["V2"] },
                new Componente { Nome = "SHARED", VeicoliAssociati = ["V1", "v2"] },
                new Componente { Nome = "NO-VEHICLE" },
                new Componente { Nome = "ARCHIVED", Archiviata = true, VeicoliAssociati = ["V1"] }
            ]);
            var requirement = new Requisito { Id = "Extra-1", Testo = "Synthetic" };
            RequisitoApplicabilita.ImpostaTipo(requirement, TipoRequisito.Extra);
            RequisitoApplicabilita.ImpostaSottoveicoli(requirement, ["V1", "V2"]);
            requirement.VeicoliEcuD4D = [
                new RequisitoVeicoloEcu { Veicolo = "V1", Componente = "V2-componente" },
                new RequisitoVeicoloEcu { Veicolo = "V1", Componente = "ARCHIVED" },
                new RequisitoVeicoloEcu { Veicolo = "V2", Componente = "SHARED" }
            ];
            using var vm = new RequisitiViewModel(state, TipoRequisito.Extra);
            LayoutHeadlessSessione.Esegui(() =>
            {
                var button = new Button();
                var panel = RequisitiView.CreaPannelloVeicoliEcu(vm, requirement, button);
                static Dictionary<string, CheckBox> Componente(StackPanel p, string vehicle)
                {
                    var group = p.Children.OfType<Expander>().Single(x => x.Header?.ToString() == vehicle);
                    return ((StackPanel)group.Content!).Children.OfType<CheckBox>()
                        .ToDictionary(x => x.Content!.ToString()!, StringComparer.OrdinalIgnoreCase);
                }

                var v1 = Componente(panel, "V1");
                var v2 = Componente(panel, "V2");
                Assert.Equal(new[] { "ARCHIVED", "SHARED", "V1-componente", "V2-componente" }, v1.Keys.OrderBy(x => x).ToArray());
                Assert.Equal(new[] { "SHARED", "V2-componente" }, v2.Keys.OrderBy(x => x).ToArray());
                Assert.DoesNotContain("NO-VEHICLE", v1.Keys);
                Assert.True(v1["ARCHIVED"].IsChecked);
                Assert.True(v1["V2-componente"].IsChecked);
                Assert.True(v1["V2-componente"].IsEnabled);
                Assert.True(v2["SHARED"].IsChecked);

                // A no-longer-mounted legacy association stays visible only to remove it.
                v1["V2-componente"].IsChecked = false;
                Assert.False(v1["V2-componente"].IsEnabled);
                v1["ARCHIVED"].IsChecked = false;
                Assert.False(v1["ARCHIVED"].IsEnabled);
                Assert.DoesNotContain(requirement.VeicoliEcuD4D!, x => x.Veicolo == "V1" && x.Componente == "V2-componente");
                v1["V1-componente"].IsChecked = true;
                Assert.Contains(requirement.VeicoliEcuD4D!, x => x.Veicolo == "V1" && x.Componente == "V1-componente");
                Assert.Contains(requirement.VeicoliEcuD4D!, x => x.Veicolo == "V2" && x.Componente == "SHARED");
                var reopened = RequisitiView.CreaPannelloVeicoliEcu(vm, requirement, button);
                Assert.DoesNotContain("V2-componente", Componente(reopened, "V1").Keys);
                Assert.DoesNotContain("ARCHIVED", Componente(reopened, "V1").Keys);
            });
            state.Master.Requisiti.Save([requirement]);
            var saved = Assert.Single(state.Master.Requisiti.Load());
            Assert.Contains(saved.VeicoliEcuD4D!, x => x.Veicolo == "V1" && x.Componente == "V1-componente");
            Assert.Contains(saved.VeicoliEcuD4D!, x => x.Veicolo == "V2" && x.Componente == "SHARED");
            Assert.DoesNotContain(saved.VeicoliEcuD4D!, x => x.Veicolo == "V1" && x.Componente == "V2-componente");
        }
        finally
        {
            state.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
