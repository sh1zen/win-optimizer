using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

/// <summary>Isolated account/session boundary; all key access stays in each test's reader and editor fakes.</summary>
internal sealed class TestCleanerRegistrySessions(IInteractiveUserService user, ICleanerRegistryReader reader,
    ICleanerRegistryEditor editor) : ICleanerRegistrySessions
{
    private IInteractiveUserService User => user;
    private ICleanerRegistryReader RegistryReader => reader;
    private ICleanerRegistryEditor RegistryEditor => editor;
    public Dictionary<string, string> Profiles { get; } = new(StringComparer.Ordinal);
    public Dictionary<(string Sid, Environment.SpecialFolder Folder), string> Folders { get; } = [];
    public List<string?> Opened { get; } = [];
    public List<string?> Closed { get; } = [];
    public IReadOnlyList<CleanerRegistryAccount> GetAccounts() => Profiles.Select(profile => new CleanerRegistryAccount(profile.Key, profile.Value, false)).ToArray();
    public ICleanerRegistrySession Open(string? sid)
    {
        if (sid is not null && !Profiles.ContainsKey(sid) && sid != user.InteractiveUserSid) throw new IOException("Account unavailable.");
        Opened.Add(sid);
        return new Session(this, sid);
    }

    private sealed class Session(TestCleanerRegistrySessions owner, string? sid) : ICleanerRegistrySession
    {
        public string UserPrefix => sid is not null ? @"HKEY_USERS\" + sid : owner.User.IsOtsElevation ? @"HKEY_USERS\" + owner.User.InteractiveUserSid : "HKEY_CURRENT_USER";
        public bool IsInteractive => sid is null && !owner.User.IsOtsElevation;
        public ICleanerRegistryReader Reader => owner.RegistryReader;
        public ICleanerRegistryEditor Editor => owner.RegistryEditor;
        public string GetFolderPath(Environment.SpecialFolder folder) => sid is null ? owner.User.GetInteractiveUserFolderPath(folder) :
            owner.Folders.TryGetValue((sid, folder), out var configured) ? configured : folder switch
        {
            Environment.SpecialFolder.UserProfile => owner.Profiles[sid],
            Environment.SpecialFolder.LocalApplicationData => Path.Combine(owner.Profiles[sid], "AppData", "Local"),
            Environment.SpecialFolder.ApplicationData => Path.Combine(owner.Profiles[sid], "AppData", "Roaming"),
            Environment.SpecialFolder.DesktopDirectory => Path.Combine(owner.Profiles[sid], "Desktop"),
            Environment.SpecialFolder.Programs => Path.Combine(owner.Profiles[sid], @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"),
            Environment.SpecialFolder.StartMenu => Path.Combine(owner.Profiles[sid], @"AppData\Roaming\Microsoft\Windows\Start Menu"),
            _ => throw new ArgumentOutOfRangeException(nameof(folder))
        };
        public CleanerRegistryHive GetUserHive() => new(UserPrefix, Path.Combine(GetFolderPath(Environment.SpecialFolder.UserProfile), "NTUSER.DAT"));
        public void Dispose() => owner.Closed.Add(sid);
    }
}
