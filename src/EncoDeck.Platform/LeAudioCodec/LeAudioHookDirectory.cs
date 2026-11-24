// SPDX-License-Identifier: GPL-3.0-only
using System.Security.AccessControl;
using System.Security.Principal;

namespace EncoDeck.Platform.LeAudioCodec;

/// <summary>
/// Makes %ProgramData%\EncoDeck\LeHook a folder only administrators can change. What is in it is loaded into Windows'
/// Bluetooth audio service and run by the elevated restore task, and a folder under %ProgramData% otherwise inherits
/// "users may create files": a program of the user could put its own DLL where the next apply expects the copy, keep
/// ownership of it and swap it later. Run by the elevated apply before anything in the folder is trusted.
/// </summary>
internal static class LeAudioHookDirectory
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalService = new(WellKnownSidType.LocalServiceSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>
    /// Creates <paramref name="directory"/> if needed, replaces its access list (and that of the EncoDeck folder above
    /// it, which otherwise lets its creator rename this one away) and removes what the elevated apply or the service
    /// did not put there. Throws when either folder is a link.
    /// </summary>
    public static void Prepare(string directory, Action<string> log) => Prepare(directory, Administrators, log);

    internal static void Prepare(string directory, SecurityIdentifier owner, Action<string> log)
    {
        var parent = Directory.GetParent(directory) ?? throw new IOException("The hook folder has no parent.");
        parent.Create();
        RejectLink(parent);
        // The Codec folder next to this one is only written by a system trace session and read by everyone.
        parent.SetAccessControl(BuildSecurity(owner, serviceWrites: false));

        var info = new DirectoryInfo(directory);
        info.Create();
        RejectLink(info);
        info.SetAccessControl(BuildSecurity(owner, serviceWrites: true));

        int removed = Clean(info, [owner, LocalSystem, LocalService], log);
        if (removed > 0) log($"hook folder: removed {removed} item(s) not written by an administrator or the service");
    }

    /// <summary>
    /// Deletes links and whatever a trusted account does not own (symbols and hook copies are downloaded or copied
    /// again), and puts the rest back on the folder's access list. Links are removed, never followed.
    /// </summary>
    private static int Clean(DirectoryInfo directory, SecurityIdentifier[] trusted, Action<string> log)
    {
        int removed = 0;
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.None };
        foreach (var entry in directory.EnumerateFileSystemInfos("*", options).ToList())
        {
            try
            {
                bool link = IsLink(entry);
                if (link || !IsOwnedBy(entry, trusted))
                {
                    if (entry is DirectoryInfo untrusted && !link) untrusted.Delete(recursive: true);
                    else entry.Delete();
                    removed++;
                    continue;
                }
                ResetToInherited(entry);
                if (entry is DirectoryInfo folder) removed += Clean(folder, trusted, log);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A copy the service has loaded cannot be deleted; it is named after its content, so the apply that
                // loaded it is the only thing that ever uses it.
                log($"hook folder: {entry.Name} left as it is ({ex.Message})");
            }
        }
        return removed;
    }

    /// <summary>
    /// Administrators and SYSTEM: everything. Users: read (the app reads the link record, the state and the logs
    /// without elevation). LOCAL SERVICE, the account of the Bluetooth audio service: loads the DLL, appends to its
    /// log and rewrites the link record. Nothing is inherited from %ProgramData%.
    /// </summary>
    internal static DirectorySecurity BuildSecurity(SecurityIdentifier owner, bool serviceWrites)
    {
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem,FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        // The same rule again unless a test stands in for the administrator.
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        if (serviceWrites)
            security.AddAccessRule(new FileSystemAccessRule(LocalService, FileSystemRights.Modify, Inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static void RejectLink(DirectoryInfo directory)
    {
        directory.Refresh();
        if (IsLink(directory)) throw new IOException($"{directory.FullName} is a link; remove it and apply again.");
    }

    private static bool IsOwnedBy(FileSystemInfo entry, SecurityIdentifier[] trusted)
    {
        FileSystemSecurity security = entry is DirectoryInfo folder
            ? folder.GetAccessControl(AccessControlSections.Owner)
            : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner);
        return security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier sid && trusted.Contains(sid);
    }

    /// <summary>Drops rights granted on the item itself (an earlier version's, or its creator's) for the folder's.</summary>
    private static void ResetToInherited(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo folder)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            folder.SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            ((FileInfo)entry).SetAccessControl(security);
        }
    }
}
