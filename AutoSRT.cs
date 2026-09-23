using System;
using System.Diagnostics;
using System.IO;

namespace AutoSRT
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            string url = "https://autosrt.anas.lol";

            // Find Microsoft Edge executable (available on all Windows 10/11 machines)
            string edgePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
            if (!File.Exists(edgePath))
            {
                edgePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe");
            }

            if (File.Exists(edgePath))
            {
                // Launch in standalone Chromium App Mode (native frameless desktop window)
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = edgePath,
                    Arguments = "--app=\"" + url + "\" --window-size=1200,850",
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            else
            {
                // Fallback to system default browser
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
        }
    }
}
