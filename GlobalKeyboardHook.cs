using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AI_desktop_tool
{
    public sealed class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_CONTROL = 0x11;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_V = 0x56;

        private readonly LowLevelKeyboardProc _proc;
        private readonly Func<Task> _onCtrlV;
        private IntPtr _hook = IntPtr.Zero;
        private static DateTime _lastExamForegroundSeen = DateTime.MinValue;
        private static volatile bool _suppressCtrlV;
        private static DateTime _swallowVKeyUpUntil = DateTime.MinValue;
        private static IntPtr _cachedForegroundHwnd = IntPtr.Zero;
        private static DateTime _cachedForegroundUntil = DateTime.MinValue;
        private static bool _cachedForegroundResult;
        private static DateTime _cachedAnyExamUntil = DateTime.MinValue;
        private static bool _cachedAnyExamResult;

        public GlobalKeyboardHook(Func<Task> onCtrlV)
        {
            _onCtrlV = onCtrlV;
            _proc = HookCallback;
        }

        public void Start()
        {
            if (_hook != IntPtr.Zero) return;
            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule? curModule = curProcess.MainModule;
            IntPtr moduleHandle = curModule == null ? IntPtr.Zero : GetModuleHandle(curModule.ModuleName);
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, moduleHandle, 0);
        }

        public bool IsInstalled => _hook != IntPtr.Zero;

        public void Restart()
        {
            Dispose();
            Start();
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool isV = info.vkCode == VK_V;
                bool ctrlDown = IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL);
                bool keyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool keyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

                // Keep the low-level hook callback extremely fast. Windows silently removes
                // slow low-level hooks; all foreground/process detection is done by the
                // WPF timer via RefreshSuppressionState().
                if (isV && _suppressCtrlV)
                {
                    if (keyDown && ctrlDown)
                    {
                        _swallowVKeyUpUntil = DateTime.Now.AddMilliseconds(1200);
                        _ = _onCtrlV();
                        return (IntPtr)1; // Eat Ctrl+V keydown only.
                    }

                    if (keyUp && DateTime.Now < _swallowVKeyUpUntil)
                    {
                        _swallowVKeyUpUntil = DateTime.MinValue;
                        return (IntPtr)1; // Eat the matching V keyup so the page cannot see Ctrl+V.
                    }
                }
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        public static bool RefreshSuppressionState()
        {
            bool shouldSuppress = ForegroundLooksLikeExamClient();
            _suppressCtrlV = shouldSuppress;
            return shouldSuppress;
        }

        private static bool ForegroundLooksLikeExamClient()
        {
            try
            {
                IntPtr hwnd = Win32Helper.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                IntPtr root = GetAncestor(hwnd, GA_ROOT);
                if (root != IntPtr.Zero) hwnd = root;

                DateTime now = DateTime.Now;
                if (hwnd == _cachedForegroundHwnd && now < _cachedForegroundUntil)
                {
                    return _cachedForegroundResult;
                }

                Win32Helper.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == Environment.ProcessId)
                {
                    CacheForeground(hwnd, false, now);
                    return false;
                }

                using Process p = Process.GetProcessById((int)pid);
                string name = p.ProcessName ?? "";
                string title = GetWindowTextSafe(hwnd);
                string cls = GetClassNameSafe(hwnd);

                bool directExam =
                    name.Equals("CXExam", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("exam", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("ctf", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("考试", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("学习通", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("CTF", StringComparison.OrdinalIgnoreCase);

                if (directExam)
                {
                    _lastExamForegroundSeen = now;
                    CacheForeground(hwnd, true, now);
                    return true;
                }

                // Some CEF builds can move focus through Chromium host windows where the
                // executable/title evidence is temporarily weak. If we have just seen the
                // exam client and a CEF/Chromium window still owns focus, keep swallowing
                // Ctrl+V so the page cannot regain its paste shortcut handler.
                bool recentExam = (now - _lastExamForegroundSeen) < TimeSpan.FromMinutes(10);
                bool chromiumHost =
                    cls.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)
                    || cls.Contains("CefBrowserWindow", StringComparison.OrdinalIgnoreCase)
                    || cls.Contains("Cef", StringComparison.OrdinalIgnoreCase);

                bool result = recentExam && chromiumHost && AnyExamClientProcessExists();
                CacheForeground(hwnd, result, now);
                return result;
            }
            catch
            {
                return false;
            }
        }

        private static void CacheForeground(IntPtr hwnd, bool result, DateTime now)
        {
            _cachedForegroundHwnd = hwnd;
            _cachedForegroundResult = result;
            _cachedForegroundUntil = now.AddMilliseconds(750);
        }

        private static bool AnyExamClientProcessExists()
        {
            try
            {
                DateTime now = DateTime.Now;
                if (now < _cachedAnyExamUntil) return _cachedAnyExamResult;

                bool result = Process.GetProcesses().Any(p =>
                {
                    try
                    {
                        string processName = p.ProcessName ?? "";
                        if (processName.Equals("CXExam", StringComparison.OrdinalIgnoreCase)) return true;
                        if (processName.Contains("exam", StringComparison.OrdinalIgnoreCase)
                            || processName.Contains("ctf", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        return false;
                    }
                    catch
                    {
                        return false;
                    }
                });
                _cachedAnyExamUntil = now.AddSeconds(5);
                _cachedAnyExamResult = result;
                return result;
            }
            catch
            {
                return false;
            }
        }

        private static string GetWindowTextSafe(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(512);
                GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static string GetClassNameSafe(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                GetClassName(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    }
}
