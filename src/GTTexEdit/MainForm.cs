using GTTexEdit.Controls;
using GTTexEdit.Core;
using GTTexEdit.Core.Editing;
using GTTexEdit.Core.Imaging;
using GTTexEdit.Core.Models;

namespace GTTexEdit;

/// <summary>
/// The editor. It shows a texture set the way the GS holds it - buffers of shared index data with the views that
/// read them - because that is what decides who an edit reaches: a palette entry belongs to one view, a texel
/// belongs to every view of its buffer. Sizes never change, so the GS layout and every file offset stay as they
/// are. Colour patches are not maintained: where an edit lands on one, that coverage is given up.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly ComboBox _sets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Name = "_sets" };
    private readonly ComboBox _variations = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Name = "_variations" };
    private readonly ComboBox _protect = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly CheckBox _dither = new() { Text = "Dither", AutoSize = true };
    private readonly CheckBox _requantize = new() { Text = "Re-solve the buffer", AutoSize = true };

    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true };
    private readonly ImageView _image = new() { Dock = DockStyle.Fill };
    private readonly PaletteGrid _palette = new() { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A) };
    private readonly JointTableView _joint = new() { Dock = DockStyle.Fill };
    private readonly TextBox _info = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.None, Font = new Font(FontFamily.GenericMonospace, 8.5f), TabStop = false };
    private readonly Label _entry = new() { Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, Padding = new Padding(4, 6, 4, 0) };
    private readonly NumericUpDown _alpha = new() { Minimum = 0, Maximum = 255, Width = 60 };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    // Materials are a spreadsheet: a row per material, a column per value, exactly as in GTPatEdit.
    private readonly DataGridView _materials = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,

        // The grid is a light control in a dark window, so it has to say what colour its own text is: left alone
        // it inherits the window's pale grey, which is what the cells are read against their white background.
        DefaultCellStyle = new DataGridViewCellStyle { BackColor = SystemColors.Window, ForeColor = Color.Black },
    };
    private readonly ComboBox _parts = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly ComboBox _materialSet = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _finish = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };

    // Several GT3 variations usually read ONE material array, so an edit through any of them reaches all of them.
    private readonly FlowLayoutPanel _sharedBar = new() { Dock = DockStyle.Top, Height = 28, Padding = new Padding(2, 2, 0, 0), Visible = false };
    private readonly Label _shared = new() { AutoSize = true, ForeColor = Color.DarkOrange, Padding = new Padding(2, 5, 0, 0) };
    private readonly Button _unshare = new() { Text = "Give this variation its own", AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(8, 1, 2, 2) };

    private readonly ListBox _variationList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _variationInfo = new() { Dock = DockStyle.Top, Height = 92, ForeColor = Color.Gainsboro, Padding = new Padding(6, 4, 4, 0) };

    private readonly List<ToolStripItem> _needsFile = [];
    private readonly List<ToolStripItem> _needsView = [];
    private readonly List<ToolStripItem> _needsVariations = [];
    private readonly List<ToolStripItem> _needsMoreVariations = [];
    private readonly List<Control> _variationButtons = [];

    private TextureDocument? _file;
    private TextureSet? _set;
    private TextureView? _view;
    private bool _loading;

    public MainForm(string? path)
    {
        Text = "GTTexEdit";
        AppIcon.Wear(this);
        MinimumSize = new Size(900, 600);
        Size = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        BuildLayout();
        WireEvents();
        UpdateEnabled();

        // the car is opened once the window exists, so its dialogs have an owner
        if (path is not null)
            Shown += (_, _) => OpenFile(path);
    }

    // ------------------------------------------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var save = new ToolStripMenuItem("&Save", null, (_, _) => Save(_file?.Path)) { ShortcutKeys = Keys.Control | Keys.S };
        var saveAs = new ToolStripMenuItem("Save &as...", null, (_, _) => Save(null));
        var import = new ToolStripMenuItem("&Import image into this view...", null, (_, _) => Import()) { ShortcutKeys = Keys.Control | Keys.I };
        var importBuffer = new ToolStripMenuItem("Import a buffer &sheet (every view at once)...", null, (_, _) => ImportSheet());
        var bake = new ToolStripMenuItem("&Bake this buffer from PNG layers...", null, (_, _) => BakeBuffer()) { ShortcutKeys = Keys.Control | Keys.B };
        var resize = new ToolStripMenuItem("Give this view a new si&ze...", null, (_, _) => ResizeView()) { ShortcutKeys = Keys.Control | Keys.R };
        _needsView.Add(resize);
        var exportView = new ToolStripMenuItem("&Export this view as PNG...", null, (_, _) => ExportView());
        var exportBuffer = new ToolStripMenuItem("Export this &buffer as a sheet...", null, (_, _) => ExportBuffer());
        _needsFile.AddRange([save, saveAs]);
        _needsView.AddRange([import, importBuffer, bake, exportView, exportBuffer]);

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.AddRange([
            new ToolStripMenuItem("&Open...", null, (_, _) => OpenFile()) { ShortcutKeys = Keys.Control | Keys.O },
            save, saveAs, new ToolStripSeparator(),
            import, importBuffer, bake, resize, exportView, exportBuffer, new ToolStripSeparator(),
            new ToolStripMenuItem("E&xit", null, (_, _) => Close()),
        ]);

        var addVariation = new ToolStripMenuItem("&Add one, copied from this one", null, (_, _) => AddVariation());
        var removeVariation = new ToolStripMenuItem("&Remove this one", null, (_, _) => RemoveVariation());
        var earlier = new ToolStripMenuItem("Move it &earlier", null, (_, _) => MoveVariation(-1));
        var later = new ToolStripMenuItem("Move it &later", null, (_, _) => MoveVariation(1));
        var exportVarying = new ToolStripMenuItem("Export a variation sheet - the palettes that &differ...", null, (_, _) => ExportSheet(variesOnly: true)) { ShortcutKeys = Keys.Control | Keys.E };
        var exportAll = new ToolStripMenuItem("Export a variation sheet - &every palette...", null, (_, _) => ExportSheet(variesOnly: false));
        var importSheet = new ToolStripMenuItem("Import a variation sheet into &this variation...", null, (_, _) => ImportVariationSheet(asNew: false));
        var importAsNew = new ToolStripMenuItem("Import a variation sheet as a &new variation...", null, (_, _) => ImportVariationSheet(asNew: true));
        _needsFile.AddRange([exportVarying, exportAll, importSheet]);
        _needsVariations.AddRange([earlier, later, removeVariation]);
        _needsMoreVariations.AddRange([addVariation, importAsNew]);

        var variations = new ToolStripMenuItem("&Variations");
        variations.DropDownItems.AddRange([
            addVariation, removeVariation, new ToolStripSeparator(), earlier, later, new ToolStripSeparator(),
            exportVarying, exportAll, importSheet, importAsNew,
        ]);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About", null, (_, _) => MessageBox.Show(this,
            "GTTexEdit - edits the textures of Gran Turismo 3 and Gran Turismo 4.\n\n" +
            "A texture set is one image of PS2 GS memory. Several pglu textures ('views') often read the SAME\n" +
            "4-bit index data through palettes of their own - that is where the original files get their size from.\n\n" +
            "Editing a palette entry changes one view; editing a texel changes every view of its buffer.\n" +
            "Sizes never change, so the GS layout and every file offset stay exactly as they are.\n\n" +
            "A variation is a whole alternative picture: the same texels read through another palette. GT4 holds\n" +
            "them in a colour patch, GT3 in a clut patch table per variation plus a material array each. A GT4\n" +
            "list of variations can grow; a GT3 one can only be reordered and shortened, because another would\n" +
            "need a palette and a material array the file does not have.\n\n" +
            "GT4 colour patches are not maintained through ordinary edits. A patch describes the bytes that WERE\n" +
            "there, so wherever an edit lands on one, that coverage is dropped and every variation then shows the\n" +
            "edited bytes. The rest of the patch is untouched.",
            "About GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        var menu = new MenuStrip();
        menu.Items.AddRange([file, variations, help]);

        _protect.Items.AddRange(["Lock the other views", "Protect them strongly", "Balanced", "Ignore them"]);
        _protect.SelectedIndex = 2;

        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2) };
        bar.Items.AddRange([
            new ToolStripLabel("Set"), new ToolStripControlHost(_sets),
            new ToolStripSeparator(), new ToolStripLabel("Variation"), new ToolStripControlHost(_variations),
            new ToolStripSeparator(), new ToolStripLabel("Import:"), new ToolStripControlHost(_protect),
            new ToolStripControlHost(_dither), new ToolStripControlHost(_requantize),
        ]);

        var paletteTab = new TabPage("Palette") { BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A), AutoScroll = true };
        var entryRow = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A) };
        var alphaRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 26, BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A) };
        alphaRow.Controls.AddRange([new Label { Text = "Alpha", ForeColor = Color.Gainsboro, AutoSize = true, Padding = new Padding(4, 5, 0, 0) }, _alpha]);
        entryRow.Controls.Add(_entry);
        paletteTab.Controls.AddRange([_palette, alphaRow, entryRow]);

        var jointTab = new TabPage("Joint table") { BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A) };
        jointTab.Controls.Add(_joint);
        var infoTab = new TabPage("Buffer") { BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A) };
        infoTab.Controls.Add(_info);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.AddRange([paletteTab, jointTab, infoTab]);

        _previewSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        _previewSplit.Panel1.Controls.Add(_image);
        _previewSplit.Panel2.Controls.Add(tabs);

        _treeSplit = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        _treeSplit.Panel1.Controls.Add(_tree);
        _treeSplit.Panel2.Controls.Add(_previewSplit);

        var textureTab = new TabPage("Texture editor") { BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A) };
        textureTab.Controls.Add(_treeSplit);
        var variationTab = new TabPage("Variation editor") { BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A) };
        variationTab.Controls.Add(BuildVariationEditor());

        var main = new TabControl { Dock = DockStyle.Fill };
        main.TabPages.AddRange([textureTab, variationTab]);

        var strip = new StatusStrip();
        strip.Items.Add(_status);

        Controls.AddRange([main, bar, menu, strip]);
        MainMenuStrip = menu;
    }

    // Splitter positions only stick once the tab pages that hold them have their real size.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _treeSplit.SplitterDistance = Math.Min(290, _treeSplit.Width / 2);
        _previewSplit.SplitterDistance = Math.Min(430, Math.Max(120, _previewSplit.Height - 320));
        _variationSplit.SplitterDistance = Math.Min(210, _variationSplit.Width / 2);
    }

    /// <summary>
    /// The variation editor: the list of variations with the tools that change the LIST, the sheets that edit every
    /// palette of one variation at once, and the materials that go with it - a GT4 car varies them through the same
    /// colour patch as its textures, a GT3 car keeps a whole array per variation.
    /// </summary>
    private Control BuildVariationEditor()
    {
        var buttons = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 64, ColumnCount = 2, RowCount = 2 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.Controls.AddRange([
            NewButton("Add", AddVariation, Need.Add), NewButton("Remove", RemoveVariation, Need.List),
            NewButton("Up", () => MoveVariation(-1), Need.List), NewButton("Down", () => MoveVariation(1), Need.List),
        ]);

        var listHost = new GroupBox { Text = "Variations", Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, Padding = new Padding(6) };
        listHost.Controls.AddRange([_variationList, buttons]);

        var sheetBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2, 3, 0, 0) };
        sheetBar.Controls.AddRange([
            new Label { Text = "Sheets", AutoSize = true, ForeColor = Color.Gainsboro, Padding = new Padding(4, 6, 0, 0) },
            NewButton("Export the palettes that differ...", () => ExportSheet(variesOnly: true), Need.Open, fill: false),
            NewButton("Export every palette...", () => ExportSheet(variesOnly: false), Need.Open, fill: false),
            NewButton("Import into this variation...", () => ImportVariationSheet(asNew: false), Need.Open, fill: false),
            NewButton("Import as a new variation...", () => ImportVariationSheet(asNew: true), Need.Add, fill: false),
        ]);

        foreach (MaterialPreset preset in TexturePart.Finishes)
            _finish.Items.Add(preset);
        _finish.SelectedIndex = 0;

        var materialBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(2, 2, 0, 0) };
        materialBar.Controls.AddRange([
            new Label { Text = "Part", AutoSize = true, ForeColor = Color.Gainsboro, Padding = new Padding(2, 6, 0, 0) }, _parts,
            new Label { Text = "Values of", AutoSize = true, ForeColor = Color.Gainsboro, Padding = new Padding(10, 6, 0, 0) }, _materialSet,
            new Label { Text = "Finish", AutoSize = true, ForeColor = Color.Gainsboro, Padding = new Padding(10, 6, 0, 0) }, _finish,
            NewButton("Apply to the selected material(s)", ApplyFinish, Need.Open, fill: false),
        ]);

        _sharedBar.Controls.AddRange([_shared, _unshare]);
        _unshare.Click += (_, _) => OwnMaterials();

        var materialHost = new GroupBox { Text = "Materials", Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, Padding = new Padding(6) };
        materialHost.Controls.AddRange([_materials, _sharedBar, materialBar]);

        var rightSide = new Panel { Dock = DockStyle.Fill };
        rightSide.Controls.AddRange([materialHost, sheetBar, _variationInfo]);

        _variationSplit = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        _variationSplit.Panel1.Controls.Add(listHost);
        _variationSplit.Panel2.Controls.Add(rightSide);
        return _variationSplit;
    }

    /// <summary>What a button needs before it can do anything: a file, more than one variation, or room for another.</summary>
    private enum Need { Open, List, Add }

    private Button NewButton(string text, Action action, Need need, bool fill = true)
    {
        // The panels are dark, and a button takes its foreground from its parent - which on a bare panel is black
        // on black. Every button here says so itself rather than depending on what it happens to sit in.
        var button = new Button
        {
            Text = text, Dock = fill ? DockStyle.Fill : DockStyle.None, AutoSize = !fill,
            Margin = new Padding(fill ? 2 : 4, 2, 2, 2), ForeColor = Color.Gainsboro,
        };
        button.Click += (_, _) => action();
        (need switch { Need.Open => _openButtons, Need.Add => _addVariationButtons, _ => _variationButtons }).Add(button);
        return button;
    }

    private readonly List<Control> _openButtons = [];
    private readonly List<Control> _addVariationButtons = [];
    private SplitContainer _variationSplit = null!, _treeSplit = null!, _previewSplit = null!;

    private void WireEvents()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Bake this buffer from PNG layers...", null, (_, _) => BakeBuffer()));
        menu.Items.Add(new ToolStripMenuItem("Import a buffer sheet...", null, (_, _) => ImportSheet()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Import an image into this view...", null, (_, _) => Import()));
        menu.Items.Add(new ToolStripMenuItem("Give this view a new size...", null, (_, _) => ResizeView()));
        menu.Items.Add(new ToolStripMenuItem("Export this view as PNG...", null, (_, _) => ExportView()));
        menu.Items.Add(new ToolStripMenuItem("Export this buffer as a sheet...", null, (_, _) => ExportBuffer()));
        _tree.ContextMenuStrip = menu;
        _tree.MouseDown += (_, e) =>
        {
            // right-clicking should act on what it points at, not on whatever was selected before
            if (e.Button == MouseButtons.Right && _tree.GetNodeAt(e.Location) is TreeNode node)
                _tree.SelectedNode = node;
        };

        _sets.SelectedIndexChanged += (_, _) => SelectSet();
        _variations.SelectedIndexChanged += (_, _) => SelectVariation();
        _variationList.SelectedIndexChanged += (_, _) =>
        {
            // The toolbar combo is the one place the selected variation lives; the list follows it and back.
            if (!_loading && _variationList.SelectedIndex >= 0 && _variationList.SelectedIndex < _variations.Items.Count)
                _variations.SelectedIndex = _variationList.SelectedIndex;
        };
        _tree.AfterSelect += (_, _) => SelectNode();
        _palette.SelectionChanged += () => ShowEntry();
        _palette.EntryActivated += entry => EditColor(_view, entry);
        _joint.CellActivated += (row, column) => EditColor(BufferView(column), row);
        _joint.SelectionChanged += () => SyncFromJoint();
        _image.PixelClicked += SelectTexel;
        _alpha.ValueChanged += (_, _) => ApplyAlpha();
        _materialSet.SelectedIndexChanged += (_, _) => { if (!_loading) ShowMaterials(); };
        _parts.SelectedIndexChanged += (_, _) => { if (!_loading) { FillMaterialSets(); ShowMaterials(); } };
        _materials.CellEndEdit += (_, e) => CommitMaterial(e.RowIndex, e.ColumnIndex);

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                OpenFile(files[0]);
        };
        FormClosing += (_, e) => e.Cancel = !ConfirmDiscard();
    }

    // -------------------------------------------------------------------------------------------- open / save

    private void OpenFile()
    {
        using var dialog = new OpenFileDialog { Title = "Open a car, a course archive, a model or a texture set", Filter = "Anything GT3 or GT4|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            OpenFile(dialog.FileName);
    }

    private void OpenFile(string path)
    {
        if (!ConfirmDiscard())
            return;
        try
        {
            _file = TextureDocument.Open(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            MessageBox.Show(this, e.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _loading = true;
        _sets.Items.Clear();
        _parts.Items.Clear();
        foreach (TexturePart part in _file.Parts)
        {
            _parts.Items.Add(part);
            foreach (TextureSet set in part.Sets)
                _sets.Items.Add(set);
        }
        _sets.DisplayMember = nameof(TextureSet.Name);
        _parts.DisplayMember = nameof(TexturePart.Name);
        _parts.SelectedIndex = 0;
        Fit(_sets);
        _loading = false;

        _sets.SelectedIndex = 0;
        FillVariationList();
        UpdateEnabled();
        Status($"{Path.GetFileName(path)} - {_file.Format}: {_file.Parts.Count} part(s), {_file.Parts.Sum(p => p.Sets.Count)} texture set(s)"
             + (_file.VariationCount > 1 ? $", {_file.VariationCount} variations" : "")
             + (_file.HasExternalMainPatch ? "; the main colour patch is the .pat beside it" : ""));
    }

    private void Save(string? path)
    {
        if (_file is null)
            return;
        if (path is null)
        {
            using var dialog = new SaveFileDialog { Title = "Save the file", FileName = Path.GetFileName(_file.Path), Filter = "Anything GT3 or GT4|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            path = dialog.FileName;
        }
        else if (!Confirm($"Overwrite {Path.GetFileName(path)}?"))
        {
            return;
        }

        try
        {
            IReadOnlyList<string> written = _file.Save(path);
            Status("Saved " + string.Join(" and ", written.Select(Path.GetFileName)));
            UpdateTitle();
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            MessageBox.Show(this, e.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool ConfirmDiscard() => _file?.IsModified != true || Confirm("There are unsaved changes. Discard them?");

    private bool Confirm(string question) =>
        MessageBox.Show(this, question, "GTTexEdit", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;

    // ------------------------------------------------------------------------------------------- the GS view

    private void SelectSet()
    {
        if (_loading)
            return;
        _set = _sets.SelectedItem as TextureSet;
        FillVariations();

        // The materials shown belong to a part; follow the set rather than make the user pick the part twice.
        if (_file?.Parts.FirstOrDefault(p => p.Sets.Any(s => ReferenceEquals(s, _set))) is TexturePart owner)
        {
            bool was = _loading;
            _loading = true;
            _parts.SelectedItem = owner;
            _loading = was;
        }

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        if (_set is not null)
        {
            foreach (TextureBuffer buffer in _set.Buffers)
            {
                var node = new TreeNode(buffer.Label) { Tag = buffer, ForeColor = buffer.IsShared ? Color.DarkOrange : SystemColors.WindowText };
                foreach (TextureView view in buffer.Views)
                    node.Nodes.Add(new TreeNode(view.Label) { Tag = view });
                if (buffer.IsShared)
                    node.Expand();
                _tree.Nodes.Add(node);
            }
        }
        _tree.EndUpdate();
        if (_tree.Nodes.Count > 0)
            _tree.SelectedNode = _tree.Nodes[0].Nodes.Count > 0 ? _tree.Nodes[0].Nodes[0] : _tree.Nodes[0];
    }

    private void SelectNode()
    {
        _view = _tree.SelectedNode?.Tag switch
        {
            TextureView view => view,
            TextureBuffer buffer => buffer.Views[0],
            _ => null,
        };
        ShowView();
    }

    /// <summary>
    /// Which variation is shown and edited. A car's colour patch does not only tint: the same texels read through
    /// another palette are another picture, so a variation is a whole alternative texture.
    /// </summary>
    private int Variation => Math.Max(0, _variations.SelectedIndex);

    /// <summary>Sizes a drop-down to what is actually in it - a course archive's set names are long.</summary>
    private static void Fit(ComboBox combo)
    {
        int widest = 0;
        using Graphics graphics = combo.CreateGraphics();
        foreach (object? item in combo.Items)
            widest = Math.Max(widest, TextRenderer.MeasureText(graphics, item?.ToString() ?? "", combo.Font).Width);

        int room = SystemInformation.VerticalScrollBarWidth + 8;
        combo.DropDownWidth = Math.Clamp(widest + room, combo.Width, 700);
        combo.Width = Math.Clamp(widest + room + 16, 140, 460);
    }

    /// <summary>
    /// The toolbar's variation list. It counts the whole FILE, not the selected set: a GT3 car's wheel or a LOD set
    /// may hold no clut patch table of its own while the car still has seven variations, and picking one has to mean
    /// the same thing everywhere.
    /// </summary>
    private void FillVariations()
    {
        bool was = _loading;
        _loading = true;
        int keep = _variations.SelectedIndex;
        _variations.Items.Clear();
        int count = VariationCount;
        for (int i = 0; i < count; i++)
            _variations.Items.Add(count == 1 ? "the only one" : "variation " + i);
        _variations.SelectedIndex = Math.Clamp(keep, 0, count - 1);
        _variations.Enabled = count > 1;
        Fit(_variations);
        _loading = was;
    }

    private int VariationCount => Math.Max(1, _file?.VariationCount ?? 1);

    /// <summary>The variation editor's own list of the same variations, with room for the tools beside it.</summary>
    private void FillVariationList()
    {
        bool was = _loading;
        _loading = true;
        _variationList.Items.Clear();
        for (int i = 0; i < VariationCount; i++)
            _variationList.Items.Add(VariationCount == 1 ? "The only variation" : $"Variation {i}");
        if (_variationList.Items.Count > 0)
            _variationList.SelectedIndex = Math.Clamp(Variation, 0, _variationList.Items.Count - 1);
        _loading = was;
        ShowVariationInfo();
    }

    private void SelectVariation()
    {
        if (_loading)
            return;
        bool was = _loading;
        _loading = true;
        if (_variationList.Items.Count > Variation)
            _variationList.SelectedIndex = Variation;
        _loading = was;

        ShowVariationInfo();
        ShowView();   // which also follows the variation with the materials
    }

    /// <summary>What these variations are and what can be done to the LIST of them - the two engines differ there.</summary>
    private void ShowVariationInfo()
    {
        if (_file is null)
        {
            _variationInfo.Text = "Open a file to see its variations.";
            return;
        }

        int varying = VariationSheet.CountPalettes(_file, variesOnly: true), all = VariationSheet.CountPalettes(_file, variesOnly: false);
        string what = _file.Variations switch
        {
            VariationSource.ColourPatch =>
                $"{VariationCount} variation(s) from a colour patch: each one holds its own bytes for the palette words the patch covers.\n"
                + "Add, remove and reorder are all in reach, and the patch grows or shrinks with them.",
            VariationSource.ClutPatch =>
                $"{VariationCount} variation(s) from the texture sets' clut patch tables: each one says which palette every texture reads,\n"
                + "and which material array it draws with. Adding one appends a copy at the end of the model - so the file gets a little\n"
                + "longer - and the copy reads the same palettes and materials as its source until you give it some of its own."
                + (_file.WhyNotAdd is string why ? $"\nThis one cannot take another: {why}" : ""),
            _ => "This file has one look: nothing in it switches palettes.",
        };
        _variationInfo.Text = what + $"\n\n{varying} of {all} palettes differ between the variations."
            + " A sheet is every palette of ONE variation in a single PNG: export it, paint it, import it back.";
    }

    private TextureView? BufferView(int column) =>
        _set is not null && _view is not null && column >= 0 && column < _set.Buffers[_view.Buffer].Views.Count
            ? _set.Buffers[_view.Buffer].Views[column]
            : null;

    private void ShowView()
    {
        if (_set is null || _view is null)
        {
            _image.Image = null;
            _palette.SetCells([]);
            _joint.SetTable([], [], [], 0);
            _info.Text = "";
            UpdateEnabled();
            return;
        }

        TextureBuffer buffer = _set.Buffers[_view.Buffer];
        _image.Image = _set.Render(_view, Variation);

        var cells = new PaletteCell[_view.PaletteSize];
        for (int entry = 0; entry < cells.Length; entry++)
        {
            cells[entry] = new PaletteCell(
                _set.GetPaletteColor(_view, entry, Variation),
                _set.IsPaletteEntryEditable(_view, entry),
                _set.GetEntryCoverage(_view, entry) != Coverage.None);
        }
        _palette.SetCells(cells);
        _palette.Height = Math.Max(24, _palette.PreferredGridHeight);

        bool[] used = _set.GetUsedRows(buffer);
        _joint.SetTable(_set.GetJointTable(buffer, Variation), [.. buffer.Views.Select(v => "t" + v.Index)], used,
                        buffer.Views.ToList().FindIndex(v => v.Index == _view.Index),
                        [.. buffer.Views.Select(v => v.PaletteSize)]);

        _info.Text = Describe(buffer, used);
        _info.Select(0, 0);   // a read-only TextBox selects everything when it is focused
        FillMaterialSets();
        ShowMaterials();
        ShowEntry();
        UpdateEnabled();
        UpdateTitle();
    }

    /// <summary>What this buffer is and what an edit to it would reach - the thing the format makes non-obvious.</summary>
    private string Describe(TextureBuffer buffer, bool[] used)
    {
        int usedCount = used.Count(u => u);
        int painted = _set!.PaintedEntriesIn(buffer);
        var lines = new List<string>
        {
            $"{buffer.Label}    blocks {buffer.FirstBlock}..{buffer.LastBlock} ({buffer.Blocks * 256 / 1024.0:0.#} KB)",
            "",
            buffer.Views.Count == 1
                ? "Nothing else reads these texels: painting here changes this view alone."
                : $"{buffer.Views.Count} views read the SAME texels ({string.Join(", ", buffer.Views.Select(v => "t" + v.Index))}).",
            "A palette entry belongs to one view. A texel belongs to every view of the buffer.",
            "",
            buffer.PaletteSize == 0
                ? "True colour: no palette."
                : usedCount >= buffer.PaletteSize
                    ? $"All {buffer.PaletteSize} index values are in use: keeping the palette means a new colour takes one away."
                    : $"{usedCount} of {buffer.PaletteSize} index values in use - {buffer.PaletteSize - usedCount} free row(s) for a new colour.",
        };

        if (buffer.PaletteSize > 0)
            lines.Add("\"Re-solve the buffer\" lifts that: it re-derives the index image AND every view's palette at once.");
        if (painted > 0)
        {
            lines.Add($"{painted} palette entr{(painted == 1 ? "y is" : "ies are")} switched by its {_set.VariationCount} variations:");
            lines.Add("each variation is a whole alternative picture. Pick one above and import into it to replace just that one.");
        }
        if (buffer.HasWindows)
            lines.Add($"It also holds a window: giving every view its own pixels would cost {buffer.DetachedBlocks} blocks instead of {buffer.Blocks}.");
        if (!buffer.IsUniform)
        {
            lines.Add("Its views read it in different storage modes, so only their palettes can be edited.");
            lines.Add($"One read 4-bit has 16 entries where an 8-bit one has {buffer.PaletteSize}, so the joint table leaves its higher rows blank.");
        }

        lines.AddRange([
            "",
            $"Set {_set.Name}: {_set.BlockCount} blocks = {_set.PixelBlocks} pixel + {_set.ClutBlocks} clut + {_set.FreeBlocks} free.",
            $"Growing this buffer is out of reach here: at twice the size it would need {buffer.BlocksAtDoubleSize} blocks"
                + (buffer.BlocksAsEightBit > 0 ? $", as 8-bit {buffer.BlocksAsEightBit} plus four clut blocks per view" : "")
                + $" - the set has {_set.FreeBlocks} to spare.",
        ]);

        foreach (TextureView view in buffer.Views)
        {
            lines.Add($"  t{view.Index,-3} {view.Width}x{view.Height,-5} {view.Format,-4} {view.PaletteSize,3} colours  clut {view.Cbp}/{view.Csa,-4} {view.Filter,-8} wrap {view.Wrap,-16} tfx {view.Tfx}"
                    + (_set.GetPaletteCoverage(view) != Coverage.None ? "   recoloured by the variations" : "")
                    + (view.IsWindow ? "   window into the buffer" : ""));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void SyncFromJoint()
    {
        if (_set is null || _view is null || _joint.SelectedRow < 0)
            return;
        TextureView? column = BufferView(_joint.SelectedColumn);
        if (column is not null && column.Index != _view.Index)
        {
            foreach (TreeNode node in _tree.Nodes[_view.Buffer].Nodes)
            {
                if (node.Tag is TextureView v && v.Index == column.Index)
                    _tree.SelectedNode = node;
            }
        }
        _palette.SelectedEntry = _joint.SelectedRow;
    }

    private void SelectTexel(int x, int y)
    {
        if (_set is null || _view is null || _view.PaletteSize == 0)
            return;
        byte index = _set.GetIndices(_view, Variation)[y * _view.Width + x];
        _palette.SelectedEntry = index;
        _joint.Select(index, _joint.SelectedColumn);
        Status($"texel {x},{y} uses index {index}" + (_set.Buffers[_view.Buffer].Views.Count > 1
            ? $" - shared with {_set.Buffers[_view.Buffer].Views.Count - 1} other view(s)" : ""));
    }

    private void ShowEntry()
    {
        if (_set is null || _view is null || _palette.SelectedEntry < 0)
        {
            _entry.Text = _view is null ? "" : "Select a palette entry.";
            return;
        }

        int entry = _palette.SelectedEntry;
        uint color = _set.GetPaletteColor(_view, entry, Variation);
        Coverage coverage = _set.GetEntryCoverage(_view, entry);
        _loading = true;
        _alpha.Value = color >> 24;
        _loading = false;

        IReadOnlyList<int> sharing = _set.VariationsSharingEntry(_view, entry, Variation);
        _entry.Text = $"Entry {entry} of t{_view.Index}:  R {color & 0xFF}  G {color >> 8 & 0xFF}  B {color >> 16 & 0xFF}  A {color >> 24}\n"
            + (sharing.Count > 0
                ? $"Variation{(sharing.Count == 1 ? "" : "s")} {string.Join(", ", sharing)} read this very colour - changing it changes what {(sharing.Count == 1 ? "it shows" : "they show")} too."
                : coverage == Coverage.None
                    ? "No other variation reads it, so changing it changes nothing else."
                    : $"Its {_set.PaintCount} variations recolour it - editing it drops that, and every one will show the new colour.")
            + "\nDouble-click a swatch to change it. It belongs to this view only.";
    }

    // ----------------------------------------------------------------------------------------------- editing

    private void EditColor(TextureView? view, int entry)
    {
        if (_set is null || view is null || entry < 0 || entry >= view.PaletteSize || !_set.IsPaletteEntryEditable(view, entry))
            return;

        uint current = _set.GetPaletteColor(view, entry, Variation);
        using var dialog = new ColorDialog { Color = Bitmaps.ToColor(current | 0xFF000000), FullOpen = true, AnyColor = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        uint chosen = (Bitmaps.FromColor(dialog.Color) & 0x00FFFFFF) | (current & 0xFF000000);
        if (SetColor(view, entry, chosen))
            Status($"entry {entry} of t{view.Index} recoloured"
                 + (_set.VariationCount > 1 ? $" in variation {Variation} only" : ""));
        ShowView();
    }

    private void ApplyAlpha()
    {
        if (_loading || _set is null || _view is null || _palette.SelectedEntry < 0)
            return;
        int entry = _palette.SelectedEntry;
        uint color = _set.GetPaletteColor(_view, entry, Variation);
        uint wanted = (color & 0x00FFFFFF) | (uint)_alpha.Value << 24;
        if (color != wanted && SetColor(_view, entry, wanted))
        {
            Status($"entry {entry} of t{_view.Index}: alpha {_alpha.Value}");
            ShowView();
        }
    }

    // With variations, a colour belongs to the variation on screen; without them there is nothing to keep apart.
    private bool SetColor(TextureView view, int entry, uint color) => _set!.VariationCount > 1
        ? _set.SetPaletteColor(view, entry, color, Variation)
        : _set.SetPaletteColor(view, entry, color);

    private double Protect => _protect.SelectedIndex switch { 0 => 1e6, 1 => 8, 2 => 1, _ => 0 };

    private RgbaImage? LoadImage(string title)
    {
        using var dialog = new OpenFileDialog { Title = title, Filter = "PNG images|*.png" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return null;
        try
        {
            return Png.Load(dialog.FileName);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            MessageBox.Show(this, e.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    private void Import()
    {
        if (_set is null || _view is null)
            return;
        if (_view.PaletteSize == 0)
        {
            MessageBox.Show(this, "This view is true colour; there is no palette to import into.", "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (LoadImage($"Image for t{_view.Index} ({_view.Width}x{_view.Height})") is not RgbaImage image)
            return;
        if (image.Width != _view.Width || image.Height != _view.Height)
        {
            MessageBox.Show(this, $"The image is {image.Width}x{image.Height}; this view is {_view.Width}x{_view.Height}.\n\n"
                + "Textures keep their size here, because the GS layout of the whole set is built around it.",
                "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_set.VariationCount > 1 && Variation > 0)
            ImportVariation(_view, image);
        else
            Apply(_set.Buffers[_view.Buffer], [new ViewImage(_view, image)]);
    }

    /// <summary>
    /// Replaces what ONE variation shows. A variation is a whole alternative texture, so this is how a number
    /// badge or a livery gets replaced one variation at a time, leaving the others exactly as they are.
    /// </summary>
    private void ImportVariation(TextureView view, RgbaImage image)
    {
        if (_set is null)
            return;

        double error = _set.PreviewPaletteOnlyError(view, Variation, image);
        int paletteBytes = view.PaletteSize * 4, pixelBytes = _set.VariationPixelBytes(view);
        VariationMode? mode = VariationFitDialog.Ask(this,
            $"Variation {Variation} of t{view.Index}  ({view.Width}x{view.Height}, {view.PaletteSize} colours)",
            $"Keeps the texels shared with the other {_set.VariationCount - 1} variation(s), the way the originals do.\n"
            + $"    It would be off by {error:0.0} of 255 per channel here, and the patch carries {paletteBytes} bytes.",
            $"Gives this variation its own index image as well: exactly the picture you gave it.\n"
            + $"    The patch carries {paletteBytes + pixelBytes} bytes instead, and nothing moves.");
        if (mode is not VariationMode chosen)
            return;

        try
        {
            ImportResult result = _set.ReplaceVariation(view, Variation, image, chosen, _dither.Checked);
            Status($"{result.Summary}; mean error {result.MeanError:0.0}/255");
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            MessageBox.Show(this, e.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        ShowView();
    }

    /// <summary>
    /// Gives the selected view a new size. The set is laid out again around it, and the picture the user chooses
    /// goes in at that size - so this is the one edit that can make the file longer.
    /// </summary>
    private void ResizeView()
    {
        if (_set is null || _view is null)
            return;
        if (_view.PaletteSize == 0)
        {
            MessageBox.Show(this, "This view is true colour, and only paletted textures can be resized here.",
                "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (ResizeDialog.Ask(this, _set, _view) is not var (width, height))
            return;
        if (LoadImage($"Picture for t{_view.Index} at {width}x{height}") is not RgbaImage image)
            return;
        if (image.Width != width || image.Height != height)
        {
            MessageBox.Show(this, $"The image is {image.Width}x{image.Height}; it has to be the {width}x{height} "
                + "the texture is being made.", "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            ImportResult result = _set.Resize(_view, width, height, image,
                new ImportOptions { ProtectOthers = Protect, Dither = _dither.Checked });
            Status(result.Summary);
            MessageBox.Show(this, result.Summary + Environment.NewLine + Environment.NewLine + "Save the file to keep it.",
                "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            MessageBox.Show(this, e.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        SelectSet();
    }

    /// <summary>
    /// The inverse of "export this buffer as a sheet": every view of the buffer stacked in one PNG. Importing one
    /// re-solves the whole buffer, which is the only way to give a shared buffer genuinely new colours.
    /// </summary>
    private void ImportSheet()
    {
        if (_set is null || _view is null)
            return;
        TextureBuffer buffer = _set.Buffers[_view.Buffer];
        var (width, height) = SheetSize(buffer);
        if (LoadImage($"Sheet for buffer {buffer.Index} ({width}x{height}, {buffer.Views.Count} view(s) stacked)") is not RgbaImage sheet)
            return;
        if (sheet.Width != width || sheet.Height != height)
        {
            MessageBox.Show(this, $"The sheet is {sheet.Width}x{sheet.Height}; this buffer's sheet is {width}x{height}.\n\n"
                + "Export the buffer first and edit that file, so every view keeps its place and size.",
                "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var images = new List<ViewImage>();
        for (int i = 0, y = 0; i < buffer.Views.Count; i++)
        {
            TextureView view = buffer.Views[i];
            var slice = new RgbaImage(view.Width, view.Height);
            for (int row = 0; row < view.Height; row++)
                Array.Copy(sheet.Pixels, ((y + row) * sheet.Width) * 4, slice.Pixels, row * view.Width * 4, view.Width * 4);
            images.Add(new ViewImage(view, slice));
            y += view.Height + 3;
        }
        Apply(buffer, images, wholeSheet: true);
    }

    private static (int Width, int Height) SheetSize(TextureBuffer buffer) =>
        (buffer.Views.Max(v => v.Width), buffer.Views.Sum(v => v.Height) + 3 * (buffer.Views.Count - 1));

    private void Apply(TextureBuffer buffer, IReadOnlyList<ViewImage> images, bool wholeSheet = false)
    {
        if (_set is null)
            return;

        // A whole sheet can only mean "re-solve": it gives every view a picture of its own.
        bool requantize = _requantize.Checked || wholeSheet;
        int painted = requantize ? _set.PaintedEntriesIn(buffer) : 0;
        if (painted > 0 && !Confirm($"{painted} palette entries of this buffer are recoloured by its {_set.PaintCount} variations.\n\n"
                                  + "Re-solving the buffer gives that up: every one will then show the new colours. Continue?"))
            return;

        try
        {
            ImportResult result = requantize
                ? _set.RequantizeBuffer(buffer, images, new RequantizeOptions { PreserveOthers = Protect, Dither = _dither.Checked })
                : _set.Import(images[0].View, images[0].Image, new ImportOptions { ProtectOthers = Protect, Dither = _dither.Checked });

            Status($"{result.Summary}; mean error {result.MeanError:0.0}/255"
                 + (result.PatchBytesDropped > 0 ? $"; {result.PatchBytesDropped} patched bytes given up" : ""));
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            MessageBox.Show(this, e.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        ShowView();
    }

    /// <summary>
    /// Opens the buffer baker: standalone PNGs stacked and placed on a canvas that is the buffer's own size and
    /// palette size, then baked in as one re-solve.
    /// </summary>
    private void BakeBuffer()
    {
        if (_set is null || _view is null)
            return;
        TextureBuffer buffer = _set.Buffers[_view.Buffer];
        if (buffer.PaletteSize == 0)
        {
            MessageBox.Show(this, "This buffer is true colour; there is no palette to bake into.", "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var baker = new BufferBakerForm(_set, buffer);
        if (baker.ShowDialog(this) == DialogResult.OK && baker.Result is ImportResult result)
        {
            // A bake at a new depth fits nothing, so it has no error to report - but it does lay the set out again,
            // which replaces the buffers the tree is showing.
            Status(result.Summary
                 + (baker.ChangedDepth ? "" : $"; mean error {result.MeanError:0.0}/255")
                 + (result.PatchBytesDropped > 0 ? $"; {result.PatchBytesDropped} patched bytes given up" : ""));
            if (baker.ChangedDepth)
            {
                MessageBox.Show(this, result.Summary + Environment.NewLine + Environment.NewLine + "Save the file to keep it.",
                    "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SelectSet();
                return;
            }
        }
        ShowView();
    }

    private void ExportView()
    {
        if (_set is null || _view is null)
            return;
        using var dialog = new SaveFileDialog { Title = "Export the view", FileName = $"t{_view.Index:00}.png", Filter = "PNG images|*.png" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            Png.Save(_set.Render(_view, Variation), dialog.FileName);
            Status("Exported " + Path.GetFileName(dialog.FileName));
        }
    }

    private void ExportBuffer()
    {
        if (_set is null || _view is null)
            return;
        TextureBuffer buffer = _set.Buffers[_view.Buffer];
        using var dialog = new SaveFileDialog { Title = "Export every view of this buffer", FileName = $"buffer{buffer.Index:00}.png", Filter = "PNG images|*.png" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var (width, height) = SheetSize(buffer);
        var sheet = new RgbaImage(width, height);
        for (int i = 0, y = 0; i < buffer.Views.Count; i++)
        {
            RgbaImage image = _set.Render(buffer.Views[i], Variation);
            for (int row = 0; row < image.Height; row++)
                Array.Copy(image.Pixels, row * image.Width * 4, sheet.Pixels, ((y + row) * width) * 4, image.Width * 4);
            y += image.Height + 3;
        }
        Png.Save(sheet, dialog.FileName);
        Status($"Exported {buffer.Views.Count} view(s) of buffer {buffer.Index} - edit it and import it back to re-solve the buffer");
    }

    // --------------------------------------------------------------------------------------------- materials

    /// <summary>Whose materials are on screen. They live on the part, not the set, so the part is picked separately.</summary>
    private TexturePart? CurrentPart => _parts.SelectedItem as TexturePart;

    private void FillMaterialSets()
    {
        bool was = _loading;
        _loading = true;
        int keep = _materialSet.SelectedIndex;
        _materialSet.Items.Clear();

        TexturePart? part = CurrentPart;
        int count = part?.MaterialSetCount ?? 0;
        // Both engines tie a set of material values to a variation - GT4 through the same colour patch as its
        // textures, GT3 by holding a whole array per variation - so follow the variation rather than ask twice.
        if (part is not null && count > 1)
            keep = part.MaterialSetForVariation(Variation);
        for (int i = 0; i < count; i++)
            _materialSet.Items.Add(count == 1 ? "the only set" : part!.MaterialSetName(i));
        if (count > 0)
            _materialSet.SelectedIndex = Math.Clamp(keep, 0, count - 1);
        _materialSet.Enabled = count > 1;
        _loading = was;
    }

    /// <summary>
    /// The spreadsheet: a row per material, a column per value of it. A tinted cell is one the car's variations give
    /// a value of its own, so editing it there changes that variation alone (GT4; GT3 keeps whole arrays apart).
    /// </summary>
    private void ShowMaterials()
    {
        bool was = _loading;
        _loading = true;
        if (_materials.Columns.Count == 0)
        {
            _materials.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Material", ReadOnly = true, Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            foreach (MaterialField field in TexturePart.MaterialFields)
                _materials.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = field.Name, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        TexturePart? part = CurrentPart;
        _materials.Rows.Clear();
        if (part is not null && _materialSet.SelectedIndex >= 0)
        {
            for (int material = 0; material < part.MaterialCount; material++)
            {
                int row = _materials.Rows.Add();
                _materials.Rows[row].Cells[0].Value = material;
                for (int f = 0; f < TexturePart.MaterialFields.Count; f++)
                    ShowMaterialCell(part, row, f);
            }
        }
        _loading = was;
        ShowSharing();
    }

    /// <summary>
    /// Says so when the variation on screen reads its materials from an array other variations read too - which is
    /// how most of the original GT3 cars are built, and the reason an edit here can show up elsewhere.
    /// </summary>
    private void ShowSharing()
    {
        TexturePart? part = CurrentPart;
        IReadOnlyList<int> others = part is null || _materialSet.SelectedIndex <= 0
            ? []
            : part.VariationsSharingMaterials(_materialSet.SelectedIndex - 1);

        _sharedBar.Visible = others.Count > 0;
        _unshare.Enabled = others.Count > 0;
        if (others.Count == 0)
            return;

        string list = others.Count == 1 ? $"variation {others[0]}" : "variations " + string.Join(", ", others);
        _shared.Text = $"These are the same {part!.MaterialCount} materials {list} read - editing them here edits {(others.Count == 1 ? "it" : "them")} too.";
    }

    /// <summary>
    /// Gives the variation on screen a material array of its own, so that editing it stops reaching the others.
    /// The copy goes on the end of the model, which makes the file longer - the only edit in the tool that does.
    /// </summary>
    private void OwnMaterials()
    {
        if (CurrentPart is not TexturePart part || _file is null || _materialSet.SelectedIndex <= 0)
            return;
        int variation = _materialSet.SelectedIndex - 1;
        IReadOnlyList<int> others = part.VariationsSharingMaterials(variation);
        if (others.Count == 0)
            return;

        if (!Confirm($"Variation {variation} reads the same materials as {(others.Count == 1 ? "variation" : "variations")} "
                   + string.Join(", ", others) + ".\n\n"
                   + $"Giving it a copy of its own makes {part.Name} - and the file - about "
                   + $"{part.MaterialCount * 0x50 / 1024.0:0.0} KB longer, because there is no room for it where it is. "
                   + "Everything else keeps its place and its bytes.\n\nGo ahead?"))
            return;

        try
        {
            int grew = part.GiveVariationItsOwnMaterials(variation);
            Status($"variation {variation} of {part.Name} now has materials of its own; the model grew by {grew} bytes");
        }
        catch (InvalidOperationException e)
        {
            MessageBox.Show(this, e.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        ShowMaterials();
        ShowSharing();
        UpdateTitle();
    }

    private void ShowMaterialCell(TexturePart part, int material, int fieldIndex)
    {
        MaterialField field = TexturePart.MaterialFields[fieldIndex];
        DataGridViewCell cell = _materials.Rows[material].Cells[fieldIndex + 1];
        cell.Value = AsText(part.GetMaterialValue(_materialSet.SelectedIndex, material, field), field);
        cell.Style.BackColor = part.GetMaterialCoverage(material, field) == Coverage.None ? SystemColors.Window : Color.FromArgb(0xFF, 0xF4, 0xC2);
    }

    /// <summary>
    /// A material value as the spreadsheet shows it. Floats are written the shortest way that reads back as the
    /// very same number, so that committing a cell nobody typed in writes nothing - which would otherwise leave
    /// the file counting as edited for the rest of the session.
    /// </summary>
    private static string AsText(float value, MaterialField field) => field.IsInteger
        ? ((uint)value).ToString("X8")
        : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void CommitMaterial(int row, int column)
    {
        if (_loading || CurrentPart is not TexturePart part || row < 0 || column < 1 || row >= part.MaterialCount)
            return;
        MaterialField field = TexturePart.MaterialFields[column - 1];
        string text = Convert.ToString(_materials.Rows[row].Cells[column].Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";

        // Nothing was typed: leaving a cell as it was found must not count as an edit.
        if (text == AsText(part.GetMaterialValue(_materialSet.SelectedIndex, row, field), field))
            return;

        bool parsed = field.IsInteger
            ? uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint integer)
            : float.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float single);
        if (parsed)
        {
            float value = field.IsInteger
                ? uint.Parse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)
                : float.Parse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
            if (part.SetMaterialValue(_materialSet.SelectedIndex, row, field, value))
                Status($"{part.Name} material {row} {field.Name} = {_materials.Rows[row].Cells[column].Value} in {_materialSet.Text}{SharedNote(part)}");
        }
        else
        {
            Status($"\"{text}\" is not a {(field.IsInteger ? "hexadecimal number" : "number")} - the value is unchanged");
        }

        bool was = _loading;
        _loading = true;
        ShowMaterialCell(part, row, column - 1);   // also puts the old value back after a typo
        _loading = was;
        UpdateTitle();
    }

    private void ApplyFinish()
    {
        if (CurrentPart is not TexturePart part || _finish.SelectedItem is not MaterialPreset preset)
            return;
        int[] rows = [.. _materials.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex).Distinct().Order()];
        if (rows.Length == 0)
        {
            MessageBox.Show(this, "Select a cell in the row of each material the finish should go to.", "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int changed = rows.Sum(material => part.ApplyFinish(_materialSet.SelectedIndex, material, preset));
        Status($"{preset.Name} applied to {rows.Length} material(s) of {part.Name} in {_materialSet.Text} ({changed} value(s) changed){SharedNote(part)}");
        ShowMaterials();
        UpdateTitle();
    }

    /// <summary>What an edit to the materials on screen ALSO reached, when the variations share an array.</summary>
    private string SharedNote(TexturePart part)
    {
        IReadOnlyList<int> others = _materialSet.SelectedIndex <= 0 ? [] : part.VariationsSharingMaterials(_materialSet.SelectedIndex - 1);
        return others.Count == 0 ? "" : $", and so in variation{(others.Count == 1 ? "" : "s")} {string.Join(", ", others)}";
    }

    // -------------------------------------------------------------------------------------------- variations

    private void AddVariation()
    {
        if (_file?.CanEditVariations != true)
            return;

        // A GT3 copy starts out reading the same palettes and materials as the one it came from, and the file has
        // to get longer to hold it. Both are worth saying before it happens rather than after.
        if (_file.Variations == VariationSource.ClutPatch && !Confirm(
                $"Add a variation copied from variation {Variation}?\n\n"
                + "It will show exactly what that one shows, because it reads the same palettes and the same "
                + "materials - which is how the originals hold their colours too. Edit its palettes, or give it "
                + "materials of its own, to make it differ.\n\n"
                + "There is nowhere in the file to put the new lists, so the model - and the file - will get a "
                + "little longer. Everything already in it keeps its place."))
            return;

        try
        {
            int index = _file.AddVariation(Variation);
            Status($"variation {index} added, a copy of variation {Variation}"
                 + (_file.Variations == VariationSource.ClutPatch ? " - it reads the same palettes until you edit them" : ""));
            AfterVariationList(index);
        }
        catch (InvalidOperationException e)
        {
            MessageBox.Show(this, e.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void RemoveVariation()
    {
        if (_file?.CanEditVariations != true || _file.VariationCount <= 1)
            return;
        if (!Confirm($"Remove variation {Variation}? Everything it shows is lost."
                   + (_file.Variations == VariationSource.ClutPatch
                        ? "\n\nIts palettes and materials stay in the file, unreferenced: nothing here moves or shortens the model."
                        : "")))
            return;

        int removed = Variation;
        _file.RemoveVariation(removed);
        Status($"variation {removed} removed");
        AfterVariationList(Math.Min(removed, _file.VariationCount - 1));
    }

    private void MoveVariation(int direction)
    {
        if (_file?.CanEditVariations != true)
            return;
        int from = Variation, to = from + direction;
        if (to < 0 || to >= _file.VariationCount)
            return;

        _file.MoveVariation(from, to);
        Status($"variation {from} is now variation {to}");
        AfterVariationList(to);
    }

    /// <summary>
    /// After the LIST of variations changed. A GT3 car keeps that list in the clut patch table of every texture set,
    /// so the sets read their structure again and the tree has to be built from the new views.
    /// </summary>
    private void AfterVariationList(int select)
    {
        int? keep = _view?.Index;
        _loading = true;
        FillVariations();
        _variations.SelectedIndex = Math.Clamp(select, 0, _variations.Items.Count - 1);
        _loading = false;

        SelectSet();   // the tree, and the views under it, are built again from the structure as it is now
        if (keep is int index)
        {
            foreach (TreeNode buffer in _tree.Nodes)
            {
                foreach (TreeNode node in buffer.Nodes)
                {
                    if (node.Tag is TextureView view && view.Index == index)
                        _tree.SelectedNode = node;
                }
            }
        }

        FillVariationList();
        UpdateEnabled();
        UpdateTitle();
    }

    private void ExportSheet(bool variesOnly)
    {
        if (_file is null)
            return;
        using var dialog = new SaveFileDialog
        {
            Title = "Export a variation sheet", Filter = "PNG images|*.png",
            FileName = $"{Path.GetFileNameWithoutExtension(_file.Path)}_variation{Variation}{(variesOnly ? "" : "_all")}.png",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            RgbaImage sheet = VariationSheet.Export(_file, Variation, variesOnly);
            Png.Save(sheet, dialog.FileName);
            Status($"Exported {VariationSheet.CountPalettes(_file, variesOnly)} palette(s) of variation {Variation} to {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            MessageBox.Show(this, e.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>
    /// Reads a sheet back into one variation - or into a new variation copied from this one, so that everything the
    /// sheet does not mention stays as it was.
    /// </summary>
    private void ImportVariationSheet(bool asNew)
    {
        if (_file is null)
            return;
        if (asNew && !_file.CanAddVariation)
        {
            MessageBox.Show(this, "This file cannot hold another variation, so a sheet can only go into one that is already there.",
                "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (LoadImage("Variation sheet") is not RgbaImage sheet)
            return;

        int target = Variation;
        try
        {
            if (asNew)
                target = _file.AddVariation(Variation);
            int changed = VariationSheet.Import(_file, target, sheet);
            Status($"{changed} palette entr{(changed == 1 ? "y" : "ies")} changed in variation {target}");
            MessageBox.Show(this,
                $"{changed} palette entr{(changed == 1 ? "y" : "ies")} changed in variation {target}.\n\n"
                + (changed == 0
                    ? "Every colour in the sheet was already the colour that variation had, so nothing was written."
                    : $"They belong to variation {target} alone - any palette it was sharing with another variation "
                      + "was given to it as a copy first, so nothing else moved.\n\nSave the file to keep it."),
                "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            // a sheet that does not belong to this file leaves nothing behind, not even the variation it was given
            if (asNew && target != Variation)
                _file.RemoveVariation(target);
            MessageBox.Show(this, e.Message, "GTTexEdit", MessageBoxButtons.OK, MessageBoxIcon.Error);
            target = Variation;
            asNew = false;
        }

        if (asNew)
        {
            AfterVariationList(target);
            return;
        }
        ShowVariationInfo();   // the sheet may have made palettes differ that did not before
        ShowView();
        UpdateTitle();
    }

    // ------------------------------------------------------------------------------------------------ chrome

    private void UpdateEnabled()
    {
        bool open = _file is not null, view = _set is not null && _view is not null;
        foreach (ToolStripItem item in _needsFile)
            item.Enabled = open;
        foreach (ToolStripItem item in _needsView)
            item.Enabled = view;
        foreach (ToolStripItem item in _needsVariations)
            item.Enabled = _file?.CanEditVariations == true && _file.VariationCount > 1;
        foreach (ToolStripItem item in _needsMoreVariations)
            item.Enabled = _file?.CanAddVariation == true;
        foreach (Control control in _openButtons)
            control.Enabled = open;
        foreach (Control control in _variationButtons)
            control.Enabled = _file?.CanEditVariations == true && _file.VariationCount > 1;
        foreach (Control control in _addVariationButtons)
            control.Enabled = _file?.CanAddVariation == true;
        _sets.Enabled = _parts.Enabled = _variationList.Enabled = open;
    }

    private void UpdateTitle() =>
        Text = _file is null ? "GTTexEdit" : $"GTTexEdit - {Path.GetFileName(_file.Path)}{(_file.IsModified ? " *" : "")}";

    private void Status(string text) => _status.Text = text;
}
