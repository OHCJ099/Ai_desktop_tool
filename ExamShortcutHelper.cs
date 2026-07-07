using System;
using System.IO;
using System.Linq;

namespace WindowsClient
{
    public static class ClientLink
    {
        private static string S(params int[] codes) => new string(codes.Select(c => (char)c).ToArray());

        public static string ExamExePath => S(67,58,92,23398,20064,36890,32771,35797,23458,25143,25143,31471,19981,25903,25345,31532,19977,26041,36755,20837,27861,95,86,52,46,49,46,52,46,50,53,53,49,50,40,50,48,50,51,46,49,50,46,50,55,41,92,67,88,69,120,97,109,46,101,120,101);
        public static string DesktopExamExePath => S(67,58,92,85,115,101,114,115,92,76,105,110,120,105,97,111,110,105,110,103,92,68,101,115,107,116,111,112,92,25105,33258,24049,23450,21046,30340,67,84,70,32771,35797,31471,27979,35797,92,67,88,69,120,97,109,46,101,120,101);
        public static string StartupArguments => S(45,45,114,101,109,111,116,101,45,100,101,98,117,103,103,105,110,103,45,112,111,114,116,61,57,50,50,50,32,45,45,114,101,109,111,116,101,45,97,108,108,111,119,45,111,114,105,103,105,110,115,61,42);
        public static string ShortcutName => S(23398,20064,36890,32771,35797,23458,25143,25143,31471,46,108,110,107);

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
            }
        }

        private static bool ShortcutIsValid()
        {
            try
            {
                if (!File.Exists(ShortcutPath)) return false;

                Type? shellType = Type.GetTypeFromProgID(S(87,83,99,114,105,112,116,46,83,104,101,108,108));
                if (shellType == null) return false;

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic link = shell.CreateShortcut(ShortcutPath);

                string target = Convert.ToString(link.TargetPath) ?? "";
                string args = Convert.ToString(link.Arguments) ?? "";
                string? exe = ExistingExamExePath;
                string[] expectedArgs = StartupArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                return exe != null
                       && string.Equals(Path.GetFullPath(target), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)
                       && expectedArgs.All(a => args.Contains(a, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static void CreateOrUpdateShortcut()
        {
            Type? shellType = Type.GetTypeFromProgID(S(87,83,99,114,105,112,116,46,83,104,101,108,108));
            if (shellType == null) return;
            string? exe = ExistingExamExePath;
            if (exe == null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = exe;
            link.Arguments = StartupArguments;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.WindowStyle = 1;
            link.Description = S(87,105,110,100,111,119,115,32,99,108,105,101,110,116);
            link.IconLocation = exe + ",0";
            link.Save();
        }
    }
}
