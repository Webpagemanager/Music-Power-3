using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MusicPower3Setup
{
    public class MainForm : Form
    {
        private bool _isUninstallMode = false;
        private string _installDir;

        private Panel _currentContainer;
        private Label _lblTitle;
        private TextBox _txtPath;
        private RoundedButton _btnBrowse;
        private RoundedCheckBox _chkDesktop;
        private RoundedCheckBox _chkStartMenu;
        private ProgressBar _progressBar;
        private Label _lblStatus;
        private RoundedButton _btnAction;
        private RoundedButton _btnCancel;

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x02000000; return cp; }
        }

        public MainForm(string[] args)
        {
            string exeName = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
            if (exeName.Equals("Uninstall", StringComparison.OrdinalIgnoreCase) || 
                (args != null && args.Length > 0 && args[0].Equals("-uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                _isUninstallMode = true;
                _installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            }
            else
            {
                _installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Music Power 3");
            }

            this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            this.UpdateStyles();

            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.BackColor = Color.FromArgb(40, 40, 40);
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 11f);
            this.Text = _isUninstallMode ? "Music Power 3 Uninstaller" : "Music Power 3 Setup";

            this.ClientSize = new Size(ResponsiveEngine.S(this, 840), ResponsiveEngine.S(this, 620));
            this.MinimumSize = new Size(ResponsiveEngine.S(this, 760), ResponsiveEngine.S(this, 560));
            this.StartPosition = FormStartPosition.CenterScreen;

            int pad = ResponsiveEngine.S(this, 24);
            _currentContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(pad) };

            TableLayoutPanel mainLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ResponsiveEngine.S(this, 58f)));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ResponsiveEngine.S(this, 42f)));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ResponsiveEngine.S(this, 78f)));

            TableLayoutPanel headerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            _lblTitle = new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 18f, FontStyle.Bold), ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft };
            _lblTitle.Text = _isUninstallMode ? "Uninstall Music Power 3" : "Install Music Power 3";
            headerLayout.Controls.Add(_lblTitle, 0, 0);
            mainLayout.Controls.Add(headerLayout, 0, 0);

            RoundedPanel bodyCard = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(55, 55, 55), Padding = new Padding(ResponsiveEngine.S(this, 24)), BorderRadius = ResponsiveEngine.S(this, 16), Margin = new Padding(0, 6, 0, 6) };
            
            if (_isUninstallMode)
            {
                Label lblUninstallDesc = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = "Uninstalling will remove all programs installed in this folder.\n\nAre you sure you want to continue?",
                    Font = new Font("Segoe UI", 14f),
                    ForeColor = Color.LightGray,
                    TextAlign = ContentAlignment.MiddleCenter
                };
                bodyCard.Controls.Add(lblUninstallDesc);
            }
            else
            {
                TableLayoutPanel bodyLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
                bodyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ResponsiveEngine.S(this, 28f))); 
                bodyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ResponsiveEngine.S(this, 48f))); 
                bodyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f)); 
                bodyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f)); 

                Label lblPath = new Label { Dock = DockStyle.Fill, Text = "Installation Folder:", Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Color.LightGray, TextAlign = ContentAlignment.BottomLeft };
                
                TableLayoutPanel pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
                pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ResponsiveEngine.S(this, 150)));

                _txtPath = new TextBox { Dock = DockStyle.Fill, Text = _installDir, BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 12f) };
                _btnBrowse = new RoundedButton { Dock = DockStyle.Fill, Text = "Browse...", BackColor = Color.SteelBlue, ForeColor = Color.White, Margin = new Padding(8, 0, 0, 4), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), BorderRadius = ResponsiveEngine.S(this, 8) };
                _btnBrowse.FlatAppearance.BorderSize = 0; _btnBrowse.Click += BtnBrowse_Click;

                pathRow.Controls.Add(_txtPath, 0, 0); pathRow.Controls.Add(_btnBrowse, 1, 0);

                _chkDesktop = new RoundedCheckBox { Appearance = Appearance.Button, Text = "Create a Desktop shortcut", Checked = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.FromArgb(65, 65, 65), ForeColor = Color.White, Margin = new Padding(0, 8, 0, 4), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 12f, FontStyle.Bold), BorderRadius = ResponsiveEngine.S(this, 8) };
                _chkDesktop.FlatAppearance.CheckedBackColor = Color.MediumSeaGreen; _chkDesktop.FlatAppearance.BorderSize = 0;

                _chkStartMenu = new RoundedCheckBox { Appearance = Appearance.Button, Text = "Add to Start Menu", Checked = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.FromArgb(65, 65, 65), ForeColor = Color.White, Margin = new Padding(0, 4, 0, 8), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 12f, FontStyle.Bold), BorderRadius = ResponsiveEngine.S(this, 8) };
                _chkStartMenu.FlatAppearance.CheckedBackColor = Color.MediumSeaGreen; _chkStartMenu.FlatAppearance.BorderSize = 0;

                bodyLayout.Controls.Add(lblPath, 0, 0); bodyLayout.Controls.Add(pathRow, 0, 1); bodyLayout.Controls.Add(_chkDesktop, 0, 2); bodyLayout.Controls.Add(_chkStartMenu, 0, 3);
                bodyCard.Controls.Add(bodyLayout);
            }

            mainLayout.Controls.Add(bodyCard, 0, 1);

            Panel progressContainer = new Panel { Dock = DockStyle.Fill };
            _lblStatus = new Label { Dock = DockStyle.Top, Height = ResponsiveEngine.S(this, 22), ForeColor = Color.LightGray, Font = new Font("Segoe UI", 10f, FontStyle.Italic), TextAlign = ContentAlignment.MiddleLeft };
            _progressBar = new ProgressBar { Dock = DockStyle.Bottom, Height = ResponsiveEngine.S(this, 16), Style = ProgressBarStyle.Continuous, Visible = false };
            progressContainer.Controls.Add(_lblStatus); progressContainer.Controls.Add(_progressBar);
            mainLayout.Controls.Add(progressContainer, 0, 2);

            TableLayoutPanel footerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
            footerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ResponsiveEngine.S(this, 180)));
            footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ResponsiveEngine.S(this, 230)));

            _btnCancel = new RoundedButton { Dock = DockStyle.Fill, Text = "Cancel", BackColor = Color.Gray, ForeColor = Color.White, Margin = new Padding(8, 14, 8, 14), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 12f, FontStyle.Bold), BorderRadius = ResponsiveEngine.S(this, 10) };
            _btnCancel.FlatAppearance.BorderSize = 0; _btnCancel.Click += (s, e) => this.Close();

            _btnAction = new RoundedButton { Dock = DockStyle.Fill, Text = _isUninstallMode ? "Uninstall" : "Install", BackColor = _isUninstallMode ? Color.IndianRed : Color.MediumSeaGreen, ForeColor = Color.White, Margin = new Padding(8, 14, 0, 14), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 12.5f, FontStyle.Bold), BorderRadius = ResponsiveEngine.S(this, 10) };
            _btnAction.FlatAppearance.BorderSize = 0; _btnAction.Click += async (s, e) => await ExecuteActionAsync();

            footerLayout.Controls.Add(new Panel(), 0, 0); footerLayout.Controls.Add(_btnCancel, 1, 0); footerLayout.Controls.Add(_btnAction, 2, 0);
            mainLayout.Controls.Add(footerLayout, 0, 3);

            _currentContainer.Controls.Add(mainLayout);
            this.Controls.Add(_currentContainer);
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Select the installation folder" })
            {
                if (fbd.ShowDialog() == DialogResult.OK) _txtPath.Text = Path.Combine(fbd.SelectedPath, "Music Power 3");
            }
        }

        private async Task ExecuteActionAsync()
        {
            if (_isUninstallMode)
            {
                _btnAction.Enabled = false; _btnCancel.Enabled = false; _progressBar.Visible = true;
                await PerformUninstallAsync();
            }
            else
            {
                _installDir = _txtPath.Text.Trim();
                
                bool isUpdate = Directory.Exists(_installDir) && File.Exists(Path.Combine(_installDir, "MusicPower3.exe"));

                if (isUpdate)
                {
                    string warnTitle = "Update Software";
                    string warnMsg = "A previous version was detected in this path. The software will be updated.\n\nDo you want to continue?";

                    DialogResult res = MessageBox.Show(warnMsg, warnTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (res == DialogResult.No) return; 
                }

                _btnAction.Enabled = false; _btnCancel.Enabled = false; _progressBar.Visible = true;
                await PerformInstallAsync(isUpdate);
            }
        }

        private async Task PerformInstallAsync(bool isUpdate)
        {
            try
            {
                _lblStatus.Text = "Extracting files...";
                _progressBar.Value = 25;

                await Task.Run(() =>
                {
                    if (!Directory.Exists(_installDir)) Directory.CreateDirectory(_installDir);

                    using (Stream resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
                    {
                        if (resStream == null) throw new Exception("Payload not found inside the executable.");
                        
                        using (ZipArchive archive = new ZipArchive(resStream, ZipArchiveMode.Read))
                        {
                            foreach (ZipArchiveEntry entry in archive.Entries)
                            {
                                if (string.IsNullOrEmpty(entry.Name)) continue;
                                string destinationPath = Path.GetFullPath(Path.Combine(_installDir, entry.FullName));

                                string destinationDir = Path.GetDirectoryName(destinationPath);
                                if (!Directory.Exists(destinationDir)) Directory.CreateDirectory(destinationDir);

                                entry.ExtractToFile(destinationPath, overwrite: true);
                            }
                        }
                    }
                });

                _progressBar.Value = 65;
                _lblStatus.Text = "Creating shortcuts...";

                string mainExe = Path.Combine(_installDir, "MusicPower3.exe");
                string uninstallerDest = Path.Combine(_installDir, "Uninstall.exe");
                File.Copy(Application.ExecutablePath, uninstallerDest, true);

                await Task.Run(() =>
                {
                    string deskDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    string startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Music Power 3");

                    if (_chkDesktop.Checked && File.Exists(mainExe))
                    {
                        CreateShortcut(mainExe, Path.Combine(deskDir, "Music Power 3.lnk"), "Music Power 3 Audio Player");
                    }
                    if (_chkStartMenu.Checked)
                    {
                        if (File.Exists(mainExe)) CreateShortcut(mainExe, Path.Combine(startDir, "Music Power 3.lnk"), "Music Power 3 Audio Player");
                        CreateShortcut(uninstallerDest, Path.Combine(startDir, "Uninstall Music Power 3.lnk"), "Uninstall Music Power 3");
                    }
                    RegisterUninstallerInRegistry(_installDir, uninstallerDest, mainExe);
                });

                _progressBar.Value = 100;
                _lblStatus.Text = "Installation completed successfully!";
                
                string successMsg = isUpdate 
                    ? "The software has been successfully updated!" 
                    : "The application has been successfully installed on your computer!";

                MessageBox.Show(successMsg, "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                this.Close();
            }
            catch (Exception ex) 
            { 
                MessageBox.Show($"Installation error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); 
                _btnAction.Enabled = true; 
                _btnCancel.Enabled = true; 
                _progressBar.Visible = false; 
            }
        }

        private async Task PerformUninstallAsync()
        {
            try
            {
                _lblStatus.Text = "Removing shortcuts...";
                _progressBar.Value = 30;

                await Task.Run(() =>
                {
                    string deskDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    string startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Music Power 3");
                    File.Delete(Path.Combine(deskDir, "Music Power 3.lnk"));
                    if (Directory.Exists(startDir)) Directory.Delete(startDir, true);
                    if (OperatingSystem.IsWindows()) Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Music Power 3", false);
                });

                _progressBar.Value = 100;
                MessageBox.Show("Uninstallation complete. The folder will now be permanently removed from your system.", "Uninstalled", MessageBoxButtons.OK, MessageBoxIcon.Information);

                ProcessStartInfo cmd = new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{_installDir}\"") { CreateNoWindow = true, UseShellExecute = false };
                Process.Start(cmd); Application.Exit();
            }
            catch (Exception ex) { MessageBox.Show($"Error during uninstallation: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void CreateShortcut(string targetExe, string shortcutPath, string desc)
        {
            try
            {
                string dir = Path.GetDirectoryName(shortcutPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // FIX 3: Fully bypassed PowerShell. This uses native Windows COM libraries to build the shortcut.
                // It is instantaneous, cannot fail due to Quotes/Spaces, and ignores local script execution restrictions.
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(t);
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetExe;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetExe);
                shortcut.Description = desc;
                shortcut.Save();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Shortcut Error: {ex.Message}");
            }
        }

        private void RegisterUninstallerInRegistry(string installDir, string uninstallerPath, string mainExe)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Music Power 3"))
                {
                    if (key != null)
                    {
                        key.SetValue("DisplayName", "Music Power 3");
                        key.SetValue("UninstallString", $"\"{uninstallerPath}\" -uninstall");
                        key.SetValue("DisplayIcon", $"\"{mainExe}\"");
                        key.SetValue("Publisher", "Elhoussain");
                        key.SetValue("DisplayVersion", "2.2.0.0");
                        key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
                    }
                }
            }
            catch { }
        }
    }

    public static class ResponsiveEngine { public static int S(Control c, float v) { return (int)(v * (c.DeviceDpi / 96f)); } }

    public class RoundedPanel : Panel
    {
        public int BorderRadius { get; set; } = 20;
        public RoundedPanel() { this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); this.UpdateStyles(); }
        private GraphicsPath GetPath(Rectangle rect, int radius) { GraphicsPath path = new GraphicsPath(); int d = radius * 2; path.AddArc(rect.X, rect.Y, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90); path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path; }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; Color pCol = Parent != null ? Parent.BackColor : Color.FromArgb(40, 40, 40); using (SolidBrush pB = new SolidBrush(pCol)) { e.Graphics.FillRectangle(pB, ClientRectangle); } Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1); using (GraphicsPath p = GetPath(r, BorderRadius)) { using (SolidBrush b = new SolidBrush(BackColor)) { e.Graphics.FillPath(b, p); } } }
    }

    public class RoundedButton : Button
    {
        public int BorderRadius { get; set; } = 12;
        public RoundedButton() { this.FlatStyle = FlatStyle.Flat; this.FlatAppearance.BorderSize = 0; this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); this.UpdateStyles(); }
        private GraphicsPath GetPath(Rectangle rect, int radius) { GraphicsPath path = new GraphicsPath(); int d = radius * 2; path.AddArc(rect.X, rect.Y, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90); path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path; }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; Color pCol = Parent != null ? Parent.BackColor : Color.FromArgb(40, 40, 40); using (SolidBrush pB = new SolidBrush(pCol)) { e.Graphics.FillRectangle(pB, ClientRectangle); } Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1); using (GraphicsPath p = GetPath(r, BorderRadius)) { using (SolidBrush b = new SolidBrush(BackColor)) { e.Graphics.FillPath(b, p); } } TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak; TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, flags); }
    }

    public class RoundedCheckBox : CheckBox
    {
        public int BorderRadius { get; set; } = 10;
        public RoundedCheckBox() { this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); this.UpdateStyles(); }
        private GraphicsPath GetPath(Rectangle rect, int radius) { GraphicsPath path = new GraphicsPath(); int d = radius * 2; path.AddArc(rect.X, rect.Y, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90); path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path; }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; Color pCol = Parent != null ? Parent.BackColor : Color.FromArgb(40, 40, 40); using (SolidBrush pB = new SolidBrush(pCol)) { e.Graphics.FillRectangle(pB, ClientRectangle); } Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1); using (GraphicsPath p = GetPath(r, BorderRadius)) { Color fill = Checked ? Color.MediumSeaGreen : BackColor; using (SolidBrush b = new SolidBrush(fill)) { e.Graphics.FillPath(b, p); } } TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter; TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, flags); }
    }
}