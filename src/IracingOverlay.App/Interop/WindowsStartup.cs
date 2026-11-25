using System.IO;
using IracingOverlay.App.Updates;
using Microsoft.Win32;

namespace IracingOverlay.App.Interop;

/// <summary>
/// "Start with Windows": the per-user Run entry (no administrator rights needed), the same place Steam or
/// Discord use. Task Manager → Startup apps lists it and can disable it; that shows here as off.
/// </summary>
internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Where Task Manager records "Disabled" for an entry under Run (absent = enabled).
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string EntryName = "iRacing Overlay";
    /// <summary>Marks launches by Windows at sign-in (the app starts in the tray either way).</summary>
    internal const string Argument = "--autostart";

    /// <summary>True when Windows starts the app at sign-in.</summary>
    public static bool IsEnabled
    {
        get
        {
            try { return RegisteredCommand() != null && !DisabledInTaskManager(); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Adds or removes the entry; returns whether it worked.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (!enabled) run.DeleteValue(EntryName, throwOnMissingValue: false);
                else if (Environment.ProcessPath is { } exe) run.SetValue(EntryName, Command(exe));
                else return false;
            }
            // Turning it on here also undoes "Disabled" from Task Manager, so both agree.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(EntryName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps an existing entry pointing at this copy, so moving or re-extracting the app doesn't break it.
    /// Development builds only take over an entry whose app no longer exists.
    /// </summary>
    public static void FollowThisCopy()
    {
        try
        {
            if (RegisteredCommand() is not { } command || Environment.ProcessPath is not { } exe) return;
            string? registered = ExecutableOf(command);
            if (string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase)) return;
            if (!UpdateService.CanInstall && registered != null && File.Exists(registered)) return;
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.SetValue(EntryName, Command(exe));
        }
        catch (Exception)
        {
            // Leave the entry as it is.
        }
    }

    private static string Command(string exe) => $"\"{exe}\" {Argument}";

    private static string? RegisteredCommand()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(EntryName) as string is { Length: > 0 } command ? command : null;
    }

    private static bool DisabledInTaskManager()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        // 02 00 00 00 … = enabled, 03 00 00 00 + time it was disabled = disabled.
        return approved?.GetValue(EntryName) is byte[] { Length: > 0 } state && (state[0] & 1) != 0;
    }

    private static string? ExecutableOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }
}
