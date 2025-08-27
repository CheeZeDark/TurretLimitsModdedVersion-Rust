﻿using ConVar;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("Turret Limits", "Whispers88, gsuberland (modded by you)", "1.3.2")]
    [Description("Limits the number of autoturrets, flame turrets, and shotgun traps per building, with optional per-player overrides.")]
    class TurretLimits : RustPlugin
    {
        private const string AutoTurretPrefabString = "autoturret";
        private const string FlameTurretPrefabString = "flameturret";
        private const string ShotgunTrapPrefabString = "guntrap";
        private const string SamSitePrefabString = "sam_site_turret";

        private const string DataFileName = "TurretLimits_PlayerData";

        private struct Configuration
        {
            public static int AutoTurretLimit = 3;
            public static int FlameTurretLimit = 3;
            public static int ShotgunTrapLimit = 3;
            public static int SamSiteLimit = 3;

            public static bool DisableAllTurrets = false;
            public static bool AllowAdminBypass = false;
        }

        // Per-player override storage
        private class PlayerLimits
        {
            public int AutoTurret = -1;   // -1 = use default
            public int FlameTurret = -1;
            public int ShotgunTrap = -1;
        }

        private Dictionary<ulong, PlayerLimits> playerLimits = new Dictionary<ulong, PlayerLimits>();

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, playerLimits);
        }

        private void LoadData()
        {
            var loaded = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerLimits>>(DataFileName);
            playerLimits = loaded ?? new Dictionary<ulong, PlayerLimits>();
        }

        private void Init()
        {
            LoadConfig();
            LoadData();

            // keep original plugin behavior
            Sentry.interferenceradius = float.MinValue;
            Sentry.maxinterference = int.MaxValue;
            AutoTurret.interferenceUpdateList.Clear();
        }

        private void Unload()
        {
            SaveData();
        }

        private new void LoadConfig()
        {
            GetConfig(ref Configuration.DisableAllTurrets, "Config", "Disable All Turrets");
            GetConfig(ref Configuration.AllowAdminBypass, "Config", "Admin Can Bypass Build Restrictions");

            GetConfig(ref Configuration.AutoTurretLimit, "Limits", "Individual Control", "AutoTurret", "Maximum");
            GetConfig(ref Configuration.FlameTurretLimit, "Limits", "Individual Control", "Flame Turret", "Maximum");
            GetConfig(ref Configuration.ShotgunTrapLimit, "Limits", "Individual Control", "Shotgun Trap", "Maximum");
            GetConfig(ref Configuration.SamSiteLimit, "Limits", "Individual Control", "Sam Site Turret", "Maximum");

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Generating new config file...");
            LoadConfig();
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoAdminLimits"] = "Admins do not have turret limits enabled.",
                ["CannotDeployWithoutTC"] = "Cannot deploy turret without tool cupboard access.",
                ["TurretsDisabled"] = "Turrets are disabled on this server.",

                ["TurretLimitReached_AutoTurret"] = "Autoturret limit reached. You have already deployed {0} or more autoturrets in this base.",
                ["TurretLimitReached_FlameTurret"] = "Flame turret limit reached. You have already deployed {0} or more flame turrets in this base.",
                ["TurretLimitReached_ShotgunTrap"] = "Shotgun trap limit reached. You have already deployed {0} or more shotgun traps in this base.",
                ["TurretLimitReached_SamSite"] = "Sam Site limit reached. You have already deployed {0} or more SAM sites in this base.",

                ["CmdUsage"] = "Usage: /setlimit <autoturret|flameturret|shotguntrap> <number>",
                ["CmdOK"] = "Limit for {0} set to {1}.",
                ["CmdBadNum"] = "Invalid number.",
                ["CmdBadType"] = "Unknown turret type. Use: autoturret, flameturret, shotguntrap."
            }, this);
        }

        private void GetConfig<T>(ref T variable, params string[] path)
        {
            if (path == null || path.Length == 0) return;

            if (Config.Get(path) == null)
            {
                Config.Set(path.Concat(new object[] { variable }).ToArray());
                PrintWarning("Added field to config: " + string.Join("/", path));
            }
            variable = (T)Convert.ChangeType(Config.Get(path), typeof(T));
        }

        // Players can set personal limits (per building) that override the server defaults
        [ChatCommand("setlimit")]
        private void CmdSetLimit(BasePlayer player, string command, string[] args)
        {
            if (args == null || args.Length != 2)
            {
                SendReply(player, lang.GetMessage("CmdUsage", this, player != null ? player.UserIDString : "0"));
                return;
            }

            int newLimit;
            if (!int.TryParse(args[1], out newLimit) || newLimit < 0)
            {
                SendReply(player, lang.GetMessage("CmdBadNum", this, player != null ? player.UserIDString : "0"));
                return;
            }

            PlayerLimits limits;
            if (!playerLimits.TryGetValue(player.userID, out limits))
            {
                limits = new PlayerLimits();
                playerLimits[player.userID] = limits;
            }

            string type = args[0].ToLowerInvariant();
            switch (type)
            {
                case "autoturret": limits.AutoTurret = newLimit; break;
                case "flameturret": limits.FlameTurret = newLimit; break;
                case "shotguntrap": limits.ShotgunTrap = newLimit; break;
                default:
                    SendReply(player, lang.GetMessage("CmdBadType", this, player != null ? player.UserIDString : "0"));
                    return;
            }

            SaveData();
            SendReply(player, string.Format(lang.GetMessage("CmdOK", this, player != null ? player.UserIDString : "0"), type, newLimit));
        }

        object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            bool isAutoTurret = prefab != null && prefab.deployable != null && prefab.deployable.fullName != null && prefab.deployable.fullName.Contains(AutoTurretPrefabString);
            bool isFlameTurret = prefab != null && prefab.deployable != null && prefab.deployable.fullName != null && prefab.deployable.fullName.Contains(FlameTurretPrefabString);
            bool isShotgunTrap = prefab != null && prefab.deployable != null && prefab.deployable.fullName != null && prefab.deployable.fullName.Contains(ShotgunTrapPrefabString);
            bool isSamSite = prefab != null && prefab.deployable != null && prefab.deployable.fullName != null && prefab.deployable.fullName.Contains(SamSitePrefabString);

            if (!(isAutoTurret || isFlameTurret || isShotgunTrap || isSamSite))
                return null;

            int sanityCheck = (isAutoTurret ? 1 : 0) + (isFlameTurret ? 1 : 0) + (isShotgunTrap ? 1 : 0) + (isSamSite ? 1 : 0);
            if (sanityCheck != 1)
                throw new Exception("Somehow multiple turret types were detected.");

            var player = planner.GetOwnerPlayer();
            // NOTE: Construction.Target is a struct (value type) => cannot compare 'target == null'
            if (player == null || !player.IsBuildingAuthed() || target.entity == null || target.entity.GetBuildingPrivilege() == null)
            {
                if (player != null)
                    SendReply(player, lang.GetMessage("CannotDeployWithoutTC", this, player.UserIDString));
                return false;
            }

            var cupboard = target.entity.GetBuildingPrivilege();
            var building = cupboard.GetBuilding();

            if (Configuration.AllowAdminBypass && player.IsAdmin)
            {
                SendReply(player, lang.GetMessage("NoAdminLimits", this, player.UserIDString));
                return null;
            }

            if (Configuration.DisableAllTurrets)
            {
                SendReply(player, lang.GetMessage("TurretsDisabled", this, player.UserIDString));
                return null;
            }

            // Helper to fetch effective limit for this player (fallback to config default)
            Func<Func<PlayerLimits, int>, int, int> GetLimit = (selector, defaultLimit) =>
            {
                PlayerLimits limits;
                if (playerLimits.TryGetValue(player.userID, out limits))
                {
                    int v = selector(limits);
                    if (v >= 0) return v;
                }
                return defaultLimit;
            };

            if (isFlameTurret)
            {
                int flameturrets = building.decayEntities.Count(e => e is FlameTurret);
                int limit = GetLimit(l => l.FlameTurret, Configuration.FlameTurretLimit);
                if (flameturrets + 1 > limit)
                {
                    SendReply(player, lang.GetMessage("TurretLimitReached_FlameTurret", this, player.UserIDString), flameturrets);
                    return false;
                }
            }
            else if (isShotgunTrap)
            {
                int guntraps = building.decayEntities.Count(e => e is GunTrap);
                int limit = GetLimit(l => l.ShotgunTrap, Configuration.ShotgunTrapLimit);
                if (guntraps + 1 > limit)
                {
                    SendReply(player, lang.GetMessage("TurretLimitReached_ShotgunTrap", this, player.UserIDString), guntraps);
                    return false;
                }
            }
            else if (isAutoTurret)
            {
                int turrets = 0;
                var nearby = new List<BaseEntity>();
                Vis.Entities(player.transform.position, 30f, nearby, LayerMask.GetMask("Deployed"), QueryTriggerInteraction.Ignore);
                foreach (var ent in nearby.Distinct())
                {
                    if (ent is AutoTurret)
                    {
                        var bp = ent.GetBuildingPrivilege();
                        if (bp != null && bp.GetBuilding().ID == building.ID)
                            turrets++;
                    }
                }

                int limit = GetLimit(l => l.AutoTurret, Configuration.AutoTurretLimit);
                if (turrets >= limit)
                {
                    SendReply(player, lang.GetMessage("TurretLimitReached_AutoTurret", this, player.UserIDString), turrets);
                    return false;
                }
            }
            else if (isSamSite)
            {
                int samsites = 0;
                var nearby = new List<BaseEntity>();
                Vis.Entities(player.transform.position, 30f, nearby, LayerMask.GetMask("Deployed"), QueryTriggerInteraction.Ignore);
                foreach (var ent in nearby.Distinct())
                {
                    if (ent is SamSite)
                    {
                        var bp = ent.GetBuildingPrivilege();
                        if (bp != null && bp.GetBuilding().ID == building.ID)
                            samsites++;
                    }
                }

                if (samsites >= Configuration.SamSiteLimit)
                {
                    SendReply(player, lang.GetMessage("TurretLimitReached_SamSite", this, player.UserIDString), samsites);
                    return false;
                }
            }

            return null;
        }
    }
}
