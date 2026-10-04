namespace GTTexEdit;

/// <summary>
/// The application's icon, read once from the assembly it is embedded in and worn by every window - the editor,
/// the baker and the dialogs - so the taskbar and every title bar show the same thing.
///
/// Each window gets a copy of its own: a form disposes the icon it is holding when it closes, and the baker and
/// the dialogs come and go while the editor stays open.
/// </summary>
internal static class AppIcon
{
    private static readonly Icon? Shared = Load();

    private static Icon? Load()
    {
        // Missing or unreadable is not worth a crash on startup: Windows then gives the window its default icon.
        try
        {
            using Stream? stream = typeof(AppIcon).Assembly.GetManifestResourceStream("GTTexEdit.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Gives a window the application's icon, if there is one.</summary>
    public static void Wear(Form form)
    {
        if (Shared is not null)
            form.Icon = (Icon)Shared.Clone();
    }
}
