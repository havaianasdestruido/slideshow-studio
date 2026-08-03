using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SlideShowStudio;

public partial class Form1 : Form
{
    // UI elements created in designer
    private ListBox lbPhotos = new();
    private Button btnAdd = new() { Text = "Add..." };
    private ComboBox cbTransition = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private NumericUpDown nudTime = new() { Minimum = 1, Maximum = 60, Value = 3 };
    private Button btnPreview = new() { Text = "Preview" };
    private Button btnExport = new() { Text = "Export" };
    private PictureBox pbPreview = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private List<string> files = new();
    private int previewIndex = 0;
    private Timer previewTimer;
    public Form1()
    {
        InitializeComponent();
        SetupUi();
    }
    private void SetupUi()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 200 };
        Controls.Add(split);
        var leftPanel = new Panel { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(leftPanel);
        lbPhotos.Dock = DockStyle.Top; lbPhotos.Height = 200; leftPanel.Controls.Add(lbPhotos);
        btnAdd.Dock = DockStyle.Top; leftPanel.Controls.Add(btnAdd);
        btnAdd.Click += (s,e)=>AddPhotos();
        pbPreview.Dock = DockStyle.Fill; split.Panel2.Controls.Add(pbPreview);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        Controls.Add(bottom);
        bottom.Controls.Add(new Label { Text = "Transition:" });
        bottom.Controls.Add(cbTransition);
        cbTransition.Items.AddRange(new string[]{"None","Fade"});
        cbTransition.SelectedIndex = 0;
        bottom.Controls.Add(new Label { Text = "Timing (s):" });
        bottom.Controls.Add(nudTime);
        bottom.Controls.Add(btnPreview);
        bottom.Controls.Add(btnExport);
        btnPreview.Click += (s,e)=>StartPreview();
        btnExport.Click += (s,e)=>ExportSequence();
    }
    private void AddPhotos()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Filter = "Images|*.jpg;*.png;*.bmp" };
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            foreach (var f in dlg.FileNames)
            { files.Add(f); lbPhotos.Items.Add(Path.GetFileName(f)); }
        }
    }
    private void StartPreview()
    {
        if (files.Count == 0) return;
        previewIndex = 0;
        previewTimer = new Timer { Interval = (int)nudTime.Value * 1000 };
        previewTimer.Tick += (s,e)=>{ previewIndex = (previewIndex+1)%files.Count; ShowImage(files[previewIndex]); };
        ShowImage(files[previewIndex]);
        previewTimer.Start();
    }
    private void ShowImage(string path)
    {
        try { var bmp = new Bitmap(path); pbPreview.Image = bmp; }
        catch { pbPreview.Image = null; }
    }
    private void ExportSequence()
    {
        if (files.Count == 0) return;
        using var fbd = new FolderBrowserDialog();
        if (fbd.ShowDialog() != DialogResult.OK) return;
        var outDir = Path.Combine(fbd.SelectedPath, "out");
        Directory.CreateDirectory(outDir);
        for (int i=0;i<files.Count;i++)
        {
            var bmp = new Bitmap(files[i]);
            var fn = Path.Combine(outDir, $"slide{(i+1):D3}.png");
            bmp.Save(fn, System.Drawing.Imaging.ImageFormat.Png);
        }
        MessageBox.Show("Export done", "SlideShowStudio");
    }
}
