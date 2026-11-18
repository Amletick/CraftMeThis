using System;
using System.Collections.Generic;

namespace CraftMeThis;

/// <summary>One line of a run's log: when, in which phase, what happened.</summary>
[Serializable]
public sealed class RunEvent
{
    public DateTime Time { get; set; }
    public string Phase { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>One run of the pipeline, kept in the History tab.</summary>
[Serializable]
public sealed class RunRecord
{
    public DateTime Started { get; set; }
    public DateTime? Ended { get; set; }
    /// <summary>running / done / failed / stopped.</summary>
    public string Result { get; set; } = "running";
    public List<string> Cart { get; set; } = new();
    public string Summary { get; set; } = "";
    public List<RunEvent> Events { get; set; } = new();
}

/// <summary>A cart saved under a name (Cart tab).</summary>
[Serializable]
public sealed class SavedCart
{
    public string Name { get; set; } = "";
    public DateTime Saved { get; set; }
    public List<CartEntry> Cart { get; set; } = new();
    public string TeamcraftListUrl { get; set; } = "";
    public List<string> ImportManual { get; set; } = new();
}
