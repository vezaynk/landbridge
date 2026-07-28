namespace Docket.Contracts;

/// <summary>
/// A point-in-time reading of the three signals §10 says docketd observes:
/// load, memory, disk. Ratios in [0, 1]. Lives in <c>Docket.Contracts</c>
/// because it rides the machine heartbeat over the wire; the readers and the
/// back-pressure monitor that produce it stay runner-local.
/// </summary>
public readonly record struct SystemLoad(double CpuLoad, double MemoryLoad, double DiskUsage);
