
using ProtoBuf;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
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
		public byte[] Data;
		[ProtoMember(3)]
		public SyncType SyncType;
	}

	public abstract class NetSync
	{
		internal static Dictionary<long, NetSync> PropertyById = new Dictionary<long, NetSync>();

		internal static object locker = new object();
		internal static long generatorId = 1;
		internal static long GeneratePropertyId()
		{
			return generatorId++;
		}

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
	}

	public class NetSync<T> : NetSync
	{	
		/// <summary>
		/// The allowed network communication direction
		/// </summary>
		public TransferType TransferType { get; private set; }

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
		private MyEntity Entity;
		private string sessionName;

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

			Init(logic.Entity as MyEntity, TransferType, startingValue, syncOnLoad, limitToSyncDistance);
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

			Init(entity as MyEntity, TransferType, startingValue, syncOnLoad, limitToSyncDistance);
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

			Init(entity, TransferType, startingValue, syncOnLoad, limitToSyncDistance);
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
			Init(null, TransferType, startingValue, syncOnLoad, limitToSyncDistance);
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
			TransferType = TransferType;
			_value = startingValue;
			SyncOnLoad = syncOnLoad;
			LimitToSyncDistance = limitToSyncDistance;

			if (entity != null)
			{
				Entity = entity;
				Entity.OnClose += Entity_OnClose;
			}

			lock (locker)
			{
				Id = GeneratePropertyId();
				PropertyById.Add(Id, this);
			}

			// sync
			if (SyncOnLoad)
			{
				if (!SessionTools.Ready)
				{
					SessionTools.WhenReady += PropertyLoaded;
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

		private void Entity_OnClose(MyEntity entity)
		{
			PropertyById.Remove(Id);
		}

		/// <summary>
		/// Allows you to change how syncing works when setting the value this way
		/// </summary>
		public void SetValue(T val, SyncType syncType = SyncType.None)
		{
			T oldval = _value;
			lock (_value)
			{
				_value = val;
			}

			SendValue(syncType);

			ValueChanged?.Invoke(oldval, val);

		}

		/// <summary>
		/// Sets the data received over the network
		/// </summary>
		internal override void SetNetworkValue(byte[] data, ulong sender)
		{
			try
			{
				T oldval = _value;
				lock (_value)
				{
					_value = MyAPIGateway.Utilities.SerializeFromBinary<T>(data);

					if (NetworkAPI.LogNetworkTraffic)
					{
						MyLog.Default.Info($"[NetworkAPI] {Descriptor()} New value: {oldval} --- Old value: {_value}");
					}
				}

				if (MyAPIGateway.Multiplayer.IsServer && (TransferType == TransferType.ServerToClient || TransferType == TransferType.Both))
				{
					SendValue();
				}

				ValueChanged?.Invoke(oldval, _value);
				ValueChangedByNetwork?.Invoke(oldval, _value, sender);
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Failed to deserialize network property data\n{e}");
			}
		}

		/// <summary>
		/// sends the value across the network
		/// </summary>
		private void SendValue(SyncType syncType = SyncType.Broadcast, ulong sendTo = ulong.MinValue)
		{
			try
			{
				if (!NetworkAPI.IsInitialized)
				{
					MyLog.Default.Error($"[NetworkAPI] The NetworkAPI has not been initialized. Use NetworkAPI.Init() to initialize it.");
					return;
				}

				if ((TransferType == TransferType.ServerToClient && !MyAPIGateway.Multiplayer.IsServer) ||
					(TransferType == TransferType.ClientToServer && MyAPIGateway.Multiplayer.IsServer))
				{
					if (NetworkAPI.LogNetworkTraffic)
					{
						MyLog.Default.Info($"[NetworkAPI] {Descriptor()} Bad send direction transfer type is {TransferType}");
					}

					return;
				}

				if (MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.OFFLINE ||
					MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.PRIVATE ||
					syncType == SyncType.None)
				{
					if (NetworkAPI.LogNetworkTraffic)
					{
						MyLog.Default.Info($"[NetworkAPI] _OFFLINE_ {Descriptor()} Attemped to send value: {Value}");
					}

					return;
				}

				if (Value == null)
				{
					MyLog.Default.Error($"[NetworkAPI] ID: {Id} Type: {typeof(T)} Value is null. Cannot transmit null value.");
					return;
				}

				if (MyAPIGateway.Multiplayer.IsServer)
				{
					if (syncType == SyncType.Fetch)
						return;

					if (NetworkAPI.LogNetworkTraffic && syncType == SyncType.Post && sendTo == ulong.MinValue)
					{
						MyLog.Default.Error($"[NetworkAPI] {Descriptor()} Sync Type is POST but the recipient is missing. Sending message as Broadcast.");
					}
				}

				if (NetworkAPI.LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] _TRANSMITTING_ {Descriptor()} Sync Type: {syncType} Value: {Value}");
				}

				SyncData data = new SyncData() {
					Id = Id,
					Data = MyAPIGateway.Utilities.SerializeToBinary(_value),
					SyncType = syncType
				};

				if (NetworkAPI.IsInitialized)
				{
					ulong id = ulong.MinValue;
					if (MyAPIGateway.Session?.LocalHumanPlayer != null)
					{
						id = MyAPIGateway.Session.LocalHumanPlayer.SteamUserId;
					}

					if (Entity != null)
					{
						NetworkAPI.Instance.SendCommand(new Command() { IsProperty = true, Data = MyAPIGateway.Utilities.SerializeToBinary(data), SteamId = id }, Entity.PositionComp.GetPosition(), steamId: sendTo);
					}
					else
					{
						NetworkAPI.Instance.SendCommand(new Command() { IsProperty = true, Data = MyAPIGateway.Utilities.SerializeToBinary(data), SteamId = id }, steamId: sendTo);
					}
				}
				else
				{
					if (NetworkAPI.LogNetworkTraffic)
					{
						MyLog.Default.Error($"[NetworkAPI] Could not send. Network not initialized.");
					}
				}
			}
			catch
			{
				MyLog.Default.Error($"[NetworkAPI] SendValue(): Problem syncing value");
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
				MyLog.Default.Info($"[NetworkAPI] Transmission type: {pack.SyncType}");
			}

			if (!PropertyById.ContainsKey(pack.Id))
			{
				MyLog.Default.Info($"[NetworkAPI] Could not locate property {pack.Id}");
				return;
			}

			NetSync property = PropertyById[pack.Id];
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

		/// <summary>
		/// Request the lastest value from the server
		/// </summary>
		public override void Fetch()
		{
			SendValue(SyncType.Fetch);
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

		private void PropertyLoaded()
		{
			Fetch();
			SessionTools.WhenReady -= PropertyLoaded;
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
					return $"<{(Entity as MyCubeBlock).CubeGrid.DisplayName}//{Entity.GetType().Name}.{Entity.EntityId}//{typeof(T).Name}.{Id}>";
				}

				return $"<{Entity.GetType().Name}.{Entity.EntityId}//{typeof(T).Name}.{Id}>";
			}

			return $"<{sessionName}//{typeof(T).Name}.{Id}>";
		}
	}
}
