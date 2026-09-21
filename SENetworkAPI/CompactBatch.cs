using ProtoBuf;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage;

namespace SENetworkAPI
{
	/// <summary>
	/// Version 1 compact batch. Values retain their original game serialization;
	/// only the repeated routing metadata and value envelopes are packed together.
	/// </summary>
	[ProtoContract]
	internal class CompactBatch
	{
		[ProtoMember(1)] public long EntityId;
		[ProtoMember(2, IsPacked = true)] public List<long> Ids;
		[ProtoMember(3, IsPacked = true)] public List<long> EntityIds;
		[ProtoMember(4)] public SyncType SyncType;
		[ProtoMember(5, IsPacked = true)] public List<SyncType> SyncTypes;
		// 0 = null; n + 1 = a non-null value containing n bytes.
		[ProtoMember(6, IsPacked = true)] public List<int> Lengths;
		[ProtoMember(7)] public byte[] Values;

		internal static void TryEncode(Command command)
		{
			List<SyncData> updates = command.Properties;
			if (updates == null || updates.Count < 2 || updates.Count > NetSync.MaxUpdatesPerPacket) return;

			SyncData first = updates[0];
			bool sameEntity = true, sameType = true, hasValues = false;
			int length = 0;
			long legacySize = 0;
			for (int i = 0; i < updates.Count; i++)
			{
				SyncData update = updates[i];
				sameEntity &= update.EntityId == first.EntityId;
				sameType &= update.SyncType == first.SyncType;
				hasValues |= update.Data != null;
				length = checked(length + (update.Data?.Length ?? 0));
				legacySize += NetSync.UpdateWireSize(update);
			}

			if (legacySize < NetworkAPI.CompactBatchThreshold) return;

			var batch = new CompactBatch {
				EntityId = sameEntity ? first.EntityId : 0,
				SyncType = sameType ? first.SyncType : SyncType.Post,
				Ids = new List<long>(updates.Count),
				EntityIds = sameEntity ? null : new List<long>(updates.Count),
				SyncTypes = sameType ? null : new List<SyncType>(updates.Count),
				Lengths = hasValues ? new List<int>(updates.Count) : null,
				Values = hasValues ? new byte[length] : null
			};
			int offset = 0;
			for (int i = 0; i < updates.Count; i++)
			{
				SyncData update = updates[i];
				batch.Ids.Add(update.Id);
				if (!sameEntity) batch.EntityIds.Add(update.EntityId);
				if (!sameType) batch.SyncTypes.Add(update.SyncType);
				if (!hasValues) continue;
				batch.Lengths.Add(update.Data == null ? 0 : checked(update.Data.Length + 1));
				if (update.Data == null) continue;
				Array.Copy(update.Data, 0, batch.Values, offset, update.Data.Length);
				offset += update.Data.Length;
			}

			byte[] data = MyAPIGateway.Utilities.SerializeToBinary(batch);
			bool compressed = false;
			if (data.Length > NetworkAPI.CompressionThreshold)
			{
				byte[] candidate = MyCompression.Compress(data);
				if (candidate.Length + 2 < data.Length)
				{
					data = candidate;
					compressed = true;
				}
			}

			// Data tag/length + version tag/value + optional compression flag.
			long size = 1L + NetSync.VarintSize((ulong)data.Length) + data.Length + 2 + (compressed ? 2 : 0);
			if (size >= legacySize) return;
			command.Properties = null;
			command.Data = data;
			command.BatchFormat = 1;
			command.IsCompressed = compressed;
		}

		internal static List<SyncData> Decode(byte[] data)
		{
			CompactBatch batch = MyAPIGateway.Utilities.SerializeFromBinary<CompactBatch>(data);
			if (batch == null || batch.Ids == null || batch.Ids.Count == 0 || batch.Ids.Count > NetSync.MaxUpdatesPerPacket)
				throw new InvalidOperationException("Invalid compact batch count.");
			int count = batch.Ids.Count;
			if ((batch.EntityIds != null && batch.EntityIds.Count != count) ||
				(batch.SyncTypes != null && batch.SyncTypes.Count != count) ||
				(batch.Lengths != null && batch.Lengths.Count != count))
				throw new InvalidOperationException("Invalid compact batch metadata lengths.");

			long length = 0;
			for (int i = 0; i < count; i++)
			{
				SyncType type = batch.SyncTypes == null ? batch.SyncType : batch.SyncTypes[i];
				if (type != SyncType.Post && type != SyncType.Fetch && type != SyncType.Broadcast)
					throw new InvalidOperationException("Invalid compact batch sync type.");
				int encodedLength = batch.Lengths == null ? 0 : batch.Lengths[i];
				if (encodedLength < 0) throw new InvalidOperationException("Invalid compact value length.");
				if (encodedLength > 0) length += encodedLength - 1;
			}
			if (length != (batch.Values?.Length ?? 0))
				throw new InvalidOperationException("Invalid compact batch payload length.");

			// Validate the complete batch before returning anything for dispatch.
			var updates = new List<SyncData>(count);
			int offset = 0;
			for (int i = 0; i < count; i++)
			{
				int encodedLength = batch.Lengths == null ? 0 : batch.Lengths[i];
				byte[] value = null;
				if (encodedLength > 0)
				{
					value = new byte[encodedLength - 1];
					if (value.Length > 0) Array.Copy(batch.Values, offset, value, 0, value.Length);
					offset += value.Length;
				}
				updates.Add(new SyncData {
					Id = batch.Ids[i],
					EntityId = batch.EntityIds == null ? batch.EntityId : batch.EntityIds[i],
					SyncType = batch.SyncTypes == null ? batch.SyncType : batch.SyncTypes[i],
					Data = value
				});
			}
			return updates;
		}
	}
}
