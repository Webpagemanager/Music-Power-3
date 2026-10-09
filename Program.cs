using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPower3
{
    /// <summary>The audio formats the app plays, scans for, and registers file associations for.</summary>
    public static class AudioFormats
    {
        public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".wma"
        };

        public static bool IsSupported(string? path) =>
            !string.IsNullOrWhiteSpace(path) && Extensions.Contains(Path.GetExtension(path));

        /// <summary>Finds the first token of a raw command line that is an existing, supported audio file.</summary>
        public static string? FindAudioFile(IEnumerable<string> tokens)
        {
            foreach (var token in tokens)
            {
                try
                {
                    if (IsSupported(token) && File.Exists(token)) return Path.GetFullPath(token);
                }
                catch { }
            }
            return null;
        }

        public static List<string> SplitCommandLine(string commandLine)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            foreach (char c in commandLine)
            {
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }
    }

    public static class Program
    {
        // Identity Windows uses for the taskbar group, volume/media flyout and lock screen controls.
        public const string AppUserModelId = "MusicPower3";
        private const string InstanceKey = "MusicPower3.Main";

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr hEvent);

        [DllImport("ole32.dll")]
        private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, ulong nHandles, IntPtr[] pHandles, out uint dwIndex);

        [STAThread]
        static int Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }

            // A second launch (for example "Open with" on another file) is forwarded to the running
            // window instead of starting a second player.
            if (RedirectToExistingInstance()) return 0;

            RegisterShellIntegration();
            App.LaunchFile = AudioFormats.FindAudioFile(args);

            Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            return 0;
        }

        private static bool RedirectToExistingInstance()
        {
            try
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                var instance = AppInstance.FindOrRegisterForKey(InstanceKey);

                if (instance.IsCurrent)
                {
                    instance.Activated += OnActivated;
                    return false;
                }

                // The redirect call must not block the STA thread directly, so wait in a COM-aware way.
                IntPtr done = CreateEvent(IntPtr.Zero, true, false, null);
                Task.Run(() =>
                {
                    try { instance.RedirectActivationToAsync(activation).AsTask().Wait(); }
                    finally { SetEvent(done); }
                });
                CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, new[] { done }, out _);
                return true;
            }
            catch
            {
                // If redirection is unavailable for any reason, just run as a normal instance.
                return false;
            }
        }

        private static void OnActivated(object? sender, AppActivationArguments args)
        {
            string? path = null;
            try
            {
                if (args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
                {
                    path = AudioFormats.FindAudioFile(AudioFormats.SplitCommandLine(launch.Arguments ?? string.Empty));
                }
                else if (args.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs)
                {
                    foreach (var item in fileArgs.Files)
                    {
                        path = AudioFormats.FindAudioFile(new[] { item.Path });
                        if (path != null) break;
                    }
                }
            }
            catch { }

            MainWindow.MainDispatcher?.TryEnqueue(() => App.RaiseFileActivated(path));
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore ppv);

        public static void SetWindowAppUserModelId(IntPtr hwnd)
        {
            try
            {
                Guid riid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                if (SHGetPropertyStoreForWindow(hwnd, ref riid, out var store) == 0 && store != null)
                {
                    var pkey = new PropertyKey(new Guid("9F4C2855-9F79-48D7-9E68-7D960F80227C"), 5);
                    using var pv = PropVariant.FromString(AppUserModelId);
                    store.SetValue(ref pkey, pv);
                    store.Commit();
                }
            }
            catch { }
        }

        /// <summary>
        /// Registers shell associations, "Open with" context menu support, and creates/updates
        /// the Start Menu shortcut with PKEY_AppUserModel_ID so Windows 11 recognizes the app name and icon.
        /// </summary>
        private static void RegisterShellIntegration()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;

                string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
                if (!File.Exists(icon)) icon = exe;

                // 1. Toast / Action Center identity
                string aumidKeyPath = @"Software\Classes\AppUserModelId\" + AppUserModelId;
                using (var aumidKey = Registry.CurrentUser.CreateSubKey(aumidKeyPath))
                {
                    if (aumidKey != null)
                    {
                        aumidKey.SetValue("DisplayName", "Music Power 3");
                        aumidKey.SetValue("IconUri", icon);
                    }
                }

                // 2. Windows Explorer "Open with" application registration
                string appKeyPath = @"Software\Classes\Applications\MusicPower3.exe";
                using (var appKey = Registry.CurrentUser.CreateSubKey(appKeyPath))
                {
                    if (appKey != null)
                    {
                        appKey.SetValue("FriendlyAppName", "Music Power 3");
                        appKey.SetValue("ApplicationCompany", "Elhoussain");
                        using (var defIcon = appKey.CreateSubKey("DefaultIcon"))
                        {
                            defIcon?.SetValue("", $"\"{icon}\",0");
                        }
                        using (var cmd = appKey.CreateSubKey(@"shell\open\command"))
                        {
                            cmd?.SetValue("", $"\"{exe}\" \"%1\"");
                        }
                        using (var supported = appKey.CreateSubKey("SupportedTypes"))
                        {
                            if (supported != null)
                            {
                                foreach (var ext in AudioFormats.Extensions)
                                {
                                    supported.SetValue(ext, string.Empty);
                                }
                            }
                        }
                    }
                }

                // 3. ProgID registration
                string progId = "MusicPower3.AudioFile";
                using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
                {
                    if (progKey != null)
                    {
                        progKey.SetValue("", "Audio File");
                        progKey.SetValue("AppUserModelID", AppUserModelId);
                        using var iconKey = progKey.CreateSubKey("DefaultIcon");
                        iconKey?.SetValue("", $"\"{icon}\",0");
                        using var cmdKey = progKey.CreateSubKey(@"shell\open\command");
                        cmdKey?.SetValue("", $"\"{exe}\" \"%1\"");
                    }
                }

                // 4. Register with OpenWithProgids for each supported extension
                foreach (var ext in AudioFormats.Extensions)
                {
                    using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
                    openWith?.SetValue(progId, string.Empty);
                }

                // 5. Windows Capabilities & RegisteredApplications
                using (var cap = Registry.CurrentUser.CreateSubKey(@"Software\MusicPower3\Capabilities"))
                {
                    if (cap != null)
                    {
                        cap.SetValue("ApplicationDescription", "Music Power 3 Audio Player");
                        cap.SetValue("ApplicationName", "Music Power 3");
                        using var fileAssoc = cap.CreateSubKey("FileAssociations");
                        if (fileAssoc != null)
                        {
                            foreach (var ext in AudioFormats.Extensions)
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

                // 6. Ensure Start Menu shortcut has PKEY_AppUserModel_ID set so Windows 11 SMTC recognizes the app
                string startMenuPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Music Power 3.lnk");
                CreateOrUpdateShortcut(startMenuPath, exe, AppContext.BaseDirectory, icon, "Music Power 3 Audio Player", AppUserModelId);

                // 7. Flush Explorer shell icon and association cache
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        public static void CreateOrUpdateShortcut(string shortcutPath, string targetExe, string? workingDir, string? iconPath, string? description, string aumid)
        {
            try
            {
                var link = (IShellLinkW)new ShellLinkClass();
                link.SetPath(targetExe);
                if (!string.IsNullOrEmpty(workingDir)) link.SetWorkingDirectory(workingDir);
                if (!string.IsNullOrEmpty(iconPath)) link.SetIconLocation(iconPath, 0);
                if (!string.IsNullOrEmpty(description)) link.SetDescription(description);

                var store = (IPropertyStore)link;
                var pkey = new PropertyKey(new Guid("9F4C2855-9F79-48D7-9E68-7D960F80227C"), 5);
                using (var pv = PropVariant.FromString(aumid))
                {
                    store.SetValue(ref pkey, pv);
                    store.Commit();
                }

                string? dir = Path.GetDirectoryName(shortcutPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var persist = (IPersistFile)link;
                persist.Save(shortcutPath, true);
            }
            catch { }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkClass { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        public interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PropertyKey pkey);
            int GetValue(ref PropertyKey key, [In, Out] PropVariant pv);
            int SetValue(ref PropertyKey key, [In] PropVariant pv);
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
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

        [StructLayout(LayoutKind.Explicit)]
        public class PropVariant : IDisposable
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pwszVal;

            public static PropVariant FromString(string val)
            {
                return new PropVariant
                {
                    vt = 31, // VT_LPWSTR
                    pwszVal = Marshal.StringToCoTaskMemUni(val)
                };
            }

            public void Dispose()
            {
                if (pwszVal != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pwszVal);
                    pwszVal = IntPtr.Zero;
                }
                GC.SuppressFinalize(this);
            }
        }
    }
}