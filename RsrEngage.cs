using System;
using System.IO;
using System.Text.Json;

namespace CraftMeThis;

/// <summary>
/// Hunter never attacks by itself: it dismounts, sends "/rsr auto", targets the mob and waits for Rotation Solver
/// Reborn to start the fight. RSR's per-job "Engage settings" (TargetHostileType) decide whether it will pull a mob that
/// is not fighting yet; its default for non-tanks, AllTargetsWhenSoloInDuty, only pulls inside duties, so in the open
/// world the character just stands next to the mob. For the mob farm the farm job is set to AllTargetsCanAttack with
/// "/rotation Settings HostileType …" and the old value (from RotationSolver.json, _hostileTypeDict) is put back afterwards.
/// </summary>
public sealed class RsrEngage
{
    private static readonly string[] Names = { "AllTargetsCanAttack", "TargetsHaveTarget", "AllTargetsWhenSoloInDuty", "AllTargetsWhenSolo", "SoloDeepDungeonSmart" };
    private const int RsrDefault = 2; // AllTargetsWhenSoloInDuty

    private string? job;
    private int previous;

    public bool Active => job != null;

    /// <summary>Reads the job's current Engage setting from RSR's config file.</summary>
    private static int Current(string jobAbbr)
    {
        try
        {
            var path = Path.Combine(Plugin.Pi.ConfigDirectory.Parent!.FullName, "RotationSolver.json");
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.TryGetProperty("_hostileTypeDict", out var dict) && dict.ValueKind == JsonValueKind.Object
                && dict.TryGetProperty(jobAbbr, out var v) && v.TryGetInt32(out var n))
                return n;
        }
        catch
        {
            // unreadable: assume RSR's default
        }
        return RsrDefault;
    }

    /// <summary>Lets RSR pull fresh mobs for the current job. Returns a chat note, or null when nothing changed.</summary>
    public string? Allow(string jobAbbr)
    {
        if (!Plugin.IsLoaded("RotationSolver") || jobAbbr.Length == 0) return null;
        var cur = Current(jobAbbr);
        if (cur == 0) return null; // already "all targets in range"
        Plugin.Commands.ProcessCommand($"/rotation Settings HostileType {Names[0]}");
        job = jobAbbr;
        previous = cur;
        return $"RSR для {jobAbbr}: Engage = все цели в радиусе (было {Name(cur)}), чтобы он начинал бой с мобом сам.";
    }

    /// <summary>Puts the job's Engage setting back (only while that job is equipped, since the command acts on it).</summary>
    public string? Restore(string currentJobAbbr)
    {
        if (job == null) return null;
        var j = job;
        job = null;
        if (!Plugin.IsLoaded("RotationSolver")) return null;
        if (!string.Equals(currentJobAbbr, j, StringComparison.OrdinalIgnoreCase))
            return $"Не вернул RSR Engage для {j} (сейчас другой класс) — верни сам: «{Name(previous)}».";
        Plugin.Commands.ProcessCommand($"/rotation Settings HostileType {Name(previous)}");
        return null;
    }

    private static string Name(int v) => v >= 0 && v < Names.Length ? Names[v] : Names[RsrDefault];
}
