namespace GTTexEdit;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Error);

        // A car file can be dropped on the exe / passed on the command line.
        Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
    }
}
