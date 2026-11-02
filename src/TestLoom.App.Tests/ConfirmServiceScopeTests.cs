using TestLoom.App.Services;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class ConfirmServiceScopeTests : IDisposable
{
    private readonly Func<string, string, string, string, Task<bool>>? _original = ConfirmService.Handler;

    public void Dispose() => ConfirmService.Handler = _original;

    [Fact]
    public async Task ConfirmedDiscard_NotifiesOnlyRequestingParticipant()
    {
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var first = new Participant();
        var second = new Participant();

        Assert.True(await ConfirmService.ConfermaAbbandonoModifiche(first));

        Assert.Equal(1, first.CleanupCount);
        Assert.Equal(0, second.CleanupCount);
    }

    [Fact]
    public async Task OwnerlessConfirmation_DoesNotNotifyAnyRegisteredCleanup()
    {
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var participant = new Participant();
        ConfirmService.RegisterConfirmedAbandonmentCleanup(participant, static owner =>
            ((Participant)owner).LegacyCleanupCount++);

        Assert.True(await ConfirmService.ConfermaAbbandonoModifiche());

        Assert.Equal(0, participant.CleanupCount);
        Assert.Equal(0, participant.LegacyCleanupCount);
    }

    [Fact]
    public async Task RejectedDiscard_NeverRunsCleanup()
    {
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false);
        var participant = new Participant();

        Assert.False(await ConfirmService.ConfermaAbbandonoModifiche(participant));
        Assert.Equal(0, participant.CleanupCount);
    }

    private sealed class Participant : IConfirmedAbandonmentParticipant
    {
        public int CleanupCount { get; private set; }
        public int LegacyCleanupCount { get; set; }
        public void PrepareConfirmedAbandonmentCleanup() => CleanupCount++;
    }
}
