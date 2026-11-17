using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace CraftMeThis;

[Serializable]
public sealed class JobPref
{
    public uint JobId { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed record JobInfo(uint Id, string Abbr, string Name, byte Role, int ExpIndex)
{
    /// <summary>Framed job icon.</summary>
    public uint Icon => 62100 + Id;
}

/// <summary>Combat jobs, their levels and gearsets; picks the first job in the user's priority order that can farm a zone.</summary>
public sealed class Jobs
{
    private readonly Dictionary<uint, JobInfo> byId;

    public Jobs(ExcelSheet<ClassJob> sheet)
    {
        // Disciples of War and Magic jobs (not base classes, not Blue Mage).
        byId = sheet.Where(j => j.JobIndex > 0 && !j.IsLimitedJob && j.ClassJobCategory.RowId is 30 or 31 && j.ExpArrayIndex >= 0)
                    .ToDictionary(j => j.RowId, j => new JobInfo(j.RowId, j.Abbreviation.ExtractText(), j.Name.ExtractText(), j.Role, j.ExpArrayIndex));
    }

    public JobInfo? Get(uint id) => byId.GetValueOrDefault(id);

    /// <summary>Keeps the user's order, drops unknown jobs and appends new ones (tanks, then healers, then DPS).</summary>
    public void Normalize(List<JobPref> prefs)
    {
        prefs.RemoveAll(p => !byId.ContainsKey(p.JobId));
        foreach (var j in byId.Values.OrderBy(j => j.Role switch { 1 => 0, 4 => 1, _ => 2 }).ThenBy(j => j.Id))
            if (prefs.All(p => p.JobId != j.Id)) prefs.Add(new JobPref { JobId = j.Id });
    }

    public static unsafe int Level(JobInfo job)
    {
        var ps = PlayerState.Instance();
        if (ps == null || job.ExpIndex < 0 || job.ExpIndex >= ps->ClassJobLevels.Length) return 0;
        return ps->ClassJobLevels[job.ExpIndex];
    }

    public static unsafe uint CurrentJob()
    {
        var ps = PlayerState.Instance();
        return ps == null ? 0u : (uint)ps->CurrentClassJobId;
    }

    /// <returns>Gearset index for the job, or -1.</returns>
    public static unsafe int Gearset(uint jobId)
    {
        var gm = RaptureGearsetModule.Instance();
        if (gm == null) return -1;
        for (var i = 0; i < 100; i++)
        {
            if (!gm->IsValidGearset(i)) continue;
            var gs = gm->GetGearset(i);
            if (gs != null && gs->ClassJob == jobId) return i;
        }
        return -1;
    }

    public static unsafe bool Equip(int gearset)
    {
        var gm = RaptureGearsetModule.Instance();
        return gm != null && gm->EquipGearset(gearset) == 0;
    }

    /// <summary>First enabled job, in priority order, that has a gearset and the level for the zone.</summary>
    public (JobInfo Job, int Gearset, int Level)? Pick(IEnumerable<JobPref> prefs, int minLevel)
    {
        foreach (var p in prefs)
        {
            if (!p.Enabled || !byId.TryGetValue(p.JobId, out var job)) continue;
            var lvl = Level(job);
            if (lvl < minLevel) continue;
            var gs = Gearset(job.Id);
            if (gs < 0) continue;
            return (job, gs, lvl);
        }
        return null;
    }
}
