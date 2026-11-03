using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Runtime.CompilerServices;
using System.ComponentModel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class TableLayoutPersonalizationHeadlessTests
{
    [Fact]
    public void AnomalieECr_MostranoDescrizioniTroncateESalvanoAltezzaRigaPerRecord()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = TestStateFactory.Create();
            var nc = new NonConformitaViewModel(stato, _ => { });
            nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
            var testoDettagliato = string.Join("\n", Enumerable.Repeat("dettaglio lungo con righe che non devono espandere la tabella", 8));
            var primaRiga = new NcRiga
            {
                Nc = new NonConformita
                {
                    Id = "NC-LUNGA",
                    DescrizioneBreveSegnalata = "Sintesi obbligatoria",
                    Descrizione = testoDettagliato,
                },
            };
            var secondaRiga = new NcRiga
            {
                Nc = new NonConformita { Id = "NC-RESIZE", Descrizione = "Descrizione breve" },
            };
            nc.Righe.ReplaceWith([primaRiga, secondaRiga]);
            nc.ChangeRequests.ReplaceWith([
                new ChangeRequest { Id = "CR-1", Titolo = "Titolo CR", DescrizioneBreve = "Sintesi breve CR", Descrizione = testoDettagliato },
            ]);

            var chiaveTabella = TableLayoutPreferences.Chiave(stato.Config.OperatoreAttivo, stato.RuoloAttivo, "anomalie");
            var preferenza = new TableLayoutPreference { Versione = 2 };
            preferenza.AltezzeRighe[TableLayoutPreferences.ChiaveRiga(secondaRiga, 1)] = 104;
            stato.Config.LayoutTabelle[chiaveTabella] = preferenza;
            ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "1");

            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(
                new MainViewModel(stato) { MostraPrimoAvvio = false }, 1600, 1000);
            var vista = new NonConformitaView();
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
                LayoutHeadlessSessione.Assesta(finestra);
                vista.DataContext = nc;
                LayoutHeadlessSessione.Assesta(finestra);
                LayoutHeadlessSessione.Assesta(finestra);

                var grigliaNc = vista.FindControl<DataGrid>("AnomalieGrid")!;
                var rigaBreve = grigliaNc.GetVisualDescendants().OfType<DataGridRow>()
                    .Single(r => ReferenceEquals(r.DataContext, primaRiga));
                var rigaRidimensionata = grigliaNc.GetVisualDescendants().OfType<DataGridRow>()
                    .Single(r => ReferenceEquals(r.DataContext, secondaRiga));
                VerificaAzioniCentrate(grigliaNc, rigaBreve,
                    "Anomalie_Riga_Dettaglio_Button", "Anomalie_Riga_ModificaRapida_Button");
                var testoBreve = grigliaNc.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Text == "Sintesi obbligatoria");
                var testoLungo = grigliaNc.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Text == testoDettagliato);

                Assert.Equal(104, rigaRidimensionata.Height);
                Assert.True(rigaBreve.Bounds.Height <= 60, $"la descrizione a righe multiple ha allargato la riga a {rigaBreve.Bounds.Height:0}px");
                Assert.Equal(1, testoLungo.MaxLines);
                var tooltipBreve = Assert.IsType<TextBlock>(ToolTip.GetTip(testoBreve));
                Assert.Equal("Sintesi obbligatoria", tooltipBreve.Text);
                Assert.Equal(TextWrapping.Wrap, tooltipBreve.TextWrapping);
                Assert.Equal(TextTrimming.None, tooltipBreve.TextTrimming);
                var tooltipLungo = Assert.IsType<TextBlock>(ToolTip.GetTip(testoLungo));
                Assert.Equal(testoDettagliato, tooltipLungo.Text);
                Assert.Equal(TextWrapping.Wrap, tooltipLungo.TextWrapping);
                Assert.Equal(TextTrimming.None, tooltipLungo.TextTrimming);
                Assert.Contains(grigliaNc.Columns, c => c.Header?.ToString() == LocalizationService.Instance.Traduci("ColDescrizioneBreve"));
                Assert.Contains(grigliaNc.Columns, c => c.Header?.ToString() == LocalizationService.Instance.Traduci("ColDescrizioneLunga"));

                var grigliaCr = vista.FindControl<DataGrid>("ListaCr")!;
                var descrizioneBreveCr = grigliaCr.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => AutomationProperties.GetAutomationId(t) == "Anomalie_Cr_RigaDescrizioneBreve_Text");
                var descrizioneCr = grigliaCr.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => AutomationProperties.GetAutomationId(t) == "Anomalie_Cr_RigaDescrizione_Text");
                Assert.Equal("Sintesi breve CR", descrizioneBreveCr.Text);
                Assert.Equal("Sintesi breve CR", ToolTip.GetTip(descrizioneBreveCr));
                Assert.NotEqual(descrizioneBreveCr.Text, descrizioneCr.Text);
                Assert.Equal(1, descrizioneCr.MaxLines);
                Assert.Equal(testoDettagliato, ToolTip.GetTip(descrizioneCr));
                var intestazioneBreveCr = grigliaCr.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => AutomationProperties.GetAutomationId(t) == "Anomalie_Cr_ColonnaDescrizioneBreve_Text");
                Assert.Equal(LocalizationService.Instance.Traduci("ColDescrizioneBreve"), intestazioneBreveCr.Text);
                var intestazioneDescrizioneCr = grigliaCr.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => AutomationProperties.GetAutomationId(t) == "Anomalie_Cr_ColonnaDescrizione_Text");
                Assert.Equal(LocalizationService.Instance.Traduci("ColDescrizioneLunga"), intestazioneDescrizioneCr.Text);

                SimulaRidimensionamentoRiga(rigaRidimensionata, finestra, 32);
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Equal(136, rigaRidimensionata.Height);
                Assert.Equal(136, stato.Config.LayoutTabelle[chiaveTabella].AltezzeRighe[
                    TableLayoutPreferences.ChiaveRiga(secondaRiga, rigaRidimensionata.Index)]);
            }
            finally
            {
                finestra.Hide();
            }
        });
    }

    /// <summary>Regressione: le colonne della tabella CR devono essere ridimensionabili come quelle delle NC, sia con filtro
    /// "Tutto" sia con filtro "CR". Causa: la CR aveva una colonna Star (Titolo); con una colonna Star e contenuto piu' largo
    /// della vista il DataGrid comprime TUTTE le colonne in pixel al loro MinWidth e rifiuta di ridimensionarle (a meno del
    /// "congelamento" in pixel fatto da TableLayoutPreferences alla pressione, dopo il quale le colonne tornano di colpo alla
    /// larghezza dichiarata e scivolano sotto il puntatore). Ora tutte le colonne sono Pixel/Auto: nessuna compressione e
    /// ridimensionamento diretto.</summary>
    [Theory]
    [InlineData(FiltroTipoAnomaliaOCr.Entrambi, "AnomalieGrid")]
    [InlineData(FiltroTipoAnomaliaOCr.Entrambi, "ListaCr")]
    [InlineData(FiltroTipoAnomaliaOCr.ChangeRequest, "ListaCr")]
    public void ColonneAnomalieECr_NonUsanoStarESonoRidimensionabiliTrascinandoLIntestazione(FiltroTipoAnomaliaOCr filtro, string nomeGriglia) =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = TestStateFactory.Create();
            ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "1");
            var nc = new NonConformitaViewModel(stato, _ => { });
            nc.FiltroTipo = filtro;
            nc.Righe.ReplaceWith([new NcRiga { Nc = new NonConformita { Id = "NC-1", Descrizione = "d" } }]);
            nc.ChangeRequests.ReplaceWith([new ChangeRequest { Id = "CR-1", Titolo = "Titolo CR", DescrizioneBreve = "breve" }]);
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(
                new MainViewModel(stato) { MostraPrimoAvvio = false }, 1920, 1000);
            var vista = new NonConformitaView();
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
                LayoutHeadlessSessione.Assesta(finestra);
                vista.DataContext = nc;
                LayoutHeadlessSessione.Assesta(finestra);
                LayoutHeadlessSessione.Assesta(finestra);

                var griglia = vista.FindControl<DataGrid>(nomeGriglia)!;
                Assert.True(griglia.CanUserResizeColumns);
                Assert.DoesNotContain(griglia.Columns, c => c.Width.IsStar);
                if (nomeGriglia == "ListaCr")
                {
                    var azioni = LocalizationService.Instance.Traduci("ColonnaAzioni");
                    Assert.Single(griglia.Columns, c => c.Header as string == azioni);
                    Assert.Contains(griglia.Columns, c => c.Header as string == "Stato TICKET");
                    Assert.Contains(griglia.Columns, c => c.Header as string == "Aperto TICKET");
                }
                var compresse = griglia.Columns.Where(c => c.IsVisible && c.Width.IsAbsolute && c.ActualWidth < c.Width.Value - 0.5)
                    .Select(c => $"{c.Header}: {c.ActualWidth:0} < {c.Width.Value:0}").ToList();
                Assert.True(compresse.Count == 0, "colonne compresse sotto la larghezza dichiarata: " + string.Join("; ", compresse));

                var visibili = griglia.Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex).ToList();
                var intestazioni = griglia.GetVisualDescendants().OfType<DataGridColumnHeader>()
                    .Where(h => h.IsEffectivelyVisible && h.Bounds.Width > 0)
                    .OrderBy(h => h.TranslatePoint(new Point(0, 0), griglia)!.Value.X).ToList();
                var provate = 0;
                for (var i = 0; i < Math.Min(visibili.Count, intestazioni.Count); i++)
                {
                    var colonna = visibili[i];
                    var intestazione = intestazioni[i];
                    var origine = intestazione.TranslatePoint(new Point(intestazione.Bounds.Width - 2, intestazione.Bounds.Height / 2), finestra)!.Value;
                    if (origine.X + 40 >= griglia.Bounds.Width) break; // bordo oltre la vista: servirebbe lo scorrimento
                    var prima = colonna.ActualWidth;
                    finestra.MouseMove(origine);
                    finestra.MouseDown(origine, MouseButton.Left);
                    LayoutHeadlessSessione.Assesta(finestra);
                    finestra.MouseMove(new Point(origine.X + 40, origine.Y));
                    LayoutHeadlessSessione.Assesta(finestra);
                    finestra.MouseUp(new Point(origine.X + 40, origine.Y), MouseButton.Left);
                    LayoutHeadlessSessione.Assesta(finestra);
                    Assert.True(Math.Abs(colonna.ActualWidth - (prima + 40)) <= 2,
                        $"colonna '{colonna.Header}' non ridimensionata: {prima:0} -> {colonna.ActualWidth:0}");
                    provate++;
                }
                Assert.True(provate >= 3, $"colonne provate: {provate}");
            }
            finally { finestra.Hide(); }
        });

    [Fact]
    public void AltezzaRiga_RispettaLimitiEMinimoDichiaratoDallaVista()
    {
        Assert.Equal(28, TableLayoutPreferences.LimitaAltezza(1, 0));
        Assert.Equal(52, TableLayoutPreferences.LimitaAltezza(40, 52));
        Assert.Equal(320, TableLayoutPreferences.LimitaAltezza(500, 0));
    }

    [Fact]
    public void OrdinamentoSalvato_SiRiapplicaSeMancaIlLayoutColonneEDopoAggiuntaColonna() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var stato = TestStateFactory.Create();
            stato.Config.OperatoreAttivo = "Operatore A";
            TableLayoutPreferences.Configure(stato);
            var primaGriglia = CreaGrigliaOrdinabile(includeNuovaColonna: false);
            TableLayoutPreferences.SetTableId(primaGriglia, "sort-only-regression");
            var primaFinestra = new Window { Width = 800, Height = 500, Content = primaGriglia };
            try
            {
                LayoutHeadlessSessione.Mostra(primaFinestra);
                primaGriglia.Columns[0].Sort(ListSortDirection.Descending);
                LayoutHeadlessSessione.Assesta(primaFinestra);
                var chiave = TableLayoutPreferences.Chiave(stato.Config.OperatoreAttivo, stato.RuoloAttivo, "sort-only-regression");
                var layout = stato.Config.LayoutTabelle[chiave];
                Assert.Empty(layout.Colonne);
                Assert.Equal(nameof(RigaOrdinabile.Nome), layout.Ordinamento!.Percorso);
                Assert.Equal(ListSortDirection.Descending, layout.Ordinamento.Direzione);
            }
            finally { primaFinestra.Hide(); }

            var secondaGriglia = CreaGrigliaOrdinabile(includeNuovaColonna: true);
            TableLayoutPreferences.SetTableId(secondaGriglia, "sort-only-regression");
            var secondaFinestra = new Window { Width = 800, Height = 500, Content = secondaGriglia };
            try
            {
                LayoutHeadlessSessione.Mostra(secondaFinestra);
                var descrizione = Assert.Single(secondaGriglia.CollectionView!.SortDescriptions);
                Assert.Equal(nameof(RigaOrdinabile.Nome), descrizione.PropertyPath);
                Assert.Equal(ListSortDirection.Descending, descrizione.Direction);
            }
            finally { secondaFinestra.Hide(); }
        });

    [Fact]
    public void TooltipGlobale_RidimensionaORiposizionaEntroLoSchermo() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            ToolTipLayoutBehavior.Configure();
            var target = new TextBlock { Width = 240, Height = 80 };
            var finestra = new Window { Width = 500, Height = 300, Content = target };
            LayoutHeadlessSessione.Mostra(finestra);
            try
            {
                ToolTip.SetTip(target, "dettaglio lungo");

                var callback = ToolTip.GetCustomPopupPlacementCallback(target);
                Assert.NotNull(callback);
                Assert.Equal(PlacementMode.Custom, ToolTip.GetPlacement(target));
                var puntatore = new Pointer(85, PointerType.Mouse, true);
                var puntoLocale = new Point(73, 29);
                var puntoFinestra = target.TranslatePoint(puntoLocale, finestra)!.Value;
                var proprieta = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
                target.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, target, puntatore, finestra,
                    puntoFinestra, 1, proprieta, KeyModifiers.None));
                var posizione = (CustomPopupPlacement)RuntimeHelpers.GetUninitializedObject(typeof(CustomPopupPlacement));
                callback(posizione);
                Assert.Equal(new Rect(puntoFinestra, new Size(1, 1)), posizione.AnchorRectangle);
                Assert.Equal(PopupAnchor.TopLeft, posizione.Anchor);
                Assert.Equal(PopupGravity.BottomRight, posizione.Gravity);
                Assert.Equal(new Point(12, 18), posizione.Offset);
                Assert.Equal(PopupPositionerConstraintAdjustment.All, posizione.ConstraintAdjustment);
            }
            finally { finestra.Hide(); }
        });

    [Fact]
    public void RidimensionamentoRigaSenzaLayout_PreservaLarghezzeStarResponsive() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var stato = TestStateFactory.Create();
            TableLayoutPreferences.Configure(stato);
            var elemento = new RigaOrdinabile("Record", 1);
            var griglia = new DataGrid { ItemsSource = new[] { elemento } };
            griglia.Columns.Add(new DataGridTextColumn
            {
                Header = "Nome",
                Binding = new Binding(nameof(RigaOrdinabile.Nome)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            });
            TableLayoutPreferences.SetTableId(griglia, "row-resize-star-regression");
            var finestra = new Window { Width = 900, Height = 500, Content = griglia };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                LayoutHeadlessSessione.Assesta(finestra);
                var riga = griglia.GetVisualDescendants().OfType<DataGridRow>().Single();

                SimulaRidimensionamentoRiga(riga, finestra, 24);

                var chiave = TableLayoutPreferences.Chiave(stato.Config.OperatoreAttivo, stato.RuoloAttivo,
                    "row-resize-star-regression");
                var layout = stato.Config.LayoutTabelle[chiave];
                Assert.Empty(layout.Colonne);
                Assert.Single(layout.AltezzeRighe);
                Assert.True(griglia.Columns[0].Width.IsStar,
                    "il ridimensionamento della riga non deve convertire una colonna Star in larghezza fissa");
            }
            finally { finestra.Hide(); }
        });

    [Fact]
    [Trait("Category", "Scale")]
    public void DataGridPersonalizzata_5000Record_MantieneVirtualizzazioneERigheMaterializzateLimitate() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var stato = TestStateFactory.Create();
            TableLayoutPreferences.Configure(stato);
            var righe = Enumerable.Range(1, 5_000).Select(i => new RigaScala($"R-{i:D5}", $"Record {i}")).ToList();
            var griglia = new DataGrid
            {
                ItemsSource = righe,
                IsReadOnly = true,
                MinHeight = 200,
            };
            griglia.Columns.Add(new DataGridTextColumn
            {
                Header = "ID",
                Binding = new Binding(nameof(RigaScala.Id)),
                Width = DataGridLength.Auto,
            });
            griglia.Columns.Add(new DataGridTextColumn
            {
                Header = "Nome",
                Binding = new Binding(nameof(RigaScala.Nome)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            });
            TableLayoutPreferences.SetTableId(griglia, "layout-scale-5000");

            var finestra = new Window { Width = 1280, Height = 672, Content = griglia };
            try
            {
                var cronometro = System.Diagnostics.Stopwatch.StartNew();
                LayoutHeadlessSessione.Mostra(finestra);
                LayoutHeadlessSessione.Assesta(finestra);
                cronometro.Stop();

                var righeRealizzate = griglia.GetVisualDescendants().OfType<DataGridRow>().Count();
                Console.WriteLine($"TABELLA_PERSONALIZZATA_SCALA|elementi={righe.Count}|righe_realizzate={righeRealizzate}|layout_ms={cronometro.Elapsed.TotalMilliseconds:0}");
                Assert.Equal(5_000, righe.Count);
                Assert.True(griglia.CanUserResizeColumns && griglia.CanUserReorderColumns,
                    "la registrazione globale della tabella deve abilitare resize e riordino colonne");
                Assert.InRange(righeRealizzate, 1, 100);

                var primaRiga = griglia.GetVisualDescendants().OfType<DataGridRow>()
                    .Single(r => r.DataContext is RigaScala { Id: "R-00001" });
                var altezzaRidimensionata = primaRiga.Bounds.Height + 24;
                SimulaRidimensionamentoRiga(primaRiga, finestra, 24, perdiCattura: true);
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Equal(altezzaRidimensionata, primaRiga.Height, 1);
                var chiaveTabella = TableLayoutPreferences.Chiave(stato.Config.OperatoreAttivo,
                    stato.RuoloAttivo, "layout-scale-5000");
                Assert.Equal(altezzaRidimensionata, stato.Config.LayoutTabelle[chiaveTabella].AltezzeRighe[
                    TableLayoutPreferences.ChiaveRiga(righe[0], 0)], 1);

                griglia.ScrollIntoView(righe[^1], null);
                LayoutHeadlessSessione.Assesta(finestra);
                var righeAlFondo = griglia.GetVisualDescendants().OfType<DataGridRow>().ToArray();
                Assert.DoesNotContain(righeAlFondo, r => r.DataContext is RigaScala { Id: "R-00001" });
                Assert.False(righeAlFondo.Any(r => double.IsFinite(r.Height)),
                    "l'altezza personalizzata non deve contaminare le righe riciclate");

                griglia.ScrollIntoView(righe[0], null);
                LayoutHeadlessSessione.Assesta(finestra);
                var primaRigaRitornata = griglia.GetVisualDescendants().OfType<DataGridRow>()
                    .Single(r => r.DataContext is RigaScala { Id: "R-00001" });
                Assert.Equal(altezzaRidimensionata, primaRigaRitornata.Height, 1);
            }
            finally
            {
                finestra.Hide();
            }
        });

    private sealed record RigaScala(string Id, string Nome);
    private sealed record RigaOrdinabile(string Nome, int Valore);

    private static DataGrid CreaGrigliaOrdinabile(bool includeNuovaColonna)
    {
        var griglia = new DataGrid
        {
            ItemsSource = new[] { new RigaOrdinabile("A", 1), new RigaOrdinabile("B", 2) },
            CanUserSortColumns = true,
        };
        griglia.Columns.Add(new DataGridTextColumn
        {
            Header = "Nome",
            Binding = new Binding(nameof(RigaOrdinabile.Nome)),
            SortMemberPath = nameof(RigaOrdinabile.Nome),
        });
        if (includeNuovaColonna)
            griglia.Columns.Add(new DataGridTextColumn
            {
                Header = "Valore",
                Binding = new Binding(nameof(RigaOrdinabile.Valore)),
                SortMemberPath = nameof(RigaOrdinabile.Valore),
            });
        return griglia;
    }

    private static void VerificaAzioniCentrate(DataGrid griglia, DataGridRow riga, string automationIdPrimo, string automationIdSecondo)
    {
        var primo = riga.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetAutomationId(b) == automationIdPrimo);
        var secondo = riga.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetAutomationId(b) == automationIdSecondo);
        var cellaAzioni = primo.GetVisualAncestors().OfType<DataGridCell>().First();
        var cella = cellaAzioni.TranslatePoint(new Point(0, 0), griglia)!.Value;
        var primoPos = primo.TranslatePoint(new Point(0, 0), griglia)!.Value;
        var secondoPos = secondo.TranslatePoint(new Point(0, 0), griglia)!.Value;
        Assert.True(primo.IsEffectivelyVisible && secondo.IsEffectivelyVisible, "le azioni della riga devono essere visibili");
        Assert.True(primoPos.X + primo.Bounds.Width <= secondoPos.X + 0.5, "le azioni della riga si sovrappongono");
        Assert.True(primoPos.X >= cella.X - 0.5 && secondoPos.X + secondo.Bounds.Width <= cella.X + cellaAzioni.Bounds.Width + 0.5,
            "le azioni della riga devono rimanere dentro la colonna");
        var centroGruppo = (primoPos.X + secondoPos.X + secondo.Bounds.Width) / 2;
        var centroCella = cella.X + cellaAzioni.Bounds.Width / 2;
        Assert.InRange(Math.Abs(centroGruppo - centroCella), 0, 2);
    }

    private static void SimulaRidimensionamentoRiga(DataGridRow riga, Window finestra, double deltaY, bool perdiCattura = false)
    {
        var puntatore = new Pointer(81, PointerType.Mouse, true);
        var inizio = riga.TranslatePoint(new Point(30, riga.Bounds.Height - 1), finestra)
            ?? throw new InvalidOperationException("La riga non e' collegata alla finestra visuale.");
        var premute = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        riga.RaiseEvent(new PointerPressedEventArgs(riga, puntatore, finestra, inizio, 1, premute, KeyModifiers.None, 1));
        Assert.Same(riga, puntatore.Captured);

        var trascinamento = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        var destinazione = new Point(inizio.X, inizio.Y + deltaY);
        riga.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, riga, puntatore, finestra, destinazione, 2, trascinamento, KeyModifiers.None));

        if (perdiCattura)
        {
            puntatore.Capture(null);
            var altezzaDopoPerdita = riga.Height;
            var spostamentoSuccessivo = new Point(inizio.X, destinazione.Y + deltaY);
            riga.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, riga, puntatore, finestra,
                spostamentoSuccessivo, 3, trascinamento, KeyModifiers.None));
            Assert.Equal(altezzaDopoPerdita, riga.Height, 1);
        }
        else
        {
            var rilascio = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
            riga.RaiseEvent(new PointerReleasedEventArgs(riga, puntatore, finestra, destinazione, 3, rilascio, KeyModifiers.None, MouseButton.Left));
        }
        Assert.Null(puntatore.Captured);
    }
}
