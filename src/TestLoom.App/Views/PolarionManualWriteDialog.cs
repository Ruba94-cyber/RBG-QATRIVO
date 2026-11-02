using Avalonia.Controls;
using TestLoom.App.Services;
using TestLoom.App.Models;

namespace TestLoom.App.Views;

/// <summary>
/// Legacy dialog API retained for binary/source compatibility.
/// REST writes are fully retired. These entry points always fail closed and
/// must not create an HTTP request, a draft submission or a pending receipt.
/// </summary>
internal static class PolarionManualWriteDialog
{
    public static Task<bool> AddCommentAsync(Window owner, DataPaths paths, string ticketId,
        Func<bool> authorizedNow) => Task.FromResult(false);

    public static Task<bool> EditTextAsync(Window owner, DataPaths paths, string ticketId,
        Func<bool> authorizedNow, PolarionTicketSnapshot original) => Task.FromResult(false);
}
