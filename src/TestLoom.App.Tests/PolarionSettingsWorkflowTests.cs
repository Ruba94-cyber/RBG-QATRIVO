using System.Net;
using System.Net.Http;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public sealed class PolarionSettingsWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SettingsSynthetic_" + Guid.NewGuid().ToString("N"));
    private readonly AppState _state;

    public PolarionSettingsWorkflowTests()
    {
        Directory.CreateDirectory(_root);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_root);
        Assert.True(_state.InizializzazioneWorkspaceCompletata);
        var master = TestOperatori.Sigilla(new Operatore
        {
            Nome = "Synthetic Master", Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234")
        });
        _state.Master.Operatori.Save([master]);
        Assert.True(_state.ImpostaOperatore(master.Nome, "1234"));
    }

    [Fact]
    public void SettingsSaveAndReloadPreserveExistingTokenAndUneditedFields()
    {
        var initial = PolarionApiSettingsService.Load(_state.Paths);
        initial.ApiRoot = "https://example.invalid/ticketing/rest/v1";
        initial.LetturaAbilitata = true;
        initial.IntervalloMinuti = 37;
        initial.AutomazioniSospese = false;
        initial.ProjectId = "SYNTHETIC_PROJECT";
        initial.ImpostaToken("synthetic-token-unchanged");
        PolarionApiSettingsService.Save(_state.Paths, initial, true);

        using (var vm = new ImpostazioniViewModel(_state, () => { }))
        {
            Assert.Equal("", vm.PolarionApiNewToken);
            var originalIndicator = vm.PolarionApiTokenIndicator;
            vm.PolarionApiNewToken = "synthetic-pending";
            Assert.NotEqual(originalIndicator, vm.PolarionApiTokenIndicator);
            Assert.DoesNotContain("synthetic-pending", vm.PolarionApiTokenIndicator);
            vm.PolarionApiNewToken = "";
            vm.PolarionAssigneeOne = "synthetic.user";
            vm.SalvaConfigurazionePolarionApiCommand.Execute(null);
            Assert.True(vm.HasPolarionApiSuccess, vm.PolarionApiFeedback);
            Assert.Equal("", vm.PolarionApiNewToken);
        }
        var saved = PolarionApiSettingsService.Load(_state.Paths);
        Assert.Equal("synthetic-token-unchanged", saved.LeggiToken());
        Assert.Equal(37, saved.IntervalloMinuti);
        Assert.False(saved.AutomazioniSospese);
        Assert.Equal("SYNTHETIC_PROJECT", saved.ProjectId);
        Assert.Equal(new[] { "synthetic.user" }, saved.AssegnatariEsta);
        Assert.DoesNotContain("synthetic-token-unchanged", File.ReadAllText(_state.Paths.PolarionApiConfigFile));

        using var reopened = new ImpostazioniViewModel(_state, () => { });
        Assert.Equal(initial.ApiRoot, reopened.PolarionApiRoot);
        Assert.Equal("synthetic.user", reopened.PolarionAssigneeOne);
        Assert.Equal("", reopened.PolarionApiNewToken);
        Assert.True(reopened.PolarionApiReadEnabled);
    }

    [Fact]
    public async Task ProbeWithNoSavedSettingsDoesNotContactNetworkOrClaimSuccess()
    {
        using var vm = new ImpostazioniViewModel(_state, () => { });
        var calls = 0;
        vm.PolarionHttpFactory = () => new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        await vm.TestaConnessionePolarionCommand.ExecuteAsync(null);
        Assert.Equal(0, calls);
        Assert.True(vm.HasPolarionApiError);
        Assert.False(vm.HasPolarionApiSuccess);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.TooManyRequests, "429")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    public async Task ProbeFailureIsVisibleWithoutExposingEndpointOrToken(HttpStatusCode status, string expectedCode)
    {
        SaveConfigured("synthetic-secret");
        using var vm = new ImpostazioniViewModel(_state, () => { });
        var calls = 0;
        vm.PolarionHttpFactory = () => new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }));
        await vm.TestaConnessionePolarionCommand.ExecuteAsync(null);
        Assert.Equal(1, calls);
        Assert.True(vm.HasPolarionApiError);
        Assert.False(vm.HasPolarionApiSuccess);
        Assert.Contains(expectedCode, vm.PolarionApiFeedback!);
        Assert.DoesNotContain("synthetic-secret", vm.PolarionApiFeedback!);
        Assert.DoesNotContain("example.invalid", vm.PolarionApiFeedback!);
        Assert.False(vm.PolarionRefreshInCorso);
    }

    [Fact]
    public async Task UnsavedChangesBlockProbeAndRefreshUntilTokenIsSaved()
    {
        SaveConfigured("synthetic-saved");
        using var vm = new ImpostazioniViewModel(_state, () => { });
        vm.PolarionApiNewToken = "synthetic-pending";
        var calls = 0;
        vm.PolarionHttpFactory = () => new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal("synthetic-pending", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json("""{"data":[]}"""));
        }));
        await vm.TestaConnessionePolarionCommand.ExecuteAsync(null);
        Assert.Equal(0, calls);
        Assert.True(vm.HasPolarionApiError);
        await vm.AggiornaPolarionOraCommand.ExecuteAsync(null);
        Assert.Equal(0, calls);
        Assert.True(vm.HasPolarionApiError);
        vm.SalvaConfigurazionePolarionApiCommand.Execute(null);
        Assert.False(vm.PolarionApiHasUnsavedChanges);
        await vm.TestaConnessionePolarionCommand.ExecuteAsync(null);
        Assert.Equal(1, calls);
        Assert.True(vm.HasPolarionApiSuccess, vm.PolarionApiFeedback);
        Assert.DoesNotContain("synthetic-saved", vm.PolarionApiFeedback!);
        Assert.DoesNotContain("synthetic-pending", vm.PolarionApiFeedback!);
        Assert.Equal("", vm.PolarionApiNewToken);
    }

    [Fact]
    public async Task ProbeCanBeCancelledAndDoesNotClaimSuccess()
    {
        SaveConfigured("synthetic-token");
        using var vm = new ImpostazioniViewModel(_state, () => { });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PolarionHttpFactory = () => new HttpClient(new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("""{"data":[]}""");
        }));
        var task = vm.TestaConnessionePolarionCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(vm.PolarionRefreshInCorso);
        vm.AnnullaPolarionCommand.Execute(null);
        await task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(vm.PolarionRefreshInCorso);
        Assert.True(vm.HasPolarionApiError);
        Assert.False(vm.HasPolarionApiSuccess);
    }

    [Fact]
    public async Task RefreshShowsProgressAndCanBeCancelled()
    {
        SaveConfigured("synthetic-token");
        _state.NonConformita.Save(new NonConformita
        {
            Id = "NC-1", DataApertura = DateTime.Now,
            CollegamentiPolarion = [new CollegamentoPolarion { Codice = "TICKET-123" }]
        });
        using var vm = new ImpostazioniViewModel(_state, () => { });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.PolarionRefreshProgressText) &&
                vm.PolarionRefreshProgressText.Contains("0/1", StringComparison.Ordinal))
                progress.TrySetResult();
        };
        vm.PolarionHttpFactory = () => new HttpClient(new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("""{"data":[]}""");
        }));
        var task = vm.AggiornaPolarionOraCommand.ExecuteAsync(null);
        await Task.WhenAll(entered.Task, progress.Task).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(vm.PolarionRefreshInCorso);
        vm.AnnullaPolarionCommand.Execute(null);
        await task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(vm.PolarionRefreshInCorso);
        Assert.True(vm.HasPolarionApiError);
        Assert.False(vm.HasPolarionApiSuccess);
    }

    private void SaveConfigured(string token)
    {
        var settings = PolarionApiSettingsService.Load(_state.Paths);
        settings.ApiRoot = "https://example.invalid/ticketing/rest/v1";
        settings.LetturaAbilitata = true;
        settings.ImpostaToken(token);
        PolarionApiSettingsService.Save(_state.Paths, settings, true);
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token); }

    [Fact]
    public void SettingsSaveRejectsStaleSnapshotWithoutReplacingNewerTokenOrSettings()
    {
        var initial = PolarionApiSettingsService.Load(_state.Paths);
        initial.ApiRoot = "https://example.invalid/ticketing/rest/v1";
        initial.LetturaAbilitata = true;
        initial.ImpostaToken("synthetic-original");
        PolarionApiSettingsService.Save(_state.Paths, initial, true);
        using var first = new ImpostazioniViewModel(_state, () => { });
        using var stale = new ImpostazioniViewModel(_state, () => { });
        first.PolarionApiNewToken = "synthetic-newer";
        first.PolarionAssigneeOne = "newer.user";
        first.SalvaConfigurazionePolarionApiCommand.Execute(null);
        Assert.True(first.HasPolarionApiSuccess, first.PolarionApiFeedback);
        stale.PolarionApiNewToken = "synthetic-stale";
        stale.PolarionAssigneeOne = "stale.user";
        stale.SalvaConfigurazionePolarionApiCommand.Execute(null);
        Assert.True(stale.HasPolarionApiError);
        Assert.DoesNotContain("synthetic-stale", stale.PolarionApiFeedback!);
        var saved = PolarionApiSettingsService.Load(_state.Paths);
        Assert.Equal("synthetic-newer", saved.LeggiToken());
        Assert.Equal(new[] { "newer.user" }, saved.AssegnatariEsta);
    }

    public void Dispose()
    {
        _state.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
