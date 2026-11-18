using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Plugin;

namespace CraftMeThis;

/// <summary>A plugin CraftMeThis works with: where it comes from, what for, and what to set up in it.</summary>
public sealed record Dependency(string Name, string InternalName, string? Repo, string UsedFor, bool Optional, string[] Notes);

/// <summary>
/// The Dependencies tab: plugin status, adding custom repositories and installing plugins. Dalamud has no public API for
/// this, so it goes through its internals by reflection, the way its own installer does it:
/// Service&lt;DalamudConfiguration&gt;.ThirdRepoList (+ QueueSave), Service&lt;PluginManager&gt;.SetPluginReposFromConfigAsync,
/// AvailablePlugins and InstallPluginAsync(manifest, useTesting: false, PluginLoadReason.Installer).
/// Every action starts only from the user's own click in the window.
/// </summary>
public sealed class Dependencies
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string CombatReborn = "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json";

    public static readonly Dependency[] All =
    {
        new("GatherBuddy Reborn (Vulcan)", "GatherBuddyReborn", CombatReborn, "покупки у вендоров, сбор, крафт", false, new[]
        {
            "Vulcan крафтит своим решателем; Artisan CraftMeThis выключает/включает сам (настройка «Выключать Artisan»).",
            "Для «Брать материалы у ретейнеров» нужен Allagan Tools.",
            "Если игра просит подтверждения при покупках — помогает YesAlready.",
        }),
        new("Auto FATE Grind", "AutoFateGrind", "https://grindless.pages.dev/repo.json", "фарм биколоров (FATE), автоуровень до 15", false, new[]
        {
            "Зону, фильтр уровней, свои покупки и очередь классов CraftMeThis ставит на время запуска и потом возвращает.",
            "Бой и уклонение — через BossMod Reborn, его пресет «Auto FATE Grind» AFG создаёт сам.",
            "Для автоуровня 1–14: выбери зоны в окне /afg.",
        }),
        new("AutoDuty", "AutoDuty", "https://puni.sh/api/repository/erdelf", "печати GC (данжи + сдача), автоуровень 15+", false, new[]
        {
            "Сдача лута в GC (/autoduty gcturnin) требует ранг GC 6+ и AutoRetainer.",
            "Режим прокачки: Support до 70, с 71 можно Trust (Trust_Solo/Trust_Group) — выбирается в настройках CraftMeThis.",
            "Нужен vnavmesh и BossMod Reborn.",
        }),
        new("Hunter", "HunterV2", "https://raw.githubusercontent.com/t3knical/DalamudPlugins/main/repo.json", "фарм дропа с мобов", false, new[]
        {
            "Сам не бьёт: включает /rsr auto и ждёт. CraftMeThis на время фарма ставит в RSR Engage = «все цели в радиусе».",
            "Нужны vnavmesh, Rotation Solver Reborn и Lifestream.",
            "Свой список «CraftMeThis» CraftMeThis создаёт сам; твои списки на время запуска выключаются и потом возвращаются.",
        }),
        new("Rotation Solver Reborn", "RotationSolver", CombatReborn, "бой для Hunter", false, new[]
        {
            "Target → Engage settings: по умолчанию «All targets when solo in duty» — в открытом мире свежих мобов не бьёт; CraftMeThis переключает на время фарма мобов.",
        }),
        new("BossMod Reborn", "BossModReborn", CombatReborn, "бой в AFG и AutoDuty", false, new[]
        {
            "AFG и AutoDuty управляют им сами через свои пресеты (IPC у Reborn тот же, что у BossMod).",
            "Не держи одновременно включённым обычный BossMod (Veyn) — они конфликтуют.",
        }),
        new("vnavmesh", "vnavmesh", "https://puni.sh/api/repository/veyn", "передвижение (Vulcan, AFG, AutoDuty, Hunter)", false, new[]
        {
            "После установки дождись построения навмеша в зоне (/vnav).",
        }),
        new("Lifestream", "Lifestream", "https://raw.githubusercontent.com/Eternita-S/MyDalamudPlugins/main/pluginmaster.json", "телепорты (Vulcan, Hunter)", false, new[]
        {
            "Hunter по окончании шлёт «/li thespot» — можно настроить свою точку возврата в Hunter.",
        }),
        new("AutoRetainer", "AutoRetainer", "https://love.puni.sh/ment.json", "сдача лута в GC через AutoDuty", false, new[]
        {
            "Для gcturnin в AutoDuty; отдельных настроек CraftMeThis не требует.",
        }),
        new("Allagan Tools", "InventoryTools", null, "материалы у ретейнеров (Vulcan)", true, new[]
        {
            "Из основного репозитория Dalamud. Нужен для «Restock from Retainers» в Vulcan.",
        }),
        new("TextAdvance", "TextAdvance", "https://github.com/NightmareXIV/MyDalamudPlugins/raw/main/pluginmaster.json", "диалоги с NPC (AFG, квесты)", true, new[]
        {
            "Включи автопропуск диалогов, иначе торговцы и квесты встают на окнах.",
        }),
        new("Questionable", "WigglyQuest", "https://aethertek.io/x.json", "джоб-квесты в автоуровне (beta)", true, new[]
        {
            "Выключи «Run AutoDuty Leveling mode when underleveled for MSQ», иначе он сам уходит качаться под MSQ.",
            "Команды /qst start и /qst stop; некоторые джоб-квесты он проходит не до конца — автоуровень помечен beta.",
        }),
        new("Artisan", "Artisan", "https://love.puni.sh/ment.json", "не нужен (Vulcan крафтит сам)", true, new[]
        {
            "Vulcan не крафтит, пока Artisan загружен; CraftMeThis выключает его на время крафта.",
        }),
    };

    public string Message { get; private set; } = "";
    public bool Busy { get; private set; }

    // ---------------------------------------------------------------- status

    public enum State { Loaded, Disabled, Missing }

    public static State StatusOf(Dependency d)
    {
        var p = Plugin.Pi.InstalledPlugins.FirstOrDefault(x => x.InternalName == d.InternalName);
        return p == null ? State.Missing : p.IsLoaded ? State.Loaded : State.Disabled;
    }

    private static Assembly DalamudAsm => typeof(IDalamudPluginInterface).Assembly;

    private static object Service(string typeName)
    {
        var t = DalamudAsm.GetType(typeName, true)!;
        var svc = DalamudAsm.GetType("Dalamud.Service`1", true)!.MakeGenericType(t);
        return svc.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
    }

    private static object Config() => Service("Dalamud.Configuration.Internal.DalamudConfiguration");
    private static object Manager() => Service("Dalamud.Plugin.Internal.PluginManager");

    private static IList RepoList() => (IList)Config().GetType().GetProperty("ThirdRepoList", Any)!.GetValue(Config())!;

    public static bool HasRepo(string url)
    {
        try
        {
            foreach (var r in RepoList())
                if (string.Equals((r.GetType().GetProperty("Url", Any)?.GetValue(r) as string)?.Trim(), url.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch
        {
            // unknown Dalamud layout: show the button anyway
        }
        return false;
    }

    // ---------------------------------------------------------------- actions (user clicks only)

    /// <summary>Adds the custom repository (enabled) and reloads the plugin lists.</summary>
    public void AddRepo(Dependency d)
    {
        if (d.Repo == null || Busy) return;
        try
        {
            var list = RepoList();
            if (!HasRepo(d.Repo))
            {
                var t = list.GetType().GetGenericArguments()[0];
                var repo = Activator.CreateInstance(t)!;
                t.GetProperty("Url", Any)!.SetValue(repo, d.Repo);
                t.GetProperty("IsEnabled", Any)?.SetValue(repo, true);
                t.GetProperty("Name", Any)?.SetValue(repo, d.Name);
                list.Add(repo);
                Config().GetType().GetMethod("QueueSave", Any, Type.EmptyTypes)?.Invoke(Config(), null);
            }
            Run($"Добавляю репозиторий {d.Repo}…", async () =>
            {
                await ReloadRepos();
                return $"Репозиторий для {d.Name} добавлен.";
            });
        }
        catch (Exception ex)
        {
            Message = $"Не удалось добавить репозиторий: {Inner(ex)}";
        }
    }

    /// <summary>Installs the plugin from the loaded plugin lists (adds its repository first if needed).</summary>
    public void Install(Dependency d)
    {
        if (Busy) return;
        Run($"Устанавливаю {d.Name}…", async () =>
        {
            if (d.Repo != null && !HasRepo(d.Repo))
            {
                var list = RepoList();
                var t = list.GetType().GetGenericArguments()[0];
                var repo = Activator.CreateInstance(t)!;
                t.GetProperty("Url", Any)!.SetValue(repo, d.Repo);
                t.GetProperty("IsEnabled", Any)?.SetValue(repo, true);
                t.GetProperty("Name", Any)?.SetValue(repo, d.Name);
                list.Add(repo);
                Config().GetType().GetMethod("QueueSave", Any, Type.EmptyTypes)?.Invoke(Config(), null);
            }
            await ReloadRepos();
            var pm = Manager();
            object? manifest = null;
            for (var i = 0; i < 30 && manifest == null; i++)
            {
                var available = (IEnumerable)pm.GetType().GetProperty("AvailablePlugins", Any)!.GetValue(pm)!;
                manifest = available.Cast<object>().FirstOrDefault(m => m.GetType().GetProperty("InternalName", Any)?.GetValue(m) as string == d.InternalName);
                if (manifest == null) await Task.Delay(1000);
            }
            if (manifest == null) return $"{d.Name} не найден в списках плагинов — проверь, что репозиторий включён в /xlsettings.";

            var install = pm.GetType().GetMethod("InstallPluginAsync", Any)!;
            var args = install.GetParameters().Select<ParameterInfo, object?>(par =>
                par.ParameterType.IsEnum ? Enum.Parse(par.ParameterType, "Installer")
                : par.ParameterType == typeof(bool) ? false
                : par.Position == 0 ? manifest
                : par.HasDefaultValue ? par.DefaultValue : null).ToArray();
            if (install.Invoke(pm, args) is Task task) await task;
            return $"{d.Name} установлен.";
        });
    }

    private static async Task ReloadRepos()
    {
        var pm = Manager();
        if (pm.GetType().GetMethod("SetPluginReposFromConfigAsync", Any)?.Invoke(pm, new object[] { true }) is Task t) await t;
    }

    private void Run(string start, Func<Task<string>> work)
    {
        Busy = true;
        Message = start;
        Task.Run(async () =>
        {
            string result;
            try { result = await work(); }
            catch (Exception ex) { result = $"Ошибка: {Inner(ex)}"; }
            await Plugin.Framework.RunOnFrameworkThread(() => { Message = result; Busy = false; });
        });
    }

    private static string Inner(Exception ex) => ((ex as TargetInvocationException)?.InnerException ?? ex.InnerException ?? ex).Message;
}
