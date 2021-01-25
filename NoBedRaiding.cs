// Requires: Clans

using System;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using Oxide.Core;
using UnityEngine;
using System.Reflection;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries;
using Oxide;
using Oxide.Game.Rust.Cui;
using Oxide.Game.Rust;
using Newtonsoft.Json;
using System.Linq;
using Oxide.Core.Libraries.Covalence;
using Newtonsoft.Json.Linq;
using Rust;

namespace Oxide.Plugins
{
    [Info("NoBedRaiding", "BingoBongo", "0.1.0")]
    [Description("Only allows raids in a set timeframe (UTC)")]
    public class NoBedRaiding : CovalencePlugin
    {
        #region Variables

        [PluginReference] private Plugin _clans;
        private DateTime _raidTimeStart;
        private DateTime _raidTimeEnd;
        private bool _showMessage;
        private bool _playSound;
        private string _sound;
        private List<object> _prefabs;

        #endregion
        
        #region Oxide hooks

        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (hitInfo == null)
                return null;
            if (entity == null)
                return null;

            return IsBlocked(entity) ? OnStructureAttack(entity, hitInfo) : null;
        }

        #endregion

        #region HelpText

        void SendHelpText(BasePlayer player)
        {
            var sb = new StringBuilder()
                .Append("NoBedRaiding by <color=#ce422b>Bingerbongerino</color>\n")
                .Append($"Raiding available between {_raidTimeStart} and ${_raidTimeEnd} UTC");

            player.ChatMessage(sb.ToString());
        }

        #endregion

        #region Config

        private static List<object> GetDefaultPrefabs()
        {
            return new List<object>()
            {
                "door.hinged",
                "door.double.hinged",
                "window.bars",
                "floor.ladder.hatch",
                "floor.frame",
                "wall.frame",
                "shutter",
                "wall.external",
                "gates.external",
                "box",
                "locker"
            };
        }

