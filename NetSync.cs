
using ProtoBuf;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

namespace SENetworkAPI
{
	public enum TransferType { ServerToClient, ClientToServer, Both }
	public enum SyncType { Post, Fetch, Broadcast, None }

	[ProtoContract]
	internal class SyncData
	{
		[ProtoMember(1)]
		public long Id;
		[ProtoMember(2)]
		public long EntityId;
		[ProtoMember(3)]
		public byte[] Data;
		[ProtoMember(4)]
		public SyncType SyncType;
	}

	public abstract class NetSync
	{
		internal static Dictionary<MyEntity, List<NetSync>> PropertiesByEntity = new Dictionary<MyEntity, List<NetSync>>();
		internal static Dictionary<long, NetSync> PropertyById = new Dictionary<long, NetSync>();

		internal static object locker = new object();
		internal static long generatorId = 1;
		internal static long GeneratePropertyId()
		{
			return generatorId++;
		}

		/// <summary>
		/// Drops every registered property. Called when the session unloads:
		/// these registries are static, so without this they would hold on to
		/// the previous world's properties - and the entities they point at -
		/// for as long as the game process lives.
		/// </summary>
		internal static void ClearRegistries()
		{
			lock (locker)
			{
				PropertiesByEntity.Clear();
				PropertyById.Clear();
				pending.Clear();
				flushScheduled = false;
				generatorId = 1;
			}
		}

		/// <summary>
		/// The allowed network communication direction
		/// </summary>
		public TransferType TransferType { get; internal set; }

		/// <summary>
		/// The identity of this property
		/// </summary>
		public long Id { get; internal set; }

		/// <summary>
		/// Enables/Disables network traffic out when setting a value
		/// </summary>
		public bool SyncOnLoad { get; internal set; }

		/// <summary>
		/// Limits sync updates to within sync distance
		/// </summary>
		public bool LimitToSyncDistance { get; internal set; }

		/// <summary>
		/// the last recorded network traffic
		/// </summary>
		public long LastMessageTimestamp { get; internal set; }

		/// <summary>
		/// The entity this property belongs to, or null for a session property.
		/// </summary>
		internal MyEntity Entity;

		/// <summary>
		/// Batch this property's updates with the other coalesced properties
		/// that change in the same frame. See NetSync&lt;T&gt;.Coalesce().
		/// </summary>
		internal bool Coalesced;

		/// <summary>Sends this property's updates unreliably when they fit.</summary>
		internal bool IsLossy;

		/// <summary>Set while this property is waiting in the pending batch.</summary>
		internal bool IsDirty;

		/// <summary>
		/// Request the lastest value from the server
		/// </summary>
		public abstract void Fetch();

		/// <summary>
		/// Triggers after recieving a fetch request from clients
		/// and allows you to modify this property before it is sent.
		/// </summary>
		public Action<ulong> BeforeFetchRequestResponse;

		internal abstract void Push(SyncType type, ulong sendTo);

		internal abstract void SetNetworkValue(byte[] data, ulong sender);

		/// <summary>
		/// Builds the update to put in a batch, or returns null when this
		/// property should not send right now (wrong direction, no value yet,
		/// offline). Mirrors the checks a direct send makes.
		/// </summary>
		internal abstract SyncData BuildUpdate();

		// ------------------------------------------------------------------
		//  Coalescing
		// ------------------------------------------------------------------

		private static readonly List<NetSync> pending = new List<NetSync>();
		private static readonly List<SyncData> batch = new List<SyncData>();
		private static bool flushScheduled;

		/// <summary>
		/// Queues this property to be sent with everything else that changes
		/// this frame. The flush runs on the game thread on the next update.
		/// </summary>
		internal static void QueueForFlush(NetSync property)
		{
			lock (locker)
			{
				if (property.IsDirty)
				{
					return;
				}

				property.IsDirty = true;
				pending.Add(property);

				if (flushScheduled)
				{
					return;
				}

				flushScheduled = true;
			}

			MyAPIGateway.Utilities.InvokeOnGameThread(Flush, "SENetworkAPI");
		}

