using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AI_desktop_tool
{
    public static class KeyboardSimulator
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_V = 0x56;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        /// <summary>
        /// Asynchronously sends a string to the currently active foreground window character by character.
        /// </summary>
        /// <param name="text">The string to send.</param>
        /// <param name="delayMs">Delay in milliseconds between characters.</param>
        public static async Task SendStringAsync(string text, int delayMs)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Uniform line endings (Windows standard)
            string normalizedText = text.Replace("\r\n", "\n").Replace("\r", "\n");

            foreach (char c in normalizedText)
            {
                // In some text fields, sending Unicode '\n' works fine, but in others, VK_RETURN (13) is preferred.
                // We will send VK_RETURN for newline characters to ensure compatibility.
                if (c == '\n')
                {
                    SendVirtualKey(13); // VK_RETURN
                }
                else
                {
                    SendChar(c);
                }

                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                }
            }
        }

        /// <summary>
        /// Sends the standard paste shortcut (Ctrl+V) to the currently active foreground window.
        /// </summary>
        public static void SendPasteShortcut()
        {
            INPUT[] inputs = new INPUT[4];

            inputs[0] = CreateVirtualKeyInput(VK_CONTROL, keyUp: false);
            inputs[1] = CreateVirtualKeyInput(VK_V, keyUp: false);
            inputs[2] = CreateVirtualKeyInput(VK_V, keyUp: true);
            inputs[3] = CreateVirtualKeyInput(VK_CONTROL, keyUp: true);

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void SendChar(char c)
        {
            INPUT[] inputs = new INPUT[2];

            // Key Down
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)c,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            // Key Up
            inputs[1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void SendVirtualKey(ushort vk)
        {
            INPUT[] inputs = new INPUT[2];

            // Key Down
            inputs[0] = CreateVirtualKeyInput(vk, keyUp: false);

            // Key Up
            inputs[1] = CreateVirtualKeyInput(vk, keyUp: true);

            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static INPUT CreateVirtualKeyInput(ushort vk, bool keyUp)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }
    }
}
