using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace Larik;

/// <summary>
/// Entry point. Windows starts a fresh process for every activation — a shortcut, and also the click
/// on an agent approval toast — so without this the second launch opened a second studio beside the
/// running one (two windows, two stores, two MCP approval channels). The first instance owns a key;
/// later activations are redirected to it, which only brings its window forward.
/// </summary>
public static class Program
{
    /// <summary>Stable per-user key: one studio per session, whatever launched it.</summary>
    private const string InstanceKey = "Larik";

    [STAThread]
    public static void Main(string[] args)
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (!instance.IsCurrent)
        {
            // Hand the activation to the running studio and stop: no second window, no second store.
            instance.RedirectActivationToAsync(activation).AsTask().Wait();
            return;
        }

        App.PrimaryInstance = instance;

        ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);

            // The XAML application owns itself for the lifetime of the process.
            new App();
        });
    }
}