		/// <summary>
		/// Sends everything queued this frame, one packet per group of
		/// properties that share a destination.
		/// </summary>
		internal static void Flush()
		{
			List<NetSync> due;

			lock (locker)
			{
				flushScheduled = false;

				if (pending.Count == 0)
				{
					return;
				}

				due = new List<NetSync>(pending);
				pending.Clear();
			}

			for (int i = 0; i < due.Count; i++)
			{
				NetSync first = due[i];

				if (!first.IsDirty)
				{
					continue;
				}

				batch.Clear();
				Collect(first, batch);

				// Everything in the group shares an owner, a distance rule and
				// a reliability choice, so one packet covers them all.
				for (int j = i + 1; j < due.Count; j++)
				{
					NetSync other = due[j];

					if (other.IsDirty && SharesDestination(first, other))
					{
						Collect(other, batch);
					}
				}

				Send(first, batch);
			}
		}

		private static void Collect(NetSync property, List<SyncData> into)
		{
			property.IsDirty = false;

			SyncData update = property.BuildUpdate();

			if (update != null)
			{
				into.Add(update);
			}
		}

		private static bool SharesDestination(NetSync a, NetSync b)
		{
			return a.Entity == b.Entity
				&& a.LimitToSyncDistance == b.LimitToSyncDistance
				&& a.IsLossy == b.IsLossy;
		}

		private static void Send(NetSync group, List<SyncData> updates)
		{
			if (updates.Count == 0 || !NetworkAPI.IsInitialized)
			{
				return;
			}

			ulong id = ulong.MinValue;
			IMyPlayer localPlayer = MyAPIGateway.Session?.LocalHumanPlayer;

			if (localPlayer != null)
			{
				id = localPlayer.SteamUserId;
			}

			Command cmd = new Command() { IsProperty = true, SteamId = id };

			// One update is the common case even when coalescing: keep it off
			// the list field so the packet stays as small as a direct send.
			if (updates.Count == 1)
			{
				cmd.Property = updates[0];
			}
			else
			{
				cmd.Properties = new List<SyncData>(updates);
			}

			bool isReliable = !group.IsLossy;

			if (group.LimitToSyncDistance && group.Entity != null)
			{
				NetworkAPI.Instance.SendCommand(cmd, group.Entity.PositionComp.GetPosition(), isReliable: isReliable);
			}
			else
			{
				NetworkAPI.Instance.SendCommand(cmd, isReliable: isReliable);
			}
		}

		/// <summary>
		/// Receives and redirects all property traffic
		/// </summary>
		/// <param name="pack">this hold the path to the property and the data to sync</param>
		internal static void RouteMessage(SyncData pack, ulong sender, long timestamp)
		{
			if (pack == null)
			{
				MyLog.Default.Error($"[NetworkAPI] Property data is null");
				return;
			}

			if (NetworkAPI.LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] Id:{pack.Id}, EId:{pack.EntityId}, {pack.SyncType}");
			}

			NetSync property;
			if (pack.EntityId == 0)
			{
				if (!PropertyById.TryGetValue(pack.Id, out property))
				{
					MyLog.Default.Info($"[NetworkAPI] id not registered in dictionary 'PropertyById'");
					return;
				}
			}
			else
			{
				MyEntity entity = (MyEntity)MyAPIGateway.Entities.GetEntityById(pack.EntityId);

				if (entity == null)
				{
					MyLog.Default.Info($"[NetworkAPI] Failed to get entity by id");
					return;
				}

				List<NetSync> properties;
				if (!PropertiesByEntity.TryGetValue(entity, out properties))
				{
					MyLog.Default.Info($"[NetworkAPI] Entity not registered in dictionary 'PropertiesByEntity'");
					return;
				}

				if (pack.Id < 0 || pack.Id >= properties.Count)
				{
					MyLog.Default.Info($"[NetworkAPI] property index out of range");
					return;
				}

				property = properties[(int)pack.Id];
			}

