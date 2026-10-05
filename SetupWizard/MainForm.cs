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

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        private static readonly string[] AudioExtensions = new[] { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".wma" };

        private static bool IsSafeDirectoryToDelete(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            foreach (char c in "%^&|<>\r\n\"")
            {
                if (dir.IndexOf(c) >= 0) return false;
            }
            try
            {
                string full = Path.GetFullPath(dir).TrimEnd('\\', '/');
                string root = Path.GetPathRoot(full);
                if (!string.IsNullOrEmpty(root) && string.Equals(full, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;

                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\', '/');
                if (string.Equals(full, win, StringComparison.OrdinalIgnoreCase)) return false;
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System).TrimEnd('\\', '/');
                if (string.Equals(full, sys, StringComparison.OrdinalIgnoreCase)) return false;
                string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
                if (string.Equals(full, user, StringComparison.OrdinalIgnoreCase)) return false;
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory).TrimEnd('\\', '/');
                if (string.Equals(full, desk, StringComparison.OrdinalIgnoreCase)) return false;

                if (!File.Exists(Path.Combine(full, "MusicPower3.exe"))) return false;
                return true;
            }
            catch { return false; }
        }

        private async Task PerformInstallAsync(bool isUpdate)
        {
            try
            {
                _lblStatus.Text = "Extracting files...";
                _progressBar.Value = 25;

                var extractedFiles = new System.Collections.Generic.List<string>();
                await Task.Run(() =>
                {
                    string targetRoot = Path.GetFullPath(_installDir);
                    if (!targetRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
                        targetRoot += Path.DirectorySeparatorChar;

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
                                if (!destinationPath.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new InvalidOperationException($"Zip entry attempted directory traversal: {entry.FullName}");
                                }

                                string destinationDir = Path.GetDirectoryName(destinationPath);
                                if (!Directory.Exists(destinationDir)) Directory.CreateDirectory(destinationDir);

                                entry.ExtractToFile(destinationPath, overwrite: true);
                                extractedFiles.Add(entry.FullName);
                            }
                        }
                    }

                    extractedFiles.Add("MusicPower3.exe");
                    extractedFiles.Add("Uninstall.exe");
                    extractedFiles.Add("install.manifest");
                    File.WriteAllLines(Path.Combine(_installDir, "install.manifest"), extractedFiles);
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
                    RegisterFileAssociations(mainExe, _installDir);
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
                if (!IsSafeDirectoryToDelete(_installDir))
                {
                    throw new InvalidOperationException("The directory cannot be safely uninstalled or does not appear to contain a valid Music Power 3 installation.");
                }

                _lblStatus.Text = "Removing shortcuts and file associations...";
                _progressBar.Value = 30;

                await Task.Run(() =>
                {
                    string deskDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    string startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Music Power 3");
                    try { File.Delete(Path.Combine(deskDir, "Music Power 3.lnk")); } catch { }
                    if (Directory.Exists(startDir)) { try { Directory.Delete(startDir, true); } catch { } }

                    if (OperatingSystem.IsWindows())
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Music Power 3", false);
                        UnregisterFileAssociations();
                    }

                    string manifestPath = Path.Combine(_installDir, "install.manifest");
                    if (File.Exists(manifestPath))
                    {
                        try
                        {
                            var lines = File.ReadAllLines(manifestPath);
                            foreach (var line in lines)
                            {
                                string trimmed = line.Trim();
                                if (string.IsNullOrEmpty(trimmed) || trimmed.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase)) continue;
                                string target = Path.Combine(_installDir, trimmed);
                                if (File.Exists(target))
                                {
                                    try { File.Delete(target); } catch { }
                                }
                            }
                        }
                        catch { }
                    }
                });

                _progressBar.Value = 100;
                MessageBox.Show("Uninstallation complete. The folder will now be cleaned up.", "Uninstalled", MessageBoxButtons.OK, MessageBoxIcon.Information);

                string safeDir = Path.GetFullPath(_installDir).TrimEnd('\\', '/');
                ProcessStartInfo cmd = new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{safeDir}\"") { CreateNoWindow = true, UseShellExecute = false };
                Process.Start(cmd); Application.Exit();
            }
            catch (Exception ex) { MessageBox.Show($"Error during uninstallation: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private static void RegisterFileAssociations(string mainExe, string installDir)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                string progId = "MusicPower3.AudioFile";
                using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
                {
                    if (progKey != null)
                    {
                        progKey.SetValue("", "Audio File");
                        progKey.SetValue("AppUserModelID", "Music Power 3");
                        using var iconKey = progKey.CreateSubKey("DefaultIcon");
                        iconKey?.SetValue("", $"\"{mainExe}\",0");
                        using var cmdKey = progKey.CreateSubKey(@"shell\open\command");
                        cmdKey?.SetValue("", $"\"{mainExe}\" \"%1\"");
                    }
                }

                foreach (var ext in AudioExtensions)
                {
                    using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
                    openWith?.SetValue(progId, string.Empty);
                }

                using (var cap = Registry.CurrentUser.CreateSubKey(@"Software\MusicPower3\Capabilities"))
                {
                    if (cap != null)
                    {
                        cap.SetValue("ApplicationDescription", "Music Power 3 Audio Player");
                        cap.SetValue("ApplicationName", "Music Power 3");
                        using var fileAssoc = cap.CreateSubKey("FileAssociations");
                        if (fileAssoc != null)
                        {
                            foreach (var ext in AudioExtensions)
                            {
                                fileAssoc.SetValue(ext, progId);
                            }
                        }
                    }
                }

                using (var regApp = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                {
                    regApp?.SetValue("MusicPower3", @"Software\MusicPower3\Capabilities");
                }

                string iconPath = Path.Combine(installDir, "Assets", "icon.ico");
                if (!File.Exists(iconPath)) iconPath = mainExe;

                // Applications\MusicPower3.exe for Windows 11 "Open with" context menu
                using (var appKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\MusicPower3.exe"))
                {
                    if (appKey != null)
                    {
                        appKey.SetValue("FriendlyAppName", "Music Power 3");
                        appKey.SetValue("ApplicationCompany", "Elhoussain");
                        using (var defIcon = appKey.CreateSubKey("DefaultIcon"))
                        {
                            defIcon?.SetValue("", $"\"{iconPath}\",0");
                        }
                        using (var cmd = appKey.CreateSubKey(@"shell\open\command"))
                        {
                            cmd?.SetValue("", $"\"{mainExe}\" \"%1\"");
                        }
                        using (var supported = appKey.CreateSubKey("SupportedTypes"))
                        {
                            if (supported != null)
                            {
                                foreach (var ext in AudioExtensions)
                                {
                                    supported.SetValue(ext, string.Empty);
                                }
                            }
                        }
                    }
                }

                using (var aumidKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\Music Power 3"))
                {
                    if (aumidKey != null)
                    {
                        aumidKey.SetValue("DisplayName", "Music Power 3");
                        aumidKey.SetValue("IconUri", iconPath);
                    }
                }

                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        private static void UnregisterFileAssociations()
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                string progId = "MusicPower3.AudioFile";
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\MusicPower3.exe", false);

                foreach (var ext in AudioExtensions)
                {
                    using var openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", true);
                    openWith?.DeleteValue(progId, false);
                }

                Registry.CurrentUser.DeleteSubKeyTree(@"Software\MusicPower3", false);

                using (var regApp = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
                {
                    regApp?.DeleteValue("MusicPower3", false);
                }

                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\Music Power 3", false);

                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        private void CreateShortcut(string targetExe, string shortcutPath, string desc)
        {
            try
            {
                string dir = Path.GetDirectoryName(shortcutPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var link = (IShellLinkW)new ShellLinkClass();
                link.SetPath(targetExe);
                link.SetWorkingDirectory(Path.GetDirectoryName(targetExe));
                link.SetDescription(desc);

                string iconPath = Path.Combine(Path.GetDirectoryName(targetExe) ?? string.Empty, "Assets", "icon.ico");
                if (File.Exists(iconPath)) link.SetIconLocation(iconPath, 0);

                var store = (IPropertyStore)link;
                var pkey = new PropertyKey(new Guid("9F4C2855-9F79-48D7-9E68-7D960F80227C"), 5);
                using (var pv = PropVariant.FromString("Music Power 3"))
                {
                    store.SetValue(ref pkey, pv);
                    store.Commit();
                }

                var persist = (System.Runtime.InteropServices.ComTypes.IPersistFile)link;
                persist.Save(shortcutPath, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Shortcut Error: {ex.Message}");
            }
        }

        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkClass { }

        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown), System.Runtime.InteropServices.Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
            void SetDescription([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFile);
        }

        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown), System.Runtime.InteropServices.Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        public interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PropertyKey pkey);
            int GetValue(ref PropertyKey key, [System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] PropVariant pv);
            int SetValue(ref PropertyKey key, [System.Runtime.InteropServices.In] PropVariant pv);
            int Commit();
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
        public struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;

            public PropertyKey(Guid guid, uint id)
            {
                fmtid = guid;
                pid = id;
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        public class PropVariant : IDisposable
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public ushort vt;
            [System.Runtime.InteropServices.FieldOffset(8)] public IntPtr pwszVal;

            public static PropVariant FromString(string val)
            {
                return new PropVariant
                {
                    vt = 31, // VT_LPWSTR
                    pwszVal = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(val)
                };
            }

            public void Dispose()
            {
                if (pwszVal != IntPtr.Zero)
                {
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pwszVal);
                    pwszVal = IntPtr.Zero;
                }
                GC.SuppressFinalize(this);
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
                        key.SetValue("DisplayVersion", "2.3.0.0");
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