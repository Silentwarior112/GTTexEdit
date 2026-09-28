using GTTexEdit.Controls;
using GTTexEdit.Core;
using GTTexEdit.Core.Editing;
using GTTexEdit.Core.Imaging;

namespace GTTexEdit;

/// <summary>
/// Builds a new picture for a buffer out of standalone PNGs and bakes it in.
///
/// The canvas is the buffer as it already is: its size and its palette size are given, so a layer bigger than the
/// canvas is refused rather than scaled, and the bake always ends in the buffer's own number of colours. Layers are
/// stacked and placed freely; the bake composites them, then re-solves the whole buffer - the shared index image
/// and every view's palette at once - so a buffer read by several views can be given genuinely new colours without
/// the views that were left alone drifting away.
/// </summary>
internal sealed class BufferBakerForm : Form
{
    private sealed class Layer
    {
        public required string Name { get; init; }
        public required RgbaImage Image { get; init; }
        public int X { get; set; }
        public int Y { get; set; }
        public bool Visible { get; set; } = true;
    }

    private readonly TextureSet _set;
    private readonly TextureBuffer _buffer;
    private readonly Dictionary<(int View, int Variation), List<Layer>> _layers = [];
    private readonly Dictionary<(int View, int Variation), bool> _keepBase = [];

    // What each view already shows, decoded once. Nothing here changes the set until the bake runs, and the live
    // colour count reads every view in every variation - decoding all of that on each drag is what this avoids.
    private readonly Dictionary<(int View, int Variation), RgbaImage> _shows = [];

    private readonly ComboBox _target = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ComboBox _variation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly CheckBox _base = new() { Text = "Start &from the picture it shows now", Dock = DockStyle.Top, Height = 24, Checked = true };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    private readonly NumericUpDown _x = new() { Width = 70, Minimum = -4096, Maximum = 4096 };
    private readonly NumericUpDown _y = new() { Width = 70, Minimum = -4096, Maximum = 4096 };
    private readonly ImageView _canvas = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _protect = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _depth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly CheckBox _dither = new() { Text = "Dit&her", AutoSize = true, Padding = new Padding(8, 4, 0, 0) };
    private readonly CheckBox _preview = new() { Text = "&Show it as it will bake", AutoSize = true, Padding = new Padding(8, 4, 0, 0) };
    private readonly Button _bake = new() { Text = "&Bake into the buffer", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 76, ForeColor = Color.Gainsboro, Padding = new Padding(6, 4, 6, 0) };

    private bool _loading;

    // The last colour count, and the composition it was taken from, so a drag does not re-solve an unchanged one.
    private string _counted = "";
    private int _count;

    public BufferBakerForm(TextureSet set, TextureBuffer buffer)
    {
        _set = set;
        _buffer = buffer;
        foreach (TextureView view in buffer.Views)
        {
            for (int variation = 0; variation < set.VariationCount; variation++)
            {
                _layers[(view.Index, variation)] = [];
                _keepBase[(view.Index, variation)] = true;
            }
        }

        Text = $"Bake buffer {buffer.Index} of {set.Name}";
        MinimumSize = new Size(880, 600);
        Size = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A);
        ForeColor = Color.Gainsboro;