        void LoadMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"Protection Message", "This building is protected: {amount}%"},
                {"Denied: Permission", "You lack permission to do that"}
            }, this);
        }

        protected override void LoadDefaultConfig()
        {
            Config.Clear();

            Config["raidTimeStart"] = "zz:zz";
            Config["raidTimeEnd"] = "zz:zz";
            Config["showMessage"] = true;
            Config["playSound"] = false;
            Config["prefabs"] = GetDefaultPrefabs();
            Config["sound"] = "assets/prefabs/weapon mods/silencers/effects/silencer_attach.fx.prefab";
            Config["VERSION"] = Version.ToString();
        }

        protected void ReloadConfig()
        {
            Config["VERSION"] = Version.ToString();

            // NEW CONFIGURATION OPTIONS HERE
            Config["raidTimeStart"] = GetConfig("raidTimeStart", "zz:zz");
            Config["raidTimeEnd"] = GetConfig("raidTimeEnd", "zz:zz");
            // END NEW CONFIGURATION OPTIONS

            PrintWarning("Upgrading configuration file");
            SaveConfig();
        }
        #endregion

        #region permissions

        private class Permission
        {
            public string Key { get; set; }
        }
        
        private class Permissions
        {
            private Permission _protect = new Permission() { Key = "nobedraid.protect" };
            private Permission _check = new Permission() { Key = "nobedraid.check" };

            public Permission GetProtectPermission() { return _protect; }
            public Permission GetCheckPermission() { return _check; }

            public List<Permission> GetAllPermissions() { return new List<Permission>() {_protect, _check}; }
        }

        #endregion

        #region Initialization & Configuration

        void OnServerInitialized()
        {
            LoadMessages();
            LoadData();
            LoadClans();

            DateTime parsedStart;
            _raidTimeStart = DateTime.TryParse(GetConfig("raidTimeStart", "zz:zz"), out parsedStart)
                ? parsedStart
                : DateTime.MinValue;
            DateTime parsedEnd;
            _raidTimeEnd = DateTime.TryParse(GetConfig("raidTimeEnd", "zz:zz"), out parsedEnd)
                ? parsedEnd
                : DateTime.MinValue;
            
            _prefabs = GetConfig("prefabs", GetDefaultPrefabs());
            _showMessage = GetConfig("showMessage", true);
            _playSound = GetConfig("playSound", false);
            _sound = GetConfig("sound", "assets/prefabs/weapon mods/silencers/effects/silencer_attach.fx.prefab");
            Puts($"NoBedRaiding initialized. Start time: {_raidTimeStart.Hour}. End time: {_raidTimeEnd.Hour}");
        }

        void LoadClans()
        {
            _clans = Manager.GetPlugin("Clans");
        }

        void LoadData()
        {
            if (Config["VERSION"] == null)
            {
                // FOR COMPATIBILITY WITH INITIAL VERSIONS WITHOUT VERSIONED CONFIG
                ReloadConfig();
            }
            else if (GetConfig("VERSION", "") != Version.ToString())
            {
                // ADDS NEW, IF ANY, CONFIGURATION OPTIONS
                ReloadConfig();
            }
        }

        #endregion

        #region Core Methods

        private object OnStructureAttack(BaseEntity entity, HitInfo hitinfo)
        {
            ulong ownerId = entity?.OwnerID ?? 1L;
            var attacker = hitinfo.Initiator as BasePlayer;
            if (ownerId == attacker?.userID || HasAttackRights(ownerId, hitinfo))
            {
                return null;
            }

            if (!ownerId.IsSteamId()) return null;
            var damageEnabled = DamageEnabled();
            if (!damageEnabled)
            {
                if (!String.IsNullOrEmpty(attacker?.UserIDString))
                {
                    Puts($"Attacker: {attacker?.userID} tried to attack structure owned by {ownerId}");
                }                
            }
            return damageEnabled ? null : MitigateDamage(hitinfo);
        }

        private bool HasAttackRights(ulong ownerId, HitInfo hitInfo)
        {
            var attacker = hitInfo.Initiator as BasePlayer;
            if (attacker == null) return false;
            
            var attackerId = attacker?.UserIDString;
            var isMemberOrAlly = _clans == null
                ? IsInTeam(ownerId, attacker)
                : IsInClan(attackerId, ownerId.ToString());
            
            Puts($"AttackerId: {attackerId} - OwnerId: {ownerId.ToString()} - IsMemberOrAlly: {isMemberOrAlly}");
            return isMemberOrAlly;
        }

        public bool IsInClan(string attackerId, string ownerId)
        {
            return _clans.Call<bool>("IsMemberOrAlly", ownerId, attackerId);
        }

        public bool IsInTeam(ulong ownerId, BasePlayer attacker)
        {
            return attacker?.Team?.members?.Contains(ownerId) ?? false;
        }

        private object MitigateDamage(HitInfo hitinfo)
        {
            var isFire = hitinfo.damageTypes.GetMajorityDamageType() == DamageType.Heat;
            hitinfo.damageTypes = new DamageTypeList();
            hitinfo.DoHitEffects = false;
            hitinfo.HitMaterial = 0;
            if (_showMessage && ((isFire && hitinfo.WeaponPrefab != null) || (!isFire)))
                SendMessage(hitinfo);

            if (_playSound && hitinfo.Initiator is BasePlayer && !isFire)
                Effect.server.Run(_sound, hitinfo.Initiator.transform.position);

            return false;
        }

        void SendMessage(HitInfo hitInfo, int amt = 100)
        {
            if (hitInfo.Initiator is BasePlayer)
                ShowMessage((BasePlayer) hitInfo.Initiator, amt);
        }

        private bool DamageEnabled()
        {
            var now = DateTime.UtcNow;
            return !RaidTimeIsConfigured() && IsRaidTime(now);
        }

        private bool RaidTimeIsConfigured()
        {
            return _raidTimeStart.Equals(DateTime.MinValue) || _raidTimeEnd.Equals(DateTime.MinValue);
        }

        private bool IsRaidTime(DateTime now)
        {
            return now.Hour >= _raidTimeStart.Hour && now.Hour < _raidTimeEnd.Hour;
        }

        private Dictionary<string, bool> blockCache = new Dictionary<string, bool>();

        private bool IsBlocked(BaseCombatEntity entity)
        {
            if (entity is BuildingBlock)
                return true;

            var result = false;

            if (string.IsNullOrEmpty(entity.ShortPrefabName)) return false;
            var prefabName = entity.ShortPrefabName;

            if (blockCache.TryGetValue(prefabName, out result))
                return result;

            if (_prefabs.Cast<string>().Any(p => prefabName.IndexOf(p, StringComparison.Ordinal) != -1))
            {
                result = true;
            }

            if (!blockCache.ContainsKey(prefabName))
                blockCache.Add(prefabName, result);


            return result;
        }

        private string RaidStatusMessage()
        {
            var sb = new StringBuilder();
            if (DamageEnabled())
            {
                var diff = _raidTimeEnd - _raidTimeStart;
                sb.AppendLine($"Raiding is currently on. Raiding ends at {_raidTimeEnd.ToString("HH:mm")}");
                sb.AppendLine(
                    $"{diff.Hours.ToString().PadLeft(2, '0')} hours and {diff.Minutes.ToString().PadLeft(2, '0')} minutes left of raiding");
            }
            else
            {
                sb.AppendLine(
                    $"Raiding is not currently available. Raiding begins at {_raidTimeStart.Hour.ToString().PadLeft(2, '0')}:{_raidTimeStart.Minute.ToString().PadLeft(2, '0')}");
            }

            return sb.ToString();
        }

        #endregion

        #region Commands

        [Command("raiding")]
        void cmdStatus(IPlayer player, string command, string[] args)
        {
            player?.Reply(RaidStatusMessage());
        }

        #endregion

        #region Helper Methods

        private T GetConfig<T>(string name, T defaultValue)
        {
            if (Config[name] != null) return (T) Convert.ChangeType(Config[name], typeof(T));
            Config[name] = defaultValue;
            Config.Save();
            return defaultValue;


        }

        protected IPlayer FindPlayerByPartialName(string nameOrIdOrIp)
        {
            if (string.IsNullOrEmpty(nameOrIdOrIp))
                return null;

            var player = covalence.Players.FindPlayer(nameOrIdOrIp);

            return player;
        }

        private bool HasPerm(BasePlayer p, string pe)
        {
            return permission.UserHasPermission(p.userID.ToString(), pe);
        }

        private bool HasPerm(string userid, string pe)
        {
            return permission.UserHasPermission(userid, pe);
        }

        private string GetMsg(string key, object userId = null)
        {
            return lang.GetMessage(key, this, userId?.ToString());
        }

        #endregion

        #region GUI

        private static void HideMessage(BasePlayer player)
        {
            if (player.net?.connection == null)
                return;

            CommunityEntity.ServerInstance.ClientRPCEx(new Network.SendInfo {connection = player.net.connection}, null,
                "DestroyUI", "NoBedRaidingMsg");
        }

        StringBuilder sb = new StringBuilder();

        private void ShowMessage(BasePlayer player, int amount = 100)
        {
            HideMessage(player);
            sb.Clear();
            sb.Append(jsonMessage);
            sb.Replace("{1}", Oxide.Core.Random.Range(1, 99999).ToString());
            sb.Replace("{protection_message}", GetMsg("Protection Message", player.UserIDString));
            sb.Replace("{amount}", amount.ToString());
            CommunityEntity.ServerInstance.ClientRPCEx(new Network.SendInfo {connection = player.net.connection}, null,
                "AddUI", sb.ToString());

            timer.In(3f, delegate() { HideMessage(player); });
        }

        readonly string jsonMessage =
            @"[{""name"":""NoBedRaidingMsg"",""parent"":""Overlay"",""components"":[{""type"":""UnityEngine.UI.Image"",""color"":""0 0 0 0.78""},{""type"":""RectTransform"",""anchormax"":""0.64 0.88"",""anchormin"":""0.38 0.79""}]},{""name"":""MessageLabel{1}"",""parent"":""NoBedRaidingMsg"",""components"":[{""type"":""UnityEngine.UI.Text"",""align"":""MiddleCenter"",""fontSize"":""19"",""text"":""{protection_message}""},{""type"":""RectTransform"",""anchormax"":""1 1"",""anchormin"":""0 0""}]}]";

        #endregion
    }
}