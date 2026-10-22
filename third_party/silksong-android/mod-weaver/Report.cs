using System.Text.Json.Serialization;

namespace ModWeaver;

/// <summary>
/// How a plugin fared. The launcher shows this; nothing else depends on it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum PluginStatus
{
    /// Every patch it declares was woven in.
    Ok,

    /// It is in the build, but something it asked for could not be honoured.
    /// The plugin still loads and its other patches still apply.
    Partial,

    /// Not in the build at all. Nothing about the game changed.
    Failed,
}

/// <summary>
/// One plugin's outcome, as the launcher displays it.
///
/// Written even for a plugin that failed, and that is the point of writing it
/// at all: the alternative is a mod that quietly does nothing after a
/// twenty-minute build, with no way to tell that from a mod that is working
/// but subtle.
/// </summary>
internal sealed class PluginReport
{
    public string File { get; init; } = "";
    public string Assembly { get; init; } = "";

    /// From [BepInPlugin]. Empty when the assembly declares no plugin -- which
    /// is normal for a mod's shared library, and is not on its own a problem.
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";

    public PluginStatus Status { get; set; } = PluginStatus.Ok;

    /// Harmony patches actually woven into the game's IL.
    public int Patched { get; set; }

    /// What could not be honoured, in the words the user needs to hear it in.
    public List<string> Issues { get; } = new();

    public void Note(string issue)
    {
        // Nothing is gained by telling someone the same thing eleven times
        // because their plugin patches eleven methods the same wrong way.
        if (!Issues.Contains(issue)) Issues.Add(issue);
        if (Status == PluginStatus.Ok) Status = PluginStatus.Partial;
    }

    public void Fail(string issue)
    {
        if (!Issues.Contains(issue)) Issues.Add(issue);
        Status = PluginStatus.Failed;
    }
}