        BuildLayout();
        FillTargets();
        _target.SelectedIndex = 0;
    }

    /// <summary>What the bake did, once it ran.</summary>
    public ImportResult? Result { get; private set; }

    /// <summary>Whether that bake changed the buffer's colour depth, which lays the whole set out again.</summary>
    public bool ChangedDepth { get; private set; }

    private TextureView Target => _buffer.Views[Math.Max(0, _target.SelectedIndex)];

    /// <summary>Which variation the canvas is for. A variation is a whole alternative picture, not a tint.</summary>
    private int Variation => Math.Max(0, _variation.SelectedIndex);

    private (int View, int Variation) Canvas => (Target.Index, Variation);
    private List<Layer> Layers => _layers[Canvas];
    // The list is drawn topmost first, so a row's Tag carries the real position in the stack.
    private int SelectedIndex => _list.SelectedIndices.Count > 0 ? (int)_list.Items[_list.SelectedIndices[0]].Tag! : -1;
    private Layer? Selected => SelectedIndex >= 0 ? Layers[SelectedIndex] : null;
    private double Protect => _protect.SelectedIndex switch { 0 => 1e6, 1 => 8, 2 => 1, _ => 0 };

    /// <summary>The depth this buffer does not have. A texture holds 16 colours or 256, so there is only the one.</summary>
    private int Other => _buffer.PaletteSize == 16 ? 256 : 16;

    /// <summary>How many colours the buffer is to have when the bake is done - what it has, or the other depth.</summary>
    private int Depth => _depth.SelectedIndex <= 0 ? _buffer.PaletteSize : Other;

    private bool ChangingDepth => Depth != _buffer.PaletteSize;

    // ------------------------------------------------------------------------------------------------- layout

    private void BuildLayout()
    {
        _list.Columns.Add("Layer", 150);
        _list.Columns.Add("X", 48, HorizontalAlignment.Right);
        _list.Columns.Add("Y", 48, HorizontalAlignment.Right);
        _list.Columns.Add("Size", 78, HorizontalAlignment.Right);

        var add = new Button { Text = "&Add PNG...", AutoSize = true };
        var remove = new Button { Text = "&Remove", AutoSize = true };
        var up = new Button { Text = "&Up", Width = 48 };
        var down = new Button { Text = "D&own", Width = 54 };
        var centre = new Button { Text = "&Centre", AutoSize = true };
        add.Click += (_, _) => AddLayer();
        remove.Click += (_, _) => RemoveLayer();
        up.Click += (_, _) => Reorder(1);     // up the list is up the stack, which is the END of the layer list
        down.Click += (_, _) => Reorder(-1);
        centre.Click += (_, _) => Centre();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 30, BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A) };
        buttons.Controls.AddRange([add, remove, up, down, centre]);

        var place = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 30, BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A) };
        place.Controls.AddRange([
            new Label { Text = "X", AutoSize = true, Padding = new Padding(2, 6, 0, 0) }, _x,
            new Label { Text = "Y", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, _y,
        ]);

        var left = new Panel { Dock = DockStyle.Left, Width = 350, BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A), Padding = new Padding(6, 0, 6, 0) };
        for (int variation = 0; variation < _set.VariationCount; variation++)
            _variation.Items.Add(_set.VariationCount == 1 ? "the only one" : "variation " + variation);
        _variation.SelectedIndex = 0;
        _variation.Enabled = _set.VariationCount > 1;

        left.Controls.AddRange([_list, place, buttons, _base, _variation, _target,
            new Label { Text = "Canvas - the view and variation this stack of layers is for", Dock = DockStyle.Top, Height = 18, ForeColor = Color.Gray }]);

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _bake.Click += (_, _) => Bake();

        _protect.Items.AddRange(["Lock the other views", "Protect them strongly", "Balanced", "Ignore them"]);
        _protect.SelectedIndex = 2;

        // A buffer has 16 colours or 256, and changing that is a different kind of bake: the pictures are taken as
        // they are, so they have to fit the depth between them rather than being fitted to it.
        _depth.Items.AddRange([
            _buffer.PaletteSize == 0 ? "Keep it true colour" : $"Keep its {_buffer.PaletteSize} colours",
            $"Make it {Other} colours"]);
        _depth.SelectedIndex = 0;

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 68, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A) };
        bottom.Controls.AddRange([cancel, _bake, _preview, _dither, _protect,
            new Label { Text = "Views with no layers:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            _depth,
            new Label { Text = "&Colours:", AutoSize = true, Padding = new Padding(8, 6, 0, 0), TabIndex = 50 }]);
        _depth.TabIndex = 51;   // the label's mnemonic moves to whatever follows it in tab order

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.AddRange([_canvas, _status, bottom]);

        Controls.AddRange([right, left]);
        CancelButton = cancel;

        _target.SelectedIndexChanged += (_, _) => { if (!_loading) ShowTarget(); };
        _variation.SelectedIndexChanged += (_, _) => { if (!_loading) ShowTarget(); };
        _base.CheckedChanged += (_, _) => { if (!_loading) { _keepBase[Canvas] = _base.Checked; Redraw(); } };
        _list.SelectedIndexChanged += (_, _) => ShowSelection();
        _list.ItemChecked += (_, e) => { if (!_loading) { Layers[(int)e.Item.Tag!].Visible = e.Item.Checked; Redraw(); } };
        _x.ValueChanged += (_, _) => Nudge();
        _y.ValueChanged += (_, _) => Nudge();
        _canvas.PixelDragged += (dx, dy) => Drag(dx, dy);
        _preview.CheckedChanged += (_, _) => Redraw();
        _dither.CheckedChanged += (_, _) => { if (_preview.Checked) Redraw(); };
        _protect.SelectedIndexChanged += (_, _) => { if (_preview.Checked) Redraw(); };
        _depth.SelectedIndexChanged += (_, _) => DepthChosen();
    }

    /// <summary>
    /// The other depth, taken up or refused with the reason. A buffer that cannot change depth says so when it is
    /// asked, rather than offering a choice that is quietly dead.
    /// </summary>
    private void DepthChosen()
    {
        if (_loading)
            return;
        if (ChangingDepth && _set.WhyNotChangeDepth(_buffer, Depth) is string why)
        {
            MessageBox.Show(this, $"Buffer {_buffer.Index} cannot be given {Depth} colours: {why}.",
                "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _loading = true;
            _depth.SelectedIndex = 0;
            _loading = false;
        }
        Redraw();
    }

    /// <summary>
    /// Changing the depth is a different kind of bake - the pictures go in exactly as they are - so the controls
    /// that fit content to a palette have nothing to do, and the button says which bake it is about to run.
    /// </summary>
    private void ShowDepthMode()
    {
        if (ChangingDepth && _preview.Checked)
        {
            _loading = true;
            _preview.Checked = false;
            _loading = false;
        }
        _preview.Enabled = _dither.Enabled = _protect.Enabled = !ChangingDepth;
        _bake.Text = ChangingDepth ? $"&Bake at {Depth} colours" : "&Bake into the buffer";
    }

    private void FillTargets()
    {
        bool was = _loading;
        _loading = true;
        int selected = _target.SelectedIndex;
        _target.BeginUpdate();
        _target.Items.Clear();
        foreach (TextureView view in _buffer.Views)
        {
            int here = _layers[(view.Index, Variation)].Count;
            int elsewhere = _layers.Where(l => l.Key.View == view.Index && l.Key.Variation != Variation).Sum(l => l.Value.Count);
            _target.Items.Add($"t{view.Index}  {view.Width}x{view.Height}"
                + (here == 0 ? "   keeps what it shows" : $"   {here} layer(s)")
                + (elsewhere > 0 ? $"   (+{elsewhere} in other variations)" : ""));
        }
        _target.SelectedIndex = Math.Clamp(selected, 0, _target.Items.Count - 1);
        _target.EndUpdate();
        _loading = was;
    }

    // -------------------------------------------------------------------------------------------- the layers

    private void AddLayer()
    {
        using var dialog = new OpenFileDialog
        {
            Title = $"Layer for t{Target.Index} (canvas {Target.Width}x{Target.Height})",
            Filter = "PNG images|*.png",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        foreach (string file in dialog.FileNames)
        {
            RgbaImage image;
            try
            {
                image = Png.Load(file);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
            {
                MessageBox.Show(this, $"{Path.GetFileName(file)}: {e.Message}", "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Error);
                continue;
            }

            // The buffer's size is not up for negotiation: nothing here may be bigger than the canvas.
            if (image.Width > Target.Width || image.Height > Target.Height)
            {
                MessageBox.Show(this,
                    $"{Path.GetFileName(file)} is {image.Width}x{image.Height}; the canvas is {Target.Width}x{Target.Height}.\n\n"
                    + "A buffer keeps the size it has, so a layer has to fit inside it. Scale the image down first.",
                    "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
                continue;
            }

            Layers.Add(new Layer
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Image = image,
                X = (Target.Width - image.Width) / 2,
                Y = (Target.Height - image.Height) / 2,
            });
        }

        FillLayers();
        SelectLayer(Layers.Count - 1);   // the one just added, which is on top
        Redraw();
    }

    private void RemoveLayer()
    {
        if (SelectedIndex is not >= 0)
            return;
        int at = SelectedIndex;
        Layers.RemoveAt(at);
        FillLayers();
        SelectLayer(Math.Min(at, Layers.Count - 1));
        Redraw();
    }

    private void Reorder(int direction)
    {
        int from = SelectedIndex, to = from + direction;
        if (from < 0 || to < 0 || to >= Layers.Count)
            return;
        (Layers[from], Layers[to]) = (Layers[to], Layers[from]);
        FillLayers();
        SelectLayer(to);
        Redraw();
    }

    private void SelectLayer(int index)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if ((int)item.Tag! == index)
                item.Selected = true;
        }
    }

    private void Centre()
    {
        if (Selected is not Layer layer)
            return;
        layer.X = (Target.Width - layer.Image.Width) / 2;
        layer.Y = (Target.Height - layer.Image.Height) / 2;
        FillLayers();
        Redraw();
    }

    private void Nudge()
    {
        if (_loading || Selected is not Layer layer)
            return;
        layer.X = (int)_x.Value;
        layer.Y = (int)_y.Value;
        UpdateRow();
        Redraw();
    }

    private void Drag(int dx, int dy)
    {
        if (Selected is not Layer layer)
            return;
        layer.X += dx;
        layer.Y += dy;
        _loading = true;
        _x.Value = Math.Clamp(layer.X, _x.Minimum, _x.Maximum);
        _y.Value = Math.Clamp(layer.Y, _y.Minimum, _y.Maximum);
        _loading = false;
        UpdateRow();
        Redraw();
    }

    // -------------------------------------------------------------------------------------------------- view

    private void ShowTarget()
    {
        _loading = true;
        _base.Checked = _keepBase[Canvas];
        _loading = false;
        FillLayers();
        Redraw();
    }

    private void FillLayers()
    {
        _loading = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        // topmost first, the way a layer stack reads
        for (int i = Layers.Count - 1; i >= 0; i--)
        {
            Layer layer = Layers[i];
            var item = new ListViewItem([layer.Name, layer.X.ToString(), layer.Y.ToString(), $"{layer.Image.Width}x{layer.Image.Height}"])
            {
                Checked = layer.Visible,
                Tag = i,
            };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _loading = false;
        FillTargets();
        ShowSelection();
    }

    private void UpdateRow()
    {
        if (_list.SelectedIndices.Count == 0 || Selected is not Layer layer)
            return;
        ListViewItem row = _list.Items[_list.SelectedIndices[0]];
        row.SubItems[1].Text = layer.X.ToString();
        row.SubItems[2].Text = layer.Y.ToString();
    }

    private void ShowSelection()
    {
        if (Selected is Layer layer)
        {
            _loading = true;
            _x.Value = Math.Clamp(layer.X, _x.Minimum, _x.Maximum);
            _y.Value = Math.Clamp(layer.Y, _y.Minimum, _y.Maximum);
            _loading = false;
            _canvas.Highlight = new Rectangle(layer.X, layer.Y, layer.Image.Width, layer.Image.Height);
        }
        else
        {
            _canvas.Highlight = null;
        }
        _x.Enabled = _y.Enabled = Selected is not null;
        _canvas.Invalidate();
    }

    /// <summary>What a view shows now, decoded once and kept. The copy is the caller's; this one is not to be drawn on.</summary>
    private RgbaImage Shows(TextureView view, int variation)
    {
        if (!_shows.TryGetValue((view.Index, variation), out RgbaImage? image))
            _shows[(view.Index, variation)] = image = _set.Render(view, variation);
        return image;
    }

    /// <summary>The canvas for one view and variation: its current picture if asked for, then the visible layers.</summary>
    private RgbaImage Compose(TextureView view, int variation)
    {
        RgbaImage canvas = _keepBase[(view.Index, variation)] ? Shows(view, variation).Clone() : new RgbaImage(view.Width, view.Height);
        foreach (Layer layer in _layers[(view.Index, variation)])
        {
            if (layer.Visible)
                Compositor.Over(canvas, layer.Image, layer.X, layer.Y);
        }
        return canvas;
    }

    /// <summary>The views of this variation that were given layers.</summary>
    private List<ViewImage> Targets(int variation) =>
        [.. _buffer.Views.Where(v => _layers[(v.Index, variation)].Count > 0).Select(v => new ViewImage(v, Compose(v, variation)))];

    private IEnumerable<int> VariationsWithLayers() =>
        Enumerable.Range(0, _set.VariationCount).Where(v => _buffer.Views.Any(view => _layers[(view.Index, v)].Count > 0));

    private void Redraw()
    {
        ShowDepthMode();
        List<ViewImage> targets = Targets(Variation);
        RgbaImage shown;
        string note;

        // Variation 0 is the buffer itself, so its bake is the joint solve across the views. A later variation is
        // a picture of its own: it is fitted to the shared texels, or given texels of its own when the bake runs.
        if (_preview.Checked && targets.Count > 0 && Variation == 0)
        {
            try
            {
                IReadOnlyList<RgbaImage> baked = _set.PreviewRequantize(_buffer, targets,
                    new RequantizeOptions { PreserveOthers = Protect, Dither = _dither.Checked });
                shown = baked[_buffer.Views.ToList().FindIndex(v => v.Index == Target.Index)];
                note = $"as it will bake, in {_buffer.PaletteSize} colours shared with {_buffer.Views.Count - 1} other view(s)";
            }
            catch (Exception e) when (e is NotSupportedException or ArgumentException)
            {
                shown = Compose(Target, Variation);
                note = "cannot be baked: " + e.Message;
            }
        }
        else
        {
            shown = Compose(Target, Variation);
            note = ChangingDepth
                ? $"exactly what a bake at {Depth} colours would put here - nothing is fitted"
                : _preview.Checked && Variation > 0
                ? "the layers as composed; a later variation is fitted when you bake"
                : _preview.Checked ? "nothing to bake yet - add a layer" : "the layers as composed, before the palette is fitted";
        }

        _canvas.Image = shown;
        ShowSelection();

        int[] pending = [.. VariationsWithLayers()];
        _status.Text =
            $"Canvas {Target.Width}x{Target.Height}, {_buffer.PaletteSize} colours for the whole buffer ({_buffer.Blocks} blocks) - {note}.\n"
            + (ChangingDepth
                // a new depth re-derives the whole buffer, so nothing is left alone: every view is written back,
                // the ones with layers from the layers and the rest from what they already show
                ? $"Baking writes all {_buffer.Views.Count} view(s) in all {_set.VariationCount} variation(s); "
                  + (pending.Length == 0 ? "with no layers, they go back exactly as they are." : "the ones with no layers keep what they show.")
                : pending.Length == 0
                ? "Nothing has layers yet; baking now would change nothing."
                : $"Baking replaces {targets.Count} of {_buffer.Views.Count} view(s) in this variation"
                  + (pending.Length > 1 ? $", and works through {pending.Length} variations in all ({string.Join(", ", pending)})." : "; the rest keep what they show."))
            + (_set.VariationCount > 1
                ? $"\nThere are {_set.VariationCount} variations - each is a whole alternative picture, so give each the layers it needs."
                : "")
            + DepthNote();
    }

    /// <summary>
    /// What a change of depth would cost, said while there is still time to change the content. The number that
    /// matters is how many distinct colour COMBINATIONS the views ask for between them, because they share their
    /// texels - so it can be far more than any one picture uses, and nothing here will approximate it down.
    /// </summary>
    private string DepthNote()
    {
        if (!ChangingDepth)
            return "";

        int needed = Needed();
        return $"\nGoing to {Depth} colours: these pictures need {needed} between them, "
            + (needed <= Depth
                ? "which fits - they go in exactly as they are, nothing approximated."
                : $"which does NOT fit. Give the views content that needs {Depth} or fewer.");
    }

    /// <summary>
    /// How many colours the whole buffer asks for as it stands. The solve walks every view in every variation, so
    /// the answer is kept until the composition changes - a drag redraws far faster than it can be re-solved.
    /// </summary>
    private int Needed()
    {
        string state = Composition();
        if (state != _counted)
        {
            _count = _set.ColoursNeeded(_buffer, Everything());
            _counted = state;
        }
        return _count;
    }

    /// <summary>A fingerprint of everything that decides the picture, so an unchanged one is recognised as such.</summary>
    private string Composition()
    {
        var text = new System.Text.StringBuilder();
        foreach ((int View, int Variation) key in _layers.Keys.OrderBy(k => k.View).ThenBy(k => k.Variation))
        {
            text.Append(key.View).Append(':').Append(key.Variation).Append(_keepBase[key] ? '+' : '-');
            foreach (Layer layer in _layers[key])
            {
                // the image is never edited once loaded, so its identity stands in for its pixels
                text.Append(layer.Image.GetHashCode()).Append('@').Append(layer.X).Append(',').Append(layer.Y)
                    .Append(layer.Visible ? '!' : '?');
            }
            text.Append(';');
        }
        return text.ToString();
    }

    // -------------------------------------------------------------------------------------------------- bake

    /// <summary>
    /// Bakes the buffer at a new colour depth. Everything that reads it is written at once, exactly as composed -
    /// there is no fitting here, so if the pictures need more colours between them than the depth holds, the whole
    /// thing is refused and says how many they need.
    /// </summary>
    private void BakeAtNewDepth()
    {
        List<ViewPicture> pictures = Everything();
        int needed = Needed();
        if (needed > Depth)
        {
            MessageBox.Show(this,
                $"These pictures need {needed} colours between them, and the buffer would have {Depth}."
                + Environment.NewLine + Environment.NewLine
                + $"The {_buffer.Views.Count} view(s) of this buffer share their texels, so what counts is how many "
                + "distinct COMBINATIONS of colours they ask for - not how many any one of them uses. Give them "
                + "content that fits; nothing here will approximate it for you.",
                "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this,
                $"Give buffer {_buffer.Index} {Depth} colours, and put these pictures in exactly as they are?"
                + Environment.NewLine + Environment.NewLine
                + $"They need {needed} of the {Depth}. Every view of the buffer is written, in every variation - "
                + "the ones you gave no layers keep what they show."
                + Environment.NewLine + Environment.NewLine
                + "The set is laid out again around the new depth, so the file may get longer or shorter.",
                "GTTexEdit", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        try
        {
            Result = _set.ChangeDepth(_buffer, Depth, pictures);
            ChangedDepth = true;
            DialogResult = DialogResult.OK;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            MessageBox.Show(this, e.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// A picture for EVERY view of the buffer in EVERY variation: the layers where there are any, and what the
    /// view already shows where there are none. Changing the colour depth re-derives the whole buffer, so
    /// everything that reads it has to be accounted for - keeping what a view shows is not inventing content.
    /// </summary>
    private List<ViewPicture> Everything()
    {
        var pictures = new List<ViewPicture>();
        foreach (TextureView view in _buffer.Views)
        {
            // every variation the dialog knows about, which is every one the set has
            for (int variation = 0; variation < _set.VariationCount; variation++)
            {
                pictures.Add(new ViewPicture(view.Index, variation,
                    _layers[(view.Index, variation)].Count > 0 ? Compose(view, variation) : Shows(view, variation)));
            }
        }
        return pictures;
    }

    private void Bake()
    {
        if (ChangingDepth)
        {
            BakeAtNewDepth();
            return;
        }

        int[] variations = [.. VariationsWithLayers()];
        if (variations.Length == 0)
        {
            MessageBox.Show(this, "Add at least one layer first.", "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Variation 0 is the buffer itself: re-solving it re-derives the shared texels, which the later variations
        // read too, so it would flatten whatever the paints switch between.
        int painted = _set.PaintedEntriesIn(_buffer);
        if (variations.Contains(0) && painted > 0 && MessageBox.Show(this,
                $"{painted} palette entries of this buffer are switched by its {_set.VariationCount} variations.\n\n"
                + "Baking variation 0 re-solves the texels they all share, so those variations are given up. "
                + "To keep them, bake into the later variations instead. Continue?",
                "GTTexEdit", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        VariationMode mode = VariationMode.PaletteOnly;
        if (variations.Any(v => v > 0))
        {
            TextureView first = _buffer.Views[0];
            double error = 0;
            foreach (ViewImage target in Targets(variations.First(v => v > 0)))
                error = Math.Max(error, _set.PreviewPaletteOnlyError(target.View, 0, target.Image));

            VariationMode? chosen = VariationFitDialog.Ask(this,
                $"{variations.Count(v => v > 0)} later variation(s) of buffer {_buffer.Index}",
                $"Keeps the texels shared with every variation, the way the original cars do.\n"
                + $"    Off by up to {error:0.0} of 255 per channel here; the patch carries {_buffer.PaletteSize * 4} bytes per view.",
                $"Gives each of those variations its own index image: exactly the pictures you built.\n"
                + $"    The patch carries about {_set.VariationPixelBytes(first)} more bytes per view, and nothing moves.");
            if (chosen is not VariationMode value)
                return;
            mode = value;
        }

        try
        {
            ImportResult? last = null;
            foreach (int variation in variations)
            {
                List<ViewImage> targets = Targets(variation);
                if (variation == 0)
                {
                    last = _set.RequantizeBuffer(_buffer, targets, new RequantizeOptions { PreserveOthers = Protect, Dither = _dither.Checked });
                }
                else
                {
                    foreach (ViewImage target in targets)
                        last = _set.ReplaceVariation(target.View, variation, target.Image, mode, _dither.Checked);
                }
            }

            Result = last;
            DialogResult = DialogResult.OK;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            MessageBox.Show(this, e.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
