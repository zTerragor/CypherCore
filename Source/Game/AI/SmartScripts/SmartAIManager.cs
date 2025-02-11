﻿/*
 * Copyright (C) 2012-2020 CypherCore <http://github.com/CypherCore>
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using Framework.Constants;
using Framework.Database;
using Game.DataStorage;
using Game.Entities;
using Game.Movement;
using Game.Spells;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Game.AI
{
    public class SmartAIManager : Singleton<SmartAIManager>
    {
        MultiMap<int, SmartScriptHolder>[] _eventMap = new MultiMap<int, SmartScriptHolder>[(int)SmartScriptType.Max];
        Dictionary<uint, WaypointPath> _waypointStore = new();

        SmartAIManager()
        {
            for (byte i = 0; i < (int)SmartScriptType.Max; i++)
                _eventMap[i] = new MultiMap<int, SmartScriptHolder>();
        }

        public void LoadFromDB()
        {
            uint oldMSTime = Time.GetMSTime();

            for (byte i = 0; i < (int)SmartScriptType.Max; i++)
                _eventMap[i].Clear();  //Drop Existing SmartAI List

            PreparedStatement stmt = DB.World.GetPreparedStatement(WorldStatements.SEL_SMART_SCRIPTS);
            SQLResult result = DB.World.Query(stmt);
            if (result.IsEmpty())
            {
                Log.outInfo(LogFilter.ServerLoading, "Loaded 0 SmartAI scripts. DB table `smartai_scripts` is empty.");
                return;
            }

            int count = 0;
            do
            {
                SmartScriptHolder temp = new();

                temp.EntryOrGuid = result.Read<int>(0);
                SmartScriptType source_type = (SmartScriptType)result.Read<byte>(1);
                if (source_type >= SmartScriptType.Max)
                {
                    Log.outError(LogFilter.Sql, "SmartAIMgr.LoadSmartAI: invalid source_type ({0}), skipped loading.", source_type);
                    continue;
                }
                if (temp.EntryOrGuid >= 0)
                {
                    switch (source_type)
                    {
                        case SmartScriptType.Creature:
                            if (Global.ObjectMgr.GetCreatureTemplate((uint)temp.EntryOrGuid) == null)
                            {
                                Log.outError(LogFilter.Sql, "SmartAIMgr.LoadSmartAI: Creature entry ({0}) does not exist, skipped loading.", temp.EntryOrGuid);
                                continue;
                            }
                            break;

                        case SmartScriptType.GameObject:
                        {
                            if (Global.ObjectMgr.GetGameObjectTemplate((uint)temp.EntryOrGuid) == null)
                            {
                                Log.outError(LogFilter.Sql, "SmartAIMgr.LoadSmartAI: GameObject entry ({0}) does not exist, skipped loading.", temp.EntryOrGuid);
                                continue;
                            }
                            break;
                        }
                        case SmartScriptType.AreaTrigger:
                        {
                            if (CliDB.AreaTableStorage.LookupByKey((uint)temp.EntryOrGuid) == null)
                            {
                                Log.outError(LogFilter.Sql, "SmartAIMgr.LoadSmartAI: AreaTrigger entry ({0}) does not exist, skipped loading.", temp.EntryOrGuid);
                                continue;
                            }
                            break;
                        }
                        case SmartScriptType.Scene:
                        {
                            if (Global.ObjectMgr.GetSceneTemplate((uint)temp.EntryOrGuid) == null)
                            {
                                Log.outError(LogFilter.Sql, "SmartAIMgr.LoadSmartAIFromDB: Scene id ({0}) does not exist, skipped loading.", temp.EntryOrGuid);
                                continue;
                            }
                            break;
                        }
                        case SmartScriptType.Quest:
                        {
                            if (Global.ObjectMgr.GetQuestTemplate((uint)temp.EntryOrGuid) == null)
                            {
                                Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: Quest id ({temp.EntryOrGuid}) does not exist, skipped loading.");
                                continue;
                            }
                            break;
                        }
                        case SmartScriptType.TimedActionlist:
                            break;//nothing to check, really
                        case SmartScriptType.AreaTriggerEntity:
                        {
                            if (Global.AreaTriggerDataStorage.GetAreaTriggerTemplate(new AreaTriggerId((uint)temp.EntryOrGuid, false)) == null)
                            {
                                Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: AreaTrigger entry ({temp.EntryOrGuid} IsServerSide false) does not exist, skipped loading.");
                                continue;
                            }
                            break;
                        }
                        case SmartScriptType.AreaTriggerEntityServerside:
                        {
                            if (Global.AreaTriggerDataStorage.GetAreaTriggerTemplate(new AreaTriggerId((uint)temp.EntryOrGuid, true)) == null)
                            {
                                Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: AreaTrigger entry ({temp.EntryOrGuid} IsServerSide true) does not exist, skipped loading.");
                                continue;
                            }
                            break;
                        }
                        default:
                            Log.outError(LogFilter.Sql, "SmartAIMgr.LoadSmartAIFromDB: not yet implemented source_type {0}", source_type);
                            continue;
                    }
                }
                else
                {
                    switch (source_type)
                    {
                        case SmartScriptType.Creature:
                        {
                            CreatureData creature = Global.ObjectMgr.GetCreatureData((ulong)-temp.EntryOrGuid);
                            if (creature == null)
                            {
                                Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: Creature guid ({-temp.EntryOrGuid}) does not exist, skipped loading.");
                                continue;
                            }

                            CreatureTemplate creatureInfo = Global.ObjectMgr.GetCreatureTemplate(creature.Id);
                            if (creatureInfo == null)
                            {
                                Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: Creature entry ({creature.Id}) guid ({-temp.EntryOrGuid}) does not exist, skipped loading.");
                                continue;
                            }

                            if (creatureInfo.AIName != "SmartAI")
                            {
                                Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: Creature entry ({creature.Id}) guid ({-temp.EntryOrGuid}) is not using SmartAI, skipped loading.");
                                continue;
                            }
                            break;
                        }
                        case SmartScriptType.GameObject:
                        {
                            GameObjectData gameObject = Global.ObjectMgr.GetGameObjectData((ulong)-temp.EntryOrGuid);
                            if (gameObject == null)
                            {
                                Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: GameObject guid ({-temp.EntryOrGuid}) does not exist, skipped loading.");
                                continue;
                            }

                            GameObjectTemplate gameObjectInfo = Global.ObjectMgr.GetGameObjectTemplate(gameObject.Id);
                            if (gameObjectInfo == null)
                            {
                                Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: GameObject entry ({gameObject.Id}) guid ({-temp.EntryOrGuid}) does not exist, skipped loading.");
                                continue;
                            }

                            if (gameObjectInfo.AIName != "SmartGameObjectAI")
                            {
                                Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: GameObject entry ({gameObject.Id}) guid ({-temp.EntryOrGuid}) is not using SmartGameObjectAI, skipped loading.");
                                continue;
                            }
                            break;
                        }
                        default:
                            Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: GUID-specific scripting not yet implemented for source_type {source_type}");
                            continue;
                    }
                }

                temp.SourceType = source_type;
                temp.EventId = result.Read<ushort>(2);
                temp.Link = result.Read<ushort>(3);
                temp.Event.type = (SmartEvents)result.Read<byte>(4);
                temp.Event.event_phase_mask = result.Read<ushort>(5);
                temp.Event.event_chance = result.Read<byte>(6);
                temp.Event.event_flags = (SmartEventFlags)result.Read<ushort>(7);

                temp.Event.raw.param1 = result.Read<uint>(8);
                temp.Event.raw.param2 = result.Read<uint>(9);
                temp.Event.raw.param3 = result.Read<uint>(10);
                temp.Event.raw.param4 = result.Read<uint>(11);
                temp.Event.raw.param5 = result.Read<uint>(12);
                temp.Event.param_string = result.Read<string>(13);

                temp.Action.type = (SmartActions)result.Read<byte>(14);
                temp.Action.raw.param1 = result.Read<uint>(15);
                temp.Action.raw.param2 = result.Read<uint>(16);
                temp.Action.raw.param3 = result.Read<uint>(17);
                temp.Action.raw.param4 = result.Read<uint>(18);
                temp.Action.raw.param5 = result.Read<uint>(19);
                temp.Action.raw.param6 = result.Read<uint>(20);

                temp.Target.type = (SmartTargets)result.Read<byte>(21);
                temp.Target.raw.param1 = result.Read<uint>(22);
                temp.Target.raw.param2 = result.Read<uint>(23);
                temp.Target.raw.param3 = result.Read<uint>(24);
                temp.Target.raw.param4 = result.Read<uint>(25);
                temp.Target.x = result.Read<float>(26);
                temp.Target.y = result.Read<float>(27);
                temp.Target.z = result.Read<float>(28);
                temp.Target.o = result.Read<float>(29);

                //check target
                if (!IsTargetValid(temp))
                    continue;

                // check all event and action params
                if (!IsEventValid(temp))
                    continue;

                // specific check for timed events
                switch (temp.Event.type)
                {
                    case SmartEvents.Update:
                    case SmartEvents.UpdateOoc:
                    case SmartEvents.UpdateIc:
                    case SmartEvents.HealthPct:
                    case SmartEvents.TargetHealthPct:
                    case SmartEvents.ManaPct:
                    case SmartEvents.TargetManaPct:
                    case SmartEvents.Range:
                    case SmartEvents.FriendlyHealth:
                    case SmartEvents.FriendlyHealthPCT:
                    case SmartEvents.FriendlyMissingBuff:
                    case SmartEvents.HasAura:
                    case SmartEvents.TargetBuffed:
                        if (temp.Event.minMaxRepeat.repeatMin == 0 && temp.Event.minMaxRepeat.repeatMax == 0 && !temp.Event.event_flags.HasAnyFlag(SmartEventFlags.NotRepeatable) && temp.SourceType != SmartScriptType.TimedActionlist)
                        {
                            temp.Event.event_flags |= SmartEventFlags.NotRepeatable;
                            Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: Entry {temp.EntryOrGuid} SourceType {temp.GetScriptType()}, Event {temp.EventId}, Missing Repeat flag.");
                        }
                        break;
                    case SmartEvents.VictimCasting:
                    case SmartEvents.IsBehindTarget:
                        if (temp.Event.minMaxRepeat.min == 0 && temp.Event.minMaxRepeat.max == 0 && !temp.Event.event_flags.HasAnyFlag(SmartEventFlags.NotRepeatable) && temp.SourceType != SmartScriptType.TimedActionlist)
                        {
                            temp.Event.event_flags |= SmartEventFlags.NotRepeatable;
                            Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: Entry {temp.EntryOrGuid} SourceType {temp.GetScriptType()}, Event {temp.EventId}, Missing Repeat flag.");
                        }
                        break;
                    case SmartEvents.FriendlyIsCc:
                        if (temp.Event.friendlyCC.repeatMin == 0 && temp.Event.friendlyCC.repeatMax == 0 && !temp.Event.event_flags.HasAnyFlag(SmartEventFlags.NotRepeatable) && temp.SourceType != SmartScriptType.TimedActionlist)
                        {
                            temp.Event.event_flags |= SmartEventFlags.NotRepeatable;
                            Log.outError(LogFilter.Sql, $"SmartAIMgr.LoadSmartAIFromDB: Entry {temp.EntryOrGuid} SourceType {temp.GetScriptType()}, Event {temp.EventId}, Missing Repeat flag.");
                        }
                        break;
                    default:
                        break;
                }

                // creature entry / guid not found in storage, create empty event list for it and increase counters
                if (!_eventMap[(int)source_type].ContainsKey(temp.EntryOrGuid))
                    ++count;

                // store the new event
                _eventMap[(int)source_type].Add(temp.EntryOrGuid, temp);
            }
            while (result.NextRow());

            // Post Loading Validation
            for (byte i = 0; i < (int)SmartScriptType.Max; ++i)
            {
                if (_eventMap[i] == null)
                    continue;

                foreach (var key in _eventMap[i].Keys)
                {
                    var list = _eventMap[i].LookupByKey(key);
                    foreach (var e in list)
                    {
                        if (e.Link != 0)
                        {
                            if (FindLinkedEvent(list, e.Link) == null)
                            {
                                Log.outError(LogFilter.Sql, "SmartAIMgr.LoadFromDB: Entry {0} SourceType {1}, Event {2}, Link Event {3} not found or invalid.",
                                        e.EntryOrGuid, e.GetScriptType(), e.EventId, e.Link);
                            }
                        }

                        if (e.GetEventType() == SmartEvents.Link)
                        {
                            if (FindLinkedSourceEvent(list, e.EventId) == null)
                            {
                                Log.outError(LogFilter.Sql, "SmartAIMgr.LoadFromDB: Entry {0} SourceType {1}, Event {2}, Link Source Event not found or invalid. Event will never trigger.",
                                        e.EntryOrGuid, e.GetScriptType(), e.EventId);
                            }
                        }
                    }
                }
            }

            Log.outInfo(LogFilter.ServerLoading, "Loaded {0} SmartAI scripts in {1} ms", count, Time.GetMSTimeDiffToNow(oldMSTime));
        }

        public void LoadWaypointFromDB()
        {
            uint oldMSTime = Time.GetMSTime();

            _waypointStore.Clear();

            PreparedStatement stmt = DB.World.GetPreparedStatement(WorldStatements.SEL_SMARTAI_WP);
            SQLResult result = DB.World.Query(stmt);

            if (result.IsEmpty())
            {
                Log.outInfo(LogFilter.ServerLoading, "Loaded 0 SmartAI Waypoint Paths. DB table `waypoints` is empty.");

                return;
            }

            uint count = 0;
            uint total = 0;
            uint lastEntry = 0;
            uint lastId = 1;

            do
            {
                uint entry = result.Read<uint>(0);
                uint id = result.Read<uint>(1);
                float x = result.Read<float>(2);
                float y = result.Read<float>(3);
                float z = result.Read<float>(4);

                if (lastEntry != entry)
                {
                    lastId = 1;
                    ++count;
                }

                if (lastId != id)
                    Log.outError(LogFilter.Sql, $"SmartWaypointMgr.LoadFromDB: Path entry {entry}, unexpected point id {id}, expected {lastId}.");

                ++lastId;

                if (!_waypointStore.ContainsKey(entry))
                    _waypointStore[entry] = new WaypointPath();

                WaypointPath path = _waypointStore[entry];
                path.id = entry;
                path.nodes.Add(new WaypointNode(id, x, y, z));

                lastEntry = entry;
                ++total;
            }
            while (result.NextRow());

            Log.outInfo(LogFilter.ServerLoading, $"Loaded {count} SmartAI waypoint paths (total {total} waypoints) in {Time.GetMSTimeDiffToNow(oldMSTime)} ms");
        }

        static bool EventHasInvoker(SmartEvents smartEvent)
        {
            switch (smartEvent)
            { // white list of events that actually have an invoker passed to them
                case SmartEvents.Aggro:
                case SmartEvents.Death:
                case SmartEvents.Kill:
                case SmartEvents.SummonedUnit:
                case SmartEvents.SpellHit:
                case SmartEvents.SpellHitTarget:
                case SmartEvents.Damaged:
                case SmartEvents.ReceiveHeal:
                case SmartEvents.ReceiveEmote:
                case SmartEvents.JustSummoned:
                case SmartEvents.DamagedTarget:
                case SmartEvents.SummonDespawned:
                case SmartEvents.PassengerBoarded:
                case SmartEvents.PassengerRemoved:
                case SmartEvents.GossipHello:
                case SmartEvents.GossipSelect:
                case SmartEvents.AcceptedQuest:
                case SmartEvents.RewardQuest:
                case SmartEvents.FollowCompleted:
                case SmartEvents.OnSpellclick:
                case SmartEvents.GoLootStateChanged:
                case SmartEvents.AreatriggerOntrigger:
                case SmartEvents.IcLos:
                case SmartEvents.OocLos:
                case SmartEvents.DistanceCreature:
                case SmartEvents.FriendlyHealth:
                case SmartEvents.FriendlyHealthPCT:
                case SmartEvents.FriendlyIsCc:
                case SmartEvents.FriendlyMissingBuff:
                case SmartEvents.ActionDone:
                case SmartEvents.TargetHealthPct:
                case SmartEvents.TargetManaPct:
                case SmartEvents.Range:
                case SmartEvents.VictimCasting:
                case SmartEvents.TargetBuffed:
                case SmartEvents.IsBehindTarget:
                case SmartEvents.InstancePlayerEnter:
                case SmartEvents.TransportAddcreature:
                case SmartEvents.DataSet:
                case SmartEvents.QuestAccepted:
                case SmartEvents.QuestObjCompletion:
                case SmartEvents.QuestCompletion:
                case SmartEvents.QuestFail:
                case SmartEvents.QuestRewarded:
                case SmartEvents.SceneStart:
                case SmartEvents.SceneTrigger:
                case SmartEvents.SceneCancel:
                case SmartEvents.SceneComplete:
                    return true;
                default:
                    return false;
            }
        }

        static bool IsTargetValid(SmartScriptHolder e)
        {
            if (Math.Abs(e.Target.o) > 2 * MathFunctions.PI)
                Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} has abs(`target.o` = {e.Target.o}) > 2*PI (orientation is expressed in radians)");

            if (e.GetActionType() == SmartActions.InstallAiTemplate)
                return true; // AI template has special handling

            switch (e.GetTargetType())
            {
                case SmartTargets.CreatureDistance:
                case SmartTargets.CreatureRange:
                {
                    if (e.Target.unitDistance.creature != 0 && Global.ObjectMgr.GetCreatureTemplate(e.Target.unitDistance.creature) == null)
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Creature entry {e.Target.unitDistance.creature} as target_param1, skipped.");
                        return false;
                    }
                    break;
                }
                case SmartTargets.GameobjectDistance:
                case SmartTargets.GameobjectRange:
                {
                    if (e.Target.goDistance.entry != 0 && Global.ObjectMgr.GetGameObjectTemplate(e.Target.goDistance.entry) == null)
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent GameObject entry {e.Target.goDistance.entry} as target_param1, skipped.");
                        return false;
                    }
                    break;
                }
                case SmartTargets.CreatureGuid:
                {
                    if (e.Target.unitGUID.entry != 0 && !IsCreatureValid(e, e.Target.unitGUID.entry))
                        return false;
                    break;
                }
                case SmartTargets.GameobjectGuid:
                {
                    if (e.Target.goGUID.entry != 0 && !IsGameObjectValid(e, e.Target.goGUID.entry))
                        return false;
                    break;
                }
                case SmartTargets.PlayerDistance:
                case SmartTargets.ClosestPlayer:
                {
                    if (e.Target.playerDistance.dist == 0)
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} has maxDist 0 as target_param1, skipped.");
                        return false;
                    }
                    break;
                }
                case SmartTargets.ActionInvoker:
                case SmartTargets.ActionInvokerVehicle:
                case SmartTargets.InvokerParty:
                    if (e.GetScriptType() != SmartScriptType.TimedActionlist && e.GetEventType() != SmartEvents.Link && !EventHasInvoker(e.Event.type))
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: Entry {e.EntryOrGuid} SourceType {e.GetScriptType()} Event {e.GetEventType()} Action {e.GetActionType()} has invoker target, but event does not provide any invoker!");
                        return false;
                    }
                    break;
                case SmartTargets.PlayerRange:
                case SmartTargets.Self:
                case SmartTargets.Victim:
                case SmartTargets.HostileSecondAggro:
                case SmartTargets.HostileLastAggro:
                case SmartTargets.HostileRandom:
                case SmartTargets.HostileRandomNotTop:
                case SmartTargets.Position:
                case SmartTargets.None:
                case SmartTargets.OwnerOrSummoner:
                case SmartTargets.ThreatList:
                case SmartTargets.ClosestGameobject:
                case SmartTargets.ClosestCreature:
                case SmartTargets.ClosestEnemy:
                case SmartTargets.ClosestFriendly:
                case SmartTargets.Stored:
                case SmartTargets.LootRecipients:
                case SmartTargets.Farthest:
                case SmartTargets.VehiclePassenger:
                case SmartTargets.SpellTarget:
                    break;
                default:
                    Log.outError(LogFilter.ScriptsAi, "SmartAIMgr: Not handled target_type({0}), Entry {1} SourceType {2} Event {3} Action {4}, skipped.", e.GetTargetType(), e.EntryOrGuid, e.GetScriptType(), e.EventId, e.GetActionType());
                    return false;
            }
            return true;
        }

        static bool IsSpellVisualKitValid(SmartScriptHolder e, uint entry)
        {
            if (!CliDB.SpellVisualKitStorage.ContainsKey(entry))
            {
                Log.outError(LogFilter.Sql, $"SmartAIMgr: Entry {e.EntryOrGuid} SourceType {e.GetScriptType()} Event {e.EventId} Action {e.GetActionType()} uses non-existent SpellVisualKit entry {entry}, skipped.");
                return false;
            }
            return true;
        }

        bool IsEventValid(SmartScriptHolder e)
        {
            if (e.Event.type >= SmartEvents.End)
            {
                Log.outError(LogFilter.ScriptsAi, "SmartAIMgr: EntryOrGuid {0} using event({1}) has invalid event type ({2}), skipped.", e.EntryOrGuid, e.EventId, e.GetEventType());
                return false;
            }

            // in SMART_SCRIPT_TYPE_TIMED_ACTIONLIST all event types are overriden by core
            if (e.GetScriptType() != SmartScriptType.TimedActionlist && !Convert.ToBoolean(GetEventMask(e.Event.type) & GetTypeMask(e.GetScriptType())))
            {
                Log.outError(LogFilter.Scripts, "SmartAIMgr: EntryOrGuid {0}, event type {1} can not be used for Script type {2}", e.EntryOrGuid, e.GetEventType(), e.GetScriptType());
                return false;
            }
            if (e.Action.type <= 0 || e.Action.type >= SmartActions.End)
            {
                Log.outError(LogFilter.ScriptsAi, "SmartAIMgr: EntryOrGuid {0} using event({1}) has invalid action type ({2}), skipped.", e.EntryOrGuid, e.EventId, e.GetActionType());
                return false;
            }
            if (e.Event.event_phase_mask > (uint)SmartEventPhaseBits.All)
            {
                Log.outError(LogFilter.ScriptsAi, "SmartAIMgr: EntryOrGuid {0} using event({1}) has invalid phase mask ({2}), skipped.", e.EntryOrGuid, e.EventId, e.Event.event_phase_mask);
                return false;
            }
            if (e.Event.event_flags > SmartEventFlags.All)
            {
                Log.outError(LogFilter.ScriptsAi, "SmartAIMgr: EntryOrGuid {0} using event({1}) has invalid event flags ({2}), skipped.", e.EntryOrGuid, e.EventId, e.Event.event_flags);
                return false;
            }
            if (e.Link != 0 && e.Link == e.EventId)
            {
                Log.outError(LogFilter.Sql, "SmartAIMgr: EntryOrGuid {0} SourceType {1}, Event {2}, Event is linking self (infinite loop), skipped.", e.EntryOrGuid, e.GetScriptType(), e.EventId);
                return false;
            }
            if (e.GetScriptType() == SmartScriptType.TimedActionlist)
            {
                e.Event.type = SmartEvents.UpdateOoc;//force default OOC, can change when calling the script!
                if (!IsMinMaxValid(e, e.Event.minMaxRepeat.min, e.Event.minMaxRepeat.max))
                    return false;

                if (!IsMinMaxValid(e, e.Event.minMaxRepeat.repeatMin, e.Event.minMaxRepeat.repeatMax))
                    return false;
            }
            else
            {
                switch (e.Event.type)
                {
                    case SmartEvents.Update:
                    case SmartEvents.UpdateIc:
                    case SmartEvents.UpdateOoc:
                    case SmartEvents.HealthPct:
                    case SmartEvents.ManaPct:
                    case SmartEvents.TargetHealthPct:
                    case SmartEvents.TargetManaPct:
                    case SmartEvents.Range:
                    case SmartEvents.Damaged:
                    case SmartEvents.DamagedTarget:
                    case SmartEvents.ReceiveHeal:
                        if (!IsMinMaxValid(e, e.Event.minMaxRepeat.min, e.Event.minMaxRepeat.max))
                            return false;

                        if (!IsMinMaxValid(e, e.Event.minMaxRepeat.repeatMin, e.Event.minMaxRepeat.repeatMax))
                            return false;
                        break;
                    case SmartEvents.SpellHit:
                    case SmartEvents.SpellHitTarget:
                        if (e.Event.spellHit.spell != 0)
                        {
                            SpellInfo spellInfo = Global.SpellMgr.GetSpellInfo(e.Event.spellHit.spell, Difficulty.None);
                            if (spellInfo == null)
                            {
                                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Spell entry {e.Event.spellHit.spell}, skipped.");
                                return false;
                            }
                            if (e.Event.spellHit.school != 0 && ((SpellSchoolMask)e.Event.spellHit.school & spellInfo.SchoolMask) != spellInfo.SchoolMask)
                            {
                                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses Spell entry {e.Event.spellHit.spell} with invalid school mask, skipped.");
                                return false;
                            }
                        }
                        if (!IsMinMaxValid(e, e.Event.spellHit.cooldownMin, e.Event.spellHit.cooldownMax))
                            return false;
                        break;
                    case SmartEvents.OocLos:
                    case SmartEvents.IcLos:
                        if (!IsMinMaxValid(e, e.Event.los.cooldownMin, e.Event.los.cooldownMax))
                            return false;
                        break;
                    case SmartEvents.Respawn:
                        if (e.Event.respawn.type == (uint)SmartRespawnCondition.Map && CliDB.MapStorage.LookupByKey(e.Event.respawn.map) == null)
                        {
                            Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Map entry {e.Event.respawn.map}, skipped.");
                            return false;
                        }
                        if (e.Event.respawn.type == (uint)SmartRespawnCondition.Area && !CliDB.AreaTableStorage.ContainsKey(e.Event.respawn.area))
                        {
                            Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Area entry {e.Event.respawn.area}, skipped.");
                            return false;
                        }
                        break;
                    case SmartEvents.FriendlyHealth:
                        if (!NotNULL(e, e.Event.friendlyHealth.radius))
                            return false;

                        if (!IsMinMaxValid(e, e.Event.friendlyHealth.repeatMin, e.Event.friendlyHealth.repeatMax))
                            return false;
                        break;
                    case SmartEvents.FriendlyIsCc:
                        if (!IsMinMaxValid(e, e.Event.friendlyCC.repeatMin, e.Event.friendlyCC.repeatMax))
                            return false;
                        break;
                    case SmartEvents.FriendlyMissingBuff:
                    {
                        if (!IsSpellValid(e, e.Event.missingBuff.spell))
                            return false;

                        if (!NotNULL(e, e.Event.missingBuff.radius))
                            return false;

                        if (!IsMinMaxValid(e, e.Event.missingBuff.repeatMin, e.Event.missingBuff.repeatMax))
                            return false;
                        break;
                    }
                    case SmartEvents.Kill:
                        if (!IsMinMaxValid(e, e.Event.kill.cooldownMin, e.Event.kill.cooldownMax))
                            return false;

                        if (e.Event.kill.creature != 0 && !IsCreatureValid(e, e.Event.kill.creature))
                            return false;
                        break;
                    case SmartEvents.VictimCasting:
                        if (e.Event.targetCasting.spellId > 0 && !Global.SpellMgr.HasSpellInfo(e.Event.targetCasting.spellId, Difficulty.None))
                        {
                            Log.outError(LogFilter.Sql, $"SmartAIMgr: Entry {e.EntryOrGuid} SourceType {e.GetScriptType()} Event {e.EventId} Action {e.GetActionType()} uses non-existent Spell entry {e.Event.spellHit.spell}, skipped.");
                            return false;
                        }

                        if (!IsMinMaxValid(e, e.Event.minMax.repeatMin, e.Event.minMax.repeatMax))
                            return false;
                        break;
                    case SmartEvents.PassengerBoarded:
                    case SmartEvents.PassengerRemoved:
                        if (!IsMinMaxValid(e, e.Event.minMax.repeatMin, e.Event.minMax.repeatMax))
                            return false;
                        break;
                    case SmartEvents.SummonDespawned:
                    case SmartEvents.SummonedUnit:
                        if (e.Event.summoned.creature != 0 && !IsCreatureValid(e, e.Event.summoned.creature))
                            return false;

                        if (!IsMinMaxValid(e, e.Event.summoned.cooldownMin, e.Event.summoned.cooldownMax))
                            return false;
                        break;
                    case SmartEvents.AcceptedQuest:
                    case SmartEvents.RewardQuest:
                        if (e.Event.quest.questId != 0 && !IsQuestValid(e, e.Event.quest.questId))
                            return false;

                        if (!IsMinMaxValid(e, e.Event.quest.cooldownMin, e.Event.quest.cooldownMax))
                            return false;
                        break;
                    case SmartEvents.ReceiveEmote:
                    {
                        if (e.Event.emote.emoteId != 0 && !IsTextEmoteValid(e, e.Event.emote.emoteId))
                            return false;

                        if (!IsMinMaxValid(e, e.Event.emote.cooldownMin, e.Event.emote.cooldownMax))
                            return false;
                        break;
                    }
                    case SmartEvents.HasAura:
                    case SmartEvents.TargetBuffed:
                    {
                        if (!IsSpellValid(e, e.Event.aura.spell))
                            return false;

                        if (!IsMinMaxValid(e, e.Event.aura.repeatMin, e.Event.aura.repeatMax))
                            return false;
                        break;
                    }
                    case SmartEvents.TransportAddcreature:
                    {
                        if (e.Event.transportAddCreature.creature != 0 && !IsCreatureValid(e, e.Event.transportAddCreature.creature))
                            return false;
                        break;
                    }
                    case SmartEvents.Movementinform:
                    {
                        if (MotionMaster.IsInvalidMovementGeneratorType((MovementGeneratorType)e.Event.movementInform.type))
                        {
                            Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses invalid Motion type {e.Event.movementInform.type}, skipped.");
                            return false;
                        }
                        break;
                    }
                    case SmartEvents.DataSet:
                    {
                        if (!IsMinMaxValid(e, e.Event.dataSet.cooldownMin, e.Event.dataSet.cooldownMax))
                            return false;
                        break;
                    }
                    case SmartEvents.AreatriggerOntrigger:
                    {
                        if (e.Event.areatrigger.id != 0 && (e.GetScriptType() == SmartScriptType.AreaTriggerEntity || e.GetScriptType() == SmartScriptType.AreaTriggerEntityServerside))
                        {
                            Log.outError(LogFilter.Sql, $"SmartAIMgr: Entry {e.EntryOrGuid} SourceType {e.GetScriptType()} Event {e.EventId} Action {e.GetActionType()} areatrigger param not supported for SMART_SCRIPT_TYPE_AREATRIGGER_ENTITY and SMART_SCRIPT_TYPE_AREATRIGGER_ENTITY_SERVERSIDE, skipped.");
                            return false;
                        }

                        if (e.Event.areatrigger.id != 0 && !IsAreaTriggerValid(e, e.Event.areatrigger.id))
                            return false;
                        break;
                    }
                    case SmartEvents.TextOver:
                    {
                        if (!IsTextValid(e, e.Event.textOver.textGroupID))
                            return false;
                        break;
                    }
                    case SmartEvents.PhaseChange:
                    {
                        if (e.Event.eventPhaseChange.phasemask == 0)
                        {
                            Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} has no param set, event won't be executed!.");
                            return false;
                        }

                        if (e.Event.eventPhaseChange.phasemask > (uint)SmartEventPhaseBits.All)
                        {
                            Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses invalid phasemask {e.Event.eventPhaseChange.phasemask}, skipped.");
                            return false;
                        }

                        if (e.Event.event_phase_mask != 0 && (e.Event.event_phase_mask & e.Event.eventPhaseChange.phasemask) == 0)
                        {
                            Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses event phasemask {e.Event.event_phase_mask} and incompatible event_param1 {e.Event.eventPhaseChange.phasemask}, skipped.");
                            return false;
                        }
                        break;
                    }
                    case SmartEvents.IsBehindTarget:
                    {
                        if (!IsMinMaxValid(e, e.Event.behindTarget.cooldownMin, e.Event.behindTarget.cooldownMax))
                            return false;
                        break;
                    }
                    case SmartEvents.GameEventStart:
                    case SmartEvents.GameEventEnd:
                    {
                        var events = Global.GameEventMgr.GetEventMap();
                        if (e.Event.gameEvent.gameEventId >= events.Length || !events[e.Event.gameEvent.gameEventId].IsValid())
                            return false;

                        break;
                    }
                    case SmartEvents.ActionDone:
                    {
                        if (e.Event.doAction.eventId > EventId.Charge)
                        {
                            Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses invalid event id {e.Event.doAction.eventId}, skipped.");
                            return false;
                        }
                        break;
                    }
                    case SmartEvents.FriendlyHealthPCT:
                        if (!IsMinMaxValid(e, e.Event.friendlyHealthPct.repeatMin, e.Event.friendlyHealthPct.repeatMax))
                            return false;

                        if (e.Event.friendlyHealthPct.maxHpPct > 100 || e.Event.friendlyHealthPct.minHpPct > 100)
                        {
                            Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} has pct value above 100, skipped.");
                            return false;
                        }

                        switch (e.GetTargetType())
                        {
                            case SmartTargets.CreatureRange:
                            case SmartTargets.CreatureGuid:
                            case SmartTargets.CreatureDistance:
                            case SmartTargets.ClosestCreature:
                            case SmartTargets.ClosestPlayer:
                            case SmartTargets.PlayerRange:
                            case SmartTargets.PlayerDistance:
                                break;
                            default:
                                Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses invalid target_type {e.GetTargetType()}, skipped.");
                                return false;
                        }
                        break;
                    case SmartEvents.DistanceCreature:
                        if (e.Event.distance.guid == 0 && e.Event.distance.entry == 0)
                        {
                            Log.outError(LogFilter.Sql, "SmartAIMgr: Event SMART_EVENT_DISTANCE_CREATURE did not provide creature guid or entry, skipped.");
                            return false;
                        }

                        if (e.Event.distance.guid != 0 && e.Event.distance.entry != 0)
                        {
                            Log.outError(LogFilter.Sql, "SmartAIMgr: Event SMART_EVENT_DISTANCE_CREATURE provided both an entry and guid, skipped.");
                            return false;
                        }

                        if (e.Event.distance.guid != 0 && Global.ObjectMgr.GetCreatureData(e.Event.distance.guid) == null)
                        {
                            Log.outError(LogFilter.Sql, "SmartAIMgr: Event SMART_EVENT_DISTANCE_CREATURE using invalid creature guid {0}, skipped.", e.Event.distance.guid);
                            return false;
                        }

                        if (e.Event.distance.entry != 0 && Global.ObjectMgr.GetCreatureTemplate(e.Event.distance.entry) == null)
                        {
                            Log.outError(LogFilter.Sql, "SmartAIMgr: Event SMART_EVENT_DISTANCE_CREATURE using invalid creature entry {0}, skipped.", e.Event.distance.entry);
                            return false;
                        }
                        break;
                    case SmartEvents.DistanceGameobject:
                        if (e.Event.distance.guid == 0 && e.Event.distance.entry == 0)
                        {
                            Log.outError(LogFilter.Sql, "SmartAIMgr: Event SMART_EVENT_DISTANCE_GAMEOBJECT did not provide gameobject guid or entry, skipped.");
                            return false;
                        }

                        if (e.Event.distance.guid != 0 && e.Event.distance.entry != 0)
                        {
                            Log.outError(LogFilter.Sql, "SmartAIMgr: Event SMART_EVENT_DISTANCE_GAMEOBJECT provided both an entry and guid, skipped.");
                            return false;
                        }

                        if (e.Event.distance.guid != 0 && Global.ObjectMgr.GetGameObjectData(e.Event.distance.guid) == null)
                        {
                            Log.outError(LogFilter.Sql, "SmartAIMgr: Event SMART_EVENT_DISTANCE_GAMEOBJECT using invalid gameobject guid {0}, skipped.", e.Event.distance.guid);
                            return false;
                        }

                        if (e.Event.distance.entry != 0 && Global.ObjectMgr.GetGameObjectTemplate(e.Event.distance.entry) == null)
                        {
                            Log.outError(LogFilter.Sql, "SmartAIMgr: Event SMART_EVENT_DISTANCE_GAMEOBJECT using invalid gameobject entry {0}, skipped.", e.Event.distance.entry);
                            return false;
                        }
                        break;
                    case SmartEvents.CounterSet:
                        if (!IsMinMaxValid(e, e.Event.counter.cooldownMin, e.Event.counter.cooldownMax))
                            return false;

                        if (e.Event.counter.id == 0)
                        {
                            Log.outError(LogFilter.Sql, "SmartAIMgr: Event SMART_EVENT_COUNTER_SET using invalid counter id {0}, skipped.", e.Event.counter.id);
                            return false;
                        }

                        if (e.Event.counter.value == 0)
                        {
                            Log.outError(LogFilter.Sql, "SmartAIMgr: Event SMART_EVENT_COUNTER_SET using invalid value {0}, skipped.", e.Event.counter.value);
                            return false;
                        }
                        break;
                    case SmartEvents.QuestObjCompletion:
                        if (Global.ObjectMgr.GetQuestObjective(e.Event.questObjective.id) == null)
                        {
                            Log.outError(LogFilter.Sql, $"SmartAIMgr: Event SMART_EVENT_QUEST_OBJ_COPLETETION using invalid objective id {e.Event.questObjective.id}, skipped.");
                            return false;
                        }
                        break;
                    case SmartEvents.QuestAccepted:
                    case SmartEvents.QuestCompletion:
                    case SmartEvents.QuestFail:
                    case SmartEvents.QuestRewarded:
                        break;
                    case SmartEvents.Link:
                    case SmartEvents.GoLootStateChanged:
                    case SmartEvents.GoEventInform:
                    case SmartEvents.TimedEventTriggered:
                    case SmartEvents.InstancePlayerEnter:
                    case SmartEvents.TransportRelocate:
                    case SmartEvents.Charmed:
                    case SmartEvents.CharmedTarget:
                    case SmartEvents.CorpseRemoved:
                    case SmartEvents.AiInit:
                    case SmartEvents.TransportAddplayer:
                    case SmartEvents.TransportRemovePlayer:
                    case SmartEvents.Aggro:
                    case SmartEvents.Death:
                    case SmartEvents.Evade:
                    case SmartEvents.ReachedHome:
                    case SmartEvents.Reset:
                    case SmartEvents.JustSummoned:
                    case SmartEvents.WaypointStart:
                    case SmartEvents.WaypointReached:
                    case SmartEvents.WaypointPaused:
                    case SmartEvents.WaypointResumed:
                    case SmartEvents.WaypointStopped:
                    case SmartEvents.WaypointEnded:
                    case SmartEvents.GossipSelect:
                    case SmartEvents.GossipHello:
                    case SmartEvents.JustCreated:
                    case SmartEvents.FollowCompleted:
                    case SmartEvents.OnSpellclick:
                    case SmartEvents.SceneStart:
                    case SmartEvents.SceneCancel:
                    case SmartEvents.SceneComplete:
                    case SmartEvents.SceneTrigger:
                    case SmartEvents.SpellEffectHit:
                        break;
                    default:
                        Log.outError(LogFilter.ScriptsAi, "SmartAIMgr: Not handled event_type({0}), Entry {1} SourceType {2} Event {3} Action {4}, skipped.", e.GetEventType(), e.EntryOrGuid, e.GetScriptType(), e.EventId, e.GetActionType());
                        return false;
                }
            }

            switch (e.GetActionType())
            {
                case SmartActions.Talk:
                case SmartActions.SimpleTalk:
                {
                    if (!IsTextValid(e, e.Action.talk.textGroupId))
                        return false;
                    break;
                }
                case SmartActions.SetFaction:
                    if (e.Action.faction.factionID != 0 && CliDB.FactionTemplateStorage.LookupByKey(e.Action.faction.factionID) == null)
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Faction {e.Action.faction.factionID}, skipped.");
                        return false;
                    }
                    break;
                case SmartActions.MorphToEntryOrModel:
                case SmartActions.MountToEntryOrModel:
                    if (e.Action.morphOrMount.creature != 0 || e.Action.morphOrMount.model != 0)
                    {
                        if (e.Action.morphOrMount.creature > 0 && Global.ObjectMgr.GetCreatureTemplate(e.Action.morphOrMount.creature) == null)
                        {
                            Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Creature entry {e.Action.morphOrMount.creature}, skipped.");
                            return false;
                        }

                        if (e.Action.morphOrMount.model != 0)
                        {
                            if (e.Action.morphOrMount.creature != 0)
                            {
                                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} has ModelID set with also set CreatureId, skipped.");
                                return false;
                            }
                            else if (!CliDB.CreatureDisplayInfoStorage.ContainsKey(e.Action.morphOrMount.model))
                            {
                                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Model id {e.Action.morphOrMount.model}, skipped.");
                                return false;
                            }
                        }
                    }
                    break;
                case SmartActions.Sound:
                    if (!IsSoundValid(e, e.Action.sound.soundId))
                        return false;
                    break;
                case SmartActions.SetEmoteState:
                case SmartActions.PlayEmote:
                    if (!IsEmoteValid(e, e.Action.emote.emoteId))
                        return false;
                    break;
                case SmartActions.PlayAnimkit:
                    if (e.Action.animKit.animKit != 0 && !IsAnimKitValid(e, e.Action.animKit.animKit))
                        return false;

                    if (e.Action.animKit.type > 3)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses invalid AnimKit type {e.Action.animKit.type}, skipped.");
                        return false;
                    }
                    break;
                case SmartActions.PlaySpellVisualKit:
                    if (e.Action.spellVisualKit.spellVisualKitId != 0 && !IsSpellVisualKitValid(e, e.Action.spellVisualKit.spellVisualKitId))
                        return false;
                    break;
                case SmartActions.FailQuest:
                case SmartActions.OfferQuest:
                    if (e.Action.quest.questId == 0 || !IsQuestValid(e, e.Action.quest.questId))
                        return false;
                    break;
                case SmartActions.ActivateTaxi:
                {
                    if (!CliDB.TaxiPathStorage.ContainsKey(e.Action.taxi.id))
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses invalid Taxi path ID {e.Action.taxi.id}, skipped.");
                        return false;
                    }
                    break;
                }
                case SmartActions.RandomEmote:
                    if (e.Action.randomEmote.emote1 != 0 && !IsEmoteValid(e, e.Action.randomEmote.emote1))
                        return false;
                    if (e.Action.randomEmote.emote2 != 0 && !IsEmoteValid(e, e.Action.randomEmote.emote2))
                        return false;
                    if (e.Action.randomEmote.emote3 != 0 && !IsEmoteValid(e, e.Action.randomEmote.emote3))
                        return false;
                    if (e.Action.randomEmote.emote4 != 0 && !IsEmoteValid(e, e.Action.randomEmote.emote4))
                        return false;
                    if (e.Action.randomEmote.emote5 != 0 && !IsEmoteValid(e, e.Action.randomEmote.emote5))
                        return false;
                    if (e.Action.randomEmote.emote6 != 0 && !IsEmoteValid(e, e.Action.randomEmote.emote6))
                        return false;
                    break;
                case SmartActions.RandomSound:
                    if (e.Action.randomSound.sound1 != 0 && !IsSoundValid(e, e.Action.randomSound.sound1))
                        return false;
                    if (e.Action.randomSound.sound2 != 0 && !IsSoundValid(e, e.Action.randomSound.sound2))
                        return false;
                    if (e.Action.randomSound.sound3 != 0 && !IsSoundValid(e, e.Action.randomSound.sound3))
                        return false;
                    if (e.Action.randomSound.sound4 != 0 && !IsSoundValid(e, e.Action.randomSound.sound4))
                        return false;
                    break;
                case SmartActions.Cast:
                {
                    if (!IsSpellValid(e, e.Action.cast.spell))
                        return false;

                    SpellInfo spellInfo = Global.SpellMgr.GetSpellInfo(e.Action.cast.spell, Difficulty.None);
                    foreach (var spellEffectInfo in spellInfo.GetEffects())
                    {
                        if (spellEffectInfo.IsEffect(SpellEffectName.KillCredit) || spellEffectInfo.IsEffect(SpellEffectName.KillCredit2))
                        {
                            if (spellEffectInfo.TargetA.GetTarget() == Targets.UnitCaster)
                                Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} Effect: SPELL_EFFECT_KILL_CREDIT: (SpellId: {e.Action.cast.spell} targetA: {spellEffectInfo.TargetA.GetTarget()} - targetB: {spellEffectInfo.TargetB.GetTarget()}) has invalid target for this Action");
                        }
                    }
                    break;
                }
                case SmartActions.CrossCast:
                {
                    if (!IsSpellValid(e, e.Action.crossCast.spell))
                        return false;
                    break;
                }
                case SmartActions.InvokerCast:
                    if (e.GetScriptType() != SmartScriptType.TimedActionlist && e.GetEventType() != SmartEvents.Link && !EventHasInvoker(e.Event.type))
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} has invoker cast action, but event does not provide any invoker!");
                        return false;
                    }
                    // no break
                    goto case SmartActions.AddAura;
                case SmartActions.SelfCast:
                case SmartActions.AddAura:
                    if (!IsSpellValid(e, e.Action.cast.spell))
                        return false;
                    break;
                case SmartActions.CallAreaexploredoreventhappens:
                case SmartActions.CallGroupeventhappens:
                    Quest qid = Global.ObjectMgr.GetQuestTemplate(e.Action.quest.questId);
                    if (qid != null)
                    {
                        if (!qid.HasSpecialFlag(QuestSpecialFlags.ExplorationOrEvent))
                        {
                            Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} SpecialFlags for Quest entry {e.Action.quest.questId} does not include FLAGS_EXPLORATION_OR_EVENT(2), skipped.");
                            return false;
                        }
                    }
                    else
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Quest entry {e.Action.quest.questId}, skipped.");
                        return false;
                    }
                    break;
                case SmartActions.SetEventPhase:
                    if (e.Action.setEventPhase.phase >= (uint)SmartPhase.Max)
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} attempts to set phase {e.Action.setEventPhase.phase}. Phase mask cannot be used past phase {SmartPhase.Max - 1}, skipped.");
                        return false;
                    }
                    break;
                case SmartActions.IncEventPhase:
                    if (e.Action.incEventPhase.inc == 0 && e.Action.incEventPhase.dec == 0)
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} is incrementing phase by 0, skipped.");
                        return false;
                    }
                    else if (e.Action.incEventPhase.inc > (uint)SmartPhase.Max || e.Action.incEventPhase.dec > (uint)SmartPhase.Max)
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} attempts to increment phase by too large value, skipped.");
                        return false;
                    }
                    break;
                case SmartActions.RemoveAurasFromSpell:
                    if (e.Action.removeAura.spell != 0 && !IsSpellValid(e, e.Action.removeAura.spell))
                        return false;
                    break;
                case SmartActions.RandomPhase:
                {
                    if (e.Action.randomPhase.phase1 >= (uint)SmartPhase.Max ||
                        e.Action.randomPhase.phase2 >= (uint)SmartPhase.Max ||
                        e.Action.randomPhase.phase3 >= (uint)SmartPhase.Max ||
                        e.Action.randomPhase.phase4 >= (uint)SmartPhase.Max ||
                        e.Action.randomPhase.phase5 >= (uint)SmartPhase.Max ||
                        e.Action.randomPhase.phase6 >= (uint)SmartPhase.Max)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: Entry {e.EntryOrGuid} SourceType {e.GetScriptType()} Event {e.EventId} Action {e.GetActionType()} attempts to set invalid phase, skipped.");
                        return false;
                    }

                    break;
                }
                case SmartActions.RandomPhaseRange:       //PhaseMin, PhaseMax
                {
                    if (e.Action.randomPhaseRange.phaseMin >= (uint)SmartPhase.Max ||
                        e.Action.randomPhaseRange.phaseMax >= (uint)SmartPhase.Max)
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} attempts to set invalid phase, skipped.");
                        return false;
                    }
                    if (!IsMinMaxValid(e, e.Action.randomPhaseRange.phaseMin, e.Action.randomPhaseRange.phaseMax))
                        return false;
                    break;
                }
                case SmartActions.SummonCreature:
                    if (!IsCreatureValid(e, e.Action.summonCreature.creature))
                        return false;

                    if (e.Action.summonCreature.type < (uint)TempSummonType.TimedOrDeadDespawn || e.Action.summonCreature.type > (uint)TempSummonType.ManualDespawn)
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses incorrect TempSummonType {e.Action.summonCreature.type}, skipped.");
                        return false;
                    }
                    break;
                case SmartActions.CallKilledmonster:
                    if (!IsCreatureValid(e, e.Action.killedMonster.creature))
                        return false;

                    if (e.GetTargetType() == SmartTargets.Position)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses incorrect TargetType {e.GetTargetType()}, skipped.");
                        return false;
                    }
                    break;
                case SmartActions.UpdateTemplate:
                    if (e.Action.updateTemplate.creature != 0 && !IsCreatureValid(e, e.Action.updateTemplate.creature))
                        return false;
                    break;
                case SmartActions.SetSheath:
                    if (e.Action.setSheath.sheath != 0 && e.Action.setSheath.sheath >= (uint)SheathState.Max)
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses incorrect Sheath state {e.Action.setSheath.sheath}, skipped.");
                        return false;
                    }
                    break;
                case SmartActions.SetReactState:
                {
                    if (e.Action.react.state > (uint)ReactStates.Aggressive)
                    {
                        Log.outError(LogFilter.ScriptsAi, "SmartAIMgr: Creature {0} Event {1} Action {2} uses invalid React State {3}, skipped.", e.EntryOrGuid, e.EventId, e.GetActionType(), e.Action.react.state);
                        return false;
                    }
                    break;
                }
                case SmartActions.SummonGo:
                    if (!IsGameObjectValid(e, e.Action.summonGO.entry))
                        return false;
                    break;
                case SmartActions.RemoveItem:
                    if (!IsItemValid(e, e.Action.item.entry))
                        return false;

                    if (!NotNULL(e, e.Action.item.count))
                        return false;
                    break;
                case SmartActions.AddItem:
                    if (!IsItemValid(e, e.Action.item.entry))
                        return false;

                    if (!NotNULL(e, e.Action.item.count))
                        return false;
                    break;
                case SmartActions.Teleport:
                    if (!CliDB.MapStorage.ContainsKey(e.Action.teleport.mapID))
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Map entry {e.Action.teleport.mapID}, skipped.");
                        return false;
                    }
                    break;
                case SmartActions.InstallAiTemplate:
                    if (e.Action.installTtemplate.id >= (uint)SmartAITemplate.End)
                    {
                        Log.outError(LogFilter.ScriptsAi, "SmartAIMgr: Creature {0} Event {1} Action {2} uses non-existent AI template id {3}, skipped.", e.EntryOrGuid, e.EventId, e.GetActionType(), e.Action.installTtemplate.id);
                        return false;
                    }
                    break;
                case SmartActions.WpStop:
                    if (e.Action.wpStop.quest != 0 && !IsQuestValid(e, e.Action.wpStop.quest))
                        return false;
                    break;
                case SmartActions.WpStart:
                {
                    WaypointPath path = GetPath(e.Action.wpStart.pathID);
                    if (path == null || path.nodes.Empty())
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent WaypointPath id {e.Action.wpStart.pathID}, skipped.");
                        return false;
                    }

                    if (e.Action.wpStart.quest != 0 && !IsQuestValid(e, e.Action.wpStart.quest))
                        return false;

                    if (e.Action.wpStart.reactState > (uint)ReactStates.Aggressive)
                    {
                        Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses invalid React State {e.Action.wpStart.reactState}, skipped.");
                        return false;
                    }
                    break;
                }
                case SmartActions.CreateTimedEvent:
                {
                    if (!IsMinMaxValid(e, e.Action.timeEvent.min, e.Action.timeEvent.max))
                        return false;

                    if (!IsMinMaxValid(e, e.Action.timeEvent.repeatMin, e.Action.timeEvent.repeatMax))
                        return false;
                    break;
                }
                case SmartActions.CallRandomRangeTimedActionlist:
                {
                    if (!IsMinMaxValid(e, e.Action.randTimedActionList.actionList1, e.Action.randTimedActionList.actionList2))
                        return false;
                    break;
                }
                case SmartActions.SetPower:
                case SmartActions.AddPower:
                case SmartActions.RemovePower:
                    if (e.Action.power.powerType > (int)PowerType.Max)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses non-existent Power {e.Action.power.powerType}, skipped.");
                        return false;
                    }
                    break;
                case SmartActions.GameEventStop:
                {
                    uint eventId = e.Action.gameEventStop.id;

                    var events = Global.GameEventMgr.GetEventMap();
                    if (eventId < 1 || eventId >= events.Length)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses non-existent event, eventId {e.Action.gameEventStop.id}, skipped.");
                        return false;
                    }

                    GameEventData eventData = events[eventId];
                    if (!eventData.IsValid())
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses non-existent event, eventId {e.Action.gameEventStop.id}, skipped.");
                        return false;
                    }
                    break;
                }
                case SmartActions.GameEventStart:
                {
                    uint eventId = e.Action.gameEventStart.id;

                    var events = Global.GameEventMgr.GetEventMap();
                    if (eventId < 1 || eventId >= events.Length)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses non-existent event, eventId {e.Action.gameEventStart.id}, skipped.");
                        return false;
                    }

                    GameEventData eventData = events[eventId];
                    if (!eventData.IsValid())
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses non-existent event, eventId {e.Action.gameEventStart.id}, skipped.");
                        return false;
                    }
                    break;
                }
                case SmartActions.Equip:
                {
                    if (e.GetScriptType() == SmartScriptType.Creature)
                    {
                        sbyte equipId = (sbyte)e.Action.equip.entry;

                        if (equipId != 0 && Global.ObjectMgr.GetEquipmentInfo((uint)e.EntryOrGuid, equipId) == null)
                        {
                            Log.outError(LogFilter.Sql, "SmartScript: SMART_ACTION_EQUIP uses non-existent equipment info id {0} for creature {1}, skipped.", equipId, e.EntryOrGuid);
                            return false;
                        }
                    }
                    break;
                }
                case SmartActions.SetInstData:
                {
                    if (e.Action.setInstanceData.type > 1)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses invalid data type {e.Action.setInstanceData.type} (value range 0-1), skipped.");
                        return false;
                    }
                    else if (e.Action.setInstanceData.type == 1)
                    {
                        if (e.Action.setInstanceData.data > (int)EncounterState.ToBeDecided)
                        {
                            Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses invalid boss state {e.Action.setInstanceData.data} (value range 0-5), skipped.");
                            return false;
                        }
                    }
                    break;
                }
                case SmartActions.SetIngamePhaseId:
                {
                    uint phaseId = e.Action.ingamePhaseId.id;
                    uint apply = e.Action.ingamePhaseId.apply;

                    if (apply != 0 && apply != 1)
                    {
                        Log.outError(LogFilter.Sql, "SmartScript: SMART_ACTION_SET_INGAME_PHASE_ID uses invalid apply value {0} (Should be 0 or 1) for creature {1}, skipped", apply, e.EntryOrGuid);
                        return false;
                    }

                    if (!CliDB.PhaseStorage.ContainsKey(phaseId))
                    {
                        Log.outError(LogFilter.Sql, "SmartScript: SMART_ACTION_SET_INGAME_PHASE_ID uses invalid phaseid {0} for creature {1}, skipped", phaseId, e.EntryOrGuid);
                        return false;
                    }
                    break;
                }
                case SmartActions.SetIngamePhaseGroup:
                {
                    uint phaseGroup = e.Action.ingamePhaseGroup.groupId;
                    uint apply = e.Action.ingamePhaseGroup.apply;

                    if (apply != 0 && apply != 1)
                    {
                        Log.outError(LogFilter.Sql, "SmartScript: SMART_ACTION_SET_INGAME_PHASE_GROUP uses invalid apply value {0} (Should be 0 or 1) for creature {1}, skipped", apply, e.EntryOrGuid);
                        return false;
                    }

                    if (Global.DB2Mgr.GetPhasesForGroup(phaseGroup).Empty())
                    {
                        Log.outError(LogFilter.Sql, "SmartScript: SMART_ACTION_SET_INGAME_PHASE_GROUP uses invalid phase group id {0} for creature {1}, skipped", phaseGroup, e.EntryOrGuid);
                        return false;
                    }
                    break;
                }
                case SmartActions.ScenePlay:
                {
                    if (Global.ObjectMgr.GetSceneTemplate(e.Action.scene.sceneId) == null)
                    {
                        Log.outError(LogFilter.Sql, "SmartScript: SMART_ACTION_SCENE_PLAY uses sceneId {0} but scene don't exist, skipped", e.Action.scene.sceneId);
                        return false;
                    }

                    break;
                }
                case SmartActions.SceneCancel:
                {
                    if (Global.ObjectMgr.GetSceneTemplate(e.Action.scene.sceneId) == null)
                    {
                        Log.outError(LogFilter.Sql, "SmartScript: SMART_ACTION_SCENE_CANCEL uses sceneId {0} but scene don't exist, skipped", e.Action.scene.sceneId);
                        return false;
                    }

                    break;
                }
                case SmartActions.RemoveAurasByType:
                {
                    if (e.Action.auraType.type >= (uint)AuraType.Total)
                    {
                        Log.outError(LogFilter.Sql, $"Entry {e.EntryOrGuid} SourceType {e.GetScriptType()} Event {e.EventId} Action {e.GetActionType()} uses invalid data type {e.Action.auraType.type} (value range 0-TOTAL_AURAS), skipped.");
                        return false;
                    }
                    break;
                }
                case SmartActions.RespawnBySpawnId:
                {
                    if (Global.ObjectMgr.GetSpawnData((SpawnObjectType)e.Action.respawnData.spawnType, e.Action.respawnData.spawnId) == null)
                    {
                        Log.outError(LogFilter.Sql, $"Entry {e.EntryOrGuid} SourceType {e.GetScriptType()} Event {e.EventId} Action {e.GetActionType()} specifies invalid spawn data ({e.Action.respawnData.spawnType},{e.Action.respawnData.spawnId})");
                        return false;
                    }
                    break;
                }
                case SmartActions.EnableTempGobj:
                {
                    if (e.Action.enableTempGO.duration == 0)
                    {
                        Log.outError(LogFilter.Sql, $"Entry {e.EntryOrGuid} SourceType {e.GetScriptType()} Event {e.EventId} Action {e.GetActionType()} does not specify duration");
                        return false;
                    }
                    break;
                }
                case SmartActions.PlayCinematic:
                {
                    if (!CliDB.CinematicSequencesStorage.ContainsKey(e.Action.cinematic.entry))
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: SMART_ACTION_PLAY_CINEMATIC {e} uses invalid entry {e.Action.cinematic.entry}, skipped.");
                        return false;
                    }

                    break;
                }
                case SmartActions.PauseMovement:
                {
                    if (e.Action.pauseMovement.pauseTimer == 0)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} does not specify pause duration");
                        return false;
                    }
                    break;
                }
                case SmartActions.SetMovementSpeed:
                {
                    if (e.Action.movementSpeed.movementType >= (int)MovementGeneratorType.Max)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: Entry {e.EntryOrGuid} SourceType {e.GetScriptType()} Event {e.EventId} Action {e.GetActionType()} uses invalid movementType {e.Action.movementSpeed.movementType}, skipped.");
                        return false;
                    }

                    if (e.Action.movementSpeed.speedInteger == 0 && e.Action.movementSpeed.speedFraction == 0)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: Entry {e.EntryOrGuid} SourceType {e.GetScriptType()} Event {e.EventId} Action {e.GetActionType()} uses speed 0, skipped.");
                        return false;
                    }

                    break;
                }
                case SmartActions.OverrideLight:
                {
                    var areaEntry = CliDB.AreaTableStorage.LookupByKey(e.Action.overrideLight.zoneId);
                    if (areaEntry == null)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses non-existent zoneId {e.Action.overrideLight.zoneId}, skipped.");
                        return false;
                    }

                    if (areaEntry.ParentAreaID != 0)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses subzone (ID: {e.Action.overrideLight.zoneId}) instead of zone, skipped.");
                        return false;
                    }

                    if (!CliDB.LightStorage.ContainsKey(e.Action.overrideLight.areaLightId))
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses non-existent areaLightId {e.Action.overrideLight.areaLightId}, skipped.");
                        return false;
                    }

                    if (e.Action.overrideLight.overrideLightId != 0 && !CliDB.LightStorage.ContainsKey(e.Action.overrideLight.overrideLightId))
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses non-existent overrideLightId {e.Action.overrideLight.overrideLightId}, skipped.");
                        return false;
                    }

                    break;
                }
                case SmartActions.OverrideWeather:
                {
                    var areaEntry = CliDB.AreaTableStorage.LookupByKey(e.Action.overrideWeather.zoneId);
                    if (areaEntry == null)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses non-existent zoneId {e.Action.overrideWeather.zoneId}, skipped.");
                        return false;
                    }

                    if (areaEntry.ParentAreaID != 0)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses subzone (ID: {e.Action.overrideWeather.zoneId}) instead of zone, skipped.");
                        return false;
                    }

                    break;
                }
                case SmartActions.CreateConversation:
                {
                    if (Global.ConversationDataStorage.GetConversationTemplate(e.Action.conversation.id) == null)
                    {
                        Log.outError(LogFilter.Sql, $"SmartAIMgr: SMART_ACTION_CREATE_CONVERSATION Entry {e.EntryOrGuid} SourceType {e.GetScriptType()} Event {e.EventId} Action {e.GetActionType()} uses invalid entry {e.Action.conversation.id}, skipped.");
                        return false;
                    }

                    break;
                }
                case SmartActions.StartClosestWaypoint:
                case SmartActions.Follow:
                case SmartActions.SetOrientation:
                case SmartActions.StoreTargetList:
                case SmartActions.Evade:
                case SmartActions.FleeForAssist:
                case SmartActions.CombatStop:
                case SmartActions.Die:
                case SmartActions.SetInCombatWithZone:
                case SmartActions.SetActive:
                case SmartActions.WpResume:
                case SmartActions.KillUnit:
                case SmartActions.SetInvincibilityHpLevel:
                case SmartActions.ResetGobject:
                case SmartActions.AttackStart:
                case SmartActions.ThreatAllPct:
                case SmartActions.ThreatSinglePct:
                case SmartActions.SetInstData64:
                case SmartActions.AutoAttack:
                case SmartActions.AllowCombatMovement:
                case SmartActions.CallForHelp:
                case SmartActions.SetData:
                case SmartActions.SetVisibility:
                case SmartActions.WpPause:
                case SmartActions.SetDisableGravity:
                case SmartActions.SetCanFly:
                case SmartActions.SetRun:
                case SmartActions.SetSwim:
                case SmartActions.ForceDespawn:
                case SmartActions.SetUnitFlag:
                case SmartActions.RemoveUnitFlag:
                case SmartActions.Playmovie:
                case SmartActions.MoveToPos:
                case SmartActions.CloseGossip:
                case SmartActions.TriggerTimedEvent:
                case SmartActions.RemoveTimedEvent:
                case SmartActions.OverrideScriptBaseObject:
                case SmartActions.ResetScriptBaseObject:
                case SmartActions.ActivateGobject:
                case SmartActions.CallScriptReset:
                case SmartActions.SetRangedMovement:
                case SmartActions.CallTimedActionlist:
                case SmartActions.SetNpcFlag:
                case SmartActions.AddNpcFlag:
                case SmartActions.RemoveNpcFlag:
                case SmartActions.CallRandomTimedActionlist:
                case SmartActions.RandomMove:
                case SmartActions.SetUnitFieldBytes1:
                case SmartActions.RemoveUnitFieldBytes1:
                case SmartActions.InterruptSpell:
                case SmartActions.SendGoCustomAnim:
                case SmartActions.SetDynamicFlag:
                case SmartActions.AddDynamicFlag:
                case SmartActions.RemoveDynamicFlag:
                case SmartActions.JumpToPos:
                case SmartActions.SendGossipMenu:
                case SmartActions.GoSetLootState:
                case SmartActions.GoSetGoState:
                case SmartActions.SendTargetToTarget:
                case SmartActions.SetHomePos:
                case SmartActions.SetHealthRegen:
                case SmartActions.SetRoot:
                case SmartActions.SetGoFlag:
                case SmartActions.AddGoFlag:
                case SmartActions.RemoveGoFlag:
                case SmartActions.SummonCreatureGroup:
                case SmartActions.MoveOffset:
                case SmartActions.SetCorpseDelay:
                case SmartActions.DisableEvade:
                case SmartActions.SetSightDist:
                case SmartActions.Flee:
                case SmartActions.AddThreat:
                case SmartActions.LoadEquipment:
                case SmartActions.TriggerRandomTimedEvent:
                case SmartActions.SetCounter:
                case SmartActions.RemoveAllGameobjects:
                case SmartActions.SpawnSpawngroup:
                case SmartActions.DespawnSpawngroup:
                case SmartActions.AddToStoredTargetList:
                    break;
                default:
                    Log.outError(LogFilter.ScriptsAi, "SmartAIMgr: Not handled action_type({0}), event_type({1}), Entry {2} SourceType {3} Event {4}, skipped.", e.GetActionType(), e.GetEventType(), e.EntryOrGuid, e.GetScriptType(), e.EventId);
                    return false;
            }

            return true;
        }
        static bool IsAnimKitValid(SmartScriptHolder e, uint entry)
        {
            if (!CliDB.AnimKitStorage.ContainsKey(entry))
            {
                Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} uses non-existent AnimKit entry {entry}, skipped.");
                return false;
            }
            return true;
        }
        static bool IsTextValid(SmartScriptHolder e, uint id)
        {
            if (e.GetScriptType() != SmartScriptType.Creature)
                return true;

            uint entry;
            if (e.GetEventType() == SmartEvents.TextOver)
            {
                entry = e.Event.textOver.creatureEntry;
            }
            else
            {
                switch (e.GetTargetType())
                {
                    case SmartTargets.CreatureDistance:
                    case SmartTargets.CreatureRange:
                    case SmartTargets.ClosestCreature:
                        return true; // ignore
                    default:
                        if (e.EntryOrGuid < 0)
                        {
                            ulong guid = (ulong)-e.EntryOrGuid;
                            CreatureData data = Global.ObjectMgr.GetCreatureData(guid);
                            if (data == null)
                            {
                                Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} using non-existent Creature guid {guid}, skipped.");
                                return false;
                            }
                            else
                                entry = data.Id;
                        }
                        else
                            entry = (uint)e.EntryOrGuid;
                        break;
                }
            }

            if (entry == 0 || !Global.CreatureTextMgr.TextExist(entry, (byte)id))
            {
                Log.outError(LogFilter.Sql, $"SmartAIMgr: {e} using non-existent Text id {id}, skipped.");
                return false;
            }

            return true;
        }
        static bool IsCreatureValid(SmartScriptHolder e, uint entry)
        {
            if (Global.ObjectMgr.GetCreatureTemplate(entry) == null)
            {
                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Creature entry {entry}, skipped.");
                return false;
            }
            return true;
        }
        static bool IsGameObjectValid(SmartScriptHolder e, uint entry)
        {
            if (Global.ObjectMgr.GetGameObjectTemplate(entry) == null)
            {
                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent GameObject entry {entry}, skipped.");
                return false;
            }
            return true;
        }
        static bool IsQuestValid(SmartScriptHolder e, uint entry)
        {
            if (Global.ObjectMgr.GetQuestTemplate(entry) == null)
            {
                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Quest entry {entry}, skipped.");
                return false;
            }
            return true;
        }
        static bool IsSpellValid(SmartScriptHolder e, uint entry)
        {
            if (!Global.SpellMgr.HasSpellInfo(entry, Difficulty.None))
            {
                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Spell entry {entry}, skipped.");
                return false;
            }
            return true;
        }
        static bool IsMinMaxValid(SmartScriptHolder e, uint min, uint max)
        {
            if (max < min)
            {
                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses min/max params wrong ({min}/{max}), skipped.");
                return false;
            }
            return true;
        }
        static bool NotNULL(SmartScriptHolder e, uint data)
        {
            if (data == 0)
            {
                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} Parameter can not be NULL, skipped.");
                return false;
            }
            return true;
        }
        static bool IsEmoteValid(SmartScriptHolder e, uint entry)
        {
            if (!CliDB.EmotesStorage.ContainsKey(entry))
            {
                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Emote entry {entry}, skipped.");
                return false;
            }
            return true;
        }
        static bool IsItemValid(SmartScriptHolder e, uint entry)
        {
            if (!CliDB.ItemSparseStorage.ContainsKey(entry))
            {
                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Item entry {entry}, skipped.");
                return false;
            }
            return true;
        }
        static bool IsTextEmoteValid(SmartScriptHolder e, uint entry)
        {
            if (!CliDB.EmotesTextStorage.ContainsKey(entry))
            {
                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Text Emote entry {entry}, skipped.");
                return false;
            }
            return true;
        }
        static bool IsAreaTriggerValid(SmartScriptHolder e, uint entry)
        {
            if (!CliDB.AreaTriggerStorage.ContainsKey(entry))
            {
                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent AreaTrigger entry {entry}, skipped.");
                return false;
            }
            return true;
        }
        static bool IsSoundValid(SmartScriptHolder e, uint entry)
        {
            if (!CliDB.SoundKitStorage.ContainsKey(entry))
            {
                Log.outError(LogFilter.ScriptsAi, $"SmartAIMgr: {e} uses non-existent Sound entry {entry}, skipped.");
                return false;
            }
            return true;
        }

        public List<SmartScriptHolder> GetScript(int entry, SmartScriptType type)
        {
            List<SmartScriptHolder> temp = new();
            if (_eventMap[(uint)type].ContainsKey(entry))
            {
                foreach (var holder in _eventMap[(uint)type][entry])
                    temp.Add(new SmartScriptHolder(holder));
            }
            else
            {
                if (entry > 0)//first search is for guid (negative), do not drop error if not found
                    Log.outDebug(LogFilter.ScriptsAi, "SmartAIMgr.GetScript: Could not load Script for Entry {0} ScriptType {1}.", entry, type);
            }

            return temp;
        }

        public WaypointPath GetPath(uint id)
        {
            return _waypointStore.LookupByKey(id);
        }

        public static SmartScriptHolder FindLinkedSourceEvent(List<SmartScriptHolder> list, uint eventId)
        {
            var sch = list.Find(p => p.Link == eventId);
            if (sch != null)
                return sch;

            return null;
        }

        public SmartScriptHolder FindLinkedEvent(List<SmartScriptHolder> list, uint link)
        {
            var sch = list.Find(p => p.EventId == link && p.GetEventType() == SmartEvents.Link);
            if (sch != null)
                return sch;

            return null;
        }

        public static uint GetTypeMask(SmartScriptType smartScriptType) =>
            smartScriptType switch
            {
                SmartScriptType.Creature => SmartScriptTypeMaskId.Creature,
                SmartScriptType.GameObject => SmartScriptTypeMaskId.Gameobject,
                SmartScriptType.AreaTrigger => SmartScriptTypeMaskId.Areatrigger,
                SmartScriptType.Event => SmartScriptTypeMaskId.Event,
                SmartScriptType.Gossip => SmartScriptTypeMaskId.Gossip,
                SmartScriptType.Quest => SmartScriptTypeMaskId.Quest,
                SmartScriptType.Spell => SmartScriptTypeMaskId.Spell,
                SmartScriptType.Transport => SmartScriptTypeMaskId.Transport,
                SmartScriptType.Instance => SmartScriptTypeMaskId.Instance,
                SmartScriptType.TimedActionlist => SmartScriptTypeMaskId.TimedActionlist,
                SmartScriptType.Scene => SmartScriptTypeMaskId.Scene,
                SmartScriptType.AreaTriggerEntity => SmartScriptTypeMaskId.AreatrigggerEntity,
                SmartScriptType.AreaTriggerEntityServerside => SmartScriptTypeMaskId.AreatrigggerEntity,
                _ => 0,
            };

        public static uint GetEventMask(SmartEvents smartEvent) =>
            smartEvent switch
            {
                SmartEvents.UpdateIc => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.TimedActionlist,
                SmartEvents.UpdateOoc => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject + SmartScriptTypeMaskId.Instance + SmartScriptTypeMaskId.AreatrigggerEntity,
                SmartEvents.HealthPct => SmartScriptTypeMaskId.Creature,
                SmartEvents.ManaPct => SmartScriptTypeMaskId.Creature,
                SmartEvents.Aggro => SmartScriptTypeMaskId.Creature,
                SmartEvents.Kill => SmartScriptTypeMaskId.Creature,
                SmartEvents.Death => SmartScriptTypeMaskId.Creature,
                SmartEvents.Evade => SmartScriptTypeMaskId.Creature,
                SmartEvents.SpellHit => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.Range => SmartScriptTypeMaskId.Creature,
                SmartEvents.OocLos => SmartScriptTypeMaskId.Creature,
                SmartEvents.Respawn => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.TargetHealthPct => SmartScriptTypeMaskId.Creature,
                SmartEvents.VictimCasting => SmartScriptTypeMaskId.Creature,
                SmartEvents.FriendlyHealth => SmartScriptTypeMaskId.Creature,
                SmartEvents.FriendlyIsCc => SmartScriptTypeMaskId.Creature,
                SmartEvents.FriendlyMissingBuff => SmartScriptTypeMaskId.Creature,
                SmartEvents.SummonedUnit => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.TargetManaPct => SmartScriptTypeMaskId.Creature,
                SmartEvents.AcceptedQuest => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.RewardQuest => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.ReachedHome => SmartScriptTypeMaskId.Creature,
                SmartEvents.ReceiveEmote => SmartScriptTypeMaskId.Creature,
                SmartEvents.HasAura => SmartScriptTypeMaskId.Creature,
                SmartEvents.TargetBuffed => SmartScriptTypeMaskId.Creature,
                SmartEvents.Reset => SmartScriptTypeMaskId.Creature,
                SmartEvents.IcLos => SmartScriptTypeMaskId.Creature,
                SmartEvents.PassengerBoarded => SmartScriptTypeMaskId.Creature,
                SmartEvents.PassengerRemoved => SmartScriptTypeMaskId.Creature,
                SmartEvents.Charmed => SmartScriptTypeMaskId.Creature,
                SmartEvents.CharmedTarget => SmartScriptTypeMaskId.Creature,
                SmartEvents.SpellHitTarget => SmartScriptTypeMaskId.Creature,
                SmartEvents.Damaged => SmartScriptTypeMaskId.Creature,
                SmartEvents.DamagedTarget => SmartScriptTypeMaskId.Creature,
                SmartEvents.Movementinform => SmartScriptTypeMaskId.Creature,
                SmartEvents.SummonDespawned => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.CorpseRemoved => SmartScriptTypeMaskId.Creature,
                SmartEvents.AiInit => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.DataSet => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.WaypointStart => SmartScriptTypeMaskId.Creature,
                SmartEvents.WaypointReached => SmartScriptTypeMaskId.Creature,
                SmartEvents.TransportAddplayer => SmartScriptTypeMaskId.Transport,
                SmartEvents.TransportAddcreature => SmartScriptTypeMaskId.Transport,
                SmartEvents.TransportRemovePlayer => SmartScriptTypeMaskId.Transport,
                SmartEvents.TransportRelocate => SmartScriptTypeMaskId.Transport,
                SmartEvents.InstancePlayerEnter => SmartScriptTypeMaskId.Instance,
                SmartEvents.AreatriggerOntrigger => SmartScriptTypeMaskId.Areatrigger + SmartScriptTypeMaskId.AreatrigggerEntity,
                SmartEvents.QuestAccepted => SmartScriptTypeMaskId.Quest,
                SmartEvents.QuestObjCompletion => SmartScriptTypeMaskId.Quest,
                SmartEvents.QuestRewarded => SmartScriptTypeMaskId.Quest,
                SmartEvents.QuestCompletion => SmartScriptTypeMaskId.Quest,
                SmartEvents.QuestFail => SmartScriptTypeMaskId.Quest,
                SmartEvents.TextOver => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.ReceiveHeal => SmartScriptTypeMaskId.Creature,
                SmartEvents.JustSummoned => SmartScriptTypeMaskId.Creature,
                SmartEvents.WaypointPaused => SmartScriptTypeMaskId.Creature,
                SmartEvents.WaypointResumed => SmartScriptTypeMaskId.Creature,
                SmartEvents.WaypointStopped => SmartScriptTypeMaskId.Creature,
                SmartEvents.WaypointEnded => SmartScriptTypeMaskId.Creature,
                SmartEvents.TimedEventTriggered => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.Update => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject + SmartScriptTypeMaskId.AreatrigggerEntity,
                SmartEvents.Link => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject + SmartScriptTypeMaskId.Areatrigger + SmartScriptTypeMaskId.Event + SmartScriptTypeMaskId.Gossip + SmartScriptTypeMaskId.Quest + SmartScriptTypeMaskId.Spell + SmartScriptTypeMaskId.Transport + SmartScriptTypeMaskId.Instance + SmartScriptTypeMaskId.AreatrigggerEntity,
                SmartEvents.GossipSelect => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.JustCreated => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.GossipHello => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.FollowCompleted => SmartScriptTypeMaskId.Creature,
                SmartEvents.PhaseChange => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.IsBehindTarget => SmartScriptTypeMaskId.Creature,
                SmartEvents.GameEventStart => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.GameEventEnd => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.GoLootStateChanged => SmartScriptTypeMaskId.Gameobject,
                SmartEvents.GoEventInform => SmartScriptTypeMaskId.Gameobject,
                SmartEvents.ActionDone => SmartScriptTypeMaskId.Creature,
                SmartEvents.OnSpellclick => SmartScriptTypeMaskId.Creature,
                SmartEvents.FriendlyHealthPCT => SmartScriptTypeMaskId.Creature,
                SmartEvents.DistanceCreature => SmartScriptTypeMaskId.Creature,
                SmartEvents.DistanceGameobject => SmartScriptTypeMaskId.Creature,
                SmartEvents.CounterSet => SmartScriptTypeMaskId.Creature + SmartScriptTypeMaskId.Gameobject,
                SmartEvents.SceneStart => SmartScriptTypeMaskId.Scene,
                SmartEvents.SceneTrigger => SmartScriptTypeMaskId.Scene,
                SmartEvents.SceneCancel => SmartScriptTypeMaskId.Scene,
                SmartEvents.SceneComplete => SmartScriptTypeMaskId.Scene,
                SmartEvents.SpellEffectHit => SmartScriptTypeMaskId.Spell,
                SmartEvents.SpellEffectHitTarget => SmartScriptTypeMaskId.Spell,
                _ => 0,
            };
    }

    public class SmartScriptHolder
    {
        public int EntryOrGuid;
        public SmartScriptType SourceType;
        public uint EventId;
        public uint Link;
        public SmartEvent Event;
        public SmartAction Action;
        public SmartTarget Target;
        public uint Timer;
        public bool Active;
        public bool RunOnce;
        public bool EnableTimed;

        public SmartScriptHolder() { }
        public SmartScriptHolder(SmartScriptHolder other)
        {
            EntryOrGuid = other.EntryOrGuid;
            SourceType = other.SourceType;
            EventId = other.EventId;
            Link = other.Link;
            Event = other.Event;
            Action = other.Action;
            Target = other.Target;
            Timer = other.Timer;
            Active = other.Active;
            RunOnce = other.RunOnce;
            EnableTimed = other.EnableTimed;
        }

        public SmartScriptType GetScriptType() { return SourceType; }
        public SmartEvents GetEventType() { return Event.type; }
        public SmartActions GetActionType() { return Action.type; }
        public SmartTargets GetTargetType() { return Target.type; }

        public override string ToString()
        {
            return $"Entry {EntryOrGuid} SourceType {GetScriptType()} Event {EventId} Action {GetActionType()}";
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct SmartEvent
    {
        [FieldOffset(0)]
        public SmartEvents type;

        [FieldOffset(4)]
        public uint event_phase_mask;

        [FieldOffset(8)]
        public uint event_chance;

        [FieldOffset(12)]
        public SmartEventFlags event_flags;

        [FieldOffset(16)]
        public MinMaxRepeat minMaxRepeat;

        [FieldOffset(16)]
        public Kill kill;

        [FieldOffset(16)]
        public SpellHit spellHit;

        [FieldOffset(16)]
        public Los los;

        [FieldOffset(16)]
        public Respawn respawn;

        [FieldOffset(16)]
        public MinMax minMax;

        [FieldOffset(16)]
        public TargetCasting targetCasting;

        [FieldOffset(16)]
        public FriendlyHealt friendlyHealth;

        [FieldOffset(16)]
        public FriendlyCC friendlyCC;

        [FieldOffset(16)]
        public MissingBuff missingBuff;

        [FieldOffset(16)]
        public Summoned summoned;

        [FieldOffset(16)]
        public Quest quest;

        [FieldOffset(16)]
        public QuestObjective questObjective;

        [FieldOffset(16)]
        public Emote emote;

        [FieldOffset(16)]
        public Aura aura;

        [FieldOffset(16)]
        public Charm charm;

        [FieldOffset(16)]
        public TargetAura targetAura;

        [FieldOffset(16)]
        public MovementInform movementInform;

        [FieldOffset(16)]
        public DataSet dataSet;

        [FieldOffset(16)]
        public Waypoint waypoint;

        [FieldOffset(16)]
        public TransportAddCreature transportAddCreature;

        [FieldOffset(16)]
        public TransportRelocate transportRelocate;

        [FieldOffset(16)]
        public InstancePlayerEnter instancePlayerEnter;

        [FieldOffset(16)]
        public Areatrigger areatrigger;

        [FieldOffset(16)]
        public TextOver textOver;

        [FieldOffset(16)]
        public TimedEvent timedEvent;

        [FieldOffset(16)]
        public GossipHello gossipHello;

        [FieldOffset(16)]
        public Gossip gossip;

        [FieldOffset(16)]
        public Dummy dummy;

        [FieldOffset(16)]
        public EventPhaseChange eventPhaseChange;

        [FieldOffset(16)]
        public BehindTarget behindTarget;

        [FieldOffset(16)]
        public GameEvent gameEvent;

        [FieldOffset(16)]
        public GoLootStateChanged goLootStateChanged;

        [FieldOffset(16)]
        public EventInform eventInform;

        [FieldOffset(16)]
        public DoAction doAction;

        [FieldOffset(16)]
        public FriendlyHealthPct friendlyHealthPct;

        [FieldOffset(16)]
        public Distance distance;

        [FieldOffset(16)]
        public Counter counter;

        [FieldOffset(16)]
        public Scene scene;

        [FieldOffset(16)]
        public Spell spell;

        [FieldOffset(16)]
        public Raw raw;

        [FieldOffset(40)]
        public string param_string;

        #region Structs
        public struct MinMaxRepeat
        {
            public uint min;
            public uint max;
            public uint repeatMin;
            public uint repeatMax;
        }
        public struct Kill
        {
            public uint cooldownMin;
            public uint cooldownMax;
            public uint playerOnly;
            public uint creature;
        }
        public struct SpellHit
        {
            public uint spell;
            public uint school;
            public uint cooldownMin;
            public uint cooldownMax;
        }
        public struct Los
        {
            public uint noHostile;
            public uint maxDist;
            public uint cooldownMin;
            public uint cooldownMax;
            public uint playerOnly;
        }
        public struct Respawn
        {
            public uint type;
            public uint map;
            public uint area;
        }
        public struct MinMax
        {
            public uint repeatMin;
            public uint repeatMax;
        }
        public struct TargetCasting
        {
            public uint repeatMin;
            public uint repeatMax;
            public uint spellId;
        }
        public struct FriendlyHealt
        {
            public uint hpDeficit;
            public uint radius;
            public uint repeatMin;
            public uint repeatMax;
        }
        public struct FriendlyCC
        {
            public uint radius;
            public uint repeatMin;
            public uint repeatMax;
        }
        public struct MissingBuff
        {
            public uint spell;
            public uint radius;
            public uint repeatMin;
            public uint repeatMax;
        }
        public struct Summoned
        {
            public uint creature;
            public uint cooldownMin;
            public uint cooldownMax;
        }
        public struct Quest
        {
            public uint questId;
            public uint cooldownMin;
            public uint cooldownMax;
        }
        public struct QuestObjective
        {
            public uint id;
        }
        public struct Emote
        {
            public uint emoteId;
            public uint cooldownMin;
            public uint cooldownMax;
        }
        public struct Aura
        {
            public uint spell;
            public uint count;
            public uint repeatMin;
            public uint repeatMax;
        }
        public struct Charm
        {
            public uint onRemove;
        }
        public struct TargetAura
        {
            public uint spell;
            public uint count;
            public uint repeatMin;
            public uint repeatMax;
        }
        public struct MovementInform
        {
            public uint type;
            public uint id;
        }
        public struct DataSet
        {
            public uint id;
            public uint value;
            public uint cooldownMin;
            public uint cooldownMax;
        }
        public struct Waypoint
        {
            public uint pointID;
            public uint pathID;
        }
        public struct TransportAddCreature
        {
            public uint creature;
        }
        public struct TransportRelocate
        {
            public uint pointID;
        }
        public struct InstancePlayerEnter
        {
            public uint team;
            public uint cooldownMin;
            public uint cooldownMax;
        }
        public struct Areatrigger
        {
            public uint id;
        }
        public struct TextOver
        {
            public uint textGroupID;
            public uint creatureEntry;
        }
        public struct TimedEvent
        {
            public uint id;
        }
        public struct GossipHello
        {
            public uint filter;
        }
        public struct Gossip
        {
            public uint sender;
            public uint action;
        }
        public struct Dummy
        {
            public uint spell;
            public uint effIndex;
        }
        public struct EventPhaseChange
        {
            public uint phasemask;
        }
        public struct BehindTarget
        {
            public uint cooldownMin;
            public uint cooldownMax;
        }
        public struct GameEvent
        {
            public uint gameEventId;
        }
        public struct GoLootStateChanged
        {
            public uint lootState;
        }
        public struct EventInform
        {
            public uint eventId;
        }
        public struct DoAction
        {
            public uint eventId;
        }
        public struct FriendlyHealthPct
        {
            public uint minHpPct;
            public uint maxHpPct;
            public uint repeatMin;
            public uint repeatMax;
        }
        public struct Distance
        {
            public uint guid;
            public uint entry;
            public uint dist;
            public uint repeat;
        }
        public struct Counter
        {
            public uint id;
            public uint value;
            public uint cooldownMin;
            public uint cooldownMax;
        }
        public struct Scene
        {
            public uint sceneId;
        }
        public struct Spell
        {
            public uint effIndex;
        }
        public struct Raw
        {
            public uint param1;
            public uint param2;
            public uint param3;
            public uint param4;
            public uint param5;
        }
        #endregion
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct SmartAction
    {
        [FieldOffset(0)]
        public SmartActions type;

        [FieldOffset(4)]
        public Talk talk;

        [FieldOffset(4)]
        public Faction faction;

        [FieldOffset(4)]
        public MorphOrMount morphOrMount;

        [FieldOffset(4)]
        public Sound sound;

        [FieldOffset(4)]
        public Emote emote;

        [FieldOffset(4)]
        public Quest quest;

        [FieldOffset(4)]
        public QuestOffer questOffer;

        [FieldOffset(4)]
        public React react;

        [FieldOffset(4)]
        public RandomEmote randomEmote;

        [FieldOffset(4)]
        public Cast cast;

        [FieldOffset(4)]
        public CrossCast crossCast;

        [FieldOffset(4)]
        public SummonCreature summonCreature;

        [FieldOffset(4)]
        public ThreatPCT threatPCT;

        [FieldOffset(4)]
        public CastCreatureOrGO castCreatureOrGO;

        [FieldOffset(4)]
        public AddUnitFlag addUnitFlag;

        [FieldOffset(4)]
        public RemoveUnitFlag removeUnitFlag;

        [FieldOffset(4)]
        public AutoAttack autoAttack;

        [FieldOffset(4)]
        public CombatMove combatMove;

        [FieldOffset(4)]
        public SetEventPhase setEventPhase;

        [FieldOffset(4)]
        public IncEventPhase incEventPhase;

        [FieldOffset(4)]
        public CastedCreatureOrGO castedCreatureOrGO;

        [FieldOffset(4)]
        public RemoveAura removeAura;

        [FieldOffset(4)]
        public Follow follow;

        [FieldOffset(4)]
        public RandomPhase randomPhase;

        [FieldOffset(4)]
        public RandomPhaseRange randomPhaseRange;

        [FieldOffset(4)]
        public KilledMonster killedMonster;

        [FieldOffset(4)]
        public SetInstanceData setInstanceData;

        [FieldOffset(4)]
        public SetInstanceData64 setInstanceData64;

        [FieldOffset(4)]
        public UpdateTemplate updateTemplate;

        [FieldOffset(4)]
        public CallHelp callHelp;

        [FieldOffset(4)]
        public SetSheath setSheath;

        [FieldOffset(4)]
        public ForceDespawn forceDespawn;

        [FieldOffset(4)]
        public InvincHP invincHP;

        [FieldOffset(4)]
        public IngamePhaseId ingamePhaseId;

        [FieldOffset(4)]
        public IngamePhaseGroup ingamePhaseGroup;

        [FieldOffset(4)]
        public SetData setData;

        [FieldOffset(4)]
        public MoveRandom moveRandom;

        [FieldOffset(4)]
        public Visibility visibility;

        [FieldOffset(4)]
        public SummonGO summonGO;

        [FieldOffset(4)]
        public Active active;

        [FieldOffset(4)]
        public Taxi taxi;

        [FieldOffset(4)]
        public WpStart wpStart;

        [FieldOffset(4)]
        public WpPause wpPause;

        [FieldOffset(4)]
        public WpStop wpStop;

        [FieldOffset(4)]
        public Item item;

        [FieldOffset(4)]
        public InstallTtemplate installTtemplate;

        [FieldOffset(4)]
        public SetRun setRun;

        [FieldOffset(4)]
        public SetDisableGravity setDisableGravity;

        [FieldOffset(4)]
        public SetFly setFly;

        [FieldOffset(4)]
        public SetSwim setSwim;

        [FieldOffset(4)]
        public Teleport teleport;

        [FieldOffset(4)]
        public SetCounter setCounter;

        [FieldOffset(4)]
        public StoreVar storeVar;

        [FieldOffset(4)]
        public StoreTargets storeTargets;

        [FieldOffset(4)]
        public TimeEvent timeEvent;

        [FieldOffset(4)]
        public Movie movie;

        [FieldOffset(4)]
        public Equip equip;

        [FieldOffset(4)]
        public UnitFlag unitFlag;

        [FieldOffset(4)]
        public SetunitByte setunitByte;

        [FieldOffset(4)]
        public DelunitByte delunitByte;

        [FieldOffset(4)]
        public EnterVehicle enterVehicle;

        [FieldOffset(4)]
        public TimedActionList timedActionList;

        [FieldOffset(4)]
        public RandTimedActionList randTimedActionList;

        [FieldOffset(4)]
        public InterruptSpellCasting interruptSpellCasting;

        [FieldOffset(4)]
        public SendGoCustomAnim sendGoCustomAnim;

        [FieldOffset(4)]
        public Jump jump;

        [FieldOffset(4)]
        public FleeAssist fleeAssist;

        [FieldOffset(4)]
        public Flee flee;

        [FieldOffset(4)]
        public EnableTempGO enableTempGO;

        [FieldOffset(4)]
        public MoveToPos moveToPos;

        [FieldOffset(4)]
        public SendGossipMenu sendGossipMenu;

        [FieldOffset(4)]
        public SetGoLootState setGoLootState;

        [FieldOffset(4)]
        public SendTargetToTarget sendTargetToTarget;

        [FieldOffset(4)]
        public SetRangedMovement setRangedMovement;

        [FieldOffset(4)]
        public SetHealthRegen setHealthRegen;

        [FieldOffset(4)]
        public SetRoot setRoot;

        [FieldOffset(4)]
        public GoFlag goFlag;

        [FieldOffset(4)]
        public GoState goState;

        [FieldOffset(4)]
        public CreatureGroup creatureGroup;

        [FieldOffset(4)]
        public Power power;

        [FieldOffset(4)]
        public GameEventStop gameEventStop;

        [FieldOffset(4)]
        public GameEventStart gameEventStart;

        [FieldOffset(4)]
        public ClosestWaypointFromList closestWaypointFromList;

        [FieldOffset(4)]
        public RandomSound randomSound;

        [FieldOffset(4)]
        public CorpseDelay corpseDelay;

        [FieldOffset(4)]
        public DisableEvade disableEvade;

        [FieldOffset(4)]
        public GroupSpawn groupSpawn;

        [FieldOffset(4)]
        public AuraType auraType;

        [FieldOffset(4)]
        public SightDistance sightDistance;

        [FieldOffset(4)]
        public LoadEquipment loadEquipment;

        [FieldOffset(4)]
        public RandomTimedEvent randomTimedEvent;

        [FieldOffset(4)]
        public PauseMovement pauseMovement;

        [FieldOffset(4)]
        public RespawnData respawnData;

        [FieldOffset(4)]
        public AnimKit animKit;

        [FieldOffset(4)]
        public Scene scene;

        [FieldOffset(4)]
        public Cinematic cinematic;

        [FieldOffset(4)]
        public MovementSpeed movementSpeed;

        [FieldOffset(4)]
        public SpellVisualKit spellVisualKit;

        [FieldOffset(4)]
        public OverrideLight overrideLight;

        [FieldOffset(4)]
        public OverrideWeather overrideWeather;

        [FieldOffset(4)]
        public Conversation conversation;

        [FieldOffset(4)]
        public AddToStoredTargets addToStoredTargets;

        [FieldOffset(4)]
        public Raw raw;

        #region Stucts
        public struct Talk
        {
            public uint textGroupId;
            public uint duration;
            public uint useTalkTarget;
        }
        public struct Faction
        {
            public uint factionID;
        }
        public struct MorphOrMount
        {
            public uint creature;
            public uint model;
        }
        public struct Sound
        {
            public uint soundId;
            public uint onlySelf;
            public uint distance;
            public uint keyBroadcastTextId;
        }
        public struct Emote
        {
            public uint emoteId;
        }
        public struct Quest
        {
            public uint questId;
        }
        public struct QuestOffer
        {
            public uint questId;
            public uint directAdd;
        }
        public struct React
        {
            public uint state;
        }
        public struct RandomEmote
        {
            public uint emote1;
            public uint emote2;
            public uint emote3;
            public uint emote4;
            public uint emote5;
            public uint emote6;
        }
        public struct Cast
        {
            public uint spell;
            public uint castFlags;
            public uint triggerFlags;
            public uint targetsLimit;
        }
        public struct CrossCast
        {
            public uint spell;
            public uint castFlags;
            public uint targetType;
            public uint targetParam1;
            public uint targetParam2;
            public uint targetParam3;
        }
        public struct SummonCreature
        {
            public uint creature;
            public uint type;
            public uint duration;
            public uint storageID;
            public uint attackInvoker;
            public uint flags; // SmartActionSummonCreatureFlags
        }
        public struct ThreatPCT
        {
            public uint threatINC;
            public uint threatDEC;
        }
        public struct CastCreatureOrGO
        {
            public uint quest;
            public uint spell;
        }
        public struct AddUnitFlag
        {
            public uint flag1;
            public uint flag2;
            public uint flag3;
            public uint flag4;
            public uint flag5;
            public uint flag6;
        }
        public struct RemoveUnitFlag
        {
            public uint flag1;
            public uint flag2;
            public uint flag3;
            public uint flag4;
            public uint flag5;
            public uint flag6;
        }
        public struct AutoAttack
        {
            public uint attack;
        }
        public struct CombatMove
        {
            public uint move;
        }
        public struct SetEventPhase
        {
            public uint phase;
        }
        public struct IncEventPhase
        {
            public uint inc;
            public uint dec;
        }
        public struct CastedCreatureOrGO
        {
            public uint creature;
            public uint spell;
        }
        public struct RemoveAura
        {
            public uint spell;
            public uint charges;
            public uint onlyOwnedAuras;
        }
        public struct Follow
        {
            public uint dist;
            public uint angle;
            public uint entry;
            public uint credit;
            public uint creditType;
        }
        public struct RandomPhase
        {
            public uint phase1;
            public uint phase2;
            public uint phase3;
            public uint phase4;
            public uint phase5;
            public uint phase6;
        }
        public struct RandomPhaseRange
        {
            public uint phaseMin;
            public uint phaseMax;
        }
        public struct KilledMonster
        {
            public uint creature;
        }
        public struct SetInstanceData
        {
            public uint field;
            public uint data;
            public uint type;
        }
        public struct SetInstanceData64
        {
            public uint field;
        }
        public struct UpdateTemplate
        {
            public uint creature;
            public uint updateLevel;
        }
        public struct CallHelp
        {
            public uint range;
            public uint withEmote;
        }
        public struct SetSheath
        {
            public uint sheath;
        }
        public struct ForceDespawn
        {
            public uint delay;
            public uint forceRespawnTimer;
        }
        public struct InvincHP
        {
            public uint minHP;
            public uint percent;
        }
        public struct IngamePhaseId
        {
            public uint id;
            public uint apply;
        }
        public struct IngamePhaseGroup
        {
            public uint groupId;
            public uint apply;
        }
        public struct SetData
        {
            public uint field;
            public uint data;
        }
        public struct MoveRandom
        {
            public uint distance;
        }
        public struct Visibility
        {
            public uint state;
        }
        public struct SummonGO
        {
            public uint entry;
            public uint despawnTime;
            public uint summonType;
        }
        public struct Active
        {
            public uint state;
        }
        public struct Taxi
        {
            public uint id;
        }
        public struct WpStart
        {
            public uint run;
            public uint pathID;
            public uint repeat;
            public uint quest;
            public uint despawnTime;
            public uint reactState;
        }
        public struct WpPause
        {
            public uint delay;
        }
        public struct WpStop
        {
            public uint despawnTime;
            public uint quest;
            public uint fail;
        }
        public struct Item
        {
            public uint entry;
            public uint count;
        }
        public struct InstallTtemplate
        {
            public uint id;
            public uint param1;
            public uint param2;
            public uint param3;
            public uint param4;
            public uint param5;
        }
        public struct SetRun
        {
            public uint run;
        }
        public struct SetDisableGravity
        {
            public uint disable;
        }
        public struct SetFly
        {
            public uint fly;
        }
        public struct SetSwim
        {
            public uint swim;
        }
        public struct Teleport
        {
            public uint mapID;
        }
        public struct SetCounter
        {
            public uint counterId;
            public uint value;
            public uint reset;
        }
        public struct StoreVar
        {
            public uint id;
            public uint number;
        }
        public struct StoreTargets
        {
            public uint id;
        }
        public struct TimeEvent
        {
            public uint id;
            public uint min;
            public uint max;
            public uint repeatMin;
            public uint repeatMax;
            public uint chance;
        }
        public struct Movie
        {
            public uint entry;
        }
        public struct Equip
        {
            public uint entry;
            public uint mask;
            public uint slot1;
            public uint slot2;
            public uint slot3;
        }
        public struct UnitFlag
        {
            public uint flag;
            public uint type;
        }
        public struct SetunitByte
        {
            public uint byte1;
            public uint type;
        }
        public struct DelunitByte
        {
            public uint byte1;
            public uint type;
        }
        public struct EnterVehicle
        {
            public uint seat;
        }
        public struct TimedActionList
        {
            public uint id;
            public uint timerType;
            public uint allowOverride;
        }
        public struct RandTimedActionList
        {
            public uint actionList1;
            public uint actionList2;
            public uint actionList3;
            public uint actionList4;
            public uint actionList5;
            public uint actionList6;
        }
        public struct InterruptSpellCasting
        {
            public uint withDelayed;
            public uint spell_id;
            public uint withInstant;
        }
        public struct SendGoCustomAnim
        {
            public uint anim;
        }
        public struct Jump
        {
            public uint speedxy;
            public uint speedz;
        }
        public struct FleeAssist
        {
            public uint withEmote;
        }
        public struct Flee
        {
            public uint fleeTime;
        }
        public struct EnableTempGO
        {
            public uint duration;
        }
        public struct MoveToPos
        {
            public uint pointId;
            public uint transport;
            public uint disablePathfinding;
            public uint contactDistance;
        }
        public struct SendGossipMenu
        {
            public uint gossipMenuId;
            public uint gossipNpcTextId;
        }
        public struct SetGoLootState
        {
            public uint state;
        }
        public struct SendTargetToTarget
        {
            public uint id;
        }
        public struct SetRangedMovement
        {
            public uint distance;
            public uint angle;
        }
        public struct SetHealthRegen
        {
            public uint regenHealth;
        }
        public struct SetRoot
        {
            public uint root;
        }
        public struct GoFlag
        {
            public uint flag;
        }
        public struct GoState
        {
            public uint state;
        }
        public struct CreatureGroup
        {
            public uint group;
            public uint attackInvoker;
        }
        public struct Power
        {
            public uint powerType;
            public uint newPower;
        }
        public struct GameEventStop
        {
            public uint id;
        }
        public struct GameEventStart
        {
            public uint id;
        }
        public struct ClosestWaypointFromList
        {
            public uint wp1;
            public uint wp2;
            public uint wp3;
            public uint wp4;
            public uint wp5;
            public uint wp6;
        }
        public struct RandomSound
        {
            public uint sound1;
            public uint sound2;
            public uint sound3;
            public uint sound4;
            public uint onlySelf;
            public uint distance;
        }
        public struct CorpseDelay
        {
            public uint timer;
        }
        public struct DisableEvade
        {
            public uint disable;
        }
        public struct GroupSpawn
        {
            public uint groupId;
            public uint minDelay;
            public uint maxDelay;
            public uint spawnflags;
        }
        public struct AuraType
        {
            public uint type;
        }
        public struct SightDistance
        {
            public uint dist;
        }
        public struct LoadEquipment
        {
            public uint id;
            public uint force;
        }
        public struct RandomTimedEvent
        {
            public uint minId;
            public uint maxId;
        }
        public struct PauseMovement
        {
            public uint movementSlot;
            public uint pauseTimer;
            public uint force;
        }
        public struct RespawnData
        {
            public uint spawnType;
            public uint spawnId;
        }
        public struct AnimKit
        {
            public uint animKit;
            public uint type;
        }
        public struct Scene
        {
            public uint sceneId;
        }
        public struct Cinematic
        {
            public uint entry;
        }
        public struct MovementSpeed
        {
            public uint movementType;
            public uint speedInteger;
            public uint speedFraction;
        }
        public struct SpellVisualKit
        {
            public uint spellVisualKitId;
            public uint kitType;
            public uint duration;
        }
        public struct OverrideLight
        {
            public uint zoneId;
            public uint areaLightId;
            public uint overrideLightId;
            public uint transitionMilliseconds;
        }
        public struct OverrideWeather
        {
            public uint zoneId;
            public uint weatherId;
            public uint intensity;
        }
        public struct Conversation
        {
            public uint id;
        }
        public struct AddToStoredTargets
        {
            public uint id;
        }
        public struct Raw
        {
            public uint param1;
            public uint param2;
            public uint param3;
            public uint param4;
            public uint param5;
            public uint param6;
        }
        #endregion
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct SmartTarget
    {
        [FieldOffset(0)]
        public SmartTargets type;

        [FieldOffset(4)]
        public float x;

        [FieldOffset(8)]
        public float y;

        [FieldOffset(12)]
        public float z;

        [FieldOffset(16)]
        public float o;

        [FieldOffset(20)]
        public HostilRandom hostilRandom;

        [FieldOffset(20)]
        public Farthest farthest;

        [FieldOffset(20)]
        public UnitRange unitRange;

        [FieldOffset(20)]
        public UnitGUID unitGUID;

        [FieldOffset(20)]
        public UnitDistance unitDistance;

        [FieldOffset(20)]
        public PlayerDistance playerDistance;

        [FieldOffset(20)]
        public PlayerRange playerRange;

        [FieldOffset(20)]
        public Stored stored;

        [FieldOffset(20)]
        public GoRange goRange;

        [FieldOffset(20)]
        public GoGUID goGUID;

        [FieldOffset(20)]
        public GoDistance goDistance;

        [FieldOffset(20)]
        public Position postion;

        [FieldOffset(20)]
        public Closest closest;

        [FieldOffset(20)]
        public ClosestAttackable closestAttackable;

        [FieldOffset(20)]
        public ClosestFriendly closestFriendly;

        [FieldOffset(20)]
        public Owner owner;

        [FieldOffset(20)]
        public Vehicle vehicle;

        [FieldOffset(20)]
        public Raw raw;

        #region Structs
        public struct HostilRandom
        {
            public uint maxDist;
            public uint playerOnly;
            public uint powerType;
        }
        public struct Farthest
        {
            public uint maxDist;
            public uint playerOnly;
            public uint isInLos;
        }
        public struct UnitRange
        {
            public uint creature;
            public uint minDist;
            public uint maxDist;
            public uint maxSize;
        }
        public struct UnitGUID
        {
            public uint dbGuid;
            public uint entry;
        }
        public struct UnitDistance
        {
            public uint creature;
            public uint dist;
            public uint maxSize;
        }
        public struct PlayerDistance
        {
            public uint dist;
        }
        public struct PlayerRange
        {
            public uint minDist;
            public uint maxDist;
        }
        public struct Stored
        {
            public uint id;
        }
        public struct GoRange
        {
            public uint entry;
            public uint minDist;
            public uint maxDist;
            public uint maxSize;
        }
        public struct GoGUID
        {
            public uint dbGuid;
            public uint entry;
        }
        public struct GoDistance
        {
            public uint entry;
            public uint dist;
            public uint maxSize;
        }
        public struct Position
        {
            public uint map;
        }
        public struct Closest
        {
            public uint entry;
            public uint dist;
            public uint dead;
        }
        public struct ClosestAttackable
        {
            public uint maxDist;
        }
        public struct ClosestFriendly
        {
            public uint maxDist;
        }
        public struct Owner
        {
            public uint useCharmerOrOwner;
        }
        public struct Vehicle
        {
            public uint seatMask;
        }
        public struct Raw
        {
            public uint param1;
            public uint param2;
            public uint param3;
            public uint param4;
        }
        #endregion
    }
}
