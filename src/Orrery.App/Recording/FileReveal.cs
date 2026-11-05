using System.Diagnostics;

namespace Orrery.App.Recording;

/// <summary>Opening a file, or its folder, in the system's own apps. Each returns what went wrong, or null.</summary>
static class FileReveal
{
    /// <summary>The folder at <paramref name="path"/>, or with <paramref name="select"/> the folder holding that file, picked out where the system can.</summary>
    public static string? Reveal(string path, bool select)
    {
        try
        {
            if (select && OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add($"/select,{path}");
                Process.Start(start);
            }
            else
            {
                Process.Start(new ProcessStartInfo(select ? Path.GetDirectoryName(path)! : path) { UseShellExecute = true });
            }

            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"Couldn't open the folder: {e.Message}";
        }
    }

    /// <summary>The file in the app the system opens it with.</summary>
    public static string? Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"Couldn't open {Path.GetFileName(path)}: {e.Message}";
        }
    }
}
