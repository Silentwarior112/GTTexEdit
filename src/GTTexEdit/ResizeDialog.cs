using GTTexEdit.Core.Editing;

namespace GTTexEdit;

/// <summary>
/// Asks what size a texture should be. The sizes offered are the ones the GS addresses directly - a texture is
/// held in a whole power of two and told how much of it is real - and the dialog says what it will cost and what
/// it will take away, because a texture that was sharing its pixels with others stops sharing when it is resized.
/// </summary>
internal sealed class ResizeDialog : Form
{
    private readonly ComboBox _width = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly ComboBox _height = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly Label _cost = new() { Dock = DockStyle.Top, Height = 96, ForeColor = Color.Gainsboro, Padding = new Padding(2, 6, 2, 0) };
    private readonly Button _ok = new() { Text = "Resize", DialogResult = DialogResult.OK, AutoSize = true, ForeColor = Color.Gainsboro };

    private readonly TextureSet _set;
    private readonly TextureView _view;

    private ResizeDialog(TextureSet set, TextureView view)
    {
        _set = set;
        _view = view;

        Text = $"Resize t{view.Index}";
        ClientSize = new Size(520, 210);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A);

        foreach (int size in Sizes)
        {
            _width.Items.Add(size);
            _height.Items.Add(size);
        }
        _width.SelectedItem = Nearest(view.Width);
        _height.SelectedItem = Nearest(view.Height);
        _width.SelectedIndexChanged += (_, _) => Describe();
        _height.SelectedIndexChanged += (_, _) => Describe();

        var chosen = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2, 4, 0, 0) };
        chosen.Controls.AddRange([
            new Label { Text = $"t{view.Index} is {view.Width}x{view.Height}.  Make it", AutoSize = true, ForeColor = Color.Gainsboro, Padding = new Padding(2, 5, 0, 0) },
            _width,
            new Label { Text = "by", AutoSize = true, ForeColor = Color.Gainsboro, Padding = new Padding(6, 5, 0, 0) },
            _height,
        ]);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 4, 6, 0) };
        buttons.Controls.AddRange([
            new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, ForeColor = Color.Gainsboro },
            _ok,
        ]);

        Controls.AddRange([_cost, chosen, buttons]);
        AcceptButton = _ok;
        Describe();
    }

    /// <summary>The sizes a texture may be: powers of two, up to what the GS can state as a real size.</summary>
    private static readonly int[] Sizes = [8, 16, 32, 64, 128, 256, 512, 1024];

    private static int Nearest(int size) => Sizes.MinBy(s => Math.Abs(s - size));

    public int ChosenWidth => (int)_width.SelectedItem!;

    public int ChosenHeight => (int)_height.SelectedItem!;

    private void Describe()
    {
        int width = ChosenWidth, height = ChosenHeight;
        string? why = _set.WhyNotResize(_view, width, height);
        _ok.Enabled = why is null && (width != _view.Width || height != _view.Height);

        int sharing = _set.Buffers[_view.Buffer].Views.Count - 1;
        _cost.Text = why is not null
            ? $"It cannot be made {width}x{height}: {why}."
            : $"{_view.Width}x{_view.Height} to {width}x{height}, in the {_view.PaletteSize} colours it already has.\n\n"
              + (sharing > 0
                    ? $"{sharing} other view(s) read these very pixels. They will go on reading them, unchanged - this\n"
                      + "one gets pixels of its own, and stops sharing.\n\n"
                    : "Nothing else reads these pixels.\n\n")
              + "The set is laid out again, so whatever nothing reads any more is given back. The file will grow if\n"
              + "what is left needs more room than it had.";
    }

    /// <summary>Asks for a new size. Returns null when the user would rather not.</summary>
    public static (int Width, int Height)? Ask(IWin32Window owner, TextureSet set, TextureView view)
    {
        using var dialog = new ResizeDialog(set, view);
        return dialog.ShowDialog(owner) == DialogResult.OK ? (dialog.ChosenWidth, dialog.ChosenHeight) : null;
    }
}
