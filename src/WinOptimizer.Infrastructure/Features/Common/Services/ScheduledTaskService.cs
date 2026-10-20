using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public class ScheduledTaskService(ILogService logService, IFileSystemService fileSystemService) : IScheduledTaskService
{
    private readonly Func<object> _createTaskService = CreateTaskService;

    /// <summary>Allows isolated scheduler sessions at the native resource boundary.</summary>
    internal ScheduledTaskService(ILogService logService, IFileSystemService fileSystemService,
        Func<object> createTaskService) : this(logService, fileSystemService)
    {
        _createTaskService = createTaskService;
    }

    /// <summary>Configures a persistent desktop application without a shell or stored credentials.</summary>
    public Task<OperationResult> ConfigureUserLogonApplicationAsync(string taskName, string executablePath,
        string arguments, string userSid, bool enabled) => Task.Run(() =>
    {
        dynamic? service = null;
        dynamic? folder = null;
        dynamic? registered = null;
        try
        {
            var xml = enabled ? BuildUserLogonApplicationXml(executablePath, arguments, userSid) : null;
            service = _createTaskService();
            if (enabled) folder = GetOrCreateWinOptimizerFolder(service);
            else
            {
                try { folder = service.GetFolder("\\win-optimizer"); }
                catch (Exception ex) when (IsMissingTaskResource(ex))
                { return OperationResult.Succeeded(); }
            }
            if (enabled)
                registered = folder.RegisterTask(taskName, xml, 6, userSid, null, 3, null); // InteractiveToken
            else
            {
                try { folder.DeleteTask(taskName, 0); }
                catch (Exception ex) when (IsMissingTaskResource(ex))
                { return OperationResult.Succeeded(); }
            }
            return OperationResult.Succeeded();
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            logService.LogError($"Unable to configure desktop startup task {taskName}", ex);
            return OperationResult.Failed($"Unable to configure Windows startup: {ex.Message}", ex);
        }
        finally
        {
            ReleaseComObject(registered);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    });

    /// <summary>Task Scheduler's missing-resource HRESULTs can also be mapped to managed I/O exceptions.</summary>
    private static bool IsMissingTaskResource(Exception exception) =>
        exception is COMException or IOException &&
        (exception.HResult == unchecked((int)0x80070002) || exception.HResult == unchecked((int)0x80070003));

    /// <summary>Builds a user-scoped logon task that keeps a desktop session alive on battery power.</summary>
    internal static string BuildUserLogonApplicationXml(string executablePath, string arguments, string userSid)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath) ||
            !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Windows startup requires an existing executable with an absolute path.");
        _ = new System.Security.Principal.SecurityIdentifier(userSid);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger", new XElement(ns + "UserId", userSid))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "User"),
                new XElement(ns + "UserId", userSid), new XElement(ns + "LogonType", "InteractiveToken"),
                new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings", new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", false), new XElement(ns + "StopIfGoingOnBatteries", false),
                new XElement(ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "User"), new XElement(ns + "Exec",
                new XElement(ns + "Command", executablePath), new XElement(ns + "Arguments", arguments),
                new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executablePath))))).ToString();
    }

    private enum TaskTriggerType
    {
        Startup = 8,
        Logon = 9
    }

    public async Task<OperationResult> RegisterScheduledTaskAsync(RemovalScript script)
    {
        return await Task.Run(async () =>
        {
            try
            {
                if (script?.ActualScriptPath == null)
                {
                    logService.LogError("Script or script path is null");
                    return OperationResult.Failed("Script or script path is null");
                }

                EnsureScriptFileExists(script);

                var triggerType = script.RunOnStartup ? TaskTriggerType.Startup : TaskTriggerType.Logon;

                return await RegisterTaskInternal(script.Name, script.ActualScriptPath, null, triggerType).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logService.LogError($"Error registering scheduled task for {script?.Name}", ex);
                return OperationResult.Failed(ex.Message, ex);
            }
        }).ConfigureAwait(false);
    }


    public async Task<OperationResult> UnregisterScheduledTaskAsync(string taskName)
    {
        return await Task.Run(() =>
        {
            dynamic? taskService = null;
            dynamic? folder = null;
            dynamic? existingTask = null;
            try
            {
                taskService = _createTaskService();
                folder = GetWinOptimizerFolder(taskService);

                if (folder == null) return OperationResult.Succeeded();

                try
                {
                    existingTask = folder.GetTask(taskName);
                    if (existingTask != null)
                    {
                        folder.DeleteTask(taskName, 0);
                        logService.LogInformation($"Unregistered task: {taskName}");
                    }
                }
                catch (Exception ex)
                {
                    logService.Log(Core.Features.Common.Enums.LogLevel.Debug, $"[ScheduledTaskService] Task '{taskName}' not found: {ex.Message}");
                }

                return OperationResult.Succeeded();
            }
            catch (Exception ex)
            {
                logService.LogError($"Error unregistering task: {taskName}", ex);
                return OperationResult.Failed(ex.Message, ex);
            }
            finally
            {
                ReleaseComObject(existingTask);
                ReleaseComObject(folder);
                ReleaseComObject(taskService);
            }
        }).ConfigureAwait(false);
    }

    public async Task<bool> IsTaskRegisteredAsync(string taskName)
    {
        return await Task.Run(() =>
        {
            dynamic? taskService = null;
            dynamic? folder = null;
            dynamic? task = null;
            try
            {
                taskService = _createTaskService();
                folder = GetWinOptimizerFolder(taskService);

                if (folder == null) return false;

                task = folder.GetTask(taskName);
                return task != null;
            }
            catch (Exception ex)
            {
                logService.Log(Core.Features.Common.Enums.LogLevel.Debug, $"[ScheduledTaskService] Task '{taskName}' not registered: {ex.Message}");
                return false;
            }
            finally
            {
                ReleaseComObject(task);
                ReleaseComObject(folder);
                ReleaseComObject(taskService);
            }
        }).ConfigureAwait(false);
    }

    public async Task<OperationResult> RunScheduledTaskAsync(string taskName)
    {
        return await Task.Run(() =>
        {
            dynamic? taskService = null;
            dynamic? folder = null;
            dynamic? task = null;
            try
            {
                taskService = _createTaskService();
                folder = GetWinOptimizerFolder(taskService);

                if (folder == null)
                {
                    logService.LogError($"win-optimizer task folder not found when trying to run: {taskName}");
                    return OperationResult.Failed("win-optimizer task folder not found");
                }

                task = folder.GetTask(taskName);
                if (task == null)
                {
                    logService.LogError($"Task not found: {taskName}");
                    return OperationResult.Failed($"Task not found: {taskName}");
                }

                task.Run(null);
                logService.LogInformation($"Started task: {taskName}");
                return OperationResult.Succeeded();
            }
            catch (Exception ex)
            {
                logService.LogError($"Error running task: {taskName}", ex);
                return OperationResult.Failed(ex.Message, ex);
            }
            finally
            {
                ReleaseComObject(task);
                ReleaseComObject(folder);
                ReleaseComObject(taskService);
            }
        }).ConfigureAwait(false);
    }

    public async Task<OperationResult> CreateUserLogonTaskAsync(string taskName, string command, string username, bool deleteAfterRun = true)
    {
        return await Task.Run(async () =>
        {
            try
            {
                return await RegisterTaskInternal(taskName, null, username, TaskTriggerType.Logon, command).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logService.LogError($"Error creating user logon task: {taskName}", ex);
                return OperationResult.Failed(ex.Message, ex);
            }
        }).ConfigureAwait(false);
    }

    private async Task<OperationResult> RegisterTaskInternal(string taskName, string? scriptPath, string? username, TaskTriggerType triggerType, string? command = null)
    {
        dynamic? taskService = null;
        dynamic? folder = null;
        dynamic? taskDefinition = null;
        try
        {
            taskService = _createTaskService();
            folder = GetOrCreateWinOptimizerFolder(taskService);

            await RemoveExistingTask(folder, taskName).ConfigureAwait(false);

            taskDefinition = CreateTaskDefinition(taskService, scriptPath, command, username, triggerType);

            folder.RegisterTaskDefinition(
                taskName,
                taskDefinition,
                6, // TASK_CREATE_OR_UPDATE
                username,
                null, // password
                username != null ? 1 : 5, // TASK_LOGON_INTERACTIVE_TOKEN or TASK_LOGON_SERVICE_ACCOUNT
                null
            );

            logService.LogInformation($"Registered task: {taskName} as {username ?? "SYSTEM"}");
            return OperationResult.Succeeded();
        }
        finally
        {
            ReleaseComObject(taskDefinition);
            ReleaseComObject(folder);
            ReleaseComObject(taskService);
        }
    }

    private static object CreateTaskService()
    {
        Type taskSchedulerType = Type.GetTypeFromProgID("Schedule.Service")!;
        dynamic taskService = Activator.CreateInstance(taskSchedulerType)!;
        taskService.Connect();
        return taskService;
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject != null)
        {
            try { Marshal.ReleaseComObject(comObject); } catch { /* best-effort COM release */ }
        }
    }

    private dynamic GetOrCreateWinOptimizerFolder(dynamic taskService)
    {
        dynamic rootFolder = taskService.GetFolder("\\");
        try
        {
            return rootFolder.GetFolder("win-optimizer");
        }
        catch (Exception ex)
        {
            logService.Log(Core.Features.Common.Enums.LogLevel.Debug, $"[ScheduledTaskService] win-optimizer folder doesn't exist, creating: {ex.Message}");
            return rootFolder.CreateFolder("win-optimizer");
        }
        finally
        {
            ReleaseComObject(rootFolder);
        }
    }

    private dynamic? GetWinOptimizerFolder(dynamic taskService)
    {
        dynamic? rootFolder = null;
        try
        {
            rootFolder = taskService.GetFolder("\\");
            return rootFolder.GetFolder("win-optimizer");
        }
        catch (Exception ex)
        {
            logService.Log(Core.Features.Common.Enums.LogLevel.Debug, $"[ScheduledTaskService] win-optimizer folder not found: {ex.Message}");
            return null;
        }
        finally
        {
            ReleaseComObject(rootFolder);
        }
    }

    private async Task RemoveExistingTask(dynamic folder, string taskName)
    {
        dynamic? existingTask = null;
        try
        {
            existingTask = folder.GetTask(taskName);
            if (existingTask != null)
            {
                folder.DeleteTask(taskName, 0);
                logService.LogInformation($"Deleted existing task: {taskName}");

                // Wait 2 seconds for Windows scheduled task cache to reset
                await Task.Delay(2000).ConfigureAwait(false);
                logService.LogInformation("Waited 2 seconds for task cache reset");
            }
        }
        catch (Exception ex)
        {
            logService.Log(Core.Features.Common.Enums.LogLevel.Debug, $"[ScheduledTaskService] No existing task '{taskName}' to remove: {ex.Message}");
        }
        finally
        {
            ReleaseComObject(existingTask);
        }
    }


    private dynamic CreateTaskDefinition(dynamic taskService, string scriptPath, string command, string username, TaskTriggerType triggerType)
    {
        var taskDefinition = taskService.NewTask(0);

        dynamic? settings = null;
        dynamic? triggers = null;
        dynamic? trigger = null;
        dynamic? actions = null;
        dynamic? action = null;
        dynamic? principal = null;
        try
        {
            // Settings
            settings = taskDefinition.Settings;
            settings.Enabled = true;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.AllowDemandStart = true;

            // Trigger
            triggers = taskDefinition.Triggers;
            trigger = triggers.Create((int)triggerType);
            trigger.Enabled = true;

            if (triggerType == TaskTriggerType.Logon && !string.IsNullOrEmpty(username))
            {
                trigger.UserId = username;
            }

            // Action
            actions = taskDefinition.Actions;
            action = actions.Create(0); // TASK_ACTION_EXEC
            action.Path = "powershell.exe";
            action.Arguments = scriptPath != null
                ? $"-ExecutionPolicy Bypass -NoProfile -Command \"iex([IO.File]::ReadAllText('{scriptPath.Replace("'", "''")}'))\""
                : command;

            // Principal
            principal = taskDefinition.Principal;
            if (!string.IsNullOrEmpty(username))
            {
                principal.UserId = username;
                principal.LogonType = 5; // Run whether logged in or not
                principal.RunLevel = 1; // Highest privileges
            }
            else
            {
                principal.UserId = "SYSTEM";
                principal.LogonType = 5;
                principal.RunLevel = 1;
            }

            return taskDefinition;
        }
        finally
        {
            ReleaseComObject(principal);
            ReleaseComObject(action);
            ReleaseComObject(actions);
            ReleaseComObject(trigger);
            ReleaseComObject(triggers);
            ReleaseComObject(settings);
        }
    }


    public async Task<OperationResult> EnableTaskAsync(string taskPath)
    {
        return await Task.Run(() => SetTaskEnabled(taskPath, true)).ConfigureAwait(false);
    }

    public async Task<OperationResult> DisableTaskAsync(string taskPath)
    {
        return await Task.Run(() => SetTaskEnabled(taskPath, false)).ConfigureAwait(false);
    }

    public async Task<bool?> IsTaskEnabledAsync(string taskPath)
    {
        return await Task.Run(() =>
        {
            dynamic? taskService = null;
            dynamic? folder = null;
            dynamic? task = null;
            try
            {
                taskService = _createTaskService();
                var (folderPath, taskName) = SplitTaskPath(taskPath);
                folder = taskService.GetFolder(folderPath);
                task = folder.GetTask(taskName);
                // State: 1 = Disabled, 3 = Ready, 4 = Running
                int state = (int)task.State;
                return (bool?)(state != 1);
            }
            catch (Exception ex)
            {
                logService.Log(Core.Features.Common.Enums.LogLevel.Warning,
                    $"Failed to query task state for {taskPath}: {ex.Message}");
                return null;
            }
            finally
            {
                ReleaseComObject(task);
                ReleaseComObject(folder);
                ReleaseComObject(taskService);
            }
        }).ConfigureAwait(false);
    }

    private OperationResult SetTaskEnabled(string taskPath, bool enabled)
    {
        dynamic? taskService = null;
        dynamic? folder = null;
        dynamic? task = null;
        try
        {
            taskService = _createTaskService();
            var (folderPath, taskName) = SplitTaskPath(taskPath);
            folder = taskService.GetFolder(folderPath);
            task = folder.GetTask(taskName);
            task.Enabled = enabled;
            logService.LogInformation($"{(enabled ? "Enabled" : "Disabled")} task: {taskPath}");
            return OperationResult.Succeeded();
        }
        catch (Exception ex)
        {
            logService.Log(Core.Features.Common.Enums.LogLevel.Warning,
                $"Failed to {(enabled ? "enable" : "disable")} task {taskPath}: {ex.Message}");
            return OperationResult.Failed(ex.Message, ex);
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(taskService);
        }
    }

    private static (string FolderPath, string TaskName) SplitTaskPath(string taskPath)
    {
        var lastSep = taskPath.LastIndexOf('\\');
        if (lastSep <= 0)
            return ("\\", taskPath.TrimStart('\\'));

        return (taskPath.Substring(0, lastSep), taskPath.Substring(lastSep + 1));
    }

    private void EnsureScriptFileExists(RemovalScript script)
    {
        if (!fileSystemService.FileExists(script.ActualScriptPath!) && !string.IsNullOrEmpty(script.Content))
        {
            string? directoryPath = fileSystemService.GetDirectoryName(script.ActualScriptPath!);
            if (directoryPath != null && !fileSystemService.DirectoryExists(directoryPath))
            {
                fileSystemService.CreateDirectory(directoryPath);
            }

            fileSystemService.WriteAllText(script.ActualScriptPath!, script.Content);
        }
    }

}
