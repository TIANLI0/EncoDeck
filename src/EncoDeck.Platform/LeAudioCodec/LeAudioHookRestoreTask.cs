// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace EncoDeck.Platform.LeAudioCodec;

/// <summary>
/// Puts the hook back after Windows' Bluetooth audio service restarts (reboot, sign-in, Bluetooth turned off and on)
/// without another administrator prompt. The elevated apply registers a hidden scheduled task, started only on demand,
/// at the highest run level; EncoDeck starts that task when LE Audio earbuds are connected and the hook is no longer in
/// the service. Anyone signed in as that user can start the task, so it must not run anything their programs can
/// replace: it runs Windows' rundll32 on the hook's copy in the folder only administrators can change
/// (<see cref="LeAudioCodecHook.AppliedCopy"/>, export <see cref="RestoreExport"/>), never EncoDeck itself, which a
/// per-user installation keeps in a folder the user can write to.
/// </summary>
public static class LeAudioHookRestoreTask
{
    public const string TaskName = @"EncoDeck\LE Audio hook";

    /// <summary>The hook DLL's rundll32 entry point (lehook.c).</summary>
    public const string RestoreExport = "LeHookRestore";

    /// <summary>
    /// What tasks registered by versions up to 1.18.0-beta.2 pass to EncoDeck.exe, which they ran instead of the hook's
    /// copy; such a run still restores, and then registers the current form over itself.
    /// </summary>
    public const string RestoreArgument = "restore";

    public static string Rundll32 => Path.Combine(Environment.SystemDirectory, "rundll32.exe");

    internal static string RestoreArguments(string hookCopy) => $"\"{hookCopy}\",{RestoreExport}";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly TimeSpan SchtasksTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The task definition (UTF-16 XML): runs <paramref name="exe"/> with <paramref name="arguments"/> as <paramref name="userSid"/>.</summary>
    internal static string BuildXml(string exe, string arguments, string userSid)
    {
        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Description", "Restores EncoDeck's LE Audio settings (Labs) after the Bluetooth audio service restarts.")),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userSid),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "HighestAvailable"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "true"),
                new XElement(Ns + "ExecutionTimeLimit", "PT5M")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", exe),
                    new XElement(Ns + "Arguments", arguments))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + task;
    }

    /// <summary>
    /// Elevated: registers (or replaces) the task for the current user, to run <paramref name="hookCopy"/> (the copy the
    /// apply just loaded into the service). Returns whether schtasks succeeded.
    /// </summary>
    public static bool Register(string hookCopy, Action<string> log)
    {
        string exe = Rundll32, arguments = RestoreArguments(hookCopy);
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) return false;
        string file = Path.Combine(Path.GetTempPath(), $"encodeck-lehook-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, BuildXml(exe, arguments, sid), Encoding.Unicode);
            int code = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            log($"restore task: register {(code == 0 ? "ok" : $"failed ({code})")}");
            return code == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"restore task: register failed ({ex.Message})");
            return false;
        }
        finally
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Elevated: removes the task (nothing to restore once Windows' own choice is applied).</summary>
    public static void Unregister(Action<string> log)
    {
        int code = Schtasks($"/Delete /TN \"{TaskName}\" /F");
        log($"restore task: remove {(code == 0 ? "ok" : $"not present ({code})")}");
    }

    /// <summary>
    /// Not elevated: starts the task. False when it does not exist or could not be started. A task an earlier version
    /// registered still runs EncoDeck.exe; that run registers the current form in its place (it is elevated, which
    /// removing the task would need too).
    /// </summary>
    public static bool Run() => Schtasks($"/Run /TN \"{TaskName}\"") == 0;

    private static int Schtasks(string arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return -1;
            // Drain both pipes so a chatty schtasks never blocks on a full buffer.
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(SchtasksTimeout))
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                return -1;
            }
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }
}
