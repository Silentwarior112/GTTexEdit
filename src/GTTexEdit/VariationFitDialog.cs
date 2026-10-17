using GTTexEdit.Core.Editing;

namespace GTTexEdit;

/// <summary>
/// Asks how a variation should hold its new picture. The two ways differ in what the colour patch has to carry,
/// and the honest answer depends on the picture: the shared texels were arranged for the pictures that are already
/// there, so a new one may or may not fit into them.
/// </summary>
internal sealed class VariationFitDialog : Form
{
    private VariationFitDialog(string title, string firstName, string first, string secondName, string second)
    {
        Text = title;
        AppIcon.Wear(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(560, 210);
        BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A);
        ForeColor = Color.Gainsboro;

        // The first choice is the one that keeps everything else as it is, so it is the one the keyboard starts on.
        var onlyButton = new Button { Text = firstName, Width = 170, Height = 30, DialogResult = DialogResult.Yes, TabIndex = 0 };
        var bothButton = new Button { Text = secondName, Width = 170, Height = 30, DialogResult = DialogResult.No, TabIndex = 1 };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 30, DialogResult = DialogResult.Cancel, TabIndex = 2 };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 0) };
        buttons.Controls.AddRange([cancel, bothButton, onlyButton]);

        var text = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 10, 12, 0),
            Text = $"{firstName}\n    {first}\n\n{secondName}\n    {second}",
        };

        Controls.AddRange([text, buttons]);
        AcceptButton = onlyButton;
        CancelButton = cancel;
        Shown += (_, _) => onlyButton.Focus();
    }

    /// <summary>Null when the user backed out.</summary>
    public static VariationMode? Ask(IWin32Window owner, string title, string paletteOnly, string paletteAndPixels)
    {
        using var dialog = new VariationFitDialog(title, "Palette only", paletteOnly, "Palette and pixels", paletteAndPixels);
        return dialog.ShowDialog(owner) switch
        {
            DialogResult.Yes => VariationMode.PaletteOnly,
            DialogResult.No => VariationMode.PaletteAndPixels,
            _ => null,
        };
    }

    /// <summary>
    /// The same question for an ordinary import: keep the colours the buffer already has, or give it the ones this
    /// picture wants. True keeps them, false re-solves the buffer, null backs out.
    /// </summary>
    public static bool? AskKeepOrResolve(IWin32Window owner, string title, string keep, string resolve)
    {
        using var dialog = new VariationFitDialog(title, "Keep these colours", keep, "Fit new colours", resolve);
        return dialog.ShowDialog(owner) switch
        {
            DialogResult.Yes => true,
            DialogResult.No => false,
            _ => null,
        };
    }
}
