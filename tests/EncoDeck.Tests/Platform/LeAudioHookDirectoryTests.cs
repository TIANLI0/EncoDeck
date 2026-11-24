// SPDX-License-Identifier: GPL-3.0-only
using System.Security.AccessControl;
using System.Security.Principal;
using EncoDeck.Platform.LeAudioCodec;

namespace EncoDeck.Tests.Platform;

public sealed class LeAudioHookDirectoryTests : IDisposable
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier LocalService = new(WellKnownSidType.LocalServiceSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "encodeck-hookdir-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Who owns what this process creates: the user, or Administrators when the tests run elevated (CI). The elevated
    /// apply always passes Administrators.
    /// </summary>
    private SecurityIdentifier NewFileOwner()
    {
        Directory.CreateDirectory(_root);
        return (SecurityIdentifier)new DirectoryInfo(_root).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))!;
    }

    private static List<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    [Fact]
    public void OnlyAdministratorsMayChangeTheFolder()
    {
        var security = LeAudioHookDirectory.BuildSecurity(Administrators, serviceWrites: true);
        // Nothing comes down from %ProgramData%, whose list lets every user create files in subfolders.
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(Administrators, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = Rules(security);
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, rules.Single(r => r.IdentityReference.Equals(Users)).FileSystemRights);
        // The Bluetooth audio service loads the DLL and writes its log and link record.
        Assert.Equal(FileSystemRights.Modify | FileSystemRights.Synchronize, rules.Single(r => r.IdentityReference.Equals(LocalService)).FileSystemRights);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null)));
        // The folder above only holds what administrators and system services write.
        Assert.DoesNotContain(Rules(LeAudioHookDirectory.BuildSecurity(Administrators, serviceWrites: false)), r => r.IdentityReference.Equals(LocalService));
    }

    [Fact]
    public void PrepareProtectsTheFolderAndDropsRightsGrantedOnItsFiles()
    {
        var me = NewFileOwner();
        string directory = Path.Combine(_root, "EncoDeck", "LeHook");
        Directory.CreateDirectory(Path.Combine(directory, "Symbols"));
        string copy = Path.Combine(directory, "EncoDeck.LeHook.00000000.dll");
        File.WriteAllText(copy, "copy");
        File.WriteAllText(Path.Combine(directory, "Symbols", "a.pdb"), "symbols");
        var granted = new FileInfo(copy).GetAccessControl();
        granted.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(copy).SetAccessControl(granted);

        var log = new List<string>();
        LeAudioHookDirectory.Prepare(directory, me, log.Add);

        Assert.Empty(log);
        foreach (string folder in new[] { directory, Path.GetDirectoryName(directory)! })
        {
            var security = new DirectoryInfo(folder).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.DoesNotContain(Rules(security), r => r.IdentityReference.Equals(Users) && (r.FileSystemRights & FileSystemRights.Write) != 0);
        }
        // Files the owner wrote stay, with the folder's list only.
        Assert.Equal("copy", File.ReadAllText(copy));
        Assert.Equal("symbols", File.ReadAllText(Path.Combine(directory, "Symbols", "a.pdb")));
        var rules = Rules(new FileInfo(copy).GetAccessControl());
        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.True(r.IsInherited));
    }

    [Fact]
    public void PrepareRemovesLinksWithoutFollowingThem()
    {
        var me = NewFileOwner();
        string directory = Path.Combine(_root, "EncoDeck", "LeHook");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        string link = Path.Combine(directory, "Symbols");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // needs developer mode or elevation

        LeAudioHookDirectory.Prepare(directory, me, _ => { });

        Assert.False(Directory.Exists(link));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void PrepareRefusesAFolderThatIsALink()
    {
        var me = NewFileOwner();
        string parent = Path.Combine(_root, "EncoDeck"), outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        string directory = Path.Combine(parent, "LeHook");
        try { Directory.CreateSymbolicLink(directory, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        Assert.Throws<IOException>(() => LeAudioHookDirectory.Prepare(directory, me, _ => { }));
    }
}
