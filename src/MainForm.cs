using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EndfieldSteamBridge
{
    internal sealed class MainForm : Form
    {
        private readonly Bitmap backgroundArt;
        private readonly Color ink = Color.FromArgb(32, 45, 57), blue = Color.FromArgb(26, 112, 180), muted = Color.FromArgb(92, 108, 120);
        private Label account, client, installed, status;
        private TextBox launcher, game;
        private Button enable, restore, refresh, chooseLauncher, chooseGame;
        private DetectionResult current;
        private bool busy, previewRendering;
        public MainForm()
        {
            using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("EndfieldSteamBridge.Background"))
            using (var image = Image.FromStream(stream)) backgroundArt = new Bitmap(image);
            DoubleBuffered = true;
            ResizeRedraw = true;
            Text = "Endfield Steam Bridge";
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ForeColor = ink; BackColor = Color.FromArgb(240, 243, 245);
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(820, 710);
            MinimumSize = new Size(820, 745); AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new BufferedLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(32, 24, 32, 20), ColumnCount = 1, RowCount = 7 };
            foreach (int height in new[] { 32, 64, 44 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 64, 64, 28 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(layout);
            layout.Controls.Add(MakeLabel("ENDFIELD  /  STEAM BRIDGE                                  v" + App.Version, 10, muted), 0, 0);
            layout.Controls.Add(MakeLabel("官服终末地接入Steam启动", 26, ink), 0, 1);
            layout.Controls.Add(MakeLabel("使用已有国服客户端 · 自动启动 · Steam 计时与原生叠加层", 10, muted), 0, 2);
            var card = new BufferedLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(184, Color.White), Padding = new Padding(20, 14, 20, 10), ColumnCount = 1, RowCount = 9, Margin = new Padding(0) };
            foreach (int h in new[] { 25, 40, 25, 33, 42, 42, 25, 36 }) card.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(card, 0, 3);
            card.Controls.Add(MakeLabel("01   STEAM 账号与游戏库", 10, muted), 0, 0);
            account = MakeLabel("正在检测…", 11, ink); card.Controls.Add(account, 0, 1);
            card.Controls.Add(MakeLabel("02   国服客户端", 10, muted), 0, 2);
            client = MakeLabel("正在查找鹰角启动器与国服…", 10, ink); card.Controls.Add(client, 0, 3);
            card.Controls.Add(PathRow("启动器", out launcher, out chooseLauncher), 0, 4);
            card.Controls.Add(PathRow("国服", out game, out chooseGame), 0, 5);
            card.Controls.Add(MakeLabel("03   载入状态", 10, muted), 0, 6);
            installed = MakeLabel("尚未载入", 10, ink); card.Controls.Add(installed, 0, 7);
            chooseLauncher.Click += delegate { Choose(true); }; chooseGame.Click += delegate { Choose(false); };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 12, 0, 0), Margin = new Padding(0) };
            enable = MakeButton("一键载入国服", 190, true); restore = MakeButton("卸载并恢复 Steam", 190, false); refresh = MakeButton("重新检测", 126, false);
            actions.Controls.AddRange(new Control[] { enable, restore, refresh }); layout.Controls.Add(actions, 0, 4);
            enable.Click += async delegate { await Mutate(false); }; restore.Click += async delegate { await Mutate(true); }; refresh.Click += async delegate { await RefreshDetection(); };
            status = MakeLabel("首次使用请先登录 Steam，并将《明日方舟：终末地》添加入库。", 9.5f, muted);
            status.Padding = new Padding(0, 5, 0, 0); layout.Controls.Add(status, 0, 5);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, WrapContents = false, Margin = new Padding(0) };
            var library = new LinkLabel { Text = "打开 Steam 游戏库", AutoSize = true, BackColor = Color.Transparent, LinkColor = blue, Margin = new Padding(0, 0, 22, 0) };
            library.LinkClicked += delegate { Process.Start("steam://nav/games/details/" + App.AppId); };
            var logs = new LinkLabel { Text = "本地记录与备份", AutoSize = true, BackColor = Color.Transparent, LinkColor = blue, Margin = new Padding(0) };
            logs.LinkClicked += delegate { Directory.CreateDirectory(App.Home); Process.Start("explorer.exe", Arguments.Quote(App.Home)); };
            footer.Controls.Add(library); footer.Controls.Add(logs); layout.Controls.Add(footer, 0, 6);
            Shown += async delegate { if (!previewRendering) await RefreshDetection(); };
            FormClosing += delegate(object s, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.Text = "正在处理，请等待当前操作完成。"; } };
            SetBusy(true);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (backgroundArt == null || ClientSize.Width == 0 || ClientSize.Height == 0) { base.OnPaintBackground(e); return; }
            float scale = Math.Max((float)ClientSize.Width / backgroundArt.Width, (float)ClientSize.Height / backgroundArt.Height);
            float width = backgroundArt.Width * scale, height = backgroundArt.Height * scale;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(backgroundArt, (ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
            // Keep the illustration visible behind the translucent content;
            // the stronger wash at the edges keeps the heading and notes readable.
            using (var wash = new LinearGradientBrush(ClientRectangle, Color.White, Color.White, 90f))
            {
                wash.InterpolationColors = new ColorBlend {
                    Colors = new[] { Color.FromArgb(202, Color.White), Color.FromArgb(126, Color.White), Color.FromArgb(24, Color.White), Color.FromArgb(24, Color.White), Color.FromArgb(218, Color.White) },
                    Positions = new[] { 0f, .24f, .40f, .74f, 1f }
                };
                e.Graphics.FillRectangle(wash, ClientRectangle);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && backgroundArt != null) backgroundArt.Dispose();
            base.Dispose(disposing);
        }
        private sealed class BufferedLayoutPanel : TableLayoutPanel
        {
            public BufferedLayoutPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        }
        private Label MakeLabel(string text, float size, Color color)
        { return new Label { Text = text, Font = new Font("Microsoft YaHei UI", size), ForeColor = color, BackColor = Color.Transparent, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(0) }; }
        private Button MakeButton(string text, int width, bool primary)
        {
            var b = new Button { Text = text, Width = width, Height = 40, FlatStyle = FlatStyle.Flat, BackColor = primary ? blue : Color.White, ForeColor = primary ? Color.White : ink, Margin = new Padding(0, 0, 12, 0), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = primary ? blue : Color.FromArgb(200, 210, 218); return b;
        }
        private Control PathRow(string title, out TextBox text, out Button browse)
        {
            var row = new BufferedLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 3, Margin = new Padding(0, 0, 0, 6) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            row.Controls.Add(MakeLabel(title, 9, muted), 0, 0);
            text = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(247, 249, 250), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 8, 0), Font = new Font("Microsoft YaHei UI", 9) };
            browse = MakeButton("选择", 60, false); browse.Dock = DockStyle.Fill; browse.Height = 30; browse.Margin = new Padding(0);
            row.Controls.Add(text, 1, 0); row.Controls.Add(browse, 2, 0); return row;
        }
        private async void Choose(bool isLauncher)
        {
            using (var dialog = new OpenFileDialog { Filter = isLauncher ? "鹰角启动器 (Launcher.exe)|Launcher.exe" : "国服终末地 (Endfield.exe)|Endfield.exe", Title = isLauncher ? "选择国服鹰角启动器根目录中的 Launcher.exe" : "选择国服游戏目录中的 Endfield.exe", CheckFileExists = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) { if (isLauncher) launcher.Text = dialog.FileName; else game.Text = dialog.FileName; await RefreshDetection(); }
        }
        private void SetBusy(bool value)
        {
            busy = value;
            refresh.Enabled = chooseLauncher.Enabled = chooseGame.Enabled = !value;
            bool stateExists = File.Exists(App.StatePath);
            enable.Enabled = !value && !stateExists && current != null && current.Account != null && current.Account.Known && current.Account.Owned && Detection.ValidLauncher(current.Launcher) && Detection.ValidGame(current.Game);
            restore.Enabled = !value && stateExists;
        }
        private async Task RefreshDetection()
        {
            SetBusy(true); status.Text = "正在读取当前 Steam 账号与国服安装位置…";
            string l = String.IsNullOrWhiteSpace(launcher.Text) ? null : launcher.Text, g = String.IsNullOrWhiteSpace(game.Text) ? null : game.Text;
            try { current = await Task.Run(() => Detection.Scan(l, g)); Populate(); }
            finally { SetBusy(false); }
        }
        private void Populate()
        {
            var a = current.Account;
            account.Text = a == null || !a.Known ? (a == null ? "Steam 未就绪" : a.Error) : (a.Owned ? "已拥有终末地  ·  " : "尚未拥有终末地  ·  ") + (a.Name ?? a.SteamId.ToString());
            account.ForeColor = a != null && a.Known && a.Owned ? blue : Color.FromArgb(162, 85, 30);
            launcher.Text = current.Launcher ?? launcher.Text; game.Text = current.Game ?? game.Text;
            client.Text = current.GameStatus ?? "等待 Steam 检测完成";
            var state = current.Installation;
            installed.Text = state == null ? "尚未载入。原来的 Steam 游戏文件会保留。" : state.Phase == "enabled" ? "已载入国服 · 卸载后" + (state.WasInstalled ? "恢复原国际服。" : "恢复“安装”按钮。") : "上次操作尚未完成，请点击“卸载并恢复 Steam”还原。";
            status.Text = current.Error ?? (a == null || !a.Known ? "请确认 Steam 已在线登录，再点“重新检测”。" : !a.Owned ? "请先在 Steam 商店将终末地免费添加入库，再重新检测。" : "载入或卸载时会正常退出并重启 Steam；请先关闭游戏和鹰角启动器。\nSteam 需要以管理员身份运行，工具重启 Steam 时会使用此权限。");
        }
        private async Task Mutate(bool uninstall)
        {
            SetBusy(true);
            Action<string> report = message => { if (!IsDisposed) BeginInvoke((Action)(() => status.Text = message)); };
            string outcome = null;
            try
            {
                if (uninstall) await Task.Run(() => InstallService.Restore(report)); else await Task.Run(() => InstallService.Enable(current, report));
                outcome = uninstall ? "已卸载并恢复 Steam。等待客户端启动后，可检查游戏库按钮。" : "载入完成。等待 Steam 启动完成后，点击终末地的“开始游戏”。";
            }
            catch (Exception e) { outcome = e.Message; MessageBox.Show(this, e.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            finally
            {
                if (current == null) current = new DetectionResult();
                current.Installation = File.Exists(App.StatePath) ? App.Load<Installation>(App.StatePath) : null;
                Populate(); status.Text = outcome; SetBusy(false);
            }
        }
        public void SavePreview(string path)
        {
            previewRendering = true; CreateControl(); current = Detection.Scan(null, null); Populate(); SetBusy(false); Show(); ActiveControl = refresh; launcher.SelectionLength = 0; Application.DoEvents();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path); }
            Hide();
        }
    }
}
