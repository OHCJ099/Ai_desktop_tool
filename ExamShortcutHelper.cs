using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace WindowsClient
{
    public static class ClientLink
    {
        private static readonly string[] SourceShortcutNames =
        {
            "考试客户端.lnk",
            "CXExame.exe.lnk",
            "CXExam.exe.lnk"
        };

        // Keep the existing startup entry point; shortcuts now live beside the current tool executable.
        public static void EnsureDesktopShortcut()
        {
            try
            {
                string directory = Path.GetDirectoryName(Environment.ProcessPath)
                    ?? AppContext.BaseDirectory;
                EnsureShortcutsInDirectory(directory);
            }
            catch
            {
                // A missing/unwritable shortcut must not prevent the tool from opening.
            }
        }

        internal static void EnsureShortcutsInDirectory(string directory)
        {
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            object? shellObject = null;
            try
            {
                shellObject = Activator.CreateInstance(shellType);
                if (shellObject == null) return;
                dynamic shell = shellObject;
                foreach (string name in SourceShortcutNames)
                {
                    string source = Path.Combine(parent, name);
                    if (!File.Exists(source)) continue;

                    object? sourceObject = null;
                    object? outputObject = null;
                    try
                    {
                        sourceObject = shell.CreateShortcut(source);
                        dynamic original = sourceObject;
                        string target = Convert.ToString(original.TargetPath) ?? "";
                        if (string.IsNullOrWhiteSpace(target)
                            || !File.Exists(Environment.ExpandEnvironmentVariables(target))) continue;

                        string arguments = AddRequiredArguments(Convert.ToString(original.Arguments) ?? "");
                        string output = Path.Combine(parent,
                            Path.GetFileNameWithoutExtension(name) + "（带启动项）.lnk");
                        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(output)), parent,
                            StringComparison.OrdinalIgnoreCase)) continue;

                        // Copy the link itself so its target, working directory, icon,
                        // description, window style, hotkey and other metadata are retained.
                        File.Copy(source, output, overwrite: true);
                        outputObject = shell.CreateShortcut(output);
                        dynamic generated = outputObject;
                        generated.Arguments = arguments;
                        generated.Save();
                    }
                    catch
                    {
                        // Continue to the other named shortcuts if this one is invalid.
                    }
                    finally
                    {
                        ReleaseCom(outputObject);
                        ReleaseCom(sourceObject);
                    }
                }
            }
            finally
            {
                ReleaseCom(shellObject);
            }
        }

        internal static string AddRequiredArguments(string arguments)
        {
            List<string> tokens = SplitArguments(arguments);
            string result = arguments;
            if (!string.Equals(GetLastSwitchValue(tokens, "--remote-debugging-port"), "9222",
                StringComparison.OrdinalIgnoreCase))
            {
                result = AppendArgument(result, "--remote-debugging-port=9222");
            }
            if (!string.Equals(GetLastSwitchValue(tokens, "--remote-allow-origins"), "*",
                StringComparison.OrdinalIgnoreCase))
            {
                result = AppendArgument(result, "--remote-allow-origins=*");
            }
            return result;
        }

        private static string AppendArgument(string arguments, string extra)
        {
            if (arguments.Length == 0) return extra;
            return arguments + (char.IsWhiteSpace(arguments[^1]) ? "" : " ") + extra;
        }

        private static string? GetLastSwitchValue(List<string> tokens, string name)
        {
            string? value = null;
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];
                if (token.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    value = token[(name.Length + 1)..];
                }
                else if (token.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal)
                        ? tokens[++i] : "";
                }
            }
            return value;
        }

        private static List<string> SplitArguments(string arguments)
        {
            IntPtr argv = CommandLineToArgvW("shortcut.exe " + arguments, out int count);
            if (argv == IntPtr.Zero) throw new InvalidOperationException("Unable to parse shortcut arguments.");
            try
            {
                var tokens = new List<string>();
                for (int i = 1; i < count; i++)
                {
                    tokens.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "");
                }
                return tokens;
            }
            finally
            {
                LocalFree(argv);
            }
        }

        private static void ReleaseCom(object? value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);
    }
}
