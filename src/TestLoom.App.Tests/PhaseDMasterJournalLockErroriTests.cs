using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Review 2026-10-01: il retry del lock del contatore Phase D ritenta solo errori di contesa
/// (lock presente o in delete-pending); disco pieno e percorso inesistente escono subito con la causa vera.</summary>
public class PhaseDMasterJournalLockErroriTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070070))] // ERROR_DISK_FULL
    [InlineData(unchecked((int)0x80070027))] // ERROR_HANDLE_DISK_FULL
    [InlineData(unchecked((int)0x80070003))] // ERROR_PATH_NOT_FOUND
    public void ErroriNonTransitori_NonVengonoRitentati(int hresult)
    {
        Assert.True(PhaseDMasterJournalService.ErroreLockNonTransitorio(new IOException("x", hresult)));
    }

    [Fact]
    public void DirectoryNotFound_NonVieneRitentata()
    {
        Assert.True(PhaseDMasterJournalService.ErroreLockNonTransitorio(new DirectoryNotFoundException()));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070050))] // ERROR_FILE_EXISTS: lock tenuto da un altro
    [InlineData(unchecked((int)0x80070020))] // ERROR_SHARING_VIOLATION
    public void ErroriDiContesa_VengonoRitentati(int hresult)
    {
        Assert.False(PhaseDMasterJournalService.ErroreLockNonTransitorio(new IOException("x", hresult)));
    }

    [Fact]
    public void AccessoNegato_DeletePending_VieneRitentato()
    {
        Assert.False(PhaseDMasterJournalService.ErroreLockNonTransitorio(new UnauthorizedAccessException()));
    }
}
