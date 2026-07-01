using System;
using System.IO;

namespace AI_desktop_tool
{
    public static class ExamShortcutHelper
    {
        public const string ExamExePath = @"C:\学习通考试客户端不支持第三方输入法_V4.1.4.25512(2023.12.27)\CXExam.exe";
        public const string DesktopExamExePath = @"C:\Users\Linxiaoning\Desktop\我自己定制的CTF考试端测试\CXExam.exe";
        public const string CdpArguments = "--remote-debugging-port=9222 --remote-allow-origins=*";
        public const string ShortcutName = "学习通考试客户端_CDP9222.lnk";

        public static string ShortcutPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

        public static string? ExistingExamExePath =>
            File.Exists(ExamExePath) ? ExamExePath :
            File.Exists(DesktopExamExePath) ? DesktopExamExePath :
            null;

        public static void EnsureDesktopShortcut()
        {
            try
            {
                if (ExistingExamExePath == null) return;
                if (ShortcutIsValid()) return;
                CreateOrUpdateShortcut();
            }
            catch
            {
                // Silent by design: shortcut creation is a convenience feature.
            }
        }

        private static bool ShortcutIsValid()
        {
            try
            {
                if (!File.Exists(ShortcutPath)) return false;

                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic link = shell.CreateShortcut(ShortcutPath);

                string target = Convert.ToString(link.TargetPath) ?? "";
                string args = Convert.ToString(link.Arguments) ?? "";
                string? exe = ExistingExamExePath;

                return exe != null
                       && string.Equals(Path.GetFullPath(target), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)
                       && args.Contains("--remote-debugging-port=9222", StringComparison.OrdinalIgnoreCase)
                       && args.Contains("--remote-allow-origins=*", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void CreateOrUpdateShortcut()
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            string? exe = ExistingExamExePath;
            if (exe == null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = exe;
            link.Arguments = CdpArguments;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.WindowStyle = 1;
            link.Description = "学习通考试客户端（开启 CEF/CDP 9222 端口）";
            link.IconLocation = exe + ",0";
            link.Save();
        }
    }
}
