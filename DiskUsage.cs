using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

class DiskUsage : Form
{
    class Entry
    {
        public string Name, Path;
        public bool IsDir;
        public long Size;
        public int Files;
    }

    ComboBox pathBox;
    Button upButton, scanButton, openButton;
    ListView list;
    Label statusLabel;
    string current;
    long currentTotal;
    Thread worker;
    volatile int generation;
    readonly Dictionary<string, List<Entry>> cache = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

    DiskUsage(string start)
    {
        Text = "DiskUsage";
        ClientSize = new Size(720, 480);
        MinimumSize = new Size(560, 340);
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;

        upButton = new Button { Text = "↑ Вверх", Location = new Point(12, 11), Size = new Size(90, 29) };
        upButton.Click += (s, e) => GoUp();
        Controls.Add(upButton);
        pathBox = new ComboBox { Location = new Point(110, 12), Width = 400, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        foreach (var d in DriveInfo.GetDrives()) if (d.IsReady) pathBox.Items.Add(d.Name);
        pathBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Open(pathBox.Text, false); } };
        pathBox.SelectionChangeCommitted += (s, e) => BeginInvoke(new Action(() => Open(pathBox.Text, false)));
        Controls.Add(pathBox);
        openButton = new Button { Text = "Обзор…", Location = new Point(518, 11), Size = new Size(90, 29), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        openButton.Click += (s, e) => Browse();
        Controls.Add(openButton);
        scanButton = new Button { Text = "Обновить", Location = new Point(616, 11), Size = new Size(92, 29), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        scanButton.Click += (s, e) => Open(current ?? pathBox.Text, true);
        Controls.Add(scanButton);

        list = new ListView
        {
            Location = new Point(12, 50), Size = new Size(696, 390), View = View.Details, FullRowSelect = true, OwnerDraw = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        list.Columns.Add("Имя", 290);
        list.Columns.Add("Размер", 100, HorizontalAlignment.Right);
        list.Columns.Add("Доля", 180);
        list.Columns.Add("Файлов", 90, HorizontalAlignment.Right);
        list.DrawColumnHeader += (s, e) => e.DrawDefault = true;
        list.DrawSubItem += DrawSubItem;
        list.DoubleClick += (s, e) => OpenSelected();
        list.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) OpenSelected();
            if (e.KeyCode == Keys.Back) GoUp();
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Показать в Проводнике", null, (s, e) => { var en = Selected(); if (en != null) Process.Start("explorer.exe", "/select,\"" + en.Path + "\""); });
        list.ContextMenuStrip = menu;
        Controls.Add(list);

        statusLabel = new Label { Location = new Point(12, 450), AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        Controls.Add(statusLabel);

        string first = start ?? (pathBox.Items.Count > 0 ? (string)pathBox.Items[0] : "C:\\");
        Shown += (s, e) => Open(first, false);
    }

    Entry Selected()
    {
        return list.SelectedItems.Count > 0 ? (Entry)list.SelectedItems[0].Tag : null;
    }

    void OpenSelected()
    {
        var en = Selected();
        if (en == null) return;
        if (en.IsDir) Open(en.Path, false);
        else try { Process.Start(en.Path); } catch { }
    }

    void GoUp()
    {
        if (current == null || Path.GetPathRoot(current) == current) return;
        Open(Directory.GetParent(current).FullName, false);
    }

    void Browse()
    {
        using (var d = new FolderBrowserDialog { SelectedPath = current })
            if (d.ShowDialog(this) == DialogResult.OK) Open(d.SelectedPath, false);
    }

    void Open(string path, bool refresh)
    {
        if (!Directory.Exists(path)) { MessageBox.Show(this, "Папка не найдена", Text); return; }
        path = Path.GetFullPath(path);
        current = path;
        pathBox.Text = path;
        upButton.Enabled = Directory.GetParent(path) != null;
        if (refresh) cache.Clear();
        List<Entry> cached;
        if (cache.TryGetValue(path, out cached)) { Show(cached, false); return; }

        list.Items.Clear();
        statusLabel.Text = "Считаю размер…";
        int gen = ++generation;
        worker = new Thread(() => Scan(path, gen)) { IsBackground = true };
        worker.Start();
    }

    void Scan(string path, int gen)
    {
        var entries = new List<Entry>();
        try
        {
            foreach (var d in new DirectoryInfo(path).GetDirectories())
                if ((d.Attributes & FileAttributes.ReparsePoint) == 0)
                    entries.Add(new Entry { Name = d.Name, Path = d.FullName, IsDir = true });
            foreach (var f in new DirectoryInfo(path).GetFiles())
                entries.Add(new Entry { Name = f.Name, Path = f.FullName, Size = f.Length, Files = 1 });
        }
        catch (Exception ex)
        {
            BeginInvoke(new Action(() => statusLabel.Text = "Нет доступа: " + ex.Message));
            return;
        }
        BeginInvoke(new Action(() => { if (gen == generation) Show(entries, true); }));

        var last = DateTime.Now;
        foreach (var en in entries.Where(x => x.IsDir))
        {
            if (gen != generation) return;
            Measure(en, en.Path, gen);
            if ((DateTime.Now - last).TotalMilliseconds > 300)
            {
                last = DateTime.Now;
                BeginInvoke(new Action(() => { if (gen == generation) Show(entries, true); }));
            }
        }
        BeginInvoke(new Action(() =>
        {
            if (gen != generation) return;
            cache[path] = entries;
            Show(entries, false);
        }));
    }

    void Measure(Entry en, string root, int gen)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0 && gen == generation)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var f in dir.GetFiles()) { en.Size += f.Length; en.Files++; }
                foreach (var d in dir.GetDirectories())
                    if ((d.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(d);
            }
            catch { }
        }
    }

    void Show(List<Entry> entries, bool scanning)
    {
        var sorted = entries.OrderByDescending(x => x.Size).ThenBy(x => x.Name).ToList();
        currentTotal = sorted.Sum(x => x.Size);
        string selected = Selected() != null ? Selected().Path : null;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var en in sorted)
        {
            var item = new ListViewItem(new[] { (en.IsDir ? "📁 " : "📄 ") + en.Name, FormatSize(en.Size), "", en.Files.ToString("N0") }) { Tag = en };
            if (en.Path == selected) item.Selected = true;
            list.Items.Add(item);
        }
        list.EndUpdate();

        string text = "Всего: " + FormatSize(currentTotal) + " в " + sorted.Sum(x => (long)x.Files).ToString("N0") + " файлах";
        var drive = new DriveInfo(Path.GetPathRoot(current));
        if (drive.IsReady) text += "   ·   Свободно на диске " + drive.Name.TrimEnd('\\') + " " + FormatSize(drive.AvailableFreeSpace) + " из " + FormatSize(drive.TotalSize);
        statusLabel.Text = (scanning ? "Считаю…   " : "") + text;
    }

    void DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex != 2) { e.DrawDefault = true; return; }
        e.DrawBackground();
        var en = (Entry)e.Item.Tag;
        double share = currentTotal > 0 ? (double)en.Size / currentTotal : 0;
        var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 4, e.Bounds.Width - 60, e.Bounds.Height - 8);
        using (var back = new SolidBrush(Color.FromArgb(230, 234, 240))) e.Graphics.FillRectangle(back, r);
        int w = (int)(r.Width * share);
        if (w > 0)
            using (var bar = new SolidBrush(share > 0.5 ? Color.FromArgb(214, 90, 70) : share > 0.2 ? Color.FromArgb(232, 160, 60) : Color.FromArgb(70, 130, 200)))
                e.Graphics.FillRectangle(bar, r.X, r.Y, w, r.Height);
        TextRenderer.DrawText(e.Graphics, (share * 100).ToString("0.0") + "%", list.Font,
            new Rectangle(r.Right + 4, e.Bounds.Y, 56, e.Bounds.Height), e.Item.Selected ? SystemColors.HighlightText : Color.DimGray,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    static string FormatSize(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return (u == 0 ? v.ToString("0") : v.ToString("0.0")) + " " + units[u];
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.Run(new DiskUsage(args.Length > 0 && Directory.Exists(args[0]) ? args[0] : null));
    }
}
