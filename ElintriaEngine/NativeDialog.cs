using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ElintriaEngine.Core
{
    /// <summary>
    /// Cross-platform native file/folder dialogs.
    /// Windows: writes a temp .ps1 file and runs it on an STA thread — avoids
    ///          all quoting issues with inline -Command scripts.
    /// Linux:   zenity
    /// macOS:   osascript
    /// </summary>
    public static class NativeDialog
    {
        // ── Public API ────────────────────────────────────────────────────────

        public static string? OpenFile(
            string title = "Open",
            string filter = "All files (*.*)|*.*",
            string initialDir = "")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return RunPs1(BuildOpenScript(title, filter, initialDir));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return Zenity($"--file-selection --title={Q(title)}{InitDir(initialDir)}");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return Osascript($"choose file with prompt {Q(title)}");
            return null;
        }

        public static string? SaveFile(
            string title = "Save As",
            string filter = "All files (*.*)|*.*",
            string defaultName = "",
            string initialDir = "")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return RunPs1(BuildSaveScript(title, filter, defaultName, initialDir));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return Zenity($"--file-selection --save --confirm-overwrite --title={Q(title)}" +
                              (string.IsNullOrEmpty(defaultName) ? "" : $" --filename={Q(defaultName)}") +
                              InitDir(initialDir));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return Osascript($"choose file name with prompt {Q(title)}" +
                                 (string.IsNullOrEmpty(defaultName) ? "" : $" default name {Q(defaultName)}"));
            return null;
        }

        public static string? SelectFolder(
            string title = "Select Folder",
            string initialDir = "")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return RunPs1(BuildFolderScript(title, initialDir));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return Zenity($"--file-selection --directory --title={Q(title)}{InitDir(initialDir)}");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return Osascript($"choose folder with prompt {Q(title)}");
            return null;
        }

        // ── Windows: write temp .ps1, run on STA thread ───────────────────────

        private static string? RunPs1(string scriptBody)
        {
            string ps1 = Path.Combine(Path.GetTempPath(), $"elintria_dlg_{Guid.NewGuid():N}.ps1");
            try
            {
                File.WriteAllText(ps1, scriptBody, Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -STA -File \"{ps1}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var p = Process.Start(psi)!;
                string stdout = p.StandardOutput.ReadToEnd().Trim();
                string stderr = p.StandardError.ReadToEnd().Trim();
                p.WaitForExit();

                if (!string.IsNullOrEmpty(stderr))
                    Console.WriteLine($"[NativeDialog] PS1 stderr: {stderr}");

                return string.IsNullOrEmpty(stdout) ? null : stdout;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NativeDialog] RunPs1: {ex.Message}");
                return null;
            }
            finally
            {
                try { File.Delete(ps1); } catch { }
            }
        }

        // ── PowerShell script builders ────────────────────────────────────────
        // Scripts are written to a file, so no argument-quoting issues.

        private static string BuildOpenScript(string title, string filter, string dir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Add-Type -AssemblyName System.Windows.Forms");
            sb.AppendLine("$d = New-Object System.Windows.Forms.OpenFileDialog");
            sb.AppendLine($"$d.Title  = '{Ps(title)}'");
            sb.AppendLine($"$d.Filter = '{Ps(WinFilter(filter))}'");
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                sb.AppendLine($"$d.InitialDirectory = '{Ps(dir)}'");
            sb.AppendLine("$d.Multiselect = $false");
            sb.AppendLine("$result = $d.ShowDialog()");
            sb.AppendLine("if ($result -eq [System.Windows.Forms.DialogResult]::OK) { $d.FileName }");
            return sb.ToString();
        }

        private static string BuildSaveScript(string title, string filter,
                                               string defName, string dir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Add-Type -AssemblyName System.Windows.Forms");
            sb.AppendLine("$d = New-Object System.Windows.Forms.SaveFileDialog");
            sb.AppendLine($"$d.Title  = '{Ps(title)}'");
            sb.AppendLine($"$d.Filter = '{Ps(WinFilter(filter))}'");
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                sb.AppendLine($"$d.InitialDirectory = '{Ps(dir)}'");
            if (!string.IsNullOrEmpty(defName))
                sb.AppendLine($"$d.FileName = '{Ps(defName)}'");
            sb.AppendLine("$d.OverwritePrompt = $true");
            sb.AppendLine("$result = $d.ShowDialog()");
            sb.AppendLine("if ($result -eq [System.Windows.Forms.DialogResult]::OK) { $d.FileName }");
            return sb.ToString();
        }

        private static string BuildFolderScript(string title, string dir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Add-Type -AssemblyName System.Windows.Forms");
            sb.AppendLine("$d = New-Object System.Windows.Forms.FolderBrowserDialog");
            sb.AppendLine($"$d.Description = '{Ps(title)}'");
            sb.AppendLine("$d.ShowNewFolderButton = $true");
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                sb.AppendLine($"$d.SelectedPath = '{Ps(dir)}'");
            sb.AppendLine("$result = $d.ShowDialog()");
            sb.AppendLine("if ($result -eq [System.Windows.Forms.DialogResult]::OK) { $d.SelectedPath }");
            return sb.ToString();
        }

        // ── Linux / macOS runners ─────────────────────────────────────────────

        private static string? Zenity(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("zenity", args)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                string raw = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return string.IsNullOrEmpty(raw) ? null : raw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NativeDialog] zenity: {ex.Message}");
                return null;
            }
        }

        private static string? Osascript(string expr)
        {
            try
            {
                // Wrap in POSIX path conversion
                string script = $"set r to ({expr})\nPOSIX path of r";
                var psi = new ProcessStartInfo("osascript", $"-e \"{script.Replace("\"", "\\\"")}\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                string raw = p.StandardOutput.ReadToEnd().Trim().TrimEnd('/');
                p.WaitForExit();
                return string.IsNullOrEmpty(raw) ? null : raw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NativeDialog] osascript: {ex.Message}");
                return null;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// Escape for PowerShell single-quoted strings (double up single quotes)
        private static string Ps(string s) => s.Replace("'", "''");

        /// Quote for shell args (Linux/macOS)
        private static string Q(string s) => $"\"{s.Replace("\"", "\\\"")}\"";

        private static string InitDir(string d) =>
            string.IsNullOrEmpty(d) ? "" : $" --filename={Q(d + "/")}";

        /// Convert filter "Desc (*.ext)|*.ext" → PowerShell format "Desc (*.ext)|*.ext"
        /// (WinForms uses same pipe format — no conversion needed)
        private static string WinFilter(string f) => f;
    }
}