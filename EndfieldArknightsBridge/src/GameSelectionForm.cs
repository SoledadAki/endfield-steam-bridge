using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace EndfieldSteamBridge
{
    // This window stays in Steam's frontend; closing it launches no helper.
    internal sealed class GameSelectionForm : Form
    {
        private readonly Bitmap endfieldArt, arknightsArt;
        internal string SelectedGame { get; private set; }
        private static Bitmap LoadArt(string name)
        {
            using (var stream = typeof(GameSelectionForm).Assembly.GetManifestResourceStream("EndfieldSteamBridge." + name))
            using (var image = Image.FromStream(stream)) return new Bitmap(image);
        }
        internal GameSelectionForm(RuntimeConfig config)
        {
            endfieldArt = LoadArt("EndfieldArt"); arknightsArt = LoadArt("ArknightsArt");
            Text = "终末地 + 明日方舟";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BackColor = Color.FromArgb(19, 25, 31);
            Font = new Font("Microsoft YaHei UI", 10);
            DoubleBuffered = true; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 660); AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 2, 22, 22), ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bool ready;
            Detection.ClientStatus(config.Launcher, config.Game, GameChannel.Official.Id, out ready);
            var endfield = new GameButton(false, endfieldArt, ready) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 9, 0) };
            var arknights = new GameButton(true, arknightsArt, Detection.ArknightsReady(config.ArknightsLauncher, config.ArknightsGame)) { Dock = DockStyle.Fill, Margin = new Padding(9, 0, 0, 0) };
            endfield.Click += delegate { Choose("endfield"); };
            arknights.Click += delegate { Choose("arknights"); };
            layout.Controls.Add(endfield, 0, 0); layout.Controls.Add(arknights, 1, 0);
            layout.Margin = new Padding(0);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(layout, 0, 1); Controls.Add(root);
            // A slim custom drag strip replaces the Windows title bar.
            var chrome = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0, 7, 22, 7), BackColor = BackColor };
            chrome.MouseDown += delegate(object sender, MouseEventArgs e) { if(e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } };
            var close = new ChromeButton(true) { Dock = DockStyle.Right, Width = 30, AccessibleName = "关闭" };
            var minimize = new ChromeButton(false) { Dock = DockStyle.Right, Width = 30, AccessibleName = "最小化" };
            close.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            minimize.Click += delegate { WindowState = FormWindowState.Minimized; };
            chrome.Controls.Add(minimize); chrome.Controls.Add(close); root.Controls.Add(chrome, 0, 0);
            KeyPreview = true;
            KeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.KeyCode == Keys.Left) { endfield.Focus(); e.Handled = true; }
                else if (e.KeyCode == Keys.Right) { arknights.Focus(); e.Handled = true; }
            };
            Shown += delegate {
                var area = Screen.FromControl(this).WorkingArea;
                float scale = Math.Min(1f, Math.Min((area.Width - 32f) / Width, (area.Height - 32f) / Height));
                if(scale < 1) { Size = new Size((int)(Width * scale), (int)(Height * scale)); Location = new Point(area.Left + (area.Width - Width)/2, area.Top + (area.Height - Height)/2); }
                if(endfield.Enabled) endfield.Focus(); else arknights.Focus();
            };
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        private void Choose(string id) { SelectedGame = id; DialogResult = DialogResult.OK; Close(); }
        internal void SavePreview(string path)
        {
            Show(); Application.DoEvents();
            using (var image = new Bitmap(Width, Height)) { DrawToBitmap(image, new Rectangle(0, 0, Width, Height)); image.Save(path); }
            Hide();
        }
        protected override void Dispose(bool disposing) { if(disposing) { endfieldArt.Dispose(); arknightsArt.Dispose(); } base.Dispose(disposing); }
        private sealed class ChromeButton : Button
        {
            private readonly bool close;
            private bool hover;
            internal ChromeButton(bool isClose) { close = isClose; Cursor = Cursors.Hand; TabStop = false; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnPaint(PaintEventArgs e) {
                var g = e.Graphics;
                g.Clear(Color.FromArgb(19, 25, 31));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float x = Width / 2f, y = Height / 2f;
                using(var shape = new GraphicsPath()) {
                    shape.AddPolygon(new[] { new PointF(x-10,y-10), new PointF(x+6,y-10), new PointF(x+10,y-6), new PointF(x+10,y+10), new PointF(x-6,y+10), new PointF(x-10,y+6) });
                    using(var fill = new SolidBrush(hover ? Color.FromArgb(44,60,70) : Color.FromArgb(26,35,42))) g.FillPath(fill,shape);
                    using(var edge = new Pen(hover ? Color.FromArgb(111,150,165) : Color.FromArgb(45,59,68),1)) g.DrawPath(edge,shape);
                }
                using(var pen = new Pen(hover ? Color.FromArgb(220,235,240) : Color.FromArgb(119,141,154),1.2f)) {
                    if(close) { g.DrawLine(pen,x-3,y-3,x+3,y+3); g.DrawLine(pen,x-3,y+3,x+3,y-3); }
                    else g.DrawLine(pen,x-3.5f,y+1.5f,x+3.5f,y+1.5f);
                }
            }
        }
        private sealed class GameButton : Button
        {
            private readonly bool arknights, ready;
            private readonly Image art;
            private bool hover;
            internal GameButton(bool isArknights, Image image, bool available)
            {
                arknights = isArknights; art = image; ready = available;
                AccessibleName = Text = isArknights ? "启动明日方舟" : "启动明日方舟：终末地";
                Enabled = available; Cursor = available ? Cursors.Hand : Cursors.Default;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Color.FromArgb(19,25,31));
                g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.ScaleTransform(Width / 469f, Height / 596f);
                bool active = ready && (hover || Focused);
                Color accent = arknights ? Color.FromArgb(147, 221, 239) : Color.FromArgb(244, 230, 78);
                using(var shape = new GraphicsPath()) {
                    shape.AddPolygon(new[] { new Point(0,0), new Point(453,0), new Point(469,16), new Point(469,596), new Point(16,596), new Point(0,580) });
                    g.SetClip(shape);
                    float scale = Math.Max(469f/art.Width, 596f/art.Height);
                    float w=art.Width*scale, h=art.Height*scale;
                    g.DrawImage(art,(469-w)/2,(596-h)/2,w,h);
                    using(var shade = new LinearGradientBrush(new Rectangle(0,0,469,596), Color.Transparent, Color.Black,90f)) {
                        shade.InterpolationColors = new ColorBlend { Colors = new[] {Color.FromArgb(12,10,18,24),Color.FromArgb(0,10,18,24),Color.FromArgb(80,10,18,24),Color.FromArgb(245,10,18,24)}, Positions = new[] {0f,.48f,.66f,1f} };
                        g.FillRectangle(shade,0,0,469,596);
                    }
                    using(var tag = new SolidBrush(Color.FromArgb(188,16,25,32))) g.FillRectangle(tag,24,24,56,27);
                    using(var text = new SolidBrush(Color.White))
                    using(var small = new Font("Microsoft YaHei UI",13,FontStyle.Regular,GraphicsUnit.Pixel))
                    using(var title = new Font("Microsoft YaHei UI",36,FontStyle.Bold,GraphicsUnit.Pixel)) {
                        g.DrawString("官服",small,text,36,28);
                        if(!arknights) g.DrawString("明日方舟",small,text,29,422);
                        g.DrawString(arknights ? "明日方舟" : "终末地",title,text,24,444);
                    }
                    var action = new Rectangle(28,521,413,48);
                    using(var fill = new SolidBrush(active ? accent : Color.FromArgb(205,27,39,49))) g.FillRectangle(fill,action);
                    using(var pen = new Pen(active ? accent : Color.FromArgb(135,184,202,212),1)) g.DrawRectangle(pen,action);
                    using(var text = new SolidBrush(active ? Color.FromArgb(18,30,37) : Color.White))
                    using(var font = new Font("Microsoft YaHei UI",17,FontStyle.Bold,GraphicsUnit.Pixel)) g.DrawString(ready ? "开始游戏" : "客户端不可用",font,text,48,534);
                    using(var pen = new Pen(active ? Color.FromArgb(18,30,37) : accent,2)) { g.DrawLine(pen,397,545,419,545); g.DrawLine(pen,413,539,419,545); g.DrawLine(pen,413,551,419,545); }
                    if(!ready) using(var dim = new SolidBrush(Color.FromArgb(100,19,25,31))) g.FillRectangle(dim,0,0,469,596);
                    g.ResetClip();
                    using(var edge = new Pen(active ? accent : Color.FromArgb(85,128,149,162),active ? 2 : 1)) g.DrawPath(edge,shape);
                }
            }
        }
    }
}