			property.LastMessageTimestamp = timestamp;
			if (pack.SyncType == SyncType.Fetch)
			{
				property.BeforeFetchRequestResponse?.Invoke(sender);
				property.Push(SyncType.Post, sender);
			}
			else
			{
				property.SetNetworkValue(pack.Data, sender);
			}
		}
	}

	public class NetSync<T> : NetSync
	{
		/// <summary>
		/// Fires each time the value is changed
		/// Provides the old value and the new value
		/// </summary>
		public Action<T, T> ValueChanged;

		/// <summary>
		/// Fires only when the a network call is made
		/// Provides the old value and the new value
		/// also provides the steamId
		/// </summary>
		public Action<T, T, ulong> ValueChangedByNetwork;

		/// <summary>
		/// this property syncs across the network when changed
		/// </summary>
		public T Value
		{
			get { return _value; }
			set
			{
				SetValue(value, SyncType.Broadcast);
			}
		}

		private T _value;
		private string sessionName;
		private bool alwaysSend;

		/// <summary>
		/// Whether comparing two values of T is both cheap and meaningful.
		///
		/// Reference types are excluded on purpose: the same instance can have
		/// different contents from one assignment to the next, so "same
		/// reference" must never be read as "nothing changed". Structs are only
		/// included when they compare without boxing.
		/// </summary>
		private static readonly bool ComparisonIsMeaningful = IsComparisonMeaningful();

		private static bool IsComparisonMeaningful()
		{
			if (typeof(T) == typeof(string))
			{
				return true;
			}

			object probe = default(T);

			if (probe == null)
			{
				return false;
			}

			return probe is IEquatable<T> || probe is IComparable;
		}

		/// <param name="entity">IMyEntity object this property is attached to</param>
		/// <param name="transferType"></param>
		/// <param name="startingValue">Sets an initial value</param>
		/// <param name="syncOnLoad">automatically syncs data to clients when the class initializes</param>
		/// <param name="limitToSyncDistance">marking this true only sends data to clients within sync distance</param>
		public NetSync(IMyEntity entity, TransferType transferType, T startingValue = default(T), bool syncOnLoad = true, bool limitToSyncDistance = true)
		{
			if (entity == null)
			{
				throw new Exception("[NetworkAPI] Attemped to create a NetSync property. MyEntity was null.");
			}

			Init(entity as MyEntity, transferType, startingValue, syncOnLoad, limitToSyncDistance);
		}

		/// <param name="entity">MyEntity object this property is attached to</param>
		/// <param name="transferType"></param>
		/// <param name="startingValue">Sets an initial value</param>
		/// <param name="syncOnLoad">automatically syncs data to clients when the class initializes</param>
		/// <param name="limitToSyncDistance">marking this true only sends data to clients within sync distance</param>
		public NetSync(MyEntity entity, TransferType transferType, T startingValue = default(T), bool syncOnLoad = true, bool limitToSyncDistance = true)
		{
			if (entity == null)
			{
				throw new Exception("[NetworkAPI] Attemped to create a NetSync property. MyEntity was null.");
			}

			Init(entity, transferType, startingValue, syncOnLoad, limitToSyncDistance);
		}

		/// <param name="logic">MyGameLogicComponent object this property is attached to</param>
		/// <param name="transferType"></param>
		/// <param name="startingValue">Sets an initial value</param>
		/// <param name="syncOnLoad">automatically syncs data to clients when the class initializes</param>
		/// <param name="limitToSyncDistance">marking this true only sends data to clients within sync distance</param>
		public NetSync(MyGameLogicComponent logic, TransferType transferType, T startingValue = default(T), bool syncOnLoad = true, bool limitToSyncDistance = true)
		{
			if (logic?.Entity == null)
			{
				throw new Exception("[NetworkAPI] Attemped to create a NetSync property. MyGameLogicComponent was null.");
			}

			Init(logic.Entity as MyEntity, transferType, startingValue, syncOnLoad, limitToSyncDistance);
		}

		/// <param name="logic">MySessionComponentBase object this property is attached to</param>
		/// <param name="transferType"></param>
		/// <param name="startingValue">Sets an initial value</param>
		/// <param name="syncOnLoad">automatically syncs data to clients when the class initializes</param>
		/// <param name="limitToSyncDistance">marking this true only sends data to clients within sync distance</param>
		public NetSync(MySessionComponentBase logic, TransferType transferType, T startingValue = default(T), bool syncOnLoad = true, bool limitToSyncDistance = true)
		{
			if (logic == null)
			{
				throw new Exception("[NetworkAPI] Attemped to create a NetSync property. MySessionComponentBase was null.");
			}

			sessionName = logic.GetType().Name;
			Init(null, transferType, startingValue, syncOnLoad, limitToSyncDistance);
		}

		/// <summary>
		/// This funtion is called by the constructer
		/// </summary>
		/// <param name="transferType"></param>
		/// <param name="startingValue">Sets an initial value</param>
		/// <param name="syncOnLoad">automatically syncs data to clients when the class initializes</param>
		/// <param name="limitToSyncDistance">marking this true only sends data to clients within sync distance</param>
		private void Init(MyEntity entity, TransferType transferType, T startingValue = default(T), bool syncOnLoad = true, bool limitToSyncDistance = true)
		{
			TransferType = transferType;
			_value = startingValue;
			SyncOnLoad = syncOnLoad;
			LimitToSyncDistance = limitToSyncDistance;

			if (entity != null)
			{
				Entity = entity;
				Entity.OnClose += Entity_OnClose;

				// The lookup has to happen inside the lock: outside it, two
				// threads could both miss and both try to Add the same entity.
				lock (locker)
				{
					List<NetSync> properties;
					if (PropertiesByEntity.TryGetValue(Entity, out properties))
					{
						properties.Add(this);
						Id = properties.Count - 1;
					}
					else
					{
						PropertiesByEntity.Add(Entity, new List<NetSync> { this });
						Id = 0;
					}
				}
			}
			else
			{
				lock (locker)
				{
					Id = GeneratePropertyId();
					PropertyById.Add(Id, this);
				}
			}

			if (SyncOnLoad)
			{
				if (Entity != null)
				{
					Entity.AddedToScene += SyncOnAddedToScene;
				}
				else
				{
					Fetch();
				}
			}

			if (NetworkAPI.LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] Property Created: {Descriptor()}, Transfer: {transferType}, SyncOnLoad: {SyncOnLoad}");
			}
		}

		private void SyncOnAddedToScene(MyEntity e) 
		{
			if (Entity != e)
				return;

			Fetch();			
			Entity.AddedToScene -= SyncOnAddedToScene;
		}

		private void Entity_OnClose(MyEntity entity)
		{
			// Entity scoped properties live in PropertiesByEntity, never in
			// PropertyById. Removing the entity's entry drops the whole property
			// list along with the strong reference to the entity itself, which
			// used to be held for the rest of the session.
			entity.OnClose -= Entity_OnClose;
			entity.AddedToScene -= SyncOnAddedToScene;

			lock (locker)
			{
				PropertiesByEntity.Remove(entity);
			}
		}

		/// <summary>
		/// Restores the original behaviour of sending on every assignment, even
		/// when the value is identical to the one already held.
		///
		/// By default an assignment that does not change the value is dropped:
		/// nothing is sent and ValueChanged does not fire. Use this if your mod
		/// treats an assignment as an event rather than a state change - a
		/// heartbeat, say. <see cref="Push()"/> always sends regardless.
		/// </summary>
		public NetSync<T> AlwaysSend(bool enabled = true)
		{
			alwaysSend = enabled;
			return this;
		}

		/// <summary>
		/// Sends this property's updates on the unreliable channel when they fit
		/// (the engine drops unreliable messages over
		/// <see cref="NetworkAPI.UnreliableMessageLimit"/> bytes, so anything
		/// larger is sent reliably anyway).
		///
		/// Worth it for values that are overwritten constantly, where a dropped
		/// update is replaced by the next one before anybody notices. Fetches
		/// are always reliable - losing one means never syncing at all.
		/// </summary>
		public NetSync<T> Lossy(bool enabled = true)
		{
			IsLossy = enabled;
			return this;
		}

		/// <summary>
		/// Batches this property's updates with every other coalesced property
		/// that changes in the same frame, so a block whose properties move
		/// together costs one packet instead of one each.
		///
		/// The update goes out on the next game update rather than immediately,
		/// so it trades a frame of latency for the traffic. <see cref="Push()"/>
		/// still sends straight away.
		/// </summary>
		public NetSync<T> Coalesce(bool enabled = true)
		{
			Coalesced = enabled;
			return this;
		}

		/// <summary>
		/// Allows you to change how syncing works when setting the value this way
		/// </summary>
		public void SetValue(T val, SyncType syncType = SyncType.None)
		{
			T oldval = _value;

			// An assignment that changes nothing is not worth a packet.
			if (!alwaysSend && ComparisonIsMeaningful && EqualityComparer<T>.Default.Equals(oldval, val))
			{
				return;
			}

			_value = val;

			// A coalesced property waits for the frame's flush; anything else
			// (Fetch, Post, None) keeps its immediate behaviour.
			if (Coalesced && syncType == SyncType.Broadcast)
			{
				QueueForFlush(this);
			}
			else
			{
				SendValue(syncType);
			}

			ValueChanged?.Invoke(oldval, val);
		}

		/// <summary>
		/// Sets the data received over the network
		/// </summary>
		internal override void SetNetworkValue(byte[] data, ulong sender)
		{
			T oldval = _value;

			try
			{
				_value = MyAPIGateway.Utilities.SerializeFromBinary<T>(data);

				if (NetworkAPI.LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] {Descriptor()} Old value: {oldval} --- New value: {_value}");
				}

			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Failed to deserialize network property data\n{e}");
				return;
			}

			if (MyAPIGateway.Multiplayer.IsServer)
			{
				// Relay the bytes we were handed instead of serializing the
				// value again: nothing has touched it since it was decoded, so
				// the two are identical.
				SendValue(SyncType.Broadcast, ulong.MinValue, data);
			}

			// A handler that throws must not look like a decode failure, and
			// must not stop the other handler from running.
			try
			{
				ValueChanged?.Invoke(oldval, _value);
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] {Descriptor()} ValueChanged handler threw:\n{e}");
			}

			try
			{
				ValueChangedByNetwork?.Invoke(oldval, _value, sender);
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] {Descriptor()} ValueChangedByNetwork handler threw:\n{e}");
			}
		}

		/// <summary>
		/// Builds this property's contribution to a batched packet, applying the
		/// same rules a direct send would.
		/// </summary>
		internal override SyncData BuildUpdate()
		{
			try
			{
				if (!CanSend(SyncType.Broadcast) || _value == null)
				{
					return null;
				}

				return new SyncData() {
					Id = Id,
					EntityId = (Entity != null) ? Entity.EntityId : 0,
					Data = MyAPIGateway.Utilities.SerializeToBinary(_value),
					SyncType = SyncType.Broadcast
				};
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] _ERROR_ BuildUpdate(): Problem encoding value: {e}");
				return null;
			}
		}

		/// <summary>
		/// Whether the network will accept this update: initialised, online, and
		/// travelling in a direction this property allows.
		/// </summary>
		private bool CanSend(SyncType syncType)
		{
			if (!NetworkAPI.IsInitialized || syncType == SyncType.None)
			{
				return false;
			}

			bool isServer = MyAPIGateway.Multiplayer.IsServer;

			if (syncType != SyncType.Fetch &&
				((TransferType == TransferType.ServerToClient && !isServer) ||
				 (TransferType == TransferType.ClientToServer && isServer)))
			{
				return false;
			}

			IMySession session = MyAPIGateway.Session;

			return session == null || session.OnlineMode != MyOnlineModeEnum.OFFLINE;
		}

		/// <summary>
		/// sends the value across the network
		/// </summary>
		/// <param name="serializedValue">
		/// Already encoded bytes for the current value, when the caller has them.
		/// Saves re-serializing on the server's relay path.
		/// </param>
		private void SendValue(SyncType syncType = SyncType.Broadcast, ulong sendTo = ulong.MinValue, byte[] serializedValue = null)
		{
			// This value is going out now, so drop any batched copy of it.
			IsDirty = false;

			try
			{
				if (!NetworkAPI.IsInitialized)
				{
					MyLog.Default.Error($"[NetworkAPI] _ERROR_ The NetworkAPI has not been initialized. Use NetworkAPI.Init() to initialize it.");
					return;
				}

				if (syncType == SyncType.None)
				{
					if (NetworkAPI.LogNetworkTraffic)
					{
						MyLog.Default.Info($"[NetworkAPI] _INTERNAL_ {Descriptor()} Wont send value: {Value}");
					}

					return;
				}

				bool isServer = MyAPIGateway.Multiplayer.IsServer;

				// A fetch is a request for a value, not a value, so it is exempt
				// from the direction check - in both directions. The brackets
				// used to be missing, and && binds tighter than ||, so a server
				// could not answer a fetch for a ClientToServer property.
				if (syncType != SyncType.Fetch &&
					((TransferType == TransferType.ServerToClient && !isServer) ||
					 (TransferType == TransferType.ClientToServer && isServer)))
				{
					if (NetworkAPI.LogNetworkTraffic)
					{
						MyLog.Default.Info($"[NetworkAPI] {Descriptor()} Bad send direction transfer type is {TransferType}");
					}

					return;
				}

				IMySession session = MyAPIGateway.Session;

				if (session != null && session.OnlineMode == MyOnlineModeEnum.OFFLINE)
				{
					if (NetworkAPI.LogNetworkTraffic)
					{
						MyLog.Default.Info($"[NetworkAPI] _OFFLINE_ {Descriptor()} Wont send value: {Value}");
					}

					return;
				}

				// A fetch is a request, not an update. The receiver ignores the
				// payload, so there is no reason to encode and ship the value -
				// and no reason a property sitting at null cannot ask for one.
				bool carriesValue = syncType != SyncType.Fetch;

				if (carriesValue && _value == null)
				{
					if (NetworkAPI.LogNetworkTraffic)
					{
						MyLog.Default.Error($"[NetworkAPI] _ERROR_ {Descriptor()} Value is null. Cannot transmit null value.");
					}

					return;
				}

				SyncData data = new SyncData() {
					Id = Id,
					EntityId = (Entity != null) ? Entity.EntityId : 0,
					Data = carriesValue ? (serializedValue ?? MyAPIGateway.Utilities.SerializeToBinary(_value)) : null,
					SyncType = syncType
				};

				ulong id = ulong.MinValue;
				IMyPlayer localPlayer = session?.LocalHumanPlayer;
				if (localPlayer != null)
				{
					id = localPlayer.SteamUserId;
				}

				if (id == sendTo && id != ulong.MinValue)
				{
					MyLog.Default.Error($"[NetworkAPI] _ERROR_ {Descriptor()} The sender id is the same as the recievers id. data will not be sent.");
				}

				if (NetworkAPI.LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] _TRANSMITTING_ {Descriptor()} - Id:{data.Id}, EId:{data.EntityId}, {data.SyncType}, {((data.SyncType == SyncType.Fetch) ? "" : $"Val:{_value}")}");
				}

				// A lost fetch means the value never arrives at all, so requests
				// stay reliable however the property is configured.
				bool isReliable = !IsLossy || syncType == SyncType.Fetch;

				if (LimitToSyncDistance && Entity != null)
				{
					NetworkAPI.Instance.SendCommand(new Command() { IsProperty = true, Property = data, SteamId = id }, Entity.PositionComp.GetPosition(), steamId: sendTo, isReliable: isReliable);
				}
				else
				{
					NetworkAPI.Instance.SendCommand(new Command() { IsProperty = true, Property = data, SteamId = id }, steamId: sendTo, isReliable: isReliable);
				}
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] _ERROR_ SendValue(): Problem syncing value: {e}");
			}
		}

		/// <summary>
		/// Request the lastest value from the server
		/// Servers are not allowed to fetch from clients
		/// </summary>
		public override void Fetch()
		{
			if (!MyAPIGateway.Multiplayer.IsServer)
			{
				SendValue(SyncType.Fetch);
			}
		}

		/// <summary>
		/// Send data now
		/// </summary>
		public void Push()
		{
			SendValue();
		}

		/// <summary>
		/// Send data to single user
		/// </summary>
		public void Push(ulong sendTo)
		{
			SendValue(SyncType.Post, sendTo);
		}

		/// <summary>
		/// Send data across the network now
		/// </summary>
		internal override void Push(SyncType type, ulong sendTo = ulong.MinValue)
		{
			SendValue(type, sendTo);
		}

		/// <summary>
		/// Identifier for logging readability
		/// </summary>
		internal string Descriptor()
		{
			if (Entity != null)
			{
				if (Entity is MyCubeBlock)
				{
					return $"<{(Entity as MyCubeBlock).CubeGrid.DisplayName}_{((Entity.DefinitionId?.SubtypeId == null) ? Entity.GetType().Name.ToString() : Entity.DefinitionId?.SubtypeId.ToString())}.{Entity.EntityId}_{typeof(T).Name}.{Id}>";
				}

				return $"<{((Entity.DefinitionId?.SubtypeId == null) ? Entity.GetType().Name.ToString() : Entity.DefinitionId?.SubtypeId.ToString())}.{Entity.EntityId}_{typeof(T).Name}.{Id}>";
			}

			return $"<{sessionName}_{typeof(T).Name}.{Id}>";
		}
	}
}
