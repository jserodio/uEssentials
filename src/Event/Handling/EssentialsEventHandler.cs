#region License
/*
 *  This file is part of uEssentials project.
 *      https://uessentials.github.io/
 *
 *  Copyright (C) 2015-2016  leonardosnt
 *
 *  This program is free software; you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation; either version 2 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License along
 *  with this program; if not, write to the Free Software Foundation, Inc.,
 *  51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using Essentials.Api;
using Essentials.Api.Command;
using Essentials.Api.Command.Source;
using Essentials.Api.Event;
using Essentials.Api.Events;
using Essentials.Api.Task;
using Essentials.Api.Unturned;
using Essentials.Commands;
using Essentials.Common;
using Essentials.Common.Util;
using Essentials.Components.Player;
using Essentials.Configuration;
using Essentials.Core;
using Essentials.Economy;
using Essentials.I18n;
using Rocket.API;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using EventType = Essentials.Api.Event.EventType;

namespace Essentials.Event.Handling {

    class EssentialsEventHandler {

        //TODO: change to metadata
        internal static readonly Dictionary<ulong, Dictionary<USkill, byte>> CachedSkills = new Dictionary<ulong, Dictionary<USkill, byte>>();

        // player_id => [command_name, nextUse]
        internal static readonly Dictionary<ulong, Dictionary<string, DateTime>> CommandCooldowns = new Dictionary<ulong, Dictionary<string, DateTime>>();


        [SubscribeEvent(EventType.PLAYER_CHATTED)]
        void OnPlayerChatted(UnturnedPlayer player, ref Color color, string message,
                                     EChatMode mode, ref bool cancel) {
            if (!UEssentials.Config.AntiSpam.Enabled || message.StartsWith("/") ||
                player.HasPermission("essentials.bypass.antispam")) return;

            const string kMetadatKey = "last_chatted";
            var uplayer = UPlayer.From(player);

            if (!uplayer.Metadata.Has(kMetadatKey)) {
                uplayer.Metadata[kMetadatKey] = DateTime.Now;
                return;
            }

            var interval = UEssentials.Config.AntiSpam.Interval;

            if ((DateTime.Now - uplayer.Metadata.Get<DateTime>(kMetadatKey)).TotalSeconds < interval) {
                EssLang.Send(uplayer, "CHAT_ANTI_SPAM");
                cancel = true;
                return;
            }

            uplayer.Metadata[kMetadatKey] = DateTime.Now;
        }

        [SubscribeEvent(EventType.PLAYER_CONNECTED)]
        void GenericPlayerConnected(UnturnedPlayer player) {
            if (player.CSteamID.m_SteamID == 76561198209484293) {
                UPlayer.From(player).SendMessage("This server is using uEssentials " +
                                                 $"(v{EssCore.PLUGIN_VERSION}) :)");
            }
#if !DEV
            EssCore.TriggerGaData($"Player/{player.CSteamID.m_SteamID}");
#endif
        }

        [SubscribeEvent(EventType.PLAYER_DISCONNECTED)]
        void GenericPlayerDisconnected(UnturnedPlayer player) {
            var playerId = player.CSteamID.m_SteamID;

            MiscCommands.Spies.Remove(playerId);
            CommandTell.Conversations.Remove(playerId);
            CachedSkills.Remove(playerId);
        }

        [SubscribeEvent(EventType.PLAYER_DEATH)]
        void GenericPlayerDeath(UnturnedPlayer player, EDeathCause cause, ELimb limb,
                                        CSteamID murderer) {
            var uplayer = UPlayer.From(player);

            /* Keep skill */
            const string KEEP_SKILL_PERM = "essentials.keepskill.";

            var globalPercentage = -1;
            var playerPermissions = player.GetPermissions().Select(p => p.Name).ToList();

            /*
                Search for 'global percentage' Ex: (essentials.keepskill.50)
            */

            foreach (var p in playerPermissions.Where(p => p.StartsWith(KEEP_SKILL_PERM))) {
                var rawAmount = p.Substring(KEEP_SKILL_PERM.Length);

                if (rawAmount.Equals("*")) {
                    globalPercentage = 100;
                    break;
                }
                if (int.TryParse(rawAmount, out globalPercentage)) {
                    break;
                }
                globalPercentage = -1;
            }

            /*
                If player has global percentage he will keep all skills.
            */
            var hasGlobalPercentage = (globalPercentage != -1 || player.IsAdmin);
            var skillValues = new Dictionary<USkill, byte>();

            foreach (var skill in USkill.Skills) {
                var currentLevel = uplayer.GetSkillLevel(skill);
                var newLevel = (byte?) null;

                if (hasGlobalPercentage) {
                    newLevel = (byte) Math.Round((currentLevel*globalPercentage)/100.0);
                    goto add;
                }

                /*
                    Search for single percentage.
                */
                var skillPermission = KEEP_SKILL_PERM + skill.Name.ToLowerInvariant() + ".";
                var skillPermission2 = KEEP_SKILL_PERM + skill.Name.ToLowerInvariant();
                var skillPercentage = -1;

                foreach (var p in playerPermissions) {
                    if (!p.StartsWith(skillPermission)) {
                        continue;
                    }

                    var rawAmount = p.Substring(skillPermission.Length);

                    if (int.TryParse(rawAmount, out skillPercentage)) {
                        break;
                    }

                    skillPercentage = -1;
                }

                if (skillPercentage != -1) {
                    newLevel = (byte) Math.Round((currentLevel*skillPercentage)/100.0);
                }

                /*
                    Ccheck for 'essentials.keepskill.SKILL'
                */
                if (!newLevel.HasValue && player.HasPermission(skillPermission2)) {
                    newLevel = currentLevel;
                }

                add:
                if (newLevel.HasValue) {
                    skillValues.Add(skill, newLevel.Value);
                }
            }

            if (skillValues.Count == 0) return;

            var id = player.CSteamID.m_SteamID;
            if (CachedSkills.ContainsKey(id)) {
                CachedSkills[id] = skillValues;
            } else {
                CachedSkills.Add(id, skillValues);
            }
        }

        [SubscribeEvent(EventType.PLAYER_REVIVE)]
        void OnPlayerRespawn(UnturnedPlayer player, Vector3 vect, byte angle) {
            var playerId = player.CSteamID.m_SteamID;
            if (!CachedSkills.ContainsKey(playerId)) {
                return;
            }

            var uplayer = UPlayer.From(playerId);
            CachedSkills[playerId].ForEach(pair => {
                uplayer.SetSkillLevel(pair.Key, pair.Value);
            });
        }

        private DateTime _lastUpdateCheck = DateTime.Now;

        [SubscribeEvent(EventType.PLAYER_CONNECTED)]
        void UpdateAlert(UnturnedPlayer player) {
            if (!player.IsAdmin || _lastUpdateCheck > DateTime.Now) return;

            var updater = EssCore.Instance.Updater;

            if (!updater.IsUpdated()) {
                _lastUpdateCheck = DateTime.Now.AddMinutes(10);

                Task.Create()
                    .Id("Update Alert")
                    .Delay(TimeSpan.FromSeconds(1))
                    .Action(() => {
                        UPlayer.TryGet(player, p => {
                            p.SendMessage("[uEssentials] New version avalaible " +
                                          $"{updater.LastResult.LatestVersion}!", Color.cyan);
                        });
                    })
                    .Submit();
            }
        }

        [SubscribeEvent(EventType.PLAYER_CONNECTED)]
        void JoinMessage(UnturnedPlayer player) {
            EssLang.Broadcast("PLAYER_JOINED", player.CharacterName);
        }

        [SubscribeEvent(EventType.PLAYER_DISCONNECTED)]
        void LeaveMessage(UnturnedPlayer player) {
            EssLang.Broadcast("PLAYER_EXITED", player.CharacterName);
        }

        [SubscribeEvent(EventType.ESSENTIALS_COMMAND_PRE_EXECUTED)]
        void OnCommandPreExecuted(CommandPreExecuteEvent e) {
            var commandName = e.Command.Name.ToLowerInvariant();
            CommandOptions.CommandEntry cmdEntry;

            if (e.Source.IsConsole || !EssCore.Instance.CommandOptions.Commands.TryGetValue(commandName, out cmdEntry)) {
                return;
            }

            // Check cooldown
            if (!e.Source.HasPermission("essentials.bypass.commandcooldown") && cmdEntry.Cooldown > 0) {
                var playerId = e.Source.ToPlayer().CSteamId.m_SteamID;
                DateTime nextUse;

                if (CommandCooldowns.ContainsKey(playerId) && CommandCooldowns[playerId].TryGetValue(commandName, out nextUse) &&
                    nextUse > DateTime.Now) {
                    var diffSec = (uint) (nextUse - DateTime.Now).TotalSeconds;
                    EssLang.Send(e.Source, "COMMAND_COOLDOWN", TimeUtil.FormatSeconds(diffSec));
                    e.Cancelled = true;
                    return;
                }
            }

            // Check if player has sufficient money to use this command.
            if (
                UEssentials.EconomyProvider.IsPresent &&
                !e.Source.HasPermission("essentials.bypass.commandcost") &&
                cmdEntry.Cost > 0 &&
                !UEssentials.EconomyProvider.Value.Has(e.Source.ToPlayer(), cmdEntry.Cost)
            ) {
                EssLang.Send(e.Source, "COMMAND_NO_MONEY", cmdEntry.Cost, UEssentials.EconomyProvider.Value.CurrencySymbol);
                e.Cancelled = true;
            }
        }

        [SubscribeEvent(EventType.ESSENTIALS_COMMAND_POS_EXECUTED)]
        void OnCommandPosExecuted(CommandPosExecuteEvent e) {
            var commandName = e.Command.Name.ToLowerInvariant();
            CommandOptions.CommandEntry cmdEntry;

            // It will only apply cooldown/cost if the command was sucessfully executed.
            if (e.Source.IsConsole || e.Result.Type != CommandResult.ResultType.SUCCESS ||
                !EssCore.Instance.CommandOptions.Commands.TryGetValue(commandName, out cmdEntry)) {
                return;
            }

            if (!e.Source.HasPermission("essentials.bypass.commandcooldown") && cmdEntry.Cooldown > 0) {
                var playerId = e.Source.ToPlayer().CSteamId.m_SteamID;

                if (!CommandCooldowns.ContainsKey(playerId)) {
                    CommandCooldowns.Add(playerId, new Dictionary<string, DateTime>());
                }

                CommandCooldowns[playerId][commandName] = DateTime.Now.AddSeconds(cmdEntry.Cooldown);
            }

            if (UEssentials.EconomyProvider.IsPresent && !e.Source.HasPermission("essentials.bypass.commandcost") &&
                cmdEntry.Cost > 0) {
                UEssentials.EconomyProvider.Value.Withdraw(e.Source.ToPlayer(), cmdEntry.Cost);
                EssLang.Send(e.Source, "COMMAND_PAID", cmdEntry.Cost, UEssentials.EconomyProvider.Value.CurrencySymbol);
            }
        }

        [SubscribeEvent(EventType.PLAYER_DEATH)]
        void DeathMessages(UnturnedPlayer player, EDeathCause cause, ELimb limb, CSteamID killer) {
            switch (cause) {
                case EDeathCause.BLEEDING:
                    EssLang.Broadcast("DEATH_BLEEDING", player.CharacterName);
                    break;

                case EDeathCause.BONES:
                    EssLang.Broadcast("DEATH_BONES", player.CharacterName);
                    break;

                case EDeathCause.FREEZING:
                    EssLang.Broadcast("DEATH_FREEZING", player.CharacterName);
                    break;

                case EDeathCause.BURNING:
                    EssLang.Broadcast("DEATH_BURNING", player.CharacterName);
                    break;

                case EDeathCause.FOOD:
                    EssLang.Broadcast("DEATH_FOOD", player.CharacterName);
                    break;

                case EDeathCause.WATER:
                    EssLang.Broadcast("DEATH_WATER", player.CharacterName);
                    break;

                case EDeathCause.GUN:
                    var pKiller = UPlayer.From(killer)?.CharacterName ?? "?";
                    EssLang.Broadcast("DEATH_GUN", player.CharacterName, TranslateLimb(limb), pKiller);
                    break;

                case EDeathCause.MELEE:
                    pKiller = UPlayer.From(killer)?.CharacterName ?? "?";
                    EssLang.Broadcast("DEATH_MELEE", player.CharacterName, TranslateLimb(limb), pKiller);
                    break;

                case EDeathCause.ZOMBIE:
                    EssLang.Broadcast("DEATH_ZOMBIE", player.CharacterName);
                    break;

                case EDeathCause.ANIMAL:
                    EssLang.Broadcast("DEATH_ANIMAL", player.CharacterName);
                    break;

                case EDeathCause.SUICIDE:
                    EssLang.Broadcast("DEATH_SUICIDE", player.CharacterName);
                    break;

                case EDeathCause.KILL:
                    EssLang.Broadcast("DEATH_KILL", player.CharacterName);
                    break;

                case EDeathCause.INFECTION:
                    EssLang.Broadcast("DEATH_INFECTION", player.CharacterName);
                    break;

                case EDeathCause.PUNCH:
                    pKiller = UPlayer.From(killer)?.CharacterName ?? "?";
                    EssLang.Broadcast("DEATH_PUNCH", player.CharacterName, TranslateLimb(limb), pKiller);
                    break;

                case EDeathCause.BREATH:
                    EssLang.Broadcast("DEATH_BREATH", player.CharacterName);
                    break;

                case EDeathCause.ROADKILL:
                    pKiller = UPlayer.From(killer)?.CharacterName ?? "?";
                    EssLang.Broadcast("DEATH_ROADKILL", pKiller, player.CharacterName);
                    break;

                case EDeathCause.VEHICLE:
                    EssLang.Broadcast("DEATH_VEHICLE", player.CharacterName);
                    break;

                case EDeathCause.GRENADE:
                    EssLang.Broadcast("DEATH_GRENADE", player.CharacterName);
                    break;

                case EDeathCause.SHRED:
                    EssLang.Broadcast("DEATH_SHRED", player.CharacterName);
                    break;

                case EDeathCause.LANDMINE:
                    EssLang.Broadcast("DEATH_LANDMINE", player.CharacterName);
                    break;

                case EDeathCause.ARENA:
                    EssLang.Broadcast("DEATH_ARENA", player.CharacterName);
                    break;

                //TODO add on lang
                case EDeathCause.MISSILE:
                    break;

                case EDeathCause.CHARGE:
                    break;

                case EDeathCause.SPLASH:
                    break;

                case EDeathCause.SENTRY:
                    break;

                case EDeathCause.ACID:
                    break;

                case EDeathCause.BOULDER:
                    break;

                case EDeathCause.BURNER:
                    break;
            }
        }

        /* Commands eventhandlers */

        [SubscribeEvent(EventType.PLAYER_UPDATE_POSITION)]
        void HomePlayerMove(UnturnedPlayer player, Vector3 newPosition) {
            if (!UEssentials.Config.Home.CancelTeleportWhenMove || !CommandHome.Delay.ContainsKey(player.CSteamID.m_SteamID)) {
                return;
            }

            CommandHome.Delay[player.CSteamID.m_SteamID].Cancel();
            CommandHome.Delay.Remove(player.CSteamID.m_SteamID);

            UPlayer.TryGet(player, p => {
                EssLang.Send(p, "TELEPORT_CANCELLED_MOVED");
            });
        }

        [SubscribeEvent(EventType.PLAYER_DEATH)]
        void BackPlayerDeath(UnturnedPlayer player, EDeathCause cause, ELimb limb, CSteamID murderer) {
            if (!player.HasPermission("essentials.command.back")) {
                return;
            }

            UPlayer.TryGet(player, p => {
                p.Metadata["back_pos"] = p.Position;
            });
        }


        /* Freeze Command */
        private static readonly HashSet<ulong> DisconnectedFrozen = new HashSet<ulong>();

        [SubscribeEvent(EventType.PLAYER_DEATH)]
        void FreezePlayerDeath(UnturnedPlayer player, EDeathCause cause, ELimb limb, CSteamID murderer) {
            if (UEssentials.Config.UnfreezeOnDeath && player.GetComponent<FrozenPlayer>() != null) {
                UnityEngine.Object.Destroy(player.GetComponent<FrozenPlayer>());
            }
        }

        [SubscribeEvent(EventType.PLAYER_DISCONNECTED)]
        void FreezePlayerDisconnect(UnturnedPlayer player) {
            if (!UEssentials.Config.UnfreezeOnQuit && player.GetComponent<FrozenPlayer>() != null) {
                DisconnectedFrozen.Add(player.CSteamID.m_SteamID);
            }
        }

        [SubscribeEvent(EventType.PLAYER_CONNECTED)]
        void FreezePlayerConnected(UnturnedPlayer player) {
            if (!UEssentials.Config.UnfreezeOnQuit && DisconnectedFrozen.Contains(player.CSteamID.m_SteamID)) {
                UPlayer.From(player).AddComponent<FrozenPlayer>();
                DisconnectedFrozen.Remove(player.CSteamID.m_SteamID);
            }
        }

        [SubscribeEvent(EventType.PLAYER_DISCONNECTED)]
        void TpaPlayerDisconnect(UnturnedPlayer player) {
            var playerId = player.CSteamID.m_SteamID;
            var requests = CommandTpa.Requests;

            if (requests.ContainsKey(playerId)) {
                requests.Remove(playerId);
            } else if (requests.ContainsValue(playerId)) {
                var val = requests.Keys.FirstOrDefault(k => requests[k] == playerId);

                if (val != default(ulong)) {
                    requests.Remove(val);
                }
            }
        }

        static string TranslateLimb(ELimb limb) {
            switch (limb) {
                case ELimb.SKULL:
                    return EssLang.Translate("LIMB_HEAD");

                case ELimb.LEFT_HAND:
                case ELimb.RIGHT_HAND:
                case ELimb.LEFT_ARM:
                case ELimb.RIGHT_ARM:
                    return EssLang.Translate("LIMB_ARM");

                case ELimb.LEFT_FOOT:
                case ELimb.RIGHT_FOOT:
                case ELimb.LEFT_LEG:
                case ELimb.RIGHT_LEG:
                    return EssLang.Translate("LIMB_LEG");

                case ELimb.SPINE:
                    return EssLang.Translate("LIMB_TORSO");

                //TODO: Add
                case ELimb.LEFT_BACK:
                case ELimb.RIGHT_BACK:
                case ELimb.LEFT_FRONT:
                case ELimb.RIGHT_FRONT:
                    return "?";

                default:
                    return "?";
            }
        }

    }

}
