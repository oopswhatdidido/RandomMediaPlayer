using MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Ookii.Dialogs.Wpf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vlc.DotNet.Wpf;
using WpfScreenHelper;

namespace RandomMediaPlayer
{
    public enum MediaMode { Photo, Video, Mixed }
    public enum MediaKind { Image, Gif, Video }
    public enum PanicAction { Stop, Exit }

    public partial class MainWindow : Window
    {
        // ---------- Extension sets ----------
        // Strictly static photo formats. Animated formats (.gif, .jfif, .gifv,
        // .apng) are NOT here - they only enter the active enumeration when the
        // "Include animated" checkbox is on.
        private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".jif",
            ".png",
            ".bmp", ".dib",
            ".tif", ".tiff",
            ".webp",
            ".heic", ".heif",
            ".ico",
            ".wdp", ".jxr"
        };

        // Image-style animated formats. Toggled in/out of enumeration by the
        // "Include animated" checkbox. In Photo mode they render as a static
        // first frame; in Video/Mixed they animate via MediaElement.
        private static readonly HashSet<string> AnimatedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".gif", ".gifv", ".jfif", ".apng"
        };

        // Used by the playback layer to decide whether to route a file to
        // MediaElement vs VLC vs Image. Same set as AnimatedExtensions.
        private static readonly HashSet<string> GifExtensions = AnimatedExtensions;

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".3g2", ".3gp", ".3gp2", ".3gpp",
            ".amv", ".avi", ".divx", ".dv",
            ".f4v", ".flv", ".m2v", ".m4v",
            ".mkv", ".mov", ".mp4", ".mpeg",
            ".mpg", ".mts", ".m2ts", ".mxf",
            ".nsv", ".ogm", ".ogv", ".rm",
            ".rmvb", ".roq", ".svi", ".ts",
            ".vob", ".wmv", ".webm"
        };

        // ---------- State ----------
        private MediaMode _mode = MediaMode.Photo;
        private string _orientationFilter = "All";

        private readonly List<string> _selectedFolders = new();
        private readonly ObservableCollection<string> _mediaFiles = new();

        private DispatcherTimer _slideshowTimer = null!;
        private readonly Random _random = new();
        private Screen[] _monitors = Array.Empty<Screen>();

        // Per-monitor configuration + runtime state. UI-bound, persisted.
        private List<MonitorContext> _monitorContexts = new();

        // Mode: false = sync (one pick shown on every selected monitor),
        // true = independent (each selected monitor picks its own using its
        // OrientationFilter).
        private bool _independentMonitors;

        private string? _currentMediaPath;
        private MediaKind _currentMediaKind = MediaKind.Image;
        private string? _nextMediaPath;
        private BitmapImage? _nextImageBuffer;
        private bool _isPreloading;
        private bool _isAdvancing;
        private bool _isSlideshowRunning;
        private bool _isPreviewEnlarged;
        private bool _includeAnimated;
        private int _minWidth = 500;
        private int _minHeight = 500;

        private bool _vlcInitialized;

        // ---------- Win32 sleep prevention ----------
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        [Flags]
        public enum EXECUTION_STATE : uint
        {
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_SYSTEM_REQUIRED = 0x00000001
        }

        // ---------- Win32 global hotkey ----------
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ---------- Win32 low-level keyboard hook (for multi-instance mode) ----------
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int vKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN     = 0x0100;
        private const int WM_SYSKEYDOWN  = 0x0104;
        private const int VK_SHIFT   = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU    = 0x12; // Alt

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        // Snapshots read from the hook callback (background system thread).
        // Bool reads/writes are atomic in .NET so no lock is needed; we just
        // need the snapshots to stay coherent with the latest UI selection.
        private bool _panicCtrlSnap, _panicAltSnap;
        private bool _holdCtrlSnap,  _holdAltSnap;

        private const int PANIC_HOTKEY_ID = 0x9F37;
        private const int HOLD_HOTKEY_ID  = 0x9F38;
        private const int WM_HOTKEY = 0x0312;

        // RegisterHotKey fsModifiers flags
        private const uint MOD_ALT     = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT   = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;

        private HwndSource? _hwndSource;
        private Key _panicKey = Key.F11;
        private PanicAction _panicAction = PanicAction.Stop;
        private bool _panicHotkeyRegistered;

        private Key _holdKey = Key.F9;
        private bool _holdHotkeyRegistered;
        private bool _isOnHold;

        // When true, we observe keys via WH_KEYBOARD_LL (works across multiple
        // app instances). When false, we claim them via RegisterHotKey
        // (single-instance, but consumed exclusively from the focused app).
        private bool _useLowLevelHook;
        private LowLevelKeyboardProc? _llHookProc;
        private IntPtr _llHookHandle = IntPtr.Zero;

        // Loaded-on-startup settings; null until LoadSettings runs.
        private AppSettings? _settings;
        // Set true once the constructor's UI population has finished, so
        // SelectionChanged events don't fire RefreshMediaListAsync etc. during
        // load.
        private bool _uiInitialized;

        // Navigation history of recently-shown items so left-arrow can step
        // back. Capped to MaxHistory.
        private readonly List<string> _history = new();
        private const int MaxHistory = 5;

        // Cancels the in-flight enumeration when the user fires another one
        // (e.g., switching modes mid-scan). Without this, two enumerations
        // race on the same progress bar and produce visible flicker.
        private System.Threading.CancellationTokenSource? _enumerationCts;

        public MainWindow()
        {
            InitializeComponent();

            _slideshowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _slideshowTimer.Tick += SlideshowTimer_Tick;

            InitializeVlc();
            LoadMonitors();
            PopulatePanicControls();
            PopulateHoldControls();

            // Load persisted settings BEFORE applying UI mode so all controls
            // reflect saved values. _uiInitialized stays false during this so
            // none of the SelectionChanged / Checked event handlers fire side
            // effects (RefreshMediaListAsync, hotkey re-register, etc.).
            _settings = AppSettings.Load();
            ApplyLoadedSettings(_settings);
            _uiInitialized = true;

            ApplyModeUi();
            UpdateHoldStatus();

            // If folder persistence was on and folders were saved, kick off
            // an initial enumeration so the saved list is usable immediately.
            if (_settings.PersistFolders && _selectedFolders.Count > 0)
                _ = RefreshMediaListAsync();

            this.Closing += MainWindow_Closing;
        }

        // ---------- Panic key ----------
        // Keys offered as panic-trigger candidates. Function keys + Pause/Esc
        // because they're rarely typed accidentally.
        private static readonly Key[] PanicKeyChoices =
        {
            Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6,
            Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12,
            Key.Pause, Key.Escape
        };

        private void PopulatePanicControls()
        {
            PanicKeyComboBox.ItemsSource = PanicKeyChoices.Select(k => k.ToString()).ToList();
            PanicKeyComboBox.SelectedItem = _panicKey.ToString();

            PanicActionComboBox.ItemsSource = new[]
            {
                "Stop slideshow",
                "Exit application"
            };
            PanicActionComboBox.SelectedIndex = (int)_panicAction;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            _hwndSource = HwndSource.FromHwnd(helper.Handle);
            _hwndSource?.AddHook(WndProcHook);
            UpdateModifierSnapshots();
            InstallHotkeys();
        }

        // Single switch point: either install LL hook (multi-instance mode) or
        // RegisterHotKey for both panic and hold (single-instance mode).
        private void InstallHotkeys()
        {
            UninstallAllHotkeys();

            if (_useLowLevelHook)
            {
                InstallLowLevelHook();
                // With LL hook there's no claim contention - mark both as Active.
                SetPanicStatus(_llHookHandle != IntPtr.Zero);
                SetHoldKeyStatus(_llHookHandle != IntPtr.Zero);
            }
            else
            {
                RegisterPanicHotKey();
                RegisterHoldHotKey();
            }
        }

        private void UninstallAllHotkeys()
        {
            UninstallLowLevelHook();
            if (_hwndSource != null)
            {
                if (_panicHotkeyRegistered)
                {
                    UnregisterHotKey(_hwndSource.Handle, PANIC_HOTKEY_ID);
                    _panicHotkeyRegistered = false;
                }
                if (_holdHotkeyRegistered)
                {
                    UnregisterHotKey(_hwndSource.Handle, HOLD_HOTKEY_ID);
                    _holdHotkeyRegistered = false;
                }
            }
        }

        private void InstallLowLevelHook()
        {
            if (_llHookHandle != IntPtr.Zero) return;

            _llHookProc = LowLevelHookCallback;
            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                var moduleName = process.MainModule?.ModuleName;
                _llHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _llHookProc,
                                                  GetModuleHandle(moduleName), 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LLHook] Install threw: {ex.Message}");
            }

            if (_llHookHandle == IntPtr.Zero)
                Debug.WriteLine("[LLHook] SetWindowsHookEx returned null.");
            else
                Debug.WriteLine("[LLHook] Installed successfully (multi-instance mode).");
        }

        private void UninstallLowLevelHook()
        {
            if (_llHookHandle == IntPtr.Zero) return;
            UnhookWindowsHookEx(_llHookHandle);
            _llHookHandle = IntPtr.Zero;
            _llHookProc = null;
            Debug.WriteLine("[LLHook] Uninstalled.");
        }

        // Runs on a system-managed input thread. Must be fast: read state,
        // marshal back to UI, return.
        private IntPtr LowLevelHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    var s = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    var key = KeyInterop.KeyFromVirtualKey((int)s.vkCode);

                    bool ctrl  = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                    bool alt   = (GetKeyState(VK_MENU)    & 0x8000) != 0;
                    bool shift = (GetKeyState(VK_SHIFT)   & 0x8000) != 0;

                    // Exact-match modifier semantics like RegisterHotKey - any
                    // unspecified modifier must NOT be pressed.
                    if (key == _panicKey &&
                        ctrl == _panicCtrlSnap && alt == _panicAltSnap && !shift)
                    {
                        Dispatcher.BeginInvoke(OnPanicTriggered);
                    }
                    if (key == _holdKey &&
                        ctrl == _holdCtrlSnap && alt == _holdAltSnap && !shift)
                    {
                        Dispatcher.BeginInvoke(ToggleHold);
                    }
                }
            }
            // Pass through. Returning non-zero would cancel propagation but
            // would also stop *other* LL hooks in the chain - including hooks
            // installed by sibling app instances - which defeats multi-instance.
            return CallNextHookEx(_llHookHandle, nCode, wParam, lParam);
        }

        // Called whenever any panic/hold modifier checkbox or key combo changes
        // so the LL hook callback sees the up-to-date target combos.
        private void UpdateModifierSnapshots()
        {
            _panicCtrlSnap = PanicCtrlCheckBox?.IsChecked == true;
            _panicAltSnap  = PanicAltCheckBox?.IsChecked  == true;
            _holdCtrlSnap  = HoldCtrlCheckBox?.IsChecked  == true;
            _holdAltSnap   = HoldAltCheckBox?.IsChecked   == true;
        }

        private void MultiInstanceCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiInitialized) return;
            _useLowLevelHook = MultiInstanceCheckBox.IsChecked == true;
            UpdateModifierSnapshots();
            InstallHotkeys();
        }

        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == PANIC_HOTKEY_ID)
                {
                    Dispatcher.BeginInvoke(OnPanicTriggered);
                    handled = true;
                }
                else if (id == HOLD_HOTKEY_ID)
                {
                    Dispatcher.BeginInvoke(ToggleHold);
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void OnPanicTriggered()
        {
            switch (_panicAction)
            {
                case PanicAction.Stop:
                    if (_isSlideshowRunning) StopSlideshow();
                    break;
                case PanicAction.Exit:
                    // Close handler does the slideshow stop + sleep release for us.
                    Application.Current.Shutdown();
                    break;
            }
        }

        private uint GetPanicModifiers()
        {
            uint mods = MOD_NOREPEAT;
            if (PanicCtrlCheckBox?.IsChecked == true) mods |= MOD_CONTROL;
            if (PanicAltCheckBox?.IsChecked == true) mods |= MOD_ALT;
            return mods;
        }

        private string DescribePanicCombo()
        {
            var parts = new List<string>();
            if (PanicCtrlCheckBox?.IsChecked == true) parts.Add("Ctrl");
            if (PanicAltCheckBox?.IsChecked == true) parts.Add("Alt");
            parts.Add(_panicKey.ToString());
            return string.Join("+", parts);
        }

        private void RegisterPanicHotKey()
        {
            if (_hwndSource == null) return;
            var hwnd = _hwndSource.Handle;

            if (_panicHotkeyRegistered)
            {
                UnregisterHotKey(hwnd, PANIC_HOTKEY_ID);
                _panicHotkeyRegistered = false;
            }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(_panicKey);
            uint mods = GetPanicModifiers();

            if (RegisterHotKey(hwnd, PANIC_HOTKEY_ID, mods, vk))
            {
                _panicHotkeyRegistered = true;
                SetPanicStatus(true);
                Debug.WriteLine($"[Panic] Registered hotkey: {DescribePanicCombo()}");
            }
            else
            {
                SetPanicStatus(false);
                Debug.WriteLine($"[Panic] Failed to register {DescribePanicCombo()} - claimed by another app.");
            }
        }

        // ---------- Hold key ----------
        private static readonly Key[] HoldKeyChoices =
        {
            Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6,
            Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12,
            Key.Pause, Key.Scroll
        };

        private void PopulateHoldControls()
        {
            HoldKeyComboBox.ItemsSource = HoldKeyChoices.Select(k => k.ToString()).ToList();
            HoldKeyComboBox.SelectedItem = _holdKey.ToString();
        }

        private uint GetHoldModifiers()
        {
            uint mods = MOD_NOREPEAT;
            if (HoldCtrlCheckBox?.IsChecked == true) mods |= MOD_CONTROL;
            if (HoldAltCheckBox?.IsChecked == true) mods |= MOD_ALT;
            return mods;
        }

        private string DescribeHoldCombo()
        {
            var parts = new List<string>();
            if (HoldCtrlCheckBox?.IsChecked == true) parts.Add("Ctrl");
            if (HoldAltCheckBox?.IsChecked == true) parts.Add("Alt");
            parts.Add(_holdKey.ToString());
            return string.Join("+", parts);
        }

        private void RegisterHoldHotKey()
        {
            if (_hwndSource == null) return;
            var hwnd = _hwndSource.Handle;

            if (_holdHotkeyRegistered)
            {
                UnregisterHotKey(hwnd, HOLD_HOTKEY_ID);
                _holdHotkeyRegistered = false;
            }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(_holdKey);
            uint mods = GetHoldModifiers();

            if (RegisterHotKey(hwnd, HOLD_HOTKEY_ID, mods, vk))
            {
                _holdHotkeyRegistered = true;
                SetHoldKeyStatus(true);
                Debug.WriteLine($"[Hold] Registered hotkey: {DescribeHoldCombo()}");
            }
            else
            {
                SetHoldKeyStatus(false);
                Debug.WriteLine($"[Hold] Failed to register {DescribeHoldCombo()} - claimed by another app.");
            }
        }

        private void SetHoldKeyStatus(bool registered)
        {
            if (HoldStatusBox == null) return;
            if (registered)
            {
                HoldStatusBox.Text = "  ✓ Active";
                HoldStatusBox.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0xCB, 0x77));
            }
            else
            {
                HoldStatusBox.Text = "  ⚠ Unavailable - try Ctrl/Alt or another key";
                HoldStatusBox.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));
            }
        }

        private void HoldSettingChanged(object sender, RoutedEventArgs e)
        {
            if (!_uiInitialized) return;

            bool needsReregister = false;

            if (HoldKeyComboBox?.SelectedItem is string keyName &&
                Enum.TryParse<Key>(keyName, out var newKey))
            {
                if (newKey != _holdKey)
                {
                    _holdKey = newKey;
                    needsReregister = true;
                }
            }
            if (sender == HoldCtrlCheckBox || sender == HoldAltCheckBox)
                needsReregister = true;

            UpdateModifierSnapshots();

            if (needsReregister)
            {
                if (_useLowLevelHook)
                {
                    SetHoldKeyStatus(_llHookHandle != IntPtr.Zero);
                }
                else
                {
                    RegisterHoldHotKey();
                }
            }
        }

        // Toggle the held state. While held: timer is stopped, video/gif loop
        // on their EndReached. Press again to release.
        private void ToggleHold()
        {
            _isOnHold = !_isOnHold;
            UpdateHoldStatus();

            if (_isOnHold)
            {
                _slideshowTimer.Stop();
            }
            else if (_isSlideshowRunning)
            {
                // Re-arm the timer so the next advance happens after the user-
                // configured delay rather than immediately.
                _slideshowTimer.Start();
            }
        }

        private void UpdateHoldStatus()
        {
            if (HoldStatusText == null) return;
            HoldStatusText.Text = _isOnHold ? "⏸ HELD" : "";
        }

        private void SetPanicStatus(bool registered)
        {
            if (PanicStatusText == null) return;
            if (registered)
            {
                PanicStatusText.Text = "  ✓ Active";
                PanicStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0xCB, 0x77));
            }
            else
            {
                PanicStatusText.Text = "  ⚠ Unavailable - try Ctrl/Alt or another key";
                PanicStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));
            }
        }

        private void PanicSettingChanged(object sender, RoutedEventArgs e)
        {
            if (!_uiInitialized) return;

            bool needsReregister = false;

            if (PanicKeyComboBox?.SelectedItem is string keyName &&
                Enum.TryParse<Key>(keyName, out var newKey))
            {
                if (newKey != _panicKey)
                {
                    _panicKey = newKey;
                    needsReregister = true;
                }
            }
            if (PanicActionComboBox?.SelectedIndex >= 0)
            {
                _panicAction = (PanicAction)PanicActionComboBox.SelectedIndex;
            }

            if (sender == PanicCtrlCheckBox || sender == PanicAltCheckBox)
                needsReregister = true;

            UpdateModifierSnapshots();

            if (needsReregister)
            {
                if (_useLowLevelHook)
                {
                    // No re-registration needed - LL hook reads the snapshots
                    // every keystroke. Just refresh the status indicator.
                    SetPanicStatus(_llHookHandle != IntPtr.Zero);
                }
                else
                {
                    RegisterPanicHotKey();
                }
            }
        }

        // ---------- VLC bootstrap ----------
        private void InitializeVlc()
        {
            var vlcLibDirectory = FindVlcLibDirectory();
            if (vlcLibDirectory == null)
            {
                Debug.WriteLine("[VLC] libvlc not found - video playback will be unavailable.");
                return;
            }

            var options = new[]
            {
                "--file-caching=1000",
                "--network-caching=1000",
                "--avcodec-hw=any"
            };

            try
            {
                VlcCanvas.SourceProvider.CreatePlayer(vlcLibDirectory, options);
                VlcCanvas.SourceProvider.MediaPlayer.EndReached +=
                    (s, e) => Dispatcher.BeginInvoke(() =>
                    {
                        // Stale event guard: only honor EndReached when this kind
                        // is currently displayed.
                        if (_currentMediaKind != MediaKind.Video) return;

                        // Held: replay the current clip on its source instead of
                        // advancing to the next file.
                        if (_isOnHold)
                        {
                            try
                            {
                                var mp = VlcCanvas.SourceProvider.MediaPlayer;
                                if (mp != null && _currentMediaPath != null)
                                {
                                    mp.Play(new Uri(_currentMediaPath));
                                }
                            }
                            catch (Exception ex) { Debug.WriteLine($"[Hold] VLC loop failed: {ex.Message}"); }
                            return;
                        }

                        if (_isSlideshowRunning) AdvanceToNextMedia();
                    });
                _vlcInitialized = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VLC] Init failed: {ex.Message}");
            }
        }

        internal static DirectoryInfo? FindVlcLibDirectory()
        {
            string arch = IntPtr.Size == 4 ? "win-x86" : "win-x64";

            // 1. App-relative candidates (used when libvlc was copied next to the exe).
            var appBases = new List<string?>
            {
                new FileInfo(Assembly.GetEntryAssembly()!.Location).DirectoryName,
                AppContext.BaseDirectory,
                Directory.GetCurrentDirectory(),
            };

            foreach (var baseDir in appBases)
            {
                if (baseDir == null) continue;

                var c1 = new DirectoryInfo(Path.Combine(baseDir, "libvlc", arch));
                if (c1.Exists && File.Exists(Path.Combine(c1.FullName, "libvlc.dll"))) return c1;

                var c2 = new DirectoryInfo(Path.Combine(baseDir, arch));
                if (c2.Exists && File.Exists(Path.Combine(c2.FullName, "libvlc.dll"))) return c2;
            }

            // 2. Fall back to the system VLC installation. The Vlc.DotNet.Wpf NuGet
            //    package does not ship native binaries on .NET 9, so most users
            //    will rely on a separately installed VLC media player here.
            //    Prefer the architecture matching the current process: a 64-bit
            //    process needs the 64-bit VLC at "Program Files\VideoLAN\VLC",
            //    a 32-bit process needs the 32-bit VLC at "Program Files (x86)".
            var systemCandidates = IntPtr.Size == 8
                ? new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),     // x64 native
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),  // last-resort fallback
                }
                : new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),  // x86 native
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                };

            foreach (var pf in systemCandidates)
            {
                if (string.IsNullOrEmpty(pf)) continue;
                var vlcDir = new DirectoryInfo(Path.Combine(pf, "VideoLAN", "VLC"));
                if (vlcDir.Exists && File.Exists(Path.Combine(vlcDir.FullName, "libvlc.dll")))
                    return vlcDir;
            }

            return null;
        }

        // ---------- Sleep prevention ----------
        private void PreventSleep() =>
            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS |
                                    EXECUTION_STATE.ES_SYSTEM_REQUIRED |
                                    EXECUTION_STATE.ES_DISPLAY_REQUIRED);

        private void AllowSleep() =>
            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);

        // ---------- Mode + UI wiring ----------
        // The mode picker is a header-only TabControl (Photos / Videos / Mixed).
        // SelectionChanged fires once during XAML init when index 0 latches in,
        // hence the _uiInitialized gate before kicking off any side effects.
        private void OnModeTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModeTabControl == null) return;

            _mode = ModeTabControl.SelectedIndex switch
            {
                1 => MediaMode.Video,
                2 => MediaMode.Mixed,
                _ => MediaMode.Photo,
            };

            ApplyModeUi();

            // Skip the auto-refresh during initial settings load - the
            // constructor explicitly kicks off enumeration once after
            // _uiInitialized is set.
            if (!_uiInitialized) return;

            if (_selectedFolders.Count > 0)
                _ = RefreshMediaListAsync();
        }

        // Pure UI update - no value mutation, so loading settings doesn't get
        // its DelayTextBox value overwritten. Mute / min-size live in the
        // Filters tab and are always visible (their relevance changes with
        // mode but the controls themselves don't move).
        private void ApplyModeUi()
        {
            // DelayLabel can be null briefly during XAML init; guard for that.
            if (DelayLabel == null) return;

            switch (_mode)
            {
                case MediaMode.Photo:
                    Title = "Random Media Player - Photos";
                    DelayLabel.Text = "Display time (s):";
                    break;

                case MediaMode.Video:
                    Title = "Random Media Player - Videos";
                    DelayLabel.Text = "Clip length (s):";
                    break;

                case MediaMode.Mixed:
                    Title = "Random Media Player - Mixed";
                    DelayLabel.Text = "Default time (s):";
                    break;
            }

            UpdateStartButtonText();
        }

        private void OnOrientationChanged(object sender, RoutedEventArgs e)
        {
            if (LandscapeRadio?.IsChecked == true) _orientationFilter = "Landscape";
            else if (VerticalRadio?.IsChecked == true) _orientationFilter = "Vertical";
            else _orientationFilter = "All";
        }

        private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            this.Topmost = AlwaysOnTopCheckBox.IsChecked == true;
            // Mirror to every open monitor window so they all stay on top together.
            foreach (var ctx in _monitorContexts.Where(c => c.Window != null))
                ctx.Window!.Topmost = this.Topmost;
        }

        // Push the new overlay state into every currently-open monitor window
        // immediately, so the toggle takes visible effect without waiting for
        // the next advance.
        private void ShowPathOverlayCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiInitialized) return;
            bool show = ShowPathOverlayCheckBox.IsChecked == true;
            foreach (var ctx in _monitorContexts.Where(c => c.Window != null))
                ctx.Window!.SetPathOverlay(ctx.CurrentPath ?? _currentMediaPath, show);
        }

        // Drives Application.ThemeMode at runtime. .NET 9 propagates the
        // change to every open Window, so the main window and any open
        // fullscreen windows re-theme immediately.
        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiInitialized) return;
            ApplyThemeFromCombo();
        }

        private void ApplyThemeFromCombo()
        {
            string label = (ThemeComboBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Dark";
            Application.Current.ThemeMode = label switch
            {
                "Light"  => ThemeMode.Light,
                "System" => ThemeMode.System,
                _        => ThemeMode.Dark,
            };
        }

        private void IncludeAnimatedCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            _includeAnimated = IncludeAnimatedCheckBox.IsChecked == true;
            if (!_uiInitialized) return;
            if (_selectedFolders.Count > 0)
                _ = RefreshMediaListAsync();
        }

        private void MinSizeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (MinWidthTextBox == null || MinHeightTextBox == null) return;

            if (int.TryParse(MinWidthTextBox.Text, out var w) && w >= 0) _minWidth = w;
            if (int.TryParse(MinHeightTextBox.Text, out var h) && h >= 0) _minHeight = h;
        }

        private void DelayTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_slideshowTimer == null) return;

            if (double.TryParse(DelayTextBox.Text, out var seconds) && seconds > 0)
                _slideshowTimer.Interval = TimeSpan.FromSeconds(seconds);
        }

        // ---------- Monitors ----------
        private void LoadMonitors()
        {
            _monitors = Screen.AllScreens.ToArray();
            _monitorContexts = _monitors
                .Select((s, i) => new MonitorContext(i, s))
                .ToList();
            MonitorsItemsControl.ItemsSource = _monitorContexts;
        }

        // Open / close the fullscreen window per monitor based on its Selected
        // flag. Returns the list of newly-opened contexts so the caller can
        // immediately push current media into them (avoids a blank window
        // until the next advance).
        private List<MonitorContext> SyncMonitorWindows()
        {
            var newlyOpened = new List<MonitorContext>();
            foreach (var ctx in _monitorContexts)
            {
                if (ctx.Selected && ctx.Window == null)
                {
                    var mon = ctx.Screen;
                    var win = new FullscreenSlideshowWindow(this)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = mon.WpfWorkingArea.Left,
                        Top = mon.WpfWorkingArea.Top,
                        Width = mon.WpfWorkingArea.Width,
                        Height = mon.WpfWorkingArea.Height,
                        Topmost = AlwaysOnTopCheckBox.IsChecked == true
                    };
                    var capturedCtx = ctx;
                    win.Closed += (s, e) => { capturedCtx.Window = null; };
                    win.Show();
                    win.WindowState = WindowState.Maximized;
                    ctx.Window = win;
                    newlyOpened.Add(ctx);
                }
                else if (!ctx.Selected && ctx.Window != null)
                {
                    ctx.Window.Close();
                    ctx.Window = null;
                    // Drop runtime state so a re-tick on this context is fresh.
                    ctx.CurrentPath = null;
                    ctx.NextPath = null;
                    ctx.NextImage = null;
                    ctx.History.Clear();
                }
            }
            return newlyOpened;
        }

        private void CloseAllMonitorWindows()
        {
            foreach (var ctx in _monitorContexts)
            {
                if (ctx.Window != null)
                {
                    try { ctx.Window.Close(); } catch { }
                    ctx.Window = null;
                }
                ctx.CurrentPath = null;
                ctx.NextPath = null;
                ctx.NextImage = null;
                ctx.IsAdvancing = false;
                ctx.IsPreloading = false;
                ctx.History.Clear();
            }
        }

        private bool AnyMonitorSelected() => _monitorContexts.Any(c => c.Selected);

        private void OnMonitorModeChanged(object sender, RoutedEventArgs e)
        {
            if (!_uiInitialized) return;
            _independentMonitors = IndependentMonitorsRadio.IsChecked == true;
        }

        private void MonitorSelectedChanged(object sender, RoutedEventArgs e)
        {
            if (!_uiInitialized) return;
            // Two-way binding has already updated MonitorContext.Selected.
            // Don't open / close fullscreen windows during configuration -
            // they'd cover the control window before the user is ready.
            // StartSlideshow() opens all selected windows when the user
            // actually starts the show; while running, toggling a monitor
            // here opens / closes its window immediately so the change is
            // live.
            if (!_isSlideshowRunning) return;

            var newlyOpened = SyncMonitorWindows();
            foreach (var ctx in newlyOpened)
                PushCurrentToContext(ctx);
        }

        // When a monitor is checked mid-show, immediately push the current
        // global media into it (sync mode) or kick off its own preload+advance
        // (independent mode).
        private async void PushCurrentToContext(MonitorContext ctx)
        {
            try
            {
                if (_independentMonitors)
                {
                    // Independent: this context picks its own.
                    await AdvanceContextAsync(ctx);
                }
                else if (!string.IsNullOrEmpty(_currentMediaPath) && ctx.Window != null)
                {
                    // Sync: push the current global pick.
                    await ShowOnContextAsync(ctx, _currentMediaPath!, _nextImageBuffer, _currentMediaKind);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Monitor] PushCurrentToContext failed: {ex.Message}");
            }
        }

        // ---------- Folder management ----------
        private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VistaFolderBrowserDialog
            {
                Description = "Select one or more folders to add to the media sources",
                UseDescriptionForTitle = true,
                Multiselect = true
            };

            if (dialog.ShowDialog(this) != true) return;

            int added = 0;
            var dupes = new List<string>();
            foreach (var folder in dialog.SelectedPaths)
            {
                if (!_selectedFolders.Contains(folder))
                {
                    _selectedFolders.Add(folder);
                    added++;
                }
                else
                {
                    dupes.Add(Path.GetFileName(folder));
                }
            }

            if (added > 0)
            {
                UpdateFolderDisplay();
                _ = RefreshMediaListAsync();
            }

            if (dupes.Count > 0 && added == 0)
                MessageBox.Show($"All selected folders are already in the list: {string.Join(", ", dupes)}");
        }

        private void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedFoldersListBox.SelectedItem is not string folder)
            {
                MessageBox.Show("Please select a folder to remove from the list.");
                return;
            }

            _selectedFolders.Remove(folder);
            UpdateFolderDisplay();

            if (_selectedFolders.Count > 0)
            {
                _ = RefreshMediaListAsync();
            }
            else
            {
                _mediaFiles.Clear();
                FileEnumerationProgressBar.Value = 0;
                ProgressLabel.Content = "No folders selected";
            }
        }

        private void ClearFoldersButton_Click(object sender, RoutedEventArgs e)
        {
            _selectedFolders.Clear();
            _mediaFiles.Clear();
            UpdateFolderDisplay();
            FileEnumerationProgressBar.Value = 0;
            ProgressLabel.Content = "No folders selected";
        }

        private void UpdateFolderDisplay()
        {
            SelectedFoldersListBox.ItemsSource = null;
            SelectedFoldersListBox.ItemsSource = _selectedFolders;
            FolderCountLabel.Content = $"Folders: {_selectedFolders.Count}";
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            _ = RefreshMediaListAsync();

        // ---------- File enumeration ----------
        private string[] GetActiveExtensions()
        {
            var list = new List<string>();
            switch (_mode)
            {
                case MediaMode.Photo:
                    list.AddRange(PhotoExtensions);
                    if (_includeAnimated) list.AddRange(GifExtensions);
                    break;
                case MediaMode.Video:
                    list.AddRange(VideoExtensions);
                    if (_includeAnimated) list.AddRange(GifExtensions);
                    break;
                case MediaMode.Mixed:
                    list.AddRange(PhotoExtensions);
                    list.AddRange(VideoExtensions);
                    if (_includeAnimated) list.AddRange(AnimatedExtensions);
                    break;
            }
            return list.Select(s => s.ToLowerInvariant()).Distinct().ToArray();
        }

        private async Task RefreshMediaListAsync()
        {
            // Cancel any enumeration that's still in flight (e.g., user just
            // switched modes mid-scan) so we don't race two scans on the same
            // progress bar.
            _enumerationCts?.Cancel();
            _enumerationCts = new System.Threading.CancellationTokenSource();
            var ct = _enumerationCts.Token;

            RefreshButton.IsEnabled = false;
            StartShowButton.IsEnabled = false;
            FileEnumerationProgressBar.Value = 0;
            ProgressLabel.Content = "Starting enumeration...";

            if (_selectedFolders.Count == 0 || !_selectedFolders.Any(Directory.Exists))
            {
                MessageBox.Show("Please select at least one valid folder.");
                RefreshButton.IsEnabled = true;
                StartShowButton.IsEnabled = true;
                return;
            }

            var extensions = GetActiveExtensions();
            var progress = new Progress<(int percentage, int matched, int scanned)>(p =>
            {
                // Drop progress reports from a cancelled run - stops the bar
                // flicker when a newer scan has already started.
                if (ct.IsCancellationRequested) return;
                FileEnumerationProgressBar.Value = p.percentage;
                ProgressLabel.Content = $"Matched {p.matched} of {p.scanned} files ({p.percentage}%)";
            });

            var collected = new List<string>();
            try
            {
                _mediaFiles.Clear();
                _nextImageBuffer = null;
                _nextMediaPath = null;

                await Task.Run(() => EnumerateMultipleFolders(_selectedFolders, extensions, collected, progress, ct), ct);

                // Don't commit results from a cancelled scan - the new scan
                // will overwrite anyway.
                if (ct.IsCancellationRequested) return;

                // Bulk-append on the UI thread once enumeration is done.
                foreach (var f in collected)
                    _mediaFiles.Add(f);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation; stay quiet.
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Enumeration error: {ex.Message}");
            }
            finally
            {
                // Only update final UI state if we weren't cancelled by a newer
                // scan - otherwise the newer scan owns the UI now.
                if (!ct.IsCancellationRequested)
                {
                    RefreshButton.IsEnabled = true;
                    StartShowButton.IsEnabled = true;
                    ProgressLabel.Content = _totalScannedFiles > 0
                        ? $"Done. Found {_mediaFiles.Count} of {_totalScannedFiles} files scanned."
                        : $"Done. Found {_mediaFiles.Count} items.";
                    FileEnumerationProgressBar.Value = 100;

                    if (_mediaFiles.Count == 0)
                        MessageBox.Show("No media found in the selected folders.");
                    else
                        await PreloadNextAsync();
                }
            }
        }

        // Tracks the total number of files we walked past (matched or not) so
        // we can show "Found X of Y scanned" to the user.
        private int _totalScannedFiles;

        private void EnumerateMultipleFolders(List<string> folders, string[] extensions,
                                              List<string> output,
                                              IProgress<(int, int, int)> progress,
                                              System.Threading.CancellationToken ct)
        {
            var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
            int folderCount = folders.Count(Directory.Exists);
            int processedFolders = 0;
            _totalScannedFiles = 0;

            foreach (var folder in folders.Where(Directory.Exists))
            {
                if (ct.IsCancellationRequested) return;

                EnumerateOneFolder(folder, extSet, output, processedFolders, folderCount, progress, ct);
                processedFolders++;
                int pct = folderCount > 0 ? (int)((double)processedFolders / folderCount * 100) : 100;
                progress.Report((Math.Min(pct, 99), output.Count, _totalScannedFiles));
            }
            if (ct.IsCancellationRequested) return;
            progress.Report((100, output.Count, _totalScannedFiles));
            Debug.WriteLine($"[Enumerate] Done. Matched {output.Count} of {_totalScannedFiles} files scanned across {folderCount} root folder(s). Active extensions: {string.Join(", ", extensions)}");
        }

        private void EnumerateOneFolder(string path, HashSet<string> extSet, List<string> output,
                                        int foldersDone, int totalFolders,
                                        IProgress<(int, int, int)> progress,
                                        System.Threading.CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            // Eagerly grab the file list with its own guard so a single bad file
            // doesn't kill enumeration of its siblings or its sister directories.
            string[] files;
            try
            {
                files = Directory.GetFiles(path);
            }
            catch (UnauthorizedAccessException) { files = Array.Empty<string>(); }
            catch (DirectoryNotFoundException) { files = Array.Empty<string>(); }
            catch (IOException ex) { Debug.WriteLine($"GetFiles failed in {path}: {ex.Message}"); files = Array.Empty<string>(); }
            catch (Exception ex) { Debug.WriteLine($"GetFiles unexpected error in {path}: {ex.Message}"); files = Array.Empty<string>(); }

            int matchedHere = 0;
            foreach (var file in files)
            {
                // Bail out fast if the user kicked off a different scan.
                if (ct.IsCancellationRequested) return;

                _totalScannedFiles++;
                try
                {
                    if (extSet.Contains(Path.GetExtension(file)))
                    {
                        output.Add(file);
                        matchedHere++;
                        if (output.Count % 50 == 0)
                        {
                            int pct = totalFolders > 0
                                ? (int)((double)(foldersDone * 100 + 50) / totalFolders) : 50;
                            progress.Report((Math.Min(pct, 99), output.Count, _totalScannedFiles));
                        }
                    }
                }
                catch { /* skip this single file */ }
            }
            if (files.Length > 0)
                Debug.WriteLine($"[Enumerate] {path}: {matchedHere}/{files.Length} matched");

            // Subdirectories - separate guard so failed file enumeration doesn't
            // prevent us from descending.
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(path);
            }
            catch (UnauthorizedAccessException) { dirs = Array.Empty<string>(); }
            catch (DirectoryNotFoundException) { dirs = Array.Empty<string>(); }
            catch (IOException ex) { Debug.WriteLine($"GetDirectories failed in {path}: {ex.Message}"); dirs = Array.Empty<string>(); }
            catch (Exception ex) { Debug.WriteLine($"GetDirectories unexpected error in {path}: {ex.Message}"); dirs = Array.Empty<string>(); }

            foreach (var dir in dirs)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    EnumerateOneFolder(dir, extSet, output, foldersDone, totalFolders, progress, ct);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Recurse into {dir} failed: {ex.Message}");
                }
            }
        }

        // ---------- Media kind classification ----------
        // Mode-aware: in Photo mode an animated GIF/JFIF is treated as a static
        // image (first frame) so it obeys the slideshow delay timer. In Video /
        // Mixed mode it routes to MediaElement and animates.
        private MediaKind ClassifyMedia(string path)
        {
            var ext = Path.GetExtension(path);
            if (VideoExtensions.Contains(ext)) return MediaKind.Video;
            if (AnimatedExtensions.Contains(ext))
                return _mode == MediaMode.Photo ? MediaKind.Image : MediaKind.Gif;
            return MediaKind.Image;
        }

        // ---------- Slideshow control ----------
        private void StartStopSlideshowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSlideshowRunning) StopSlideshow();
            else StartSlideshow();
        }

        private void StartSlideshow()
        {
            // Idempotent: a second call while running is a no-op rather than
            // resetting state (avoids the "out of sync" double-toggle bug).
            if (_isSlideshowRunning) return;

            if (_selectedFolders.Count == 0 || !_selectedFolders.Any(Directory.Exists))
            {
                MessageBox.Show("Please select at least one valid folder.");
                return;
            }
            if (_mediaFiles.Count == 0)
            {
                MessageBox.Show("Media list is empty. Try refreshing.");
                return;
            }

            _isSlideshowRunning = true;
            // Clear any leftover hold state from a prior run.
            _isOnHold = false;
            UpdateHoldStatus();
            UpdateStartButtonText();
            PreventSleep();
            PreviewPlaceholder.Visibility = Visibility.Collapsed;

            // Open the fullscreen window for every currently-selected monitor.
            // (We deliberately do NOT do this when the user merely checks the
            // monitor box - only here, so the control window stays usable
            // during configuration.)
            SyncMonitorWindows();

            // Trigger first advance immediately
            AdvanceToNextMedia();
            _slideshowTimer.Start();
        }

        private void StopSlideshow()
        {
            // Idempotent.
            if (!_isSlideshowRunning) return;

            _isSlideshowRunning = false;
            _isOnHold = false;
            UpdateHoldStatus();
            UpdateStartButtonText();
            AllowSleep();
            _slideshowTimer.Stop();

            try
            {
                if (_vlcInitialized && VlcCanvas.SourceProvider.MediaPlayer != null)
                    VlcCanvas.SourceProvider.MediaPlayer.Pause();
            }
            catch { }

            try { GifPlayer.Stop(); } catch { }

            // Close every monitor's fullscreen window and clear runtime state.
            CloseAllMonitorWindows();
        }

        private void UpdateStartButtonText()
        {
            string label = _mode switch
            {
                MediaMode.Photo => _isSlideshowRunning ? "Stop Slideshow" : "Start Slideshow",
                MediaMode.Video => _isSlideshowRunning ? "Stop Videoshow" : "Start Videoshow",
                _ => _isSlideshowRunning ? "Stop Show" : "Start Show"
            };
            StartShowButton.Content = label;
        }

        private void SlideshowTimer_Tick(object? sender, EventArgs e) => AdvanceToNextMedia();

        // Timer-driven advance. Honors hold (no-op) and the in-flight guard.
        // Branches on monitor mode: sync uses one global pick everywhere,
        // independent advances each selected monitor with its own pick.
        private async void AdvanceToNextMedia()
        {
            if (_isAdvancing || !_isSlideshowRunning || _isOnHold) return;

            _isAdvancing = true;
            _slideshowTimer.Stop();

            try
            {
                bool useIndependent = _independentMonitors && AnyMonitorSelected();

                if (useIndependent)
                {
                    // Advance each selected monitor independently.
                    var selected = _monitorContexts.Where(c => c.Selected && c.Window != null).ToList();
                    var tasks = selected.Select(AdvanceContextAsync).ToArray();
                    await Task.WhenAll(tasks);

                    // Mirror the first selected monitor's media in the main
                    // preview pane so the user can see something.
                    var first = selected.FirstOrDefault();
                    if (first?.CurrentPath != null)
                    {
                        await UpdateMainPreviewFromContextAsync(first);
                        UpdateFilePathDisplay(first.CurrentPath);
                        _currentMediaPath = first.CurrentPath;
                        _currentMediaKind = first.CurrentKind;
                    }
                }
                else
                {
                    // Sync mode (or no monitors selected): one global pick.
                    if (string.IsNullOrEmpty(_nextMediaPath)) await PreloadNextAsync();
                    if (string.IsNullOrEmpty(_nextMediaPath)) return;

                    if (_currentMediaPath != null) PushHistory(_currentMediaPath);

                    var path = _nextMediaPath!;
                    await ShowMediaAsync(path, _nextImageBuffer);

                    await PreloadNextAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AdvanceToNextMedia error: {ex.Message}");
            }
            finally
            {
                _isAdvancing = false;
                // Honor hold: don't restart the timer if user is holding.
                if (_isSlideshowRunning && !_isOnHold) _slideshowTimer.Start();
            }
        }

        // Independent-mode advance for one monitor context. Used by both the
        // timer tick (each selected monitor advances simultaneously) and the
        // right-arrow / NextRandomAsync flow.
        private async Task AdvanceContextAsync(MonitorContext ctx)
        {
            if (ctx.IsAdvancing || ctx.Window == null) return;
            ctx.IsAdvancing = true;
            try
            {
                if (string.IsNullOrEmpty(ctx.NextPath))
                    await PreloadForContextAsync(ctx);
                if (string.IsNullOrEmpty(ctx.NextPath)) return;

                // Push the OLD current onto this context's own history before
                // ShowOnContextAsync overwrites CurrentPath. Per-context history
                // means left-arrow steps each monitor back through its own
                // sequence, not a single global one.
                if (ctx.CurrentPath != null) PushContextHistory(ctx, ctx.CurrentPath);

                var path = ctx.NextPath!;
                var kind = ClassifyMedia(path);

                await ShowOnContextAsync(ctx, path, ctx.NextImage, kind);

                // Preload the next candidate for this context so timer-driven
                // advances stay smooth.
                await PreloadForContextAsync(ctx);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{ctx.Screen.DeviceName}] AdvanceContextAsync failed: {ex.Message}");
            }
            finally
            {
                ctx.IsAdvancing = false;
            }
        }

        private void PushContextHistory(MonitorContext ctx, string path)
        {
            if (ctx.History.Count > 0 && ctx.History[^1] == path) return;
            ctx.History.Add(path);
            while (ctx.History.Count > MaxHistory) ctx.History.RemoveAt(0);
        }

        // In independent mode the main preview pane mirrors whatever the first
        // selected monitor is showing so the user has a visual reference.
        private async Task UpdateMainPreviewFromContextAsync(MonitorContext ctx)
        {
            if (ctx.CurrentPath == null) return;
            string path = ctx.CurrentPath;
            MediaKind kind = ctx.CurrentKind;

            await Dispatcher.InvokeAsync(() =>
            {
                StopPreviewOfOtherKinds(kind);
                bool scaleToFill = ScaleToFillCheckBox.IsChecked == true;

                if (kind == MediaKind.Image)
                {
                    var bmp = ctx.NextImage; // may be null if already advanced
                    if (bmp == null)
                    {
                        var loaded = TryLoadBitmap(path);
                        if (loaded == null) return;
                        bmp = ApplyRotationIfNeeded(loaded, path);
                    }
                    ShowOnly(PreviewImage);
                    PreviewImage.Stretch = scaleToFill ? Stretch.UniformToFill : Stretch.Uniform;
                    PreviewImage.Source = bmp;
                }
                else if (kind == MediaKind.Gif)
                {
                    ShowOnly(GifPlayer);
                    GifPlayer.Stretch = scaleToFill ? Stretch.UniformToFill : Stretch.Uniform;
                    GifPlayer.Source = new Uri(path, UriKind.Absolute);
                    GifPlayer.Play();
                }
                else // Video
                {
                    if (!_vlcInitialized) return;
                    ShowOnly(VlcCanvas);
                    var mp = VlcCanvas.SourceProvider.MediaPlayer;
                    mp.Audio.Volume = MuteCheckBox.IsChecked == true ? 0 : 100;
                    mp.Play(new Uri(path));
                }
            });
        }

        // Like StopPlaybackOfOtherKinds but only touches the main window's
        // own preview elements (not monitor windows).
        private void StopPreviewOfOtherKinds(MediaKind incoming)
        {
            if (incoming != MediaKind.Video)
            {
                try
                {
                    if (_vlcInitialized && VlcCanvas.SourceProvider.MediaPlayer != null)
                        VlcCanvas.SourceProvider.MediaPlayer.Pause();
                }
                catch { }
            }
            if (incoming != MediaKind.Gif)
            {
                try { GifPlayer.Stop(); GifPlayer.Source = null; } catch { }
            }
        }

        // Core media-display routine. Handles the kind-switch + playback.
        // Used by AdvanceToNextMedia, NextRandomAsync, PreviousAsync, delete flow.
        private async Task ShowMediaAsync(string path, BitmapImage? bufferedImage)
        {
            var kind = ClassifyMedia(path);

            // Stop other kinds BEFORE updating _currentMediaKind so stale
            // EndReached events get gated by the now-changing kind.
            StopPlaybackOfOtherKinds(kind);
            _currentMediaKind = kind;

            if (kind == MediaKind.Image)
                await DisplayImageAsync(path, bufferedImage);
            else
                await PlayVideoOrGifAsync(path, kind);

            _currentMediaPath = path;
            UpdateFilePathDisplay(path);
        }

        // History stack for left-arrow back navigation. Capped at MaxHistory so
        // we don't grow without bound.
        private void PushHistory(string path)
        {
            // Avoid duplicating the very last entry (e.g., looping a single file).
            if (_history.Count > 0 && _history[^1] == path) return;
            _history.Add(path);
            while (_history.Count > MaxHistory) _history.RemoveAt(0);
        }

        // User-initiated forward (right-arrow). Branches on monitor mode:
        // sync = one global pick shown everywhere; independent = each selected
        // monitor advances to its own next random pick.
        public async Task NextRandomAsync()
        {
            if (_isAdvancing) return;
            if (_mediaFiles.Count == 0) return;

            bool useIndependent = _independentMonitors && AnyMonitorSelected();

            _isAdvancing = true;
            _slideshowTimer.Stop();
            try
            {
                if (useIndependent)
                {
                    var selected = _monitorContexts
                        .Where(c => c.Selected && c.Window != null)
                        .ToList();
                    if (selected.Count == 0) return;

                    // Each monitor advances in parallel using its own preload
                    // pipeline. AdvanceContextAsync handles the per-context
                    // history push internally, so we don't double-push here.
                    var tasks = selected.Select(AdvanceContextAsync).ToArray();
                    await Task.WhenAll(tasks);

                    // Mirror the first selected monitor's content into the
                    // main preview pane so the user has a visual reference.
                    var first = selected.FirstOrDefault();
                    if (first?.CurrentPath != null)
                    {
                        await UpdateMainPreviewFromContextAsync(first);
                        _currentMediaPath = first.CurrentPath;
                        _currentMediaKind = first.CurrentKind;
                        UpdateFilePathDisplay(first.CurrentPath);
                    }
                }
                else
                {
                    // Sync mode: one global pick shown on main + every selected
                    // monitor (or just main if none are selected).
                    if (_currentMediaPath != null) PushHistory(_currentMediaPath);

                    if (string.IsNullOrEmpty(_nextMediaPath))
                        await PreloadNextAsync();
                    if (string.IsNullOrEmpty(_nextMediaPath)) return;

                    await ShowMediaAsync(_nextMediaPath, _nextImageBuffer);

                    await PreloadNextAsync();
                }
            }
            finally
            {
                _isAdvancing = false;
                if (_isSlideshowRunning && !_isOnHold) _slideshowTimer.Start();
            }
        }

        // Step back. Sync mode pops the global history; independent mode pops
        // each selected monitor's own history list.
        public async Task PreviousAsync()
        {
            if (_isAdvancing) return;

            bool useIndependent = _independentMonitors && AnyMonitorSelected();

            _isAdvancing = true;
            _slideshowTimer.Stop();
            try
            {
                if (useIndependent)
                {
                    // Only step back monitors that have history to give.
                    var withHistory = _monitorContexts
                        .Where(c => c.Selected && c.Window != null && c.History.Count > 0)
                        .ToList();
                    if (withHistory.Count == 0) return;

                    var tasks = withHistory.Select(async ctx =>
                    {
                        var prev = ctx.History[^1];
                        ctx.History.RemoveAt(ctx.History.Count - 1);
                        var kind = ClassifyMedia(prev);
                        // ShowOnContextAsync re-loads images from disk; history
                        // items don't carry a buffered BitmapImage.
                        await ShowOnContextAsync(ctx, prev, null, kind);
                    }).ToArray();
                    await Task.WhenAll(tasks);

                    var first = withHistory.FirstOrDefault();
                    if (first?.CurrentPath != null)
                    {
                        await UpdateMainPreviewFromContextAsync(first);
                        _currentMediaPath = first.CurrentPath;
                        _currentMediaKind = first.CurrentKind;
                        UpdateFilePathDisplay(first.CurrentPath);
                    }
                }
                else
                {
                    if (_history.Count == 0) return;
                    var prev = _history[^1];
                    _history.RemoveAt(_history.Count - 1);
                    await ShowMediaAsync(prev, null);
                }
            }
            finally
            {
                _isAdvancing = false;
                if (_isSlideshowRunning && !_isOnHold) _slideshowTimer.Start();
            }
        }

        // ---------- Preloading ----------
        // Worker-thread random-pick filtered by an orientation. Used by both
        // the global preload (sync mode) and per-context preload (independent).
        private async Task<(string? path, BitmapImage? image)> PickRandomMediaAsync(string orientation)
        {
            return await Task.Run<(string?, BitmapImage?)>(() =>
            {
                int safety = 0;
                while (safety++ < 200)
                {
                    var candidate = GetRandomMedia();
                    if (candidate == null) return (null, null);

                    var kind = ClassifyMedia(candidate);
                    if (kind == MediaKind.Image)
                    {
                        var bmp = TryLoadBitmap(candidate);
                        if (bmp == null) continue;
                        var rotated = ApplyRotationIfNeeded(bmp, candidate);
                        if (!ImagePassesOrientation(rotated, orientation)) continue;
                        return (candidate, rotated);
                    }
                    else if (kind == MediaKind.Video)
                    {
                        if (!VideoPassesFilters(candidate, orientation)) continue;
                        return (candidate, null);
                    }
                    else // Gif (only when classified as Gif by the active mode)
                    {
                        return (candidate, null);
                    }
                }
                return (null, null);
            });
        }

        // Global preload (sync mode + main preview). Uses the default
        // orientation filter from the Filters tab.
        private async Task PreloadNextAsync()
        {
            if (_isPreloading) return;
            _isPreloading = true;

            try
            {
                var (path, image) = await PickRandomMediaAsync(_orientationFilter);
                _nextMediaPath = path;
                _nextImageBuffer = image;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Preload error: {ex.Message}");
            }
            finally
            {
                _isPreloading = false;
            }
        }

        // Per-monitor preload for independent mode. Each monitor uses its own
        // orientation filter; "All" falls back to the default.
        private async Task PreloadForContextAsync(MonitorContext ctx)
        {
            if (ctx.IsPreloading) return;
            ctx.IsPreloading = true;

            try
            {
                string orientation = ctx.OrientationFilter == "All" ? "All" : ctx.OrientationFilter;
                var (path, image) = await PickRandomMediaAsync(orientation);
                ctx.NextPath = path;
                ctx.NextImage = image;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{ctx.Screen.DeviceName}] Preload error: {ex.Message}");
            }
            finally
            {
                ctx.IsPreloading = false;
            }
        }

        private string? GetRandomMedia()
        {
            if (_mediaFiles.Count == 0) return null;
            return _mediaFiles[_random.Next(_mediaFiles.Count)];
        }

        // ---------- Image loading + rotation ----------
        private static BitmapImage? TryLoadBitmap(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Image load failed for {path}: {ex.Message}");
                return null;
            }
        }

        private static BitmapImage ApplyRotationIfNeeded(BitmapImage image, string path)
        {
            try
            {
                var frame = BitmapFrame.Create(new Uri(path, UriKind.Absolute),
                                               BitmapCreateOptions.DelayCreation,
                                               BitmapCacheOption.OnLoad);
                if (frame.Metadata is BitmapMetadata md && md.ContainsQuery("System.Photo.Orientation"))
                {
                    if (md.GetQuery("System.Photo.Orientation") is ushort orientation)
                    {
                        int angle = orientation switch
                        {
                            6 => 90,
                            3 => 180,
                            8 => 270,
                            _ => 0
                        };
                        if (angle != 0)
                            return EncodeBitmap(new TransformedBitmap(frame, new RotateTransform(angle)));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Rotation check failed: {ex.Message}");
            }
            return image;
        }

        private static BitmapImage EncodeBitmap(BitmapSource source)
        {
            var bi = new BitmapImage();
            using var ms = new MemoryStream();
            BitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(ms);
            ms.Position = 0;
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        // ---------- Filters ----------
        // Orientation parameter so the same filter logic works for the global
        // pick (uses _orientationFilter) and for per-monitor independent picks
        // (each monitor passes its own).
        private static bool ImagePassesOrientation(BitmapImage bmp, string orientation)
        {
            if (orientation == "All") return true;
            bool isLandscape = bmp.PixelWidth > bmp.PixelHeight;
            bool isPortrait = bmp.PixelHeight > bmp.PixelWidth;
            return (orientation == "Landscape" && isLandscape)
                || (orientation == "Vertical"  && isPortrait);
        }

        private bool VideoPassesFilters(string path, string orientation)
        {
            try
            {
                var info = new MediaInfoWrapper(path, NullLogger<MediaInfoWrapper>.Instance);
                int w = info.Width, h = info.Height;

                if (w < _minWidth || h < _minHeight) return false;

                return orientation switch
                {
                    "Landscape" => w > h,
                    "Vertical"  => h > w,
                    _ => true
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MediaInfo failed for {path}: {ex.Message}");
                return false;
            }
        }

        // ---------- Display image (sync mode entry point) ----------
        private async Task DisplayImageAsync(string path, BitmapImage? image)
        {
            image ??= TryLoadBitmap(path);
            if (image == null) return;
            var rotated = ApplyRotationIfNeeded(image, path);

            await Dispatcher.InvokeAsync(() =>
            {
                bool scaleToFill = ScaleToFillCheckBox.IsChecked == true;
                bool showOverlay = ShowPathOverlayCheckBox.IsChecked == true;

                ShowOnly(PreviewImage);
                PreviewImage.Stretch = scaleToFill ? Stretch.UniformToFill : Stretch.Uniform;
                PreviewImage.Source = rotated;

                _slideshowTimer.Interval =
                    TimeSpan.FromSeconds(double.TryParse(DelayTextBox.Text, out var d) && d > 0 ? d : 3);

                SyncMonitorWindows();
                foreach (var ctx in _monitorContexts.Where(c => c.Selected && c.Window != null))
                {
                    ctx.Window!.DisplayImage(rotated, scaleToFill);
                    ctx.Window!.SetPathOverlay(path, showOverlay);
                }
            });
        }

        // ---------- Display video / GIF (sync mode entry point) ----------
        private async Task PlayVideoOrGifAsync(string path, MediaKind kind)
        {
            (double clipMs, int startTimeMs) = ComputeVideoTiming(path, kind);
            _slideshowTimer.Interval = TimeSpan.FromMilliseconds(clipMs);

            await Dispatcher.InvokeAsync(() =>
            {
                bool scaleToFill = ScaleToFillCheckBox.IsChecked == true;
                bool showOverlay = ShowPathOverlayCheckBox.IsChecked == true;

                SyncMonitorWindows();

                if (kind == MediaKind.Gif)
                {
                    ShowOnly(GifPlayer);
                    GifPlayer.Stretch = scaleToFill ? Stretch.UniformToFill : Stretch.Uniform;
                    GifPlayer.Source = new Uri(path, UriKind.Absolute);
                    GifPlayer.Play();
                    foreach (var ctx in _monitorContexts.Where(c => c.Selected && c.Window != null))
                    {
                        ctx.Window!.PlayGif(path, scaleToFill);
                        ctx.Window!.SetPathOverlay(path, showOverlay);
                    }
                }
                else
                {
                    if (!_vlcInitialized)
                    {
                        MessageBox.Show(
                            "Video playback requires libvlc, which was not found.\n\n" +
                            "Install VLC media player from https://www.videolan.org/vlc/ " +
                            "(64-bit if your Windows is 64-bit) and restart the app. " +
                            "The default install location is automatically detected.",
                            "libvlc not found",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    ShowOnly(VlcCanvas);
                    var mp = VlcCanvas.SourceProvider.MediaPlayer;
                    mp.Audio.Volume = MuteCheckBox.IsChecked == true ? 0 : 100;
                    mp.Play(new Uri(path));
                    mp.Time = startTimeMs;
                    foreach (var ctx in _monitorContexts.Where(c => c.Selected && c.Window != null))
                    {
                        ctx.Window!.PlayVideo(path, startTimeMs);
                        ctx.Window!.SetPathOverlay(path, showOverlay);
                    }
                }
            });
        }

        // Computes (timer interval ms, random start position ms) for a given file.
        private (double clipMs, int startTimeMs) ComputeVideoTiming(string path, MediaKind kind)
        {
            double clipMs = (double.TryParse(DelayTextBox.Text, out var d) && d > 0 ? d : 10) * 1000;
            int startTimeMs = 0;

            if (kind == MediaKind.Video && !GifExtensions.Contains(Path.GetExtension(path))
                && !string.Equals(Path.GetExtension(path), ".webm", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var info = new MediaInfoWrapper(path, NullLogger<MediaInfoWrapper>.Instance);
                    long duration = info.Duration;
                    if (duration > clipMs)
                    {
                        long maxStart = duration - (long)clipMs;
                        startTimeMs = _random.Next(0, (int)maxStart);
                    }
                    else
                    {
                        clipMs = Math.Max(duration, 1000);
                    }
                }
                catch { /* fall back to clipMs default */ }
            }
            return (clipMs, startTimeMs);
        }

        // ---------- Independent-mode per-context display ----------
        // Pushes a single piece of media into one specific monitor's window.
        private async Task ShowOnContextAsync(MonitorContext ctx, string path,
                                              BitmapImage? bufferedImage, MediaKind kind)
        {
            if (ctx.Window == null) return;

            // Stop other media kinds on this window first (so a still-playing
            // video doesn't fire EndReached past the new image, etc.).
            ctx.Window.StopPlaybackOfOtherKinds(kind);
            ctx.CurrentKind = kind;
            ctx.CurrentPath = path;

            bool scaleToFill = ScaleToFillCheckBox.IsChecked == true;
            bool showOverlay = ShowPathOverlayCheckBox.IsChecked == true;

            if (kind == MediaKind.Image)
            {
                var bmp = bufferedImage ?? TryLoadBitmap(path);
                if (bmp == null) return;
                var rotated = ApplyRotationIfNeeded(bmp, path);
                await Dispatcher.InvokeAsync(() =>
                {
                    ctx.Window?.DisplayImage(rotated, scaleToFill);
                    ctx.Window?.SetPathOverlay(path, showOverlay);
                });
            }
            else if (kind == MediaKind.Gif)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ctx.Window?.PlayGif(path, scaleToFill);
                    ctx.Window?.SetPathOverlay(path, showOverlay);
                });
            }
            else // Video
            {
                (_, int startTimeMs) = ComputeVideoTiming(path, kind);
                await Dispatcher.InvokeAsync(() =>
                {
                    ctx.Window?.PlayVideo(path, startTimeMs);
                    ctx.Window?.SetPathOverlay(path, showOverlay);
                });
            }
        }

        private void ShowOnly(UIElement element)
        {
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = element == PreviewImage ? Visibility.Visible : Visibility.Collapsed;
            VlcCanvas.Visibility = element == VlcCanvas ? Visibility.Visible : Visibility.Collapsed;
            GifPlayer.Visibility = element == GifPlayer ? Visibility.Visible : Visibility.Collapsed;
        }

        // Halts playback of any media that isn't of the kind we're about to show.
        // Pause (not Stop) on VLC to avoid the wind-down deadlock; EndReached gate
        // on _currentMediaKind keeps the paused-but-buffered video harmless.
        // Applied to the main window's preview AND every monitor's fullscreen window.
        private void StopPlaybackOfOtherKinds(MediaKind incoming)
        {
            if (incoming != MediaKind.Video)
            {
                try
                {
                    if (_vlcInitialized && VlcCanvas.SourceProvider.MediaPlayer != null)
                        VlcCanvas.SourceProvider.MediaPlayer.Pause();
                }
                catch (Exception ex) { Debug.WriteLine($"VLC pause failed: {ex.Message}"); }
            }

            if (incoming != MediaKind.Gif)
            {
                try
                {
                    GifPlayer.Stop();
                    GifPlayer.Source = null;
                }
                catch (Exception ex) { Debug.WriteLine($"GifPlayer stop failed: {ex.Message}"); }
            }

            foreach (var ctx in _monitorContexts.Where(c => c.Window != null))
                ctx.Window!.StopPlaybackOfOtherKinds(incoming);
        }

        private void GifPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            // Stale event guard.
            if (_currentMediaKind != MediaKind.Gif) return;

            // Held: rewind and loop the same gif.
            if (_isOnHold)
            {
                try
                {
                    GifPlayer.Position = TimeSpan.Zero;
                    GifPlayer.Play();
                }
                catch (Exception ex) { Debug.WriteLine($"[Hold] GIF loop failed: {ex.Message}"); }
                return;
            }

            if (_isSlideshowRunning) AdvanceToNextMedia();
        }

        // ---------- Preview enlarge ----------
        private void MediaContainer_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (MediaContainer.Parent is not Border border) return;
            if (border.Parent is not Grid parentGrid) return;

            if (!_isPreviewEnlarged)
            {
                Grid.SetRow(border, 0);
                Grid.SetRowSpan(border, parentGrid.RowDefinitions.Count);
                Panel.SetZIndex(border, 100);
                _isPreviewEnlarged = true;
            }
            else
            {
                // Row 6 is the preview row in the current layout
                // (header, mode tabs, toolbar, progress, settings tabs, file path, preview).
                Grid.SetRow(border, 6);
                Grid.SetRowSpan(border, 1);
                Panel.SetZIndex(border, 0);
                _isPreviewEnlarged = false;
            }
        }

        // ---------- File path hyperlink ----------
        private void UpdateFilePathDisplay(string filePath)
        {
            FilePathHyperlink.Inlines.Clear();
            FilePathHyperlink.Inlines.Add(new Run(filePath));
        }

        private void FilePathHyperlink_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentMediaPath)) return;
            try
            {
                Process.Start("explorer.exe", $"/select,\"{_currentMediaPath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to open the file location: {ex.Message}");
            }
        }

        // ---------- Focus-based key handlers (Space, Del, Left, Right) ----------
        // Wired from MainWindow.xaml via Window-level KeyDown.
        // FullscreenSlideshowWindow forwards its own KeyDown to HandleHotKey
        // so both contexts share the same behavior.
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Don't intercept while the user is typing in a text/numeric field
            // or popping a combo. Otherwise Space/arrows/Del wouldn't behave
            // normally inside those controls.
            var focused = Keyboard.FocusedElement;
            if (focused is TextBox || focused is ComboBox) return;

            HandleHotKey(e);
        }

        // Shared by main and fullscreen windows.
        public void HandleHotKey(KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Space:
                    if (_isSlideshowRunning) StopSlideshow();
                    e.Handled = true;
                    break;

                case Key.Delete:
                    _ = HandleDeleteCurrentAsync();
                    e.Handled = true;
                    break;

                case Key.Right:
                    _ = NextRandomAsync();
                    e.Handled = true;
                    break;

                case Key.Left:
                    _ = PreviousAsync();
                    e.Handled = true;
                    break;
            }
        }

        // Delete-current flow: hold the slideshow, confirm, advance, delete on disk.
        private async Task HandleDeleteCurrentAsync()
        {
            if (string.IsNullOrEmpty(_currentMediaPath)) return;
            string toDelete = _currentMediaPath;
            if (!File.Exists(toDelete)) return;

            // Hold so the timer doesn't tick under us while the user reads the
            // confirm dialog. Remember whether we toggled it so we can restore.
            bool wasOnHold = _isOnHold;
            if (!wasOnHold)
            {
                _isOnHold = true;
                _slideshowTimer.Stop();
                UpdateHoldStatus();
            }

            var result = MessageBox.Show(
                $"Permanently delete this file?\n\n{toDelete}\n\nThis cannot be undone.",
                "Delete File",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                // Restore prior hold state and continue normally.
                if (!wasOnHold)
                {
                    _isOnHold = false;
                    UpdateHoldStatus();
                    if (_isSlideshowRunning) _slideshowTimer.Start();
                }
                return;
            }

            // Drop from the working list so it can't be re-randomly-picked.
            _mediaFiles.Remove(toDelete);
            // Drop from history too.
            _history.RemoveAll(p => string.Equals(p, toDelete, StringComparison.OrdinalIgnoreCase));

            // Release the hold so NextRandomAsync can run, then advance to a
            // different file. This in turn calls VLC mp.Play(newUri) which
            // releases the file handle on the deleted-target.
            _isOnHold = false;
            UpdateHoldStatus();

            await NextRandomAsync();

            // Brief pause to let VLC fully release the previous file before we
            // delete it on disk.
            await Task.Delay(250);

            try
            {
                File.Delete(toDelete);
                Debug.WriteLine($"[Delete] Removed {toDelete}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not delete file:\n{toDelete}\n\n{ex.Message}",
                    "Delete failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- Settings persistence ----------
        private void ApplyLoadedSettings(AppSettings s)
        {
            // Mode (drives the header TabControl)
            switch (s.Mode)
            {
                case "Video": ModeTabControl.SelectedIndex = 1; _mode = MediaMode.Video; break;
                case "Mixed": ModeTabControl.SelectedIndex = 2; _mode = MediaMode.Mixed; break;
                default:      ModeTabControl.SelectedIndex = 0; _mode = MediaMode.Photo; break;
            }

            // Orientation
            switch (s.Orientation)
            {
                case "Landscape": LandscapeRadio.IsChecked = true; _orientationFilter = "Landscape"; break;
                case "Vertical":  VerticalRadio.IsChecked = true;  _orientationFilter = "Vertical";  break;
                default:          AllOrientationRadio.IsChecked = true; _orientationFilter = "All"; break;
            }

            IncludeAnimatedCheckBox.IsChecked = s.IncludeAnimated;
            _includeAnimated = s.IncludeAnimated;

            DelayTextBox.Text = s.DelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _slideshowTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.1, s.DelaySeconds));

            MinWidthTextBox.Text = s.MinWidth.ToString();
            MinHeightTextBox.Text = s.MinHeight.ToString();
            _minWidth = s.MinWidth;
            _minHeight = s.MinHeight;

            AlwaysOnTopCheckBox.IsChecked = s.AlwaysOnTop;
            this.Topmost = s.AlwaysOnTop;
            ScaleToFillCheckBox.IsChecked = s.ScaleToFill;
            MuteCheckBox.IsChecked = s.Mute;
            ShowPathOverlayCheckBox.IsChecked = s.ShowPathOverlay;

            // Theme: select the matching combo item AND set Application.ThemeMode
            // before _uiInitialized goes true so the SelectionChanged handler
            // doesn't double-apply.
            ThemeComboBox.SelectedIndex = s.Theme switch
            {
                "Light"  => 0,
                "System" => 2,
                _        => 1, // Dark default
            };
            ApplyThemeFromCombo();

            // Monitor configs: match each saved entry to a current MonitorContext
            // by device name. Monitors that no longer exist are silently dropped;
            // newly-attached monitors retain their default (Selected=false, All).
            if (s.Monitors != null)
            {
                foreach (var saved in s.Monitors)
                {
                    var ctx = _monitorContexts.FirstOrDefault(c =>
                        string.Equals(c.Screen.DeviceName, saved.DeviceName,
                                      StringComparison.OrdinalIgnoreCase));
                    if (ctx == null) continue;
                    ctx.Selected = saved.Selected;
                    ctx.OrientationFilter = string.IsNullOrEmpty(saved.Orientation)
                        ? "All" : saved.Orientation;
                }
            }
            // Backward compat: a settings.json from before this multi-monitor
            // change has MonitorDeviceName instead of a Monitors list.
            else if (!string.IsNullOrEmpty(s.MonitorDeviceName))
            {
                var ctx = _monitorContexts.FirstOrDefault(c =>
                    string.Equals(c.Screen.DeviceName, s.MonitorDeviceName,
                                  StringComparison.OrdinalIgnoreCase));
                if (ctx != null) ctx.Selected = true;
            }

            _independentMonitors = s.IndependentMonitors;
            if (_independentMonitors)
                IndependentMonitorsRadio.IsChecked = true;
            else
                SyncMonitorsRadio.IsChecked = true;

            // Panic hotkey
            if (Enum.TryParse<Key>(s.PanicKey, out var panicKey))
            {
                _panicKey = panicKey;
                PanicKeyComboBox.SelectedItem = panicKey.ToString();
            }
            PanicCtrlCheckBox.IsChecked = s.PanicCtrl;
            PanicAltCheckBox.IsChecked = s.PanicAlt;
            _panicAction = s.PanicAction == "Exit" ? PanicAction.Exit : PanicAction.Stop;
            PanicActionComboBox.SelectedIndex = (int)_panicAction;

            // Hold hotkey
            if (Enum.TryParse<Key>(s.HoldKey, out var holdKey))
            {
                _holdKey = holdKey;
                HoldKeyComboBox.SelectedItem = holdKey.ToString();
            }
            HoldCtrlCheckBox.IsChecked = s.HoldCtrl;
            HoldAltCheckBox.IsChecked = s.HoldAlt;

            MultiInstanceCheckBox.IsChecked = s.MultiInstanceHotkeys;
            _useLowLevelHook = s.MultiInstanceHotkeys;

            // Folder persistence
            PersistFoldersCheckBox.IsChecked = s.PersistFolders;
            if (s.PersistFolders && s.Folders != null)
            {
                _selectedFolders.Clear();
                _selectedFolders.AddRange(s.Folders.Where(Directory.Exists));
                UpdateFolderDisplay();
            }
        }

        private void SaveSettings()
        {
            try
            {
                var s = new AppSettings
                {
                    Mode = _mode.ToString(),
                    Orientation = _orientationFilter,
                    IncludeAnimated = _includeAnimated,
                    DelaySeconds = double.TryParse(DelayTextBox.Text, out var d) ? d : 3,
                    MinWidth = _minWidth,
                    MinHeight = _minHeight,
                    AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true,
                    ScaleToFill = ScaleToFillCheckBox.IsChecked == true,
                    Mute = MuteCheckBox.IsChecked == true,
                    ShowPathOverlay = ShowPathOverlayCheckBox.IsChecked == true,
                    Theme = (ThemeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Dark",
                    Monitors = _monitorContexts.Select(c => new AppSettings.MonitorConfig
                    {
                        DeviceName = c.Screen.DeviceName,
                        Selected = c.Selected,
                        Orientation = c.OrientationFilter,
                    }).ToList(),
                    IndependentMonitors = _independentMonitors,
                    MonitorDeviceName = "", // legacy field, no longer used
                    PanicKey = _panicKey.ToString(),
                    PanicCtrl = PanicCtrlCheckBox.IsChecked == true,
                    PanicAlt = PanicAltCheckBox.IsChecked == true,
                    PanicAction = _panicAction.ToString(),
                    HoldKey = _holdKey.ToString(),
                    HoldCtrl = HoldCtrlCheckBox.IsChecked == true,
                    HoldAlt = HoldAltCheckBox.IsChecked == true,
                    MultiInstanceHotkeys = _useLowLevelHook,
                    PersistFolders = PersistFoldersCheckBox.IsChecked == true,
                    Folders = PersistFoldersCheckBox.IsChecked == true
                                ? new List<string>(_selectedFolders)
                                : new List<string>(),
                };
                s.Save();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Save threw: {ex.Message}");
            }
        }

        // ---------- Closing ----------
        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            AllowSleep();

            // Persist settings before tearing down.
            SaveSettings();

            // Release global hotkeys (whichever flavor is installed) so other
            // apps / future runs can claim them again.
            UninstallAllHotkeys();
            _hwndSource?.RemoveHook(WndProcHook);

            CloseAllMonitorWindows();
            Application.Current.Shutdown();
        }
    }
}
