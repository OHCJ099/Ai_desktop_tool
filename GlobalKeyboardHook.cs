using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AI_desktop_tool
{
    public sealed class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_CONTROL = 0x11;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_V = 0x56;

        private readonly LowLevelKeyboardProc _proc;
        private readonly Func<Task> _onCtrlV;
        private IntPtr _hook = IntPtr.Zero;

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
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool isV = info.vkCode == VK_V;
                bool ctrlDown = IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL);

                if (isV && ctrlDown && ForegroundLooksLikeExamClient())
                {
                    _ = _onCtrlV();
                    return (IntPtr)1; // Eat Ctrl+V so the exam page cannot show "不能粘贴".
                }
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private static bool ForegroundLooksLikeExamClient()
        {
            try
            {
                IntPtr hwnd = Win32Helper.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                Win32Helper.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == Environment.ProcessId) return false;

                using Process p = Process.GetProcessById((int)pid);
                string name = p.ProcessName ?? "";
                string path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { }

                return name.Equals("CXExam", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(@"\CXExam.exe", StringComparison.OrdinalIgnoreCase)
                       || path.Contains("考试端", StringComparison.OrdinalIgnoreCase)
                       || path.Contains("CTF考试", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
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
    }
}
