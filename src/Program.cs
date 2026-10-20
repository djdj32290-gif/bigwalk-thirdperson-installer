using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Big Walk 第三人称安装器")]
[assembly: System.Reflection.AssemblyDescription("ThirdPersonComfort 一键安装与卸载")]
[assembly: System.Reflection.AssemblyVersion("1.0.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.2.0")]

namespace BigWalkInstaller
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 2 && (args[0] == "--install" || args[0] == "--uninstall"))
            {
                var output = args.Length >= 3 ? args[2] : null;
                var lines = new System.Collections.Generic.List<string>();
                try
                {
                    var result = args[0] == "--install" ? Core.Install(args[1], true, lines.Add) : Core.Uninstall(args[1], lines.Add);
                    lines.Add(result.Message);
                    if (output != null) File.WriteAllLines(output, lines, System.Text.Encoding.UTF8);
                    return result.Complete ? 0 : 2;
                }
                catch (Exception ex)
                {
                    lines.Add(ex.ToString());
                    if (output != null) File.WriteAllLines(output, lines, System.Text.Encoding.UTF8);
                    return 1;
                }
            }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => MessageBox.Show(e.Exception.Message, "Big Walk 第三人称", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new MainForm(args.Length >= 2 && args[0] == "--game" ? args[1] : null));
            return 0;
        }
    }
    sealed class MainForm : Form
    {
        readonly Color ink = Color.FromArgb(25, 36, 49);
        readonly Color blue = Color.FromArgb(34, 93, 213);
        TextBox path, log;
        Label status, detail;
        CheckBox stayThirdPerson;
        Button install, uninstall, browse, launch;
        ProgressBar progress;
        bool busy;
        public MainForm(string selected)
        {
            Text = "Big Walk 第三人称安装器";
            Font = new Font("Microsoft YaHei UI", 9F);
            ForeColor = ink;
            BackColor = Color.FromArgb(246, 248, 251);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(760, 660);
            MinimumSize = Size;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 22, 26, 18), ColumnCount = 1, RowCount = 10 };
            foreach (float height in new float[] { 86, 82, 28, 40, 54, 56, 19, 26 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            Controls.Add(layout);

            var header = new Panel { Dock = DockStyle.Fill };
            header.Controls.Add(new Label { Text = "BIG WALK", Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = blue, Location = new Point(0, 0), AutoSize = true });
            header.Controls.Add(new Label { Text = "第三人称，一键就位", Font = new Font("Microsoft YaHei UI", 22F, FontStyle.Bold), Location = new Point(-3, 20), AutoSize = true });
            var version = new Label { Text = "ThirdPersonComfort 1.1.0\n内置安装包 · 无需管理器", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Right, Width = 225, ForeColor = Color.FromArgb(103, 115, 132) };
            header.Controls.Add(version);
            layout.Controls.Add(header, 0, 0);

            var statePanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14, 10, 14, 8), Margin = new Padding(0, 0, 0, 8) };
            status = new Label { Text = "正在查找游戏……", Dock = DockStyle.Top, Height = 25, Font = new Font(Font.FontFamily, 11F, FontStyle.Bold) };
            detail = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(105, 116, 133) };
            statePanel.Controls.Add(detail); statePanel.Controls.Add(status); layout.Controls.Add(statePanel, 0, 1);
            layout.Controls.Add(new Label { Text = "游戏安装位置", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);

            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            path = new TextBox { Dock = DockStyle.Fill, Text = selected ?? Core.FindGame(), Margin = new Padding(0, 5, 10, 0), AccessibleName = "游戏安装位置" };
            path.TextChanged += (s, e) => RefreshStatus();
            browse = MakeButton("更改位置", false); browse.Margin = new Padding(0); browse.Dock = DockStyle.Fill; browse.Click += Browse;
            pathRow.Controls.Add(path, 0, 0); pathRow.Controls.Add(browse, 1, 0); layout.Controls.Add(pathRow, 0, 3);

            var options = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            stayThirdPerson = new CheckBox { Text = "交互时也保持第三人称", Checked = true, Location = new Point(0, 8), AutoSize = true };
            options.Controls.Add(stayThirdPerson);
            options.Controls.Add(new Label { Text = "F4 切换视角    ·    滚轮调整镜头距离", AutoSize = true, Location = new Point(0, 32), ForeColor = Color.FromArgb(105, 116, 133) });
            layout.Controls.Add(options, 0, 4);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            install = MakeButton("安装第三人称", true); uninstall = MakeButton("卸载 mod", false);
            install.Margin = new Padding(0, 4, 7, 5); uninstall.Margin = new Padding(7, 4, 0, 5);
            install.Click += async (s, e) => await RunOperation(true); uninstall.Click += async (s, e) => await RunOperation(false);
            actions.Controls.Add(install, 0, 0); actions.Controls.Add(uninstall, 1, 0); layout.Controls.Add(actions, 0, 5);
            progress = new ProgressBar { Dock = DockStyle.Fill, Height = 6, Margin = new Padding(0, 5, 0, 5), Style = ProgressBarStyle.Continuous };
            layout.Controls.Add(progress, 0, 6);
            layout.Controls.Add(new Label { Text = "操作记录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(105, 116, 133) }, 0, 7);
            log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei UI", 8.5F), AccessibleName = "操作记录" };
            layout.Controls.Add(log, 0, 8);

            var footer = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            var folder = new LinkLabel { Text = "打开游戏目录", AutoSize = true, Location = new Point(0, 14), LinkColor = blue };
            folder.LinkClicked += (s, e) => { try { var p = Core.Normalize(path.Text); if (Directory.Exists(p)) Process.Start("explorer.exe", "\"" + p + "\""); } catch (Exception ex) { ShowError(ex); } };
            var about = new LinkLabel { Text = "关于与许可", AutoSize = true, Location = new Point(125, 14), LinkColor = blue };
            about.LinkClicked += (s, e) => MessageBox.Show("Big Walk 第三人称安装器 1.0\n\n内置 ThirdPersonComfort 1.1.0（Ranats，MIT）与 BepInEx 6.0.788（LGPL-2.1）。\n原作者的许可与说明已随软件交付。\n\n安装器会记录新增文件并备份原有 mod 配置。卸载时保留游戏、存档，以及其他 mod 共用的加载器。\n\n首次启动游戏可能需要联网下载 Unity 支持库并生成程序集，请耐心等待。", "关于与许可", MessageBoxButtons.OK, MessageBoxIcon.Information);
            launch = MakeButton("启动游戏", false); launch.Dock = DockStyle.Right; launch.Width = 130; launch.Margin = new Padding(0); launch.Click += (s, e) => { try { Process.Start("steam://run/1478500"); } catch (Exception ex) { ShowError(ex); } };
            footer.Controls.Add(folder); footer.Controls.Add(about); footer.Controls.Add(launch); layout.Controls.Add(footer, 0, 9);
            FormClosing += (s, e) => { if (busy) { e.Cancel = true; MessageBox.Show("正在完成文件操作，请等待结束后再关闭。", Text); } };
            Shown += (s, e) => { RefreshStatus(); Append("已就绪。安装和卸载前请先退出 Big Walk。"); Append("安装后默认开启第三人称；卸载会保留游戏与存档。"); };
        }
        Button MakeButton(string text, bool primary)
        {
            var b = new Button { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, BackColor = primary ? blue : Color.White, ForeColor = primary ? Color.White : ink, Font = new Font(Font.FontFamily, 10F, FontStyle.Bold), UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderColor = primary ? blue : Color.FromArgb(212, 220, 231);
            return b;
        }
        void Browse(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择 Big Walk.exe 所在文件夹", ShowNewFolderButton = false, SelectedPath = path.Text })
                if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.SelectedPath;
        }
        void RefreshStatus()
        {
            if (busy || install == null) return;
            bool found = false, tracked = Core.HasState(path.Text), installed = Core.IsInstalled(path.Text);
            try { Core.ValidateGame(path.Text); found = true; } catch { }
            status.Text = installed ? "已安装第三人称 mod" : tracked ? "发现安装记录，可卸载或恢复" : found ? "已找到游戏 · 尚未安装" : "请选择 Big Walk 游戏目录";
            status.ForeColor = installed ? Color.FromArgb(26, 124, 87) : ink;
            detail.Text = installed ? "F4 切换视角，滚轮调整距离；退出游戏后可卸载。" : tracked ? "卸载功能也可在 Steam 卸载游戏后清理记录中的文件。" : found ? "点击下方按钮即可安装，无需手动复制文件。" : "请选择含有 Big Walk.exe 的文件夹。";
            install.Enabled = found && !tracked; uninstall.Enabled = tracked; stayThirdPerson.Enabled = !tracked; launch.Enabled = found;
        }
        void Append(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Append), text); return; }
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
        }
        async Task RunOperation(bool installing)
        {
            if (busy) return;
            var root = path.Text; var stay = stayThirdPerson.Checked;
            busy = true; install.Enabled = uninstall.Enabled = browse.Enabled = launch.Enabled = stayThirdPerson.Enabled = path.Enabled = false;
            progress.Style = ProgressBarStyle.Marquee; status.Text = installing ? "正在安装……" : "正在卸载……";
            try
            {
                var result = await Task.Run(() => installing ? Core.Install(root, stay, Append) : Core.Uninstall(root, Append));
                Append(result.Message);
                MessageBox.Show(this, result.Message, installing ? "安装结果" : "卸载结果", MessageBoxButtons.OK, result.Complete ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (UnauthorizedAccessException) { ShowError(new Exception("没有写入此游戏目录的权限。请关闭游戏，或右键此安装器选择“以管理员身份运行”。")); }
            catch (Exception ex) { ShowError(ex); }
            finally
            {
                busy = false; path.Enabled = browse.Enabled = true; progress.Style = ProgressBarStyle.Continuous; progress.Value = 0; RefreshStatus();
            }
        }
        void ShowError(Exception ex) { Append("操作未完成：" + ex.Message); MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
