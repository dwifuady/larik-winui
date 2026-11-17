using Larik.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Larik.Controls;

/// <summary>
/// The one decision a run on a disconnected workspace needs: connect now, or leave things as they
/// are. It is asked in the window that started the run, because that window owns the XamlRoot, and
/// the caller performs the connection so a declined prompt writes no result at all.
/// </summary>
internal static class ConnectPrompt
{
    public static async Task<bool> ShowAsync(XamlRoot xamlRoot, SpaceViewModel space)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Connect to run queries",
            Content = new TextBlock
            {
                Text = $"“{space.Name}” is disconnected. Connect now to run this query.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Connect",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
