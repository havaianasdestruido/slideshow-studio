using System.Diagnostics;
using SlideShowStudio.Core;
using SlideShowStudio.Engine;

namespace SlideShowStudio.Forms;

public sealed class MainForm : Form
{
    private readonly SlideshowProject _project = new();
    private readonly SlideshowEngine _engine = new();
    private SlideshowRenderer? _renderer;
    private readonly System.Windows.Forms.Timer _playTimer = new();
    private readonly CancellationTokenSource _renderCts = new();
    private bool _rendering;
    private bool _applying;
    private string? _projectPath;

    // photos
    private readonly ListBox _photoList = new();
    private readonly Button _btnAdd = new() { Text = "Add Photos…" };
    private readonly Button _btnRemove = new() { Text = "Remove" };
    private readonly Button _btnUp = new() { Text = "↑ Up" };
    private readonly Button _btnDown = new() { Text = "↓ Down" };

    // per-slide
    private readonly ComboBox _slideTransition = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _slideMs = new() { Minimum = 100, Maximum = 600000, Increment = 100, Value = 5000 };
    private readonly NumericUpDown _transMs = new() { Minimum = 0, Maximum = 600000, Increment = 100, Value = 1000 };
    private readonly ComboBox _panZoom = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _easing = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    // global
    private readonly ComboBox _resolution = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _fps = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _defaultSlideMs = new() { Minimum = 100, Maximum = 600000, Increment = 100, Value = 5000 };
    private readonly NumericUpDown _defaultTransMs = new() { Minimum = 0, Maximum = 600000, Increment = 100, Value = 1000 };
    private readonly TextBox _musicPath = new() { ReadOnly = true };
    private readonly Button _btnMusic = new() { Text = "…" };
    private readonly ComboBox _format = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    // preview
    private readonly PictureBox _preview = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Button _btnPlay = new() { Text = "▶ Play" };
    private readonly TrackBar _scrub = new() { Minimum = 0, Maximum = 100 };
    private readonly Label _timeLabel = new() { Text = "0.0 s / 0.0 s", AutoSize = true };
    private readonly Label _previewDim = new() { AutoSize = true, ForeColor = Color.Gray };

    // render
    private readonly Button _btnRender = new() { Text = "Render" };
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new() { AutoSize = false, Height = 30, Dock = DockStyle.Fill, Text = "" };

    // engine
    private readonly DataGridView _probeGrid = new();
    private readonly Button _btnProbe = new() { Text = "Re-probe Engine" };
    private readonly Button _btnMovieMaker = new() { Text = "Run in Movie Maker…" };
    private readonly Label _engineStatus = new() { AutoSize = false, Height = 40, Text = "", ForeColor = Color.FromArgb(60, 60, 60) };

    public MainForm()
    {
        Text = "SlideShow Studio";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 820);
        MinimumSize = new Size(1040, 680);

        BuildUi();
        LoadDefaults();
        FillFromProject();
        var probes = _engine.ProbeAll();
        ShowProbes(probes);
        _playTimer.Interval = 40;
        _playTimer.Tick += (_, _) => StepPreview();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        var menu = new MenuStrip();
        var mFile = new ToolStripMenuItem("&File");
        mFile.DropDownItems.Add(new ToolStripMenuItem("New", null, (_, _) => NewProject()));
        mFile.DropDownItems.Add(new ToolStripMenuItem("Open…", null, (_, _) => OpenProject()));
        mFile.DropDownItems.Add(new ToolStripMenuItem("Save", null, (_, _) => SaveProject(false)));
        mFile.DropDownItems.Add(new ToolStripMenuItem("Save As…", null, (_, _) => SaveProject(true)));
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add(new ToolStripMenuItem("Exit", null, (_, _) => Close()));
        menu.Items.Add(mFile);

