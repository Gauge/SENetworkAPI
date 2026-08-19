
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
	/// <summary>
	/// Directions a property is allowed to travel. Enforced on the sending
	/// machine only.
	/// </summary>
	public enum TransferType { ServerToClient, ClientToServer, Both }
	/// <summary>
	/// Purpose of a property packet. Values are part of the wire format and
	/// must not be reordered.
	/// </summary>
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

	/// <summary>
	/// Type independent part of a synced property: registration, addressing
	/// and batching. See <see cref="NetSync{T}"/>.
	/// </summary>
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

		internal static void ClearRegistries()
		{
			lock (locker)
			{
				PropertiesByEntity.Clear();
				PropertyById.Clear();
				pending.Clear();
				due.Clear();
				pendingFetches.Clear();
				dueFetches.Clear();
				pendingAnswers.Clear();
				dueAnswers.Clear();
				pendingAnswerTargets.Clear();
				dueAnswerTargets.Clear();
				flushScheduled = false;
				generatorId = 1;
			}
		}

		/// <summary>Directions this property is allowed to travel.</summary>
		public TransferType TransferType { get; internal set; }

		/// <summary>
		/// Address of this property. Declaration order for a property on an
		/// entity, a generated number for one on a session component.
		/// </summary>
		public long Id { get; internal set; }

		/// <summary>Fetches the current value from the server as soon as this side is ready.</summary>
		public bool SyncOnLoad { get; internal set; }

		/// <summary>Restricts updates to players within sync distance of the owning entity.</summary>
		public bool LimitToSyncDistance { get; internal set; }

		/// <summary>DateTime.Ticks of the last update received for this property.</summary>
		public long LastMessageTimestamp { get; internal set; }

		internal MyEntity Entity;

		internal bool Coalesced;

		internal bool IsLossy;

		internal bool IsDirty;

		internal bool IsFetchPending;

		/// <summary>Requests the current value from the server. No-op on a server.</summary>
		public abstract void Fetch();

		/// <summary>
		/// Raised before answering a fetch, so the value can be brought up to
		/// date first. Provides the requesting steam id.
		/// </summary>
		public Action<ulong> BeforeFetchRequestResponse;

		internal abstract void Push(SyncType type, ulong sendTo);

		internal abstract void SetNetworkValue(byte[] data, ulong sender);

		internal abstract SyncData BuildUpdate(SyncType syncType);

		internal abstract SyncData BuildFetch();

		internal abstract void RaiseFetchRequest(ulong sender);

		internal const int MaxUpdatesPerPacket = 500;

		private static List<NetSync> pending = new List<NetSync>();
		private static List<NetSync> due = new List<NetSync>();
		private static readonly List<SyncData> batch = new List<SyncData>();
		private static bool flushScheduled;

		private static List<NetSync> pendingFetches = new List<NetSync>();
		private static List<NetSync> dueFetches = new List<NetSync>();

		private static List<NetSync> pendingAnswers = new List<NetSync>();
		private static List<ulong> pendingAnswerTargets = new List<ulong>();
		private static List<NetSync> dueAnswers = new List<NetSync>();
		private static List<ulong> dueAnswerTargets = new List<ulong>();

		internal static void QueueForFlush(NetSync property)
		{
			bool schedule;

			lock (locker)
			{
				if (property.IsDirty)
				{
					return;
				}

				property.IsDirty = true;
				pending.Add(property);
				schedule = ClaimFlush();
			}

			if (schedule)
			{
				MyAPIGateway.Utilities.InvokeOnGameThread(Flush, "SENetworkAPI");
			}
		}

		internal static void QueueFetch(NetSync property)
		{
			bool schedule;

			lock (locker)
			{
				if (property.IsFetchPending)
				{
					return;
				}

				property.IsFetchPending = true;
				pendingFetches.Add(property);
				schedule = ClaimFlush();
			}

			if (schedule)
			{
				MyAPIGateway.Utilities.InvokeOnGameThread(Flush, "SENetworkAPI");
			}
		}

		internal static void QueueFetchAnswer(NetSync property, ulong sendTo)
		{
			bool schedule;

			lock (locker)
			{
				pendingAnswers.Add(property);
				pendingAnswerTargets.Add(sendTo);
				schedule = ClaimFlush();
			}

			if (schedule)
			{
				MyAPIGateway.Utilities.InvokeOnGameThread(Flush, "SENetworkAPI");
			}
		}

		private static bool ClaimFlush()
		{
			if (flushScheduled)
			{
				return false;
			}

			flushScheduled = true;
			return true;
		}

		internal static void Flush()
		{
			lock (locker)
			{
				flushScheduled = false;

				List<NetSync> swap = due;
				due = pending;
				pending = swap;
				pending.Clear();

				swap = dueFetches;
				dueFetches = pendingFetches;
				pendingFetches = swap;
				pendingFetches.Clear();

				swap = dueAnswers;
				dueAnswers = pendingAnswers;
				pendingAnswers = swap;
				pendingAnswers.Clear();

				List<ulong> swapTargets = dueAnswerTargets;
				dueAnswerTargets = pendingAnswerTargets;
				pendingAnswerTargets = swapTargets;
				pendingAnswerTargets.Clear();
			}

			FlushUpdates();
			FlushFetches();
			FlushFetchAnswers();
		}

		private static void FlushUpdates()
		{
			if (due.Count == 0)
			{
				return;
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

				for (int j = i + 1; j < due.Count; j++)
				{
					NetSync other = due[j];

					if (other.IsDirty && SharesDestination(first, other))
					{
						Collect(other, batch);
					}
				}

				SendBatch(batch, first, ulong.MinValue, "updates");
			}

			due.Clear();
		}

		private static void FlushFetches()
		{
			if (dueFetches.Count == 0)
			{
				return;
			}

			batch.Clear();

			for (int i = 0; i < dueFetches.Count; i++)
			{
				NetSync property = dueFetches[i];
				property.IsFetchPending = false;

				SyncData request = property.BuildFetch();

				if (request != null)
				{
					batch.Add(request);
				}
			}

			dueFetches.Clear();
			SendBatch(batch, null, ulong.MinValue, "fetches");
		}

		private static void FlushFetchAnswers()
		{
			if (dueAnswers.Count == 0)
			{
				return;
			}

			for (int i = 0; i < dueAnswers.Count; i++)
			{
				if (dueAnswers[i] == null)
				{
					continue;
				}

				ulong target = dueAnswerTargets[i];
				batch.Clear();
				CollectAnswer(dueAnswers[i], target, batch);

				for (int j = i + 1; j < dueAnswers.Count; j++)
				{
					if (dueAnswers[j] != null && dueAnswerTargets[j] == target)
					{
						CollectAnswer(dueAnswers[j], target, batch);
						dueAnswers[j] = null;
					}
				}

				SendBatch(batch, null, target, "fetch answers");
			}

			dueAnswers.Clear();
			dueAnswerTargets.Clear();
		}

		private static void CollectAnswer(NetSync property, ulong sender, List<SyncData> into)
		{
			try
			{
				property.RaiseFetchRequest(sender);
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] BeforeFetchRequestResponse handler threw:\n{e}");
			}

			SyncData update = property.BuildUpdate(SyncType.Post);

			if (update != null)
			{
				into.Add(update);
			}
		}

		private static void Collect(NetSync property, List<SyncData> into)
		{
			property.IsDirty = false;

			SyncData update = property.BuildUpdate(SyncType.Broadcast);

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

		private static void SendBatch(List<SyncData> updates, NetSync group, ulong sendTo, string what)
		{
			if (updates.Count == 0 || !NetworkAPI.IsInitialized)
			{
				return;
			}

			try
			{
				ulong id = ulong.MinValue;
				IMyPlayer localPlayer = MyAPIGateway.Session?.LocalHumanPlayer;

				if (localPlayer != null)
				{
					id = localPlayer.SteamUserId;
				}

				bool isReliable = group == null || !group.IsLossy;
				bool positional = group != null && group.LimitToSyncDistance && group.Entity != null;

				for (int start = 0; start < updates.Count; start += MaxUpdatesPerPacket)
				{
					int count = Math.Min(MaxUpdatesPerPacket, updates.Count - start);
					Command cmd = new Command() { IsProperty = true, SteamId = id };

					if (count == 1)
					{
						cmd.Property = updates[start];
					}
					else
					{
						List<SyncData> carried = new List<SyncData>(count);

						for (int i = 0; i < count; i++)
						{
							carried.Add(updates[start + i]);
						}

						cmd.Properties = carried;
					}

					if (positional)
					{
						NetworkAPI.Instance.SendCommand(cmd, group.Entity.PositionComp.GetPosition(), steamId: sendTo, isReliable: isReliable);
					}
					else
					{
						NetworkAPI.Instance.SendCommand(cmd, steamId: sendTo, isReliable: isReliable);
					}
				}
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] _ERROR_ Flush(): Problem sending batched {what}: {e}");
			}
		}

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
				MyEntity entity = MyAPIGateway.Entities.GetEntityById(pack.EntityId) as MyEntity;

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
				QueueFetchAnswer(property, sender);
			}
			else
			{
				property.SetNetworkValue(pack.Data, sender);
			}
		}
	}

	/// <summary>
	/// A value kept in step across the network. Attach to an entity for
	/// per-block state, or to a session component for mod wide state.
	/// T must be serializable by MyAPIGateway.Utilities.SerializeToBinary.
	/// </summary>
	public class NetSync<T> : NetSync
	{
		/// <summary>Raised on every change, local or remote. Provides old and new value.</summary>
		public Action<T, T> ValueChanged;

		/// <summary>
		/// Raised only for changes arriving over the network. Provides old value,
		/// new value and sender.
		/// </summary>
		public Action<T, T, ulong> ValueChangedByNetwork;

		/// <summary>
		/// The value. Assigning a different value broadcasts it; assigning an
		/// equal one does nothing unless <see cref="AlwaysSend"/> is set.
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

		/// <summary>A property owned by an entity.</summary>
		/// <param name="entity">The owning entity</param>
		/// <param name="transferType">Directions this property is allowed to travel</param>
		/// <param name="startingValue">Initial local value. A null value cannot be transmitted</param>
		/// <param name="syncOnLoad">Fetch the current value once the entity is in the scene</param>
		/// <param name="limitToSyncDistance">Restrict updates to players within sync distance</param>
		/// <exception cref="Exception">The entity is null</exception>
		public NetSync(IMyEntity entity, TransferType transferType, T startingValue = default(T), bool syncOnLoad = true, bool limitToSyncDistance = true)
		{
			if (entity == null)
			{
				throw new Exception("[NetworkAPI] Attemped to create a NetSync property. MyEntity was null.");
			}

			Init(entity as MyEntity, transferType, startingValue, syncOnLoad, limitToSyncDistance);
		}

		/// <summary>A property owned by an entity.</summary>
		/// <param name="entity">The owning entity</param>
		/// <param name="transferType">Directions this property is allowed to travel</param>
		/// <param name="startingValue">Initial local value. A null value cannot be transmitted</param>
		/// <param name="syncOnLoad">Fetch the current value once the entity is in the scene</param>
		/// <param name="limitToSyncDistance">Restrict updates to players within sync distance</param>
		/// <exception cref="Exception">The entity is null</exception>
		public NetSync(MyEntity entity, TransferType transferType, T startingValue = default(T), bool syncOnLoad = true, bool limitToSyncDistance = true)
		{
			if (entity == null)
			{
				throw new Exception("[NetworkAPI] Attemped to create a NetSync property. MyEntity was null.");
			}

			Init(entity, transferType, startingValue, syncOnLoad, limitToSyncDistance);
		}

		/// <summary>A property owned by the entity this game logic component is attached to.</summary>
		/// <param name="logic">The owning game logic component</param>
		/// <param name="transferType">Directions this property is allowed to travel</param>
		/// <param name="startingValue">Initial local value. A null value cannot be transmitted</param>
		/// <param name="syncOnLoad">Fetch the current value once the entity is in the scene</param>
		/// <param name="limitToSyncDistance">Restrict updates to players within sync distance</param>
		/// <exception cref="Exception">The component or its entity is null</exception>
		public NetSync(MyGameLogicComponent logic, TransferType transferType, T startingValue = default(T), bool syncOnLoad = true, bool limitToSyncDistance = true)
		{
			if (logic?.Entity == null)
			{
				throw new Exception("[NetworkAPI] Attemped to create a NetSync property. MyGameLogicComponent was null.");
			}

			Init(logic.Entity as MyEntity, transferType, startingValue, syncOnLoad, limitToSyncDistance);
		}

		/// <summary>A property owned by the mod rather than by any entity.</summary>
		/// <param name="logic">The owning session component</param>
		/// <param name="transferType">Directions this property is allowed to travel</param>
		/// <param name="startingValue">Initial local value. A null value cannot be transmitted</param>
		/// <param name="syncOnLoad">Fetch the current value immediately</param>
		/// <param name="limitToSyncDistance">Unused: a session property has no position</param>
		/// <exception cref="Exception">The component is null</exception>
		public NetSync(MySessionComponentBase logic, TransferType transferType, T startingValue = default(T), bool syncOnLoad = true, bool limitToSyncDistance = true)
		{
			if (logic == null)
			{
				throw new Exception("[NetworkAPI] Attemped to create a NetSync property. MySessionComponentBase was null.");
			}

			sessionName = logic.GetType().Name;
			Init(null, transferType, startingValue, syncOnLoad, limitToSyncDistance);
		}

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
			entity.OnClose -= Entity_OnClose;
			entity.AddedToScene -= SyncOnAddedToScene;

			lock (locker)
			{
				PropertiesByEntity.Remove(entity);
			}
		}

		/// <summary>
		/// Sends on every assignment, including one that does not change the
		/// value. Off by default, in which case an unchanged assignment sends
		/// nothing and does not raise <see cref="ValueChanged"/>.
		/// </summary>
		/// <param name="enabled">False restores change detection</param>
		/// <returns>This property, for chaining at the declaration</returns>
		public NetSync<T> AlwaysSend(bool enabled = true)
		{
			alwaysSend = enabled;
			return this;
		}

		/// <summary>
		/// Permits the unreliable channel for updates that fit within
		/// <see cref="NetworkAPI.UnreliableMessageLimit"/>. Fetches stay reliable.
		/// </summary>
		/// <param name="enabled">False restores reliable sends</param>
		/// <returns>This property, for chaining at the declaration</returns>
		public NetSync<T> Lossy(bool enabled = true)
		{
			IsLossy = enabled;
			return this;
		}

		/// <summary>
		/// Batches updates with every other coalesced property that changes in
		/// the same frame, into one packet per destination. Costs one frame of
		/// latency. <see cref="Push()"/> still sends immediately.
		/// </summary>
		/// <param name="enabled">False restores immediate sends</param>
		/// <returns>This property, for chaining at the declaration</returns>
		public NetSync<T> Coalesce(bool enabled = true)
		{
			Coalesced = enabled;
			return this;
		}

		/// <summary>Sets the value, choosing what to send. Sends nothing by default.</summary>
		/// <param name="val">The new value</param>
		/// <param name="syncType">What to transmit, if anything</param>
		public void SetValue(T val, SyncType syncType = SyncType.None)
		{
			T oldval = _value;

			if (!alwaysSend && ComparisonIsMeaningful && EqualityComparer<T>.Default.Equals(oldval, val))
			{
				return;
			}

			_value = val;

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
				SendValue(SyncType.Broadcast, ulong.MinValue, data);
			}

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

		internal override SyncData BuildFetch()
		{
			if (!CanSend(SyncType.Fetch))
			{
				return null;
			}

			return new SyncData() {
				Id = Id,
				EntityId = (Entity != null) ? Entity.EntityId : 0,
				SyncType = SyncType.Fetch
			};
		}

		internal override void RaiseFetchRequest(ulong sender)
		{
			BeforeFetchRequestResponse?.Invoke(sender);
		}

		internal override SyncData BuildUpdate(SyncType syncType)
		{
			try
			{
				if (!CanSend(syncType) || _value == null)
				{
					return null;
				}

				return new SyncData() {
					Id = Id,
					EntityId = (Entity != null) ? Entity.EntityId : 0,
					Data = MyAPIGateway.Utilities.SerializeToBinary(_value),
					SyncType = syncType
				};
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] _ERROR_ BuildUpdate(): Problem encoding value: {e}");
				return null;
			}
		}

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

		private void SendValue(SyncType syncType = SyncType.Broadcast, ulong sendTo = ulong.MinValue, byte[] serializedValue = null)
		{
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

		/// <summary>Requests the current value from the server. No-op on a server.</summary>
		public override void Fetch()
		{
			if (!MyAPIGateway.Multiplayer.IsServer)
			{
				QueueFetch(this);
			}
		}

		/// <summary>Broadcasts the current value now, changed or not, ignoring batching.</summary>
		public void Push()
		{
			SendValue();
		}

		/// <summary>Sends the current value to one player now.</summary>
		/// <param name="sendTo">The recipient's steam id</param>
		public void Push(ulong sendTo)
		{
			SendValue(SyncType.Post, sendTo);
		}

		internal override void Push(SyncType type, ulong sendTo = ulong.MinValue)
		{
			SendValue(type, sendTo);
		}

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
