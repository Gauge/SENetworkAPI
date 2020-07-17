using ProtoBuf;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game;
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
		public long EntityId;
		[ProtoMember(2)]
		public bool IsGameLogicComponent;
		[ProtoMember(3)]
		public string ComponentType;
		[ProtoMember(4)]
		public int PropertyId;
		[ProtoMember(5)]
		public byte[] Data;
		[ProtoMember(6)]
		public SyncType SyncType;
	}

	public abstract class NetSync
	{
		/// <summary>
		/// The identity of this property
		/// </summary>
		public int Id { get; internal set; }

		/// <summary>
		/// Enables/Disables network traffic out when setting a value
		/// </summary>
		public bool SyncOnLoad { get; internal set; }

		/// <summary>
		/// Limits sync updates to within sync distance
		/// </summary>
		public bool LimitToSyncDistance { get; internal set; }

		public long LastMessageTimestamp { get; set; }

		protected static List<MyNetworkSessionComponent> SessionComponents = new List<MyNetworkSessionComponent>();

		/// <summary>
		/// Request the lastest value from the server
		/// </summary>
		public abstract void Fetch();

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
		private MyNetworkGameLogicComponent LogicComponent;
		private MyNetworkSessionComponent SessionComponent;
		private bool isLogicComponent => LogicComponent != null;
		private string componentType;

		private int SessionComponentId;


		/// <summary>
		/// A dynamically syncing object. Used best with block terminal properties
		/// Make sure to initialize this as a class level variable
		/// </summary>
		public NetSync(MyNetworkGameLogicComponent logic, TransferType transferType, T startingValue = default(T), bool syncOnLoad = true, bool limitToSyncDistance = true)
		{
			LogicComponent = logic;
			TransferType = transferType;
			_value = startingValue;
			SyncOnLoad = syncOnLoad;
			LimitToSyncDistance = limitToSyncDistance;
			componentType = logic.GetType().ToString();


			Id = logic.AddNetworkProperty(this);

			if (NetworkAPI.LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] Property Created - ID: {Id}, Transfer: {transferType}, SyncOnStart: {SyncOnLoad}, Type: {typeof(T)}, Class: {componentType}");
			}

		}

		public NetSync(MyNetworkSessionComponent session, TransferType transferType, T startingValue = default(T), bool syncOnLoad = true)
		{
			SessionComponent = session;
			TransferType = transferType;
			_value = startingValue;
			SyncOnLoad = syncOnLoad;
			LimitToSyncDistance = false;
			componentType = session.GetType().ToString();

			SessionComponentId = SessionComponents.IndexOf(session);

			if (SessionComponentId == -1)
			{
				SessionComponents.Add(session);
				SessionComponentId = SessionComponents.Count - 1;
			}

			Id = session.AddNetworkProperty(this);

			if (NetworkAPI.LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] Property Created - ID: {Id}, Transfer: {transferType}, SyncOnStart: {SyncOnLoad}, Type: {typeof(T)}, Class: {componentType}");
			}

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

			if ((TransferType == TransferType.ServerToClient && MyAPIGateway.Multiplayer.IsServer) ||
				(TransferType == TransferType.ClientToServer && !MyAPIGateway.Multiplayer.IsServer) ||
				TransferType == TransferType.Both)
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
			try
			{
				lock (_value)
				{
					T val = _value;
					_value = MyAPIGateway.Utilities.SerializeFromBinary<T>(data);

					if (NetworkAPI.LogNetworkTraffic)
					{
						MyLog.Default.Info($"[NetworkAPI] <{componentType} - {Id}> New value: {val} --- Old value: {_value}");
					}

					ValueChanged?.Invoke(val, _value);
					ValueChangedByNetwork?.Invoke(val, _value, sender);
				}
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Failed to deserialize network property data\n{e}");
			}
		}

		/// <summary>
		/// sends the value across the network
		/// </summary>
		/// <param name="fetch"></param>
		private void SendValue(SyncType syncType = SyncType.Broadcast, ulong sendTo = ulong.MinValue)
		{
			if (MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.OFFLINE ||
				MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.PRIVATE ||
				syncType == SyncType.None)
				return;

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
					MyLog.Default.Error($"[NetworkAPI] <{componentType} - {Id}> Sync Type is POST but the recipient is missing. Sending message as Broadcast.");
				}
			}

			if (NetworkAPI.LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] TRANSMITTING: Property: {Id} Sync Type: {syncType} Value: {Value}");
			}

			SyncData data = new SyncData() {
				EntityId = (isLogicComponent) ? LogicComponent.Entity.EntityId : SessionComponentId,
				IsGameLogicComponent = isLogicComponent,
				ComponentType = componentType,
				PropertyId = Id,
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

				if (isLogicComponent)
				{
					if (LogicComponent.Entity != null)
					{
						LogicComponent.Network.SendCommand(new Command() { IsProperty = true, Data = MyAPIGateway.Utilities.SerializeToBinary(data), SteamId = id }, LogicComponent.Entity.GetPosition(), steamId: sendTo);
					}
					else
					{
						LogicComponent.Network.SendCommand(new Command() { IsProperty = true, Data = MyAPIGateway.Utilities.SerializeToBinary(data), SteamId = id }, sendTo);
					}
				}
				else
				{
					SessionComponent.Network.SendCommand(new Command() { IsProperty = true, Data = MyAPIGateway.Utilities.SerializeToBinary(data), SteamId = id }, sendTo);
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

		/// <summary>
		/// Receives and processes all property changes
		/// </summary>
		/// <param name="pack">this hold the path to the property and the data to sync</param>
		internal static void RouteMessage(SyncData pack, ulong sender, long timestamp)
		{
			try
			{
				if (pack == null)
				{
					throw new Exception("Property date is null");
				}

				if (NetworkAPI.LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] Transmission type: {pack.SyncType}");
				}

				if (pack.IsGameLogicComponent)
				{
					IMyEntity entity = MyAPIGateway.Entities.GetEntityById(pack.EntityId);

					if (entity == null)
					{
						throw new Exception("Could not locate game entity");
					}

					MyNetworkGameLogicComponent netLogic = entity.GameLogic.GetAs<MyNetworkGameLogicComponent>();

					if (netLogic == null)
					{
						throw new Exception("The inherited \"MyGameLogicComponent\" needs to be replaced with \"MyNetworkGameLogicComponent\"");
					}

					NetSync property = netLogic.GetNetworkProperty(pack.PropertyId);
					property.LastMessageTimestamp = timestamp;

					if (property == null)
					{
						throw new Exception("Property return null");
					}

					if (pack.SyncType == SyncType.Fetch)
					{
						property.Push(SyncType.Post, sender);
					}
					else
					{
						property.SetNetworkValue(pack.Data, sender);
					}
				}
				else
				{
					if (SessionComponents.Count <= pack.EntityId)
					{
						throw new Exception($"Could not find Session Component in list");
					}

					MyNetworkSessionComponent netSession = SessionComponents[(int)pack.EntityId];

					if (netSession == null)
					{
						throw new Exception("The Session Component was destoryed and is returning null");
					}

					NetSync property = netSession.GetNetworkProperty(pack.PropertyId);
					property.LastMessageTimestamp = timestamp;

					if (property == null)
					{
						throw new Exception("Property return null");
					}

					if (pack.SyncType == SyncType.Fetch)
					{
						property.Push(SyncType.Post, sender);
					}
					else
					{
						property.SetNetworkValue(pack.Data, sender);
					}
				}
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Entity: {pack.EntityId} - Property ID: {pack.PropertyId} - Class: {pack.ComponentType}: Failed to route data \n{e}");
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

	}
}
