using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ElintriaEngine.Core
{
    /// <summary>
    /// Thin wrapper around OS-native open/save/folder dialogs.
    /// Windows  → PowerShell WinForms dialogs (no extra dependency).
    /// Linux    → zenity (most distros ship it; graceful fallback).
    /// macOS    → osascript.
    /// All calls are synchronous and return null on cancel / error.
    /// </summary>
    public static class NativeDialog
    {
        // ── Open file ─────────────────────────────────────────────────────────
        public static string? OpenFile(string title = "Open",
                                       string filter = "All files (*.*)|*.*",
                                       string initialDir = "")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return RunPowerShell(BuildOpenFileScript(title, filter, initialDir));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return RunZenity($"--file-selection --title=\"{Esc(title)}\"{DirArg(initialDir)}");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return RunOsascript($"choose file with prompt \"{Esc(title)}\"");
            return null;
        }

        // ── Save file ─────────────────────────────────────────────────────────
        public static string? SaveFile(string title = "Save As",
                                       string filter = "All files (*.*)|*.*",
                                       string defaultName = "",
                                       string initialDir = "")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return RunPowerShell(BuildSaveFileScript(title, filter, defaultName, initialDir));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return RunZenity($"--file-selection --save --confirm-overwrite --title=\"{Esc(title)}\"{DirArg(initialDir)}" +
                                 (string.IsNullOrEmpty(defaultName) ? "" : $" --filename=\"{Esc(defaultName)}\""));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return RunOsascript($"choose file name with prompt \"{Esc(title)}\"" +
                                    (string.IsNullOrEmpty(defaultName) ? "" : $" default name \"{Esc(defaultName)}\""));
            return null;
        }

        // ── Select folder ─────────────────────────────────────────────────────
        public static string? SelectFolder(string title = "Select Folder",
                                           string initialDir = "")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return RunPowerShell(BuildFolderScript(title, initialDir));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return RunZenity($"--file-selection --directory --title=\"{Esc(title)}\"{DirArg(initialDir)}");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return RunOsascript($"choose folder with prompt \"{Esc(title)}\"");
            return null;
        }

        // ── Platform runners ──────────────────────────────────────────────────
        private static string? RunPowerShell(string script)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell",
                    $"-NoProfile -NonInteractive -Command \"{EscPs(script)}\"")
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
                Console.WriteLine($"[NativeDialog] PowerShell: {ex.Message}");
                return null;
            }
        }

        private static string? RunZenity(string args)
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

        private static string? RunOsascript(string appleScript)
        {
            try
            {
                var psi = new ProcessStartInfo("osascript", $"-e \"set result to (POSIX path of ({appleScript}))\" -e \"result\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                string raw = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return string.IsNullOrEmpty(raw) ? null : raw.TrimEnd('/');
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NativeDialog] osascript: {ex.Message}");
                return null;
            }
        }

        // ── PowerShell script builders ────────────────────────────────────────
        private static string BuildOpenFileScript(string title, string filter, string dir)
        {
            string init = string.IsNullOrEmpty(dir) ? "" : $"$d.InitialDirectory='{EscPs(dir)}';";
            return $"Add-Type -An System.Windows.Forms;" +
                   $"$d=New-Object System.Windows.Forms.OpenFileDialog;" +
                   $"$d.Title='{EscPs(title)}';" +
                   $"$d.Filter='{EscPs(WinFilter(filter))}';" +
                   $"{init}" +
                   $"if($d.ShowDialog() -eq 'OK'){{$d.FileName}}";
        }

        private static string BuildSaveFileScript(string title, string filter, string defName, string dir)
        {
            string init = string.IsNullOrEmpty(dir) ? "" : $"$d.InitialDirectory='{EscPs(dir)}';";
            string name = string.IsNullOrEmpty(defName) ? "" : $"$d.FileName='{EscPs(defName)}';";
            return $"Add-Type -An System.Windows.Forms;" +
                   $"$d=New-Object System.Windows.Forms.SaveFileDialog;" +
                   $"$d.Title='{EscPs(title)}';" +
                   $"$d.Filter='{EscPs(WinFilter(filter))}';" +
                   $"{init}{name}" +
                   $"if($d.ShowDialog() -eq 'OK'){{$d.FileName}}";
        }

        private static string BuildFolderScript(string title, string dir)
        {
            string init = string.IsNullOrEmpty(dir) ? "" : $"$d.SelectedPath='{EscPs(dir)}';";
            return $"Add-Type -An System.Windows.Forms;" +
                   $"$d=New-Object System.Windows.Forms.FolderBrowserDialog;" +
                   $"$d.Description='{EscPs(title)}';" +
                   $"$d.ShowNewFolderButton=$true;" +
                   $"{init}" +
                   $"if($d.ShowDialog() -eq 'OK'){{$d.SelectedPath}}";
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static string Esc(string s) => s.Replace("\"", "\\\"");
        private static string EscPs(string s) => s.Replace("'", "''");
        private static string DirArg(string d) => string.IsNullOrEmpty(d) ? "" : $" --filename=\"{Esc(d)}/\"";

        /// Convert "Description (*.ext)|*.ext" → "Description (*.ext)|*.ext" (Windows format is same)
        private static string WinFilter(string f) => f;
    }
}