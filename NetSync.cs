using ProtoBuf;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Components;
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
		public string LogicType;
		[ProtoMember(3)]
		public int PropertyId;
		[ProtoMember(4)]
		public byte[] Data;
		[ProtoMember(5)]
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
				if (!value.Equals(_value))
				{
					SetValue(value, SyncType.Broadcast);
				}
			}
		}

		private T _value;
		private MyNetworkAPIGameLogicComponent LogicComponent;

		/// <summary>
		/// A dynamically syncing object. Used best with block terminal properties
		/// Make sure to initialize this as a class level variable
		/// </summary>
		public NetSync(MyNetworkAPIGameLogicComponent logic, TransferType transferType, T startingValue = default(T), bool enableSync = true)
		{
			LogicComponent = logic;
			TransferType = transferType;
			_value = startingValue;
			SyncOnLoad = enableSync;

			Id = logic.AddNetworkProperty(this);

			if (NetworkAPI.LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] Property Created - ID: {Id}, Type: {transferType.ToString()}, Sync On Start: {SyncOnLoad}, Logic Class: {logic.GetType().ToString()}, EntityId: {logic.Entity.EntityId}");
			}
		}

		public static bool operator == (NetSync<T> property, T val)
		{
			return property.Value.Equals(val);
		}

		public static bool operator != (NetSync<T> property, T val)
		{
			return !property.Value.Equals(val);
		}

		/// <summary>
		/// Allows you to change how syncing works when setting the value this way
		/// </summary>
		public void SetValue(T val, SyncType syncType)
		{
			T oldval = _value;
			_value = val;
			if (TransferType == TransferType.ServerToClient)
			{
				if (MyAPIGateway.Multiplayer.IsServer)
				{
					SendValue(syncType);
				}
			}
			else if (TransferType == TransferType.ClientToServer)
			{
				if (!MyAPIGateway.Multiplayer.IsServer)
				{
					SendValue(syncType);
				}
			}
			else if (TransferType == TransferType.Both)
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
				T val = _value;
				_value = MyAPIGateway.Utilities.SerializeFromBinary<T>(data);

				if (NetworkAPI.LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] New value: {_value}, Old value: {_value} - <Sender: {sender}, EntityId: {LogicComponent.Entity.EntityId}, EntityName: {LogicComponent.Entity.GetFriendlyName()}, Type: {LogicComponent.GetType().ToString()}, PropertyType: {typeof(T).ToString()}, PropertyIndex: {Id}>");
				}

				ValueChanged?.Invoke(val, _value);
				ValueChangedByNetwork?.Invoke(val, _value, sender);
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Failed to deserialize network property data\n{e.ToString()}");
			}
		}

		/// <summary>
		/// sends the value across the network
		/// </summary>
		/// <param name="fetch"></param>
		private void SendValue(SyncType syncType = SyncType.Broadcast, ulong sendTo = ulong.MinValue)
		{

			if (syncType == SyncType.None) 
			{
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

				if (syncType == SyncType.Post && sendTo == ulong.MinValue && NetworkAPI.LogNetworkTraffic)
				{
					MyLog.Default.Error($"[NetworkAPI] Sync Type is POST but the recipient is missing. Sending message as Broadcast - <EntityId: {LogicComponent.Entity.EntityId}, EntityName: {LogicComponent.Entity.GetFriendlyName()}, Type: {LogicComponent.GetType().ToString()}, PropertyType: {typeof(T).ToString()}, PropertyIndex: {Id}>");
				}
			}

			if (NetworkAPI.LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] TRANSMITTING: Sync Type: {syncType.ToString()} Value: {Value.ToString()} - <EntityId: {LogicComponent.Entity.EntityId}, EntityName: {LogicComponent.Entity.GetFriendlyName()}, Type: {LogicComponent.GetType().ToString()}, PropertyType: {typeof(T).ToString()}, PropertyIndex: {Id}>");
			}

			SyncData data = new SyncData() {
				EntityId = LogicComponent.Entity.EntityId,
				LogicType = LogicComponent.GetType().ToString(),
				PropertyId = Id,
				Data = MyAPIGateway.Utilities.SerializeToBinary(_value),
				SyncType = syncType
			};

			if (LogicComponent.Network != null)
			{
				LogicComponent.Network.SendCommand(new Command() { IsProperty = true, Data = MyAPIGateway.Utilities.SerializeToBinary(data) }, sendTo);
			}
		}

		/// <summary>
		/// Receives and processes all property changes
		/// </summary>
		/// <param name="pack">this hold the path to the property and the data to sync</param>
		internal static void RouteMessage(SyncData pack, ulong sender)
		{
			try
			{
				if (pack == null)
				{
					throw new Exception("Property date is null");
				}

				if (NetworkAPI.LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] Received {pack.SyncType.ToString()} request");
				}

				IMyEntity entity = MyAPIGateway.Entities.GetEntityById(pack.EntityId);

				if (entity == null)
				{
					throw new Exception("Could not locate game entity");
				}

				MyNetworkAPIGameLogicComponent netLogic = entity.GameLogic.GetAs<MyNetworkAPIGameLogicComponent>();

				if (netLogic == null)
				{
					throw new Exception("The inherited \"MyGameLogicComponent\" needs to be replaced with \"MyNetworkAPIGameLogicComponent\"");
				}

				NetSync property = netLogic.GetNetworkProperty(pack.PropertyId);

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
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Entity: {pack.EntityId} - Property ID: {pack.PropertyId} - Class: {pack.LogicType}: Failed to route data \n{e.ToString()}");
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

	[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
	public class SessionTools : MySessionComponentBase
	{
		public static bool Ready { get; private set; }

		public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
		{
			MyAPIGateway.Session.OnSessionReady += OnReady;
		}

		private void OnReady()
		{
			Ready = true;
			MyAPIGateway.Session.OnSessionReady -= OnReady;
		}
	}

	/// <summary>
	/// Use as a replacement for MyGameLogicComponent
	/// </summary>
	public class MyNetworkAPIGameLogicComponent : MyGameLogicComponent
	{
		public NetworkAPI Network => NetworkAPI.Instance;
		private List<NetSync> NetworkProperties = new List<NetSync>();

		public int AddNetworkProperty(NetSync property)
		{
			NetworkProperties.Add(property);
			return NetworkProperties.Count - 1;
		}

		public NetSync GetNetworkProperty(int i)
		{
			return NetworkProperties[i];
		}

		public MyNetworkAPIGameLogicComponent()
		{
			if (!MyAPIGateway.Session.IsServer)
				SessionReadyCheck();
		}

		/// <summary>
		/// Be sure to call base.OnAddedToScene(); within this function
		/// </summary>
		public override void OnAddedToScene()
		{
			base.OnAddedToScene();

			if (!MyAPIGateway.Session.IsServer && SessionReadyCheck())
			{
				SyncOnLoad();
			}
		}

		private bool SessionReadyCheck()
		{
			if (!SessionTools.Ready)
			{
				MyAPIGateway.Session.OnSessionReady += Ready;
			}

			return SessionTools.Ready;
		}

		private void Ready()
		{
			MyAPIGateway.Session.OnSessionReady -= Ready;
			SyncOnLoad();

		}

		private void SyncOnLoad()
		{
			foreach (NetSync prop in NetworkProperties)
			{
				if (prop.SyncOnLoad)
				{
					prop.Fetch();
				}
			}
		}
	}
}
