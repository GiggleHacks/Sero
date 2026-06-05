using System.Text.Json;
using Microsoft.Win32;

namespace SeroStub;

internal static class StartupManagerFeature
{
    internal static string GetList()
    {
        var entries = new List<StartupEntryStub>();

        // HKCU Run
        AddRegEntries(entries, Registry.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Reg", "HKCU\\Run");
        // HKLM Run
        AddRegEntries(entries, Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Reg", "HKLM\\Run");

        // Startup folders
        AddStartupFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "File", "User Startup");
        AddStartupFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "File", "Common Startup");

        // Scheduled tasks — only non-system ones (skip \Microsoft\, machine-name tasks, Windows system paths)
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks", "/query /fo CSV /nh /v")
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p != null)
            {
                var csv = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                var hostname = Environment.MachineName;
                foreach (var line in csv.Split('\n'))
                {
                    var cols = SplitCsv(line);
                    if (cols.Length < 9) continue;
                    var status = cols[3].Trim('"');
                    if (status == "Disabled") continue;
                    var name = cols[0].Trim('"');
                    if (name.StartsWith("\\Microsoft\\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.TrimStart('\\').StartsWith(hostname, StringComparison.OrdinalIgnoreCase)) continue;
                    var action = cols[8].Trim('"');
                    if (string.IsNullOrWhiteSpace(action) || action == "N/A") continue;
                    var al = action.ToLowerInvariant();
                    if (al.StartsWith(@"c:\windows\system32\") || al.StartsWith(@"c:\windows\syswow64\")) continue;
                    if (al.StartsWith("%systemroot%\\") || al.StartsWith("%windir%\\")) continue;
                    entries.Add(new StartupEntryStub
                    {
                        Name = name.TrimStart('\\'),
                        Path = action,
                        Type = "Task",
                        Location = "Task Scheduler"
                    });
                }
            }
        }
        catch { }

        return JsonSerializer.Serialize(new StartupListResultStub { Entries = entries }, SeroJson.Default.StartupListResultStub);
    }

    internal static void Delete(string name, string type, string location)
    {
        try
        {
            switch (type)
            {
                case "Reg":
                    RegistryHive hive = location.StartsWith("HKLM") ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                    string subKey = location.Contains("RunOnce")
                        ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"
                        : @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                    using (var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey, true))
                        key?.DeleteValue(name, false);
                    break;

                case "File":
                    var startupDirs = new[]
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                    };
                    foreach (var dir in startupDirs)
                    {
                        var file = Path.Combine(dir, name);
                        if (File.Exists(file)) { File.Delete(file); break; }
                    }
                    break;

                case "Task":
                    var psi = new System.Diagnostics.ProcessStartInfo("schtasks",
                        $"/delete /tn \"{name}\" /f")
                    { CreateNoWindow = true, UseShellExecute = false };
                    using (var p = System.Diagnostics.Process.Start(psi)) p?.WaitForExit(5000);
                    break;
            }
        }
        catch { }
    }

    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        bool inQuotes = false;
        var cur = new System.Text.StringBuilder();
        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; }
            else if (c == ',' && !inQuotes) { fields.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        fields.Add(cur.ToString());
        return [.. fields];
    }

    private static void AddRegEntries(List<StartupEntryStub> list, RegistryKey root, string keyPath, string type, string location)
    {
        try
        {
            using var key = root.OpenSubKey(keyPath);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                var val = key.GetValue(name)?.ToString() ?? "";
                list.Add(new StartupEntryStub { Name = name, Path = val, Type = type, Location = location });
            }
        }
        catch { }
    }

    private static void AddStartupFolder(List<StartupEntryStub> list, string dir, string type, string location)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir))
            {
                list.Add(new StartupEntryStub
                {
                    Name = Path.GetFileName(file),
                    Path = file,
                    Type = type,
                    Location = location
                });
            }
        }
        catch { }
    }


}

internal class StartupEntryStub   { public string Name { get; set; } = ""; public string Path { get; set; } = ""; public string Type { get; set; } = ""; public string Location { get; set; } = ""; }
internal class StartupListResultStub { public List<StartupEntryStub> Entries { get; set; } = []; }
internal class StartupDeleteDataStub { public string Name { get; set; } = ""; public string Type { get; set; } = ""; public string Location { get; set; } = ""; }
