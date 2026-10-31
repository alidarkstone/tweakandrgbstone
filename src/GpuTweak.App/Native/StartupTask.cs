using System.IO;
using System.Security;
using System.Security.Principal;

namespace GpuTweak.App.Native;

/// <summary>
/// Starts tweakandrgbstone at Windows sign-in. A Task Scheduler task is used instead of the Run registry key,
/// because the app needs administrator rights and Run-key entries are not allowed to elevate.
/// The app never registers the task itself: an unsigned exe that creates an elevated logon task for itself is what
/// Defender reports as Behavior:Win32/Persistence.A!ml, whether it uses schtasks.exe or the Task Scheduler API.
/// It writes the task definition next to its settings and the user imports it once from an administrator terminal.
/// Reading and deleting tasks is not flagged and stays here.
/// </summary>
internal static class StartupTask
{
    private const string TaskName = "tweakandrgbstone";
    /// <summary>Tasks of the separate apps this one replaces: Tweakstone (and its first name, GPU Tweak) and rgbstone.</summary>
    private static readonly string[] LegacyTaskNames = { "Tweakstone", "GPU Tweak", "rgbstone" };
    public const string StartupArgument = "--startup";

    private static dynamic RootFolder()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Görev Zamanlayıcı bulunamadı.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service.GetFolder("\\");
    }

    private static dynamic? Find(string name)
    {
        try { return RootFolder().GetTask(name); } catch { return null; }
    }

    public static bool IsEnabled() => Find(TaskName) is not null;

    /// <summary>Writes the task definition for this exe and returns the command that creates it from an administrator terminal.</summary>
    public static string WriteDefinition(string dataDirectory)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Exe yolu bulunamadı.");
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Kullanıcı bulunamadı.");
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>tweakandrgbstone: Windows ile başlat</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>{StartupArgument}</Arguments></Exec></Actions>
            </Task>
            """;
        var path = Path.Combine(dataDirectory, "windows-ile-baslat.xml");
        File.WriteAllText(path, xml, System.Text.Encoding.Unicode);
        return $"schtasks /Create /F /TN {TaskName} /XML \"{path}\"";
    }

    /// <summary>The exe the existing task starts, or null when there is no task or it cannot be read.</summary>
    public static string? TargetPath()
    {
        try
        {
            var task = Find(TaskName);
            if (task is null) return null;
            return ((string)task.Definition.Actions.Item(1).Path).Trim('"');
        }
        catch { }
        return null;
    }

    public static bool Disable() => Delete(TaskName) || !IsEnabled();

    /// <summary>Old Tweakstone / rgbstone start-up tasks that still exist. Running them next to this app would fight over the card and the LEDs.</summary>
    public static List<string> LegacyTasks() => LegacyTaskNames.Where(n => Find(n) is not null).ToList();

    public static void RemoveLegacyTasks()
    {
        foreach (var name in LegacyTaskNames) Delete(name);
    }

    private static bool Delete(string name)
    {
        try { RootFolder().DeleteTask(name, 0); return true; } catch { return false; }
    }
}
