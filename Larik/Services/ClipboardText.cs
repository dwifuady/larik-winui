using Windows.ApplicationModel.DataTransfer;

namespace Larik.Services;

/// <summary>
/// Clipboard text for the <c>${paste}</c> snippet token. Completion is synchronous, so the editor
/// reads this when a snippet is accepted rather than when the suggestion list is built. Another
/// app can hold the clipboard open; the snippet then inserts nothing instead of failing the edit.
/// </summary>
public static class ClipboardText
{
    public static async Task<string> ReadAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Text)
                ? await content.GetTextAsync()
                : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
