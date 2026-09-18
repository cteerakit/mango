using System.Reflection.Metadata;
using Mango.Services;
using Mango.Views.Shell;

[assembly: MetadataUpdateHandler(typeof(Mango.HotReloadHandler))]

namespace Mango;

/// <summary>
/// Applies .NET Hot Reload deltas to the live UI. Visual Studio injects method-body
/// changes while debugging; XAML Hot Reload still requires Visual Studio.
/// Packed <c>dotnet run</c> / Cursor sessions typically restart via <c>dotnet watch</c>
/// instead, because WinApp CLI cannot inject deltas into the MSIX process yet.
/// </summary>
internal static class HotReloadHandler
{
    public static void ClearCache(Type[]? updatedTypes)
    {
        _ = updatedTypes;
    }

    public static void UpdateApplication(Type[]? updatedTypes)
    {
        _ = updatedTypes;
        var dispatcher = App.DispatcherQueue;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.TryEnqueue(static () =>
        {
            if (App.Window is MainWindow window)
            {
                window.ReloadAfterHotReload();
            }

            AppServices.OverlayService.RecreateIfVisible();
        });
    }
}
