using redb.Core.Attributes;

namespace redb.Route.Demo;

/// <summary>
/// Simple Props model for CRUD demo via named IRedbService.
/// </summary>
[RedbScheme("Demo Item")]
public class DemoItemProps
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int Priority { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
