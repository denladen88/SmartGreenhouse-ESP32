using Microsoft.EntityFrameworkCore;

namespace SmartGreenhouse.Backend.Models;

// Persistent hysteresis state for the local controller. Crossing a profile
// boundary starts a correction cycle; the cycle remains active until the
// measured value reaches the midpoint of that profile range. Keeping this in
// SQLite makes the behavior survive backend restarts and keeps state isolated
// when the current planting changes.
[Index(nameof(PlantName), IsUnique = true)]
public class ControlRecoveryState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PlantName { get; set; } = string.Empty;

    public bool AirHeatingActive { get; set; }
    public bool AirCoolingActive { get; set; }
    public bool AirDryingActive { get; set; }
    public bool SoilHeatingActive { get; set; }
    public bool SoilWateringActive { get; set; }
    public bool SoilDryingActive { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
