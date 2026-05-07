namespace OilAutoService.Configuration;

public class ServiceSettings
{
    public int CheckIntervalMinutes { get; set; } = 5;
    public int MaxParallelMachines { get; set; } = 4;
}

public class MachineConfig
{
    public string Name { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;
}
