using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Disks;

/// <summary>Keeps a tool's progress and result attached to its destination across navigation.</summary>
internal sealed class DiskToolSessions(ILocalizationService localization, IApplicationModeService mode,
    ITaskProgressService tasks, ILogService log)
{
    private readonly Dictionary<DiskTool, DiskToolsViewModel> _sessions = [];

    public DiskToolsViewModel Get(DiskTool tool)
    {
        if (!_sessions.TryGetValue(tool, out var session))
        {
            session = new DiskToolsViewModel(localization, mode, tasks, log, tool.TitleKey());
            _sessions.Add(tool, session);
        }
        return session;
    }

    public Task WaitForWritesAsync() => Task.WhenAll(_sessions.Values.Select(session => session.WaitForWriteAsync()));
}
