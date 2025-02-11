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
using Game.DataStorage;
using Game.Maps;
using Game.Networking;
using Game.Spells;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Entities
{
    public class Conversation : WorldObject
    {
        public Conversation() : base(false)
        {
            ObjectTypeMask |= TypeMask.Conversation;
            ObjectTypeId = TypeId.Conversation;

            m_updateFlag.Stationary = true;
            m_updateFlag.Conversation = true;

            m_conversationData = new ConversationData();
        }

        public override void AddToWorld()
        {
            //- Register the Conversation for guid lookup and for caster
            if (!IsInWorld)
            {
                GetMap().GetObjectsStore().Add(GetGUID(), this);
                base.AddToWorld();
            }
        }

        public override void RemoveFromWorld()
        {
            //- Remove the Conversation from the accessor and from all lists of objects in world
            if (IsInWorld)
            {
                base.RemoveFromWorld();
                GetMap().GetObjectsStore().Remove(GetGUID());
            }
        }

        public override void Update(uint diff)
        {
            if (GetDuration() > TimeSpan.FromMilliseconds(diff))
            {
                _duration -= TimeSpan.FromMilliseconds(diff);
                DoWithSuppressingObjectUpdates(() =>
                {
                    // Only sent in CreateObject
                    ApplyModUpdateFieldValue(m_values.ModifyValue(m_conversationData).ModifyValue(m_conversationData.Progress), diff, true);
                    m_conversationData.ClearChanged(m_conversationData.Progress);
                });
            }
            else
            {
                Remove(); // expired
                return;
            }

            base.Update(diff);
        }

        public void Remove()
        {
            if (IsInWorld)
            {
                AddObjectToRemoveList(); // calls RemoveFromWorld
            }
        }

        public static Conversation CreateConversation(uint conversationEntry, Unit creator, Position pos, ObjectGuid privateObjectOwner, SpellInfo spellInfo = null)
        {
            ConversationTemplate conversationTemplate = Global.ConversationDataStorage.GetConversationTemplate(conversationEntry);
            if (conversationTemplate == null)
                return null;

            ulong lowGuid = creator.GetMap().GenerateLowGuid(HighGuid.Conversation);

            Conversation conversation = new();
            if (!conversation.Create(lowGuid, conversationEntry, creator.GetMap(), creator, pos, privateObjectOwner, spellInfo))
                return null;

            return conversation;
        }

        bool Create(ulong lowGuid, uint conversationEntry, Map map, Unit creator, Position pos, ObjectGuid privateObjectOwner, SpellInfo spellInfo = null)
        {
            ConversationTemplate conversationTemplate = Global.ConversationDataStorage.GetConversationTemplate(conversationEntry);
            //ASSERT(conversationTemplate);

            _creatorGuid = creator.GetGUID();
            SetPrivateObjectOwner(privateObjectOwner);

            SetMap(map);
            Relocate(pos);
            RelocateStationaryPosition(pos);

            _Create(ObjectGuid.Create(HighGuid.Conversation, GetMapId(), conversationEntry, lowGuid));
            PhasingHandler.InheritPhaseShift(this, creator);

            SetEntry(conversationEntry);
            SetObjectScale(1.0f);

            _textureKitId = conversationTemplate.TextureKitId;

            if (conversationTemplate.Actors != null)
            {
                foreach (var actor in conversationTemplate.Actors)
                {
                    if (actor != null)
                    {
                        ConversationActorField actorField = new();
                        actorField.CreatureID = actor.CreatureId;
                        actorField.CreatureDisplayInfoID = actor.CreatureDisplayInfoId;
                        actorField.Id = (int)actor.ActorId;
                        actorField.Type = ConversationActorType.CreatureActor;

                        AddDynamicUpdateFieldValue(m_values.ModifyValue(m_conversationData).ModifyValue(m_conversationData.Actors), actorField);
                    }
                }
            }

            if (conversationTemplate.ActorGuids != null)
            {
                for (ushort actorIndex = 0; actorIndex < conversationTemplate.ActorGuids.Count; ++actorIndex)
                {
                    ulong actorGuid = conversationTemplate.ActorGuids[actorIndex];
                    if (actorGuid == 0)
                        continue;

                    foreach (var creature in map.GetCreatureBySpawnIdStore().LookupByKey(actorGuid))
                    {
                        // we just need the last one, overriding is legit
                        AddActor(creature.GetGUID(), actorIndex);
                    }
                }
            }

            Global.ScriptMgr.OnConversationCreate(this, creator);

            List<ushort> actorIndices = new();
            List<ConversationLine> lines = new();
            foreach (ConversationLineTemplate line in conversationTemplate.Lines)
            {
                if (!Global.ConditionMgr.IsObjectMeetingNotGroupedConditions(ConditionSourceType.ConversationLine, line.Id, creator))
                    continue;

                actorIndices.Add(line.ActorIdx);

                ConversationLine lineField = new();
                lineField.ConversationLineID = line.Id;
                lineField.UiCameraID = line.UiCameraID;
                lineField.ActorIndex = line.ActorIdx;
                lineField.Flags = line.Flags;

                ConversationLineRecord convoLine = CliDB.ConversationLineStorage.LookupByKey(line.Id); // never null for conversationTemplate->Lines

                for (Locale locale = Locale.enUS; locale < Locale.Total; locale = locale + 1)
                {
                    if (locale == Locale.None)
                        continue;

                    _lineStartTimes[(locale, line.Id)] = _lastLineEndTimes[(int)locale];
                    if (locale == Locale.enUS)
                        lineField.StartTime = (uint)_lastLineEndTimes[(int)locale].TotalMilliseconds;

                    int broadcastTextDuration = Global.DB2Mgr.GetBroadcastTextDuration((int)convoLine.BroadcastTextID, locale);
                    if (broadcastTextDuration != 0)
                        _lastLineEndTimes[(int)locale] += TimeSpan.FromMilliseconds(broadcastTextDuration);

                    _lastLineEndTimes[(int)locale] += TimeSpan.FromMilliseconds(convoLine.AdditionalDuration);
                }

                lines.Add(lineField);
            }

            _duration = _lastLineEndTimes.Max();
            SetUpdateFieldValue(m_values.ModifyValue(m_conversationData).ModifyValue(m_conversationData.LastLineEndTime), (uint)_duration.TotalMilliseconds);
            SetUpdateFieldValue(m_values.ModifyValue(m_conversationData).ModifyValue(m_conversationData.Lines), lines);

            // conversations are despawned 5-20s after LastLineEndTime
            _duration += TimeSpan.FromSeconds(10);

            Global.ScriptMgr.OnConversationCreate(this, creator);

            // All actors need to be set
            foreach (ushort actorIndex in actorIndices)
            {
                ConversationActorField actor = actorIndex < m_conversationData.Actors.Size() ? m_conversationData.Actors[actorIndex] : null;
                if (actor == null || (actor.CreatureID == 0 && actor.ActorGUID.IsEmpty() && actor.NoActorObject == 0))
                {
                    Log.outError(LogFilter.Conversation, $"Failed to create conversation (Id: {conversationEntry}) due to missing actor (Idx: {actorIndex}).");
                    return false;
                }
            }

            if (!GetMap().AddToMap(this))
                return false;

            return true;
        }

        public void AddActor(ObjectGuid actorGuid, ushort actorIdx)
        {
            ConversationActorField actorField = m_values.ModifyValue(m_conversationData).ModifyValue(m_conversationData.Actors, actorIdx);
            SetUpdateFieldValue(ref actorField.ActorGUID, actorGuid);
            SetUpdateFieldValue(ref actorField.Type, ConversationActorType.WorldObjectActor);
        }

        public TimeSpan GetLineStartTime(Locale locale, int lineId)
        {
            return _lineStartTimes.LookupByKey((locale, lineId));
        }

        public TimeSpan GetLastLineEndTime(Locale locale)
        {
            return _lastLineEndTimes[(int)locale];
        }

        public uint GetScriptId()
        {
            return Global.ConversationDataStorage.GetConversationTemplate(GetEntry()).ScriptId;
        }

        public override void BuildValuesCreate(WorldPacket data, Player target)
        {
            UpdateFieldFlag flags = GetUpdateFieldFlagsFor(target);
            WorldPacket buffer = new();

            m_objectData.WriteCreate(buffer, flags, this, target);
            m_conversationData.WriteCreate(buffer, flags, this, target);

            data.WriteUInt32(buffer.GetSize());
            data.WriteUInt8((byte)flags);
            data.WriteBytes(buffer);
        }

        public override void BuildValuesUpdate(WorldPacket data, Player target)
        {
            UpdateFieldFlag flags = GetUpdateFieldFlagsFor(target);
            WorldPacket buffer = new();

            buffer.WriteUInt32(m_values.GetChangedObjectTypeMask());
            if (m_values.HasChanged(TypeId.Object))
                m_objectData.WriteUpdate(buffer, flags, this, target);

            if (m_values.HasChanged(TypeId.Conversation))
                m_conversationData.WriteUpdate(buffer, flags, this, target);

            data.WriteUInt32(buffer.GetSize());
            data.WriteBytes(buffer);
        }

        void BuildValuesUpdateForPlayerWithMask(UpdateData data, UpdateMask requestedObjectMask, UpdateMask requestedConversationMask, Player target)
        {
            UpdateMask valuesMask = new((int)TypeId.Max);
            if (requestedObjectMask.IsAnySet())
                valuesMask.Set((int)TypeId.Object);

            if (requestedConversationMask.IsAnySet())
                valuesMask.Set((int)TypeId.Conversation);

            WorldPacket buffer = new();
            buffer.WriteUInt32(valuesMask.GetBlock(0));

            if (valuesMask[(int)TypeId.Object])
                m_objectData.WriteUpdate(buffer, requestedObjectMask, true, this, target);

            if (valuesMask[(int)TypeId.Conversation])
                m_conversationData.WriteUpdate(buffer, requestedConversationMask, true, this, target);

            WorldPacket buffer1 = new();
            buffer1.WriteUInt8((byte)UpdateType.Values);
            buffer1.WritePackedGuid(GetGUID());
            buffer1.WriteUInt32(buffer.GetSize());
            buffer1.WriteBytes(buffer.GetData());

            data.AddUpdateBlock(buffer1);
        }

        public override void ClearUpdateMask(bool remove)
        {
            m_values.ClearChangesMask(m_conversationData);
            base.ClearUpdateMask(remove);
        }

        TimeSpan GetDuration() { return _duration; }
        public uint GetTextureKitId() { return _textureKitId; }

        public ObjectGuid GetCreatorGuid() { return _creatorGuid; }
        public override ObjectGuid GetOwnerGUID() { return GetCreatorGuid(); }
        public override uint GetFaction() { return 0; }

        public override float GetStationaryX() { return _stationaryPosition.GetPositionX(); }
        public override float GetStationaryY() { return _stationaryPosition.GetPositionY(); }
        public override float GetStationaryZ() { return _stationaryPosition.GetPositionZ(); }
        public override float GetStationaryO() { return _stationaryPosition.GetOrientation(); }
        void RelocateStationaryPosition(Position pos) { _stationaryPosition.Relocate(pos); }

        ConversationData m_conversationData;

        Position _stationaryPosition = new();
        ObjectGuid _creatorGuid;
        TimeSpan _duration;
        uint _textureKitId;

        Dictionary<(Locale locale, uint lineId), TimeSpan> _lineStartTimes = new();
        TimeSpan[] _lastLineEndTimes = new TimeSpan[(int)Locale.Total];
    }
}