        var mHelp = new ToolStripMenuItem("&Help");
        mHelp.DropDownItems.Add(new ToolStripMenuItem("Engine probes…", null, (_, _) => MessageBox.Show(
            string.Join("\n", _probeGrid.Rows.Cast<DataGridViewRow>().Select(r => r.Cells["colValue"].Value)),
            "WMMR engine probes", MessageBoxButtons.OK, MessageBoxIcon.Information)));
        menu.Items.Add(mHelp);
        Controls.Add(menu);
        Controls.Add(root);
        menu.Dock = DockStyle.Top;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 380, Panel1MinSize = 330, Panel2MinSize = 600 };
        split.Panel1.Controls.Add(BuildLeftPanel());
        split.Panel2.Controls.Add(BuildRightPanel());
        root.Controls.Add(split, 0, 1);
    }

    private Control BuildLeftPanel()
    {
        var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), AutoScroll = true };
        var tbl = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };

        // photos
        tbl.Controls.Add(Group("Photos (top = first)", BuildPhotos()), 0, 0);

        // per-slide
        tbl.Controls.Add(Group("Selected slide", BuildSlideProps()), 0, 1);

        // global
        tbl.Controls.Add(Group("Timings", BuildGlobal()), 0, 2);

        p.Controls.Add(tbl);
        return p;
    }

    private Control BuildPhotos()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        _photoList.Dock = DockStyle.Fill;
        _photoList.Height = 150;
        _photoList.SelectedIndexChanged += (_, _) => FillSlideProps();
        _photoList.DragEnter += (_, e) => { if (e.Data is not null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        _photoList.DragDrop += (_, e) => AddPhotos((e.Data?.GetData(DataFormats.FileDrop) as string[]) ?? Array.Empty<string>());
        _photoList.AllowDrop = true;

        var btns = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        btns.Controls.Add(_btnAdd);
        btns.Controls.Add(_btnRemove);
        btns.Controls.Add(_btnUp);
        btns.Controls.Add(_btnDown);

        _btnAdd.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
                Title = "Add photos to slideshow",
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
                AddPhotos(ofd.FileNames);
        };
        _btnRemove.Click += (_, _) => RemoveSelected();
        _btnUp.Click += (_, _) => Move(-1);
        _btnDown.Click += (_, _) => Move(1);

        t.Controls.Add(_photoList, 0, 0);
        t.Controls.Add(btns, 0, 1);
        return t;
    }

    private Control BuildSlideProps()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(4) };
        int row = 0;
        foreach (var (label, ctrl) in new (string, Control)[]
                 {
                     ("Transition", _slideTransition),
                     ("Slide duration (ms)", _slideMs),
                     ("Transition length (ms)", _transMs),
                     ("Pan/Zoom", _panZoom),
                     ("Easing", _easing),
                 })
        {
            t.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true }, 0, row);
            ctrl.Dock = DockStyle.Fill;
            t.Controls.Add(ctrl, 1, row);
            row++;
        }
        _slideTransition.Items.AddRange(Enum.GetValues<SlideTransition>().Cast<object>().ToArray());
        _panZoom.Items.AddRange(Enum.GetValues<PanZoom>().Cast<object>().ToArray());
        _easing.Items.AddRange(Enum.GetValues<Easing>().Cast<object>().ToArray());

        void Commit(object? _, EventArgs __)
        {
            if (_applying) return;
            var s = SelectedSlide();
            if (s is null) return;
            s.Transition = (SlideTransition)_slideTransition.SelectedItem;
            s.TransitionMs = (long)_transMs.Value;
            s.DurationMs = (long)_slideMs.Value;
            s.PanZoom = (PanZoom)_panZoom.SelectedItem;
            s.Easing = (Easing)_easing.SelectedItem;
            MarkDirty();
        }
        _slideTransition.SelectedIndexChanged += Commit;
        _slideMs.ValueChanged += Commit;
        _transMs.ValueChanged += Commit;
        _panZoom.SelectedIndexChanged += Commit;
        _easing.SelectedIndexChanged += Commit;

        return t;
    }

    private Control BuildGlobal()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(4) };
        _resolution.Items.AddRange(new object[] { "640x360", "854x480", "1280x720", "1920x1080" });
        _resolution.SelectedIndex = 2;
        _fps.Items.AddRange(new object[] { 15, 24, 30, 60 });
        _fps.SelectedIndex = 2;

        int row = 0;
        foreach (var (label, ctrl) in new (string, Control)[]
                 {
                     ("Output resolution", _resolution),
                     ("Frame rate (fps)", _fps),
                     ("Default slide (ms)", _defaultSlideMs),
                     ("Default transition (ms)", _defaultTransMs),
                 })
        {
            t.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true }, 0, row);
            ctrl.Dock = DockStyle.Fill;
            t.Controls.Add(ctrl, 1, row);
            row++;
        }

        var musicRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        musicRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        musicRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        musicRow.Controls.Add(_musicPath, 0, 0);
        musicRow.Controls.Add(_btnMusic, 1, 0);
        t.Controls.Add(new Label { Text = "Music (handoff only)", TextAlign = ContentAlignment.MiddleLeft, AutoSize = true }, 0, row);
        t.Controls.Add(musicRow, 1, row);
        row++;

        t.Controls.Add(new Label { Text = "Output format", TextAlign = ContentAlignment.MiddleLeft, AutoSize = true }, 0, row);
        _format.Items.AddRange(new object[] { OutputKind.Avi, OutputKind.Gif, OutputKind.PngSequence });
        _format.SelectedIndex = 0;
        _format.Dock = DockStyle.Fill;
        t.Controls.Add(_format, 1, row);

        _btnMusic.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog { Filter = "Audio|*.mp3;*.wav;*.wma;*.m4a;*.aac|All files|*.*", Title = "Pick background music" };
            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                _musicPath.Text = ofd.FileName;
                _project.MusicPath = ofd.FileName;
                MarkDirty();
            }
        };

        void Commit(object? _, EventArgs __)
        {
            if (_applying) return;
            _project.DefaultSlideMs = (long)_defaultSlideMs.Value;
            _project.DefaultTransMs = (long)_defaultTransMs.Value;
            MarkDirty();
        }
        _defaultSlideMs.ValueChanged += Commit;
        _defaultTransMs.ValueChanged += Commit;

        return t;
    }

    private Control BuildRightPanel()
    {
        var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        tbl.Controls.Add(BuildPreview(), 0, 0);
        tbl.Controls.Add(BuildRender(), 0, 1);
        tbl.Controls.Add(BuildEngine(), 0, 2);
        return tbl;
    }

    private Control BuildPreview()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        _preview.Dock = DockStyle.Fill;

        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1 };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        _btnPlay.Click += (_, _) => TogglePlay();
        _scrub.Dock = DockStyle.Fill;
        _scrub.Scroll += (_, _) => { if (!_playTimer.Enabled) ScrubPreview(); };
        _scrub.MouseUp += (_, _) => ScrubPreview();
        bar.Controls.Add(_btnPlay, 0, 0);
        bar.Controls.Add(_scrub, 1, 0);
        bar.Controls.Add(_timeLabel, 2, 0);
        bar.Controls.Add(_previewDim, 3, 0);

        t.Controls.Add(_preview, 0, 0);
        t.Controls.Add(bar, 0, 1);
        return t;
    }

    private Control BuildRender()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new Padding(8) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        _btnRender.Click += (_, _) => StartRender();
        _btnRender.Height = 28;
        _status.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _progress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        t.Controls.Add(_btnRender, 0, 0);
        t.Controls.Add(_progress, 1, 0);
        t.Controls.Add(_status, 1, 1);
        _status.Text = "Ready.";
        return t;
    }

    private Control BuildEngine()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        var note = new Label
        {
            Text = "WMMR engine (WLXSlideshow / WLXPhotoCinematic / WLXPhotoBase / MovieMakerCore)",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        bar.Controls.Add(note, 0, 0);
        bar.Controls.Add(_btnProbe, 1, 0);
        _btnProbe.Click += (_, _) => { _engineStatus.Text = "Probing…"; BeginInvoke(() => ShowProbes(_engine.ProbeAll())); };

        _probeGrid.Dock = DockStyle.Fill;
        _probeGrid.AllowUserToAddRows = false;
        _probeGrid.AllowUserToDeleteRows = false;
        _probeGrid.ReadOnly = true;
        _probeGrid.RowHeadersVisible = false;
        _probeGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _probeGrid.Columns.Add("colResult", "Result");
        _probeGrid.Columns.Add("colDll", "DLL");
        _probeGrid.Columns.Add("colFn", "Function");
        _probeGrid.Columns.Add("colKind", "Kind");
        _probeGrid.Columns.Add("colValue", "Value");
        _probeGrid.Columns["colResult"]!.Width = 52;
        _probeGrid.Columns["colDll"]!.FillWeight = 55;
        _probeGrid.Columns["colFn"]!.FillWeight = 55;
        _probeGrid.Columns["colKind"]!.FillWeight = 28;
        _probeGrid.Columns["colValue"]!.FillWeight = 90;

        var engineRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        engineRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        engineRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        _btnMovieMaker.Height = 26;
        _btnMovieMaker.Click += (_, _) => RunInMovieMaker();
        engineRow.Controls.Add(_engineStatus, 0, 0);
        engineRow.Controls.Add(_btnMovieMaker, 1, 0);

        t.Controls.Add(bar, 0, 0);
        t.Controls.Add(_probeGrid, 0, 1);
        t.Controls.Add(engineRow, 0, 2);
        return t;
    }

    // ------------------------------------------------------------------
    // Photo list management
    // ------------------------------------------------------------------
    private void AddPhotos(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var p in paths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)))
        {
            _project.Slides.Add(new Slide
            {
                Path = p,
                DurationMs = _project.DefaultSlideMs,
                TransitionMs = _project.DefaultTransMs,
                Transition = _project.DefaultTransition,
            });
            added++;
        }
        if (added > 0)
        {
            RefreshList();
            _photoList.SelectedIndex = _project.Slides.Count - 1;
            MarkDirty();
        }
    }

    private void RemoveSelected()
    {
        int i = _photoList.SelectedIndex;
        if (i < 0) return;
        _project.Slides.RemoveAt(i);
        RefreshList();
        if (_photoList.Items.Count > 0)
            _photoList.SelectedIndex = Math.Min(i, _photoList.Items.Count - 1);
        MarkDirty();
    }

    private void Move(int dir)
    {
        int i = _photoList.SelectedIndex;
        int j = i + dir;
        if (i < 0 || j < 0 || j >= _project.Slides.Count) return;
        (_project.Slides[i], _project.Slides[j]) = (_project.Slides[j], _project.Slides[i]);
        RefreshList();
        _photoList.SelectedIndex = j;
        MarkDirty();
    }

    private void RefreshList()
    {
        _applying = true;
        _photoList.BeginUpdate();
        _photoList.Items.Clear();
        foreach (var s in _project.Slides)
            _photoList.Items.Add($"#{_photoList.Items.Count + 1}  {s.FileName}");
        _photoList.EndUpdate();
        _applying = false;
        UpdateTotalLabel();
    }

    private Slide? SelectedSlide() =>
        _photoList.SelectedIndex >= 0 && _photoList.SelectedIndex < _project.Slides.Count
            ? _project.Slides[_photoList.SelectedIndex]
            : null;

    private void FillSlideProps()
    {
        var s = SelectedSlide();
        _applying = true;
        if (s is null)
        {
            _slideTransition.Enabled = _slideMs.Enabled = _transMs.Enabled = _panZoom.Enabled = _easing.Enabled = false;
        }
        else
        {
            _slideTransition.Enabled = _slideMs.Enabled = _transMs.Enabled = _panZoom.Enabled = _easing.Enabled = true;
            _slideTransition.SelectedItem = s.Transition;
            _slideMs.Value = Math.Clamp(s.DurationMs, (long)_slideMs.Minimum, (long)_slideMs.Maximum);
            _transMs.Value = Math.Clamp(s.TransitionMs, (long)_transMs.Minimum, (long)_transMs.Maximum);
            _panZoom.SelectedItem = s.PanZoom;
            _easing.SelectedItem = s.Easing;
        }
        _applying = false;
    }

    // ------------------------------------------------------------------
    // Project load/save/defaults
    // ------------------------------------------------------------------
    private void LoadDefaults()
    {
        _project.DefaultSlideMs = 5000;
        _project.DefaultTransMs = 1000;
        _project.Width = 1280;
        _project.Height = 720;
        _project.Fps = 30;
    }

    private void NewProject()
    {
        _project.Slides.Clear();
        LoadDefaults();
        _projectPath = null;
        RefreshList();
        FillFromProject();
        MarkDirty();
    }

    private void OpenProject()
    {
        using var ofd = new OpenFileDialog { Filter = "SlideShow Studio project|*.ssproj|All files|*.*", Title = "Open project" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var p = SlideshowProject.Load(ofd.FileName);
            _project.Slides.Clear();
            _project.Slides.AddRange(p.Slides);
            _project.Name = p.Name;
            _project.Width = p.Width;
            _project.Height = p.Height;
            _project.Fps = p.Fps;
            _project.DefaultSlideMs = p.DefaultSlideMs;
            _project.DefaultTransMs = p.DefaultTransMs;
            _project.DefaultTransition = p.DefaultTransition;
            _project.MusicPath = p.MusicPath;
            _projectPath = ofd.FileName;
            RefreshList();
            FillFromProject();
            MarkDirty();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open project:\n{ex.Message}", "SlideShow Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveProject(bool saveAs)
    {
        _project.Sanitize();
        if (saveAs || _projectPath is null)
        {
            using var sfd = new SaveFileDialog { Filter = "SlideShow Studio project|*.ssproj", Title = "Save project", FileName = _projectPath ?? "slideshow.ssproj" };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;
            _projectPath = sfd.FileName;
        }
        _project.Save(_projectPath);
        Text = $"SlideShow Studio — {Path.GetFileName(_projectPath)}";
        _status.Text = $"Saved {_projectPath}";
    }

    private void FillFromProject()
    {
        _applying = true;
        string res = $"{_project.Width}x{_project.Height}";
        int ri = _resolution.Items.IndexOf(res);
        _resolution.SelectedIndex = ri >= 0 ? ri : 2;
        int fi = _fps.Items.IndexOf(_project.Fps);
        _fps.SelectedIndex = fi >= 0 ? fi : 2;
        _defaultSlideMs.Value = Math.Clamp(_project.DefaultSlideMs, 100, 600000);
        _defaultTransMs.Value = Math.Clamp(_project.DefaultTransMs, 0, 600000);
        _musicPath.Text = _project.MusicPath;
        _applying = false;
    }

    private void MarkDirty()
    {
        UpdateTotalLabel();
        _renderer = null; // force rebuild on next preview/render
    }

    private void UpdateTotalLabel()
    {
        _project.Sanitize();
        _previewDim.Text = $"{_project.Width}×{_project.Height} @ {_project.Fps}fps · {_project.SlideCount} slides · {(_project.TotalDurationMs() / 1000.0):F1}s";
    }

    // ------------------------------------------------------------------
    // Preview
    // ------------------------------------------------------------------
    private SlideshowRenderer GetRenderer()
    {
        _project.Sanitize();
        var (w, h) = ParseResolution((string)_resolution.SelectedItem!);
        if (w != _project.Width || h != _project.Height)
        {
            _project.Width = w;
            _project.Height = h;
        }
        _project.Fps = (int)_fps.SelectedItem!;
        _project.DefaultSlideMs = (long)_defaultSlideMs.Value;
        _project.DefaultTransMs = (long)_defaultTransMs.Value;

        _renderer ??= new SlideshowRenderer(_project);
        return _renderer;
    }

    private void StepPreview()
    {
        if (_renderer is null) return;
        long ms = (long)_scrub.Value;
        long total = _renderer.TotalDurationMs;
        ms += 40;
        if (ms >= total) { ms = total - 1; TogglePlay(); }
        _scrub.Value = (int)ms;
        ShowFrame(ms);
    }

    private void ScrubPreview()
    {
        if (_renderer is null) return;
        ShowFrame(_scrub.Value);
    }

    private void ShowFrame(long ms)
    {
        if (_renderer is null) return;
        using var frame = _renderer.RenderFrameAt(ms);
        var small = new Bitmap(frame, _preview.Width > 0 ? _preview.Width : 320, _preview.Height > 0 ? _preview.Height : 240);
        var old = _preview.Image;
        _preview.Image = small;
        old?.Dispose();
        _timeLabel.Text = $"{ms / 1000.0:F1}s / {_renderer.TotalDurationMs / 1000.0:F1}s";
    }

    private void TogglePlay()
    {
        if (_renderer is null) GetRenderer();
        if (_playTimer.Enabled)
        {
            _playTimer.Stop();
            _btnPlay.Text = "▶ Play";
        }
        else
        {
            if (_renderer is null || _scrub.Maximum != (int)_renderer.TotalDurationMs)
            {
                _scrub.Minimum = 0;
                _scrub.Maximum = (int)_renderer.TotalDurationMs;
            }
            _playTimer.Start();
            _btnPlay.Text = "⏸ Pause";
        }
    }

    private static (int w, int h) ParseResolution(string s)
    {
        var parts = s.Split('x');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    // ------------------------------------------------------------------
    // Render
    // ------------------------------------------------------------------
    private async void StartRender()
    {
        if (_rendering) return;
        if (_project.SlideCount == 0)
        {
            MessageBox.Show("Add at least one photo first.", "SlideShow Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var kind = (OutputKind)_format.SelectedItem!;
        using var sfd = new SaveFileDialog
        {
            Filter = kind switch
            {
                OutputKind.Avi => "AVI video|*.avi",
                OutputKind.Gif => "Animated GIF|*.gif",
                _ => "PNG frame sequence|*",
            },
            FileName = kind switch
            {
                OutputKind.Avi => "slideshow.avi",
                OutputKind.Gif => "slideshow.gif",
                _ => "slideshow_frames",
            },
            Title = "Render slideshow",
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        _rendering = true;
        _btnRender.Enabled = false;
        _status.Text = "Rendering…";
        _progress.Value = 0;
        try
        {
            var opts = new RenderOptions
            {
                OutputPath = sfd.FileName,
                Kind = kind,
                Cancellation = _renderCts.Token,
                OnProgress = (cur, tot) => BeginInvoke(() =>
                {
                    _progress.Maximum = tot;
                    _progress.Value = cur;
                    _status.Text = $"Rendering… {cur}/{tot} frames";
                }),
            };

            var stats = await Task.Run(() =>
            {
                var r = GetRenderer();
                return r.Render(opts);
            }, _renderCts.Token);

            _status.Text = $"Done: {stats.Frames} frames, {stats.DurationMs}ms, {stats.Bytes:N0} bytes in {stats.ElapsedSec:F1}s → {stats.OutputPath}";
            _progress.Value = _progress.Maximum;
            var ask = MessageBox.Show($"Rendered {stats.Frames} frames to:\n{stats.OutputPath}\n\nOpen it now?",
                "Render complete", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (ask == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(stats.OutputPath) { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Render cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Render failed: {ex.Message}";
            MessageBox.Show($"Render failed:\n{ex.Message}", "SlideShow Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _rendering = false;
            _btnRender.Enabled = true;
        }
    }

    // ------------------------------------------------------------------
    // Engine UI
    // ------------------------------------------------------------------
    private void ShowProbes(IReadOnlyList<DllProbe> probes)
    {
        _probeGrid.Rows.Clear();
        foreach (var p in probes)
            _probeGrid.Rows.Add(p.Pass ? "OK" : "FAIL", p.Dll, p.Function, p.Kind, p.Value);
        int pass = probes.Count(p => p.Pass);
        _engineStatus.Text = _engine.DllDirectory is null
            ? "Engine DLLs NOT FOUND — set WMMR_BUILD_DIR or place them next to the app. (Render uses the managed compositor.)"
            : $"{pass}/{probes.Count} probes passed · dll dir: {_engine.DllDirectory}";
        _btnMovieMaker.Enabled = _engine.EngineDllsPresent;
    }

    private async void RunInMovieMaker()
    {
        if (_project.SlideCount == 0 || _renderer is null)
        {
            MessageBox.Show("Add photos and preview once so the project is ready.", "SlideShow Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var first = _project.Slides.First(s => File.Exists(s.Path));
        using var sfd = new SaveFileDialog { Filter = "Windows Media Video|*.wmv|MPEG-4|*.mp4", Title = "Export with Windows Live Movie Maker", FileName = "slideshow.wmv" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        var confirm = MessageBox.Show(
            "This launches the REAL Windows Live Movie Maker application (via MovieMakerCore.MovieMakerMain --import/--export).\n\n" +
            "It is single-instance guarded and will block until you close the app window. Continue?",
            "Run in Movie Maker", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        string format = sfd.FilterIndex == 2 ? "mp4" : "wmv";
        var argv = SlideshowEngine.BuildMovieMakerArgs(first, sfd.FileName, format);
        _btnMovieMaker.Enabled = false;
        _engineStatus.Text = "Launching MovieMakerMain (real app)…";
        await Task.Run(() =>
        {
            var r = _engine.RunMovieMakerMain(argv, 60_000);
            BeginInvoke(() =>
            {
                _btnMovieMaker.Enabled = true;
                _engineStatus.Text = r.TimedOut
                    ? "MovieMakerMain still running (real app launched) — it blocks until you close it."
                    : r.Error is not null
                        ? $"MovieMakerMain crashed: {r.Error}"
                        : $"MovieMakerMain exited 0x{r.ExitCode:X8} (SUNDANCE_EXIT_SUCCESS = 0).";
            });
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _playTimer.Stop();
        _renderCts.Cancel();
        base.OnFormClosing(e);
    }

    private static GroupBox Group(string title, Control inner)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        g.Controls.Add(inner);
        return g;
    }
}
