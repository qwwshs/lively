using Lively.Common.Helpers;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace Lively.Helpers
{
    public static class WindowsStartup
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private static readonly object StartupLock = new();

        public async static Task<StartupTaskState> TrySetStartup(bool isStartWithWindow, bool highPriority)
        {
            var result = StartupTaskState.Disabled;
            try
            {
                if (PackageUtil.IsRunningAsPackaged)
                    result = await SetStartupTask(isStartWithWindow);
                else
                {
                    lock (StartupLock)
                    {
                        var useScheduledStartup = isStartWithWindow && highPriority;
                        try
                        {
                            SetScheduledStartup(useScheduledStartup);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Failed to update the high-priority logon task. Using the standard startup setting.");
                            useScheduledStartup = false;
                        }
                        SetStartupRegistry(isStartWithWindow && !useScheduledStartup);
                        result = isStartWithWindow ? StartupTaskState.Enabled : StartupTaskState.Disabled;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to update Windows startup.");
            }
            return result;
        }

        private static void SetScheduledStartup(bool enabled)
        {
            const int logonTrigger = 9;
            const int executeAction = 0;
            const int interactiveToken = 3;
            const int createOrUpdate = 6;
            const int aboveNormalPriority = 3;
            const int fileNotFound = unchecked((int)0x80070002);

            using var identity = WindowsIdentity.GetCurrent();
            var userSid = identity.User.Value;
            var taskName = $"Lively Wallpaper - {userSid}";
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true));
            service.Connect();
            dynamic folder = service.GetFolder(@"\");
            if (!enabled)
            {
                try
                {
                    folder.DeleteTask(taskName, 0);
                }
                catch (COMException ex) when (ex.HResult == fileNotFound)
                {
                }
                return;
            }

            dynamic task = service.NewTask(0);
            task.RegistrationInfo.Description = "Start Lively Wallpaper at sign-in.";
            task.Principal.UserId = userSid;
            task.Principal.LogonType = interactiveToken;
            task.Settings.Priority = aboveNormalPriority;
            task.Settings.ExecutionTimeLimit = "PT0S";
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Triggers.Create(logonTrigger).UserId = userSid;
            dynamic action = task.Actions.Create(executeAction);
            action.Path = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
            action.WorkingDirectory = Path.GetDirectoryName((string)action.Path);
            folder.RegisterTaskDefinition(taskName, task, createOrUpdate, userSid, null, interactiveToken, null);
        }

        /// <summary>
        /// Adds startup entry in registry under application name "livelywpf", current user ONLY. (Does not require admin rights).
        /// </summary>
        /// <param name="isStartWithWindows">Add or delete entry.</param>
        private static void SetStartupRegistry(bool isStartWithWindows = false)
        {
            Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            Assembly curAssembly = Assembly.GetExecutingAssembly();
            try
            {
                if (isStartWithWindows)
                {
                    key.SetValue(curAssembly.GetName().Name, "\"" + Path.ChangeExtension(curAssembly.Location, ".exe") + "\"");
                }
                else
                {
                    key.DeleteValue(curAssembly.GetName().Name, false);
                }
            }
            finally
            {
                key.Close();
            }
        }

        //ref: https://docs.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask?view=winrt-19041
        private async static Task<StartupTaskState> SetStartupTask(bool isStartWithWindows = false)
        {
            // Pass the task ID you specified in the appxmanifest file
            StartupTask startupTask = await StartupTask.GetAsync("AppStartup");
            var startupState = startupTask.State;
            switch (startupState)
            {
                case StartupTaskState.Disabled:
                    {
                        // Task is disabled but can be enabled.
                        // ensure that you are on a UI thread when you call RequestEnableAsync()
                        if (isStartWithWindows)
                            startupState = await startupTask.RequestEnableAsync();
                    }
                    break;
                case StartupTaskState.DisabledByUser:
                    {
                        // Task is disabled and user must enable it manually.
                    }
                    break;
                case StartupTaskState.DisabledByPolicy:
                    {
                        // Startup disabled by group policy, or not supported on this device.
                    }
                    break;
                case StartupTaskState.Enabled:
                    {
                        if (!isStartWithWindows)
                            startupTask.Disable();
                    }
                    break;
                    default:
                    {
                        if (isStartWithWindows)
                            startupState = await startupTask.RequestEnableAsync();
                    }
                    break;
            }
            return startupState;
        }
    }
}
