using GTTexEdit.Core.Editing;

namespace GTTexEdit;

/// <summary>
/// Asks how a variation should hold its new picture. The two ways differ in what the colour patch has to carry,
/// and the honest answer depends on the picture: the shared texels were arranged for the pictures that are already
/// there, so a new one may or may not fit into them.
/// </summary>
internal sealed class VariationFitDialog : Form
{
    private VariationFitDialog(string title, string paletteOnly, string paletteAndPixels)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(560, 210);
        BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A);
        ForeColor = Color.Gainsboro;

        var onlyButton = new Button { Text = "Palette only", Width = 150, Height = 30, DialogResult = DialogResult.Yes };
        var bothButton = new Button { Text = "Palette and pixels", Width = 150, Height = 30, DialogResult = DialogResult.No };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 0) };
        buttons.Controls.AddRange([cancel, bothButton, onlyButton]);

        var text = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 10, 12, 0),
            Text = $"Palette only\n    {paletteOnly}\n\nPalette and pixels\n    {paletteAndPixels}",
        };

        Controls.AddRange([text, buttons]);
        AcceptButton = onlyButton;
        CancelButton = cancel;
    }

    /// <summary>Null when the user backed out.</summary>
    public static VariationMode? Ask(IWin32Window owner, string title, string paletteOnly, string paletteAndPixels)
    {
        using var dialog = new VariationFitDialog(title, paletteOnly, paletteAndPixels);
        return dialog.ShowDialog(owner) switch
        {
            DialogResult.Yes => VariationMode.PaletteOnly,
            DialogResult.No => VariationMode.PaletteAndPixels,
            _ => null,
        };
    }
}
