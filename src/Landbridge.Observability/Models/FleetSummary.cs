namespace Landbridge.Observability.Models;

/// <summary>Header-strip roll-up: counts by state plus fleet-wide relay stats.</summary>
public sealed class FleetSummary
{
    public int Working { get; set; }
    public int Waiting { get; set; }
    public int Failed { get; set; }
    public int Submitted { get; set; }
    public int MachineCount { get; set; }
    public int ForwardsOpen { get; set; }
    public int RelayMb { get; set; }
}
