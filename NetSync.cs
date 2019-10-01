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
		public byte[] data;
		[ProtoMember(5)]
		public ulong Fetch;
	}

	public interface IProperty
	{
		/// <summary>
		/// The automatically assigned id
		/// </summary>
		int Id { get; }

		bool SyncOnLoad { get; }

		/// <summary>
		/// deserializes data, sets it and triggers the network event
		/// </summary>
		/// <param name="data"></param>
		void SetValue(byte[] data);

		/// <summary>
		/// requests server info (client only)
		/// </summary>
		void Fetch();

		/// <summary>
		/// Sends the current value across the network (use with object when setting their property values)
		/// </summary>
		void Push(ulong sender);
	}

	public class NetSync<T> : IProperty
	{
		public int Id { get; private set; }

		public bool SyncOnLoad { get; private set; }

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
		/// </summary>
		public Action<T, T> ValueChangedByNetwork;

		/// <summary>
		/// this property syncs across the network when changed
		/// </summary>
		public T Value
		{
			get { return value; }
			set
			{
				T val = this.value;
				this.value = value;
				if (TransferType == TransferType.ServerToClient)
				{
					if (MyAPIGateway.Multiplayer.IsServer)
					{
						SendValue();
					}
				}
				else if (TransferType == TransferType.ClientToServer)
				{
					if (!MyAPIGateway.Multiplayer.IsServer)
					{
						SendValue();
					}
				}
				else if (TransferType == TransferType.Both)
				{
					SendValue();
				}

				ValueChanged?.Invoke(val, value);
			}
		}

		private T value;
		private MyNetworkAPIGameLogicComponent LogicComponent;

		/// <summary>
		/// A dynamically syncing object. Used best with block terminal properties
		/// Make sure to initialize this as a class level variable
		/// </summary>
		public NetSync(MyNetworkAPIGameLogicComponent logic, TransferType transferType, T startingValue = default(T), bool syncOnLoad = true)
		{
			LogicComponent = logic;
			TransferType = transferType;
			value = startingValue;
			SyncOnLoad = !syncOnLoad;

			Id = logic.AddNetworkProperty(this);

			if (NetworkAPI.LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] Property Created - ID: {Id} Type: {transferType.ToString()} Sync On Start: {SyncOnLoad} Logic Class: {logic.GetType().ToString()}");
			}

		}

		/// <summary>
		/// sends the value across the network
		/// </summary>
		/// <param name="fetch"></param>
		private void SendValue(ulong fetch = ulong.MinValue, ulong sendTo = ulong.MinValue)
		{
			if (fetch != ulong.MinValue && MyAPIGateway.Multiplayer.IsServer)
				return;

			if (NetworkAPI.LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] TRANSMITTING: {Value}{((sendTo != ulong.MinValue) ? $" TO USER: {sendTo}" : "")}");
			}

			SyncData data = new SyncData() {
				EntityId = LogicComponent.Entity.EntityId,
				LogicType = LogicComponent.GetType().ToString(),
				PropertyId = Id,
				data = MyAPIGateway.Utilities.SerializeToBinary(value),
				Fetch = fetch
			};

			if (LogicComponent.Network != null)
			{
				LogicComponent.Network.SendCommand(new Command() { IsProperty = true, Data = MyAPIGateway.Utilities.SerializeToBinary(data) }, sendTo);
			}
		}

		/// <summary>
		/// receives and processes all property changes
		/// </summary>
		/// <param name="pack">this hold the path to the property and the data to sync</param>
		internal static void RouteMessage(SyncData pack)
		{
			try
			{
				if (pack == null)
				{
					throw new Exception("Property date is null");
				}

				if (NetworkAPI.LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] Received {(pack.Fetch != ulong.MinValue ? "FETCH" : "POST")} request");
				}

				IMyEntity entity = MyAPIGateway.Entities.GetEntityById(pack.EntityId);

				if (entity == null)
				{
					throw new Exception("Could not locate game entity");
				}

				MyNetworkAPIGameLogicComponent netLogic = entity.GameLogic as MyNetworkAPIGameLogicComponent;

				if (netLogic == null)
				{
					throw new Exception("The inherited \"MyGameLogicComponent\" needs to be replaced with \"MyNetworkAPIGameLogicComponent\"");
				}

				IProperty property = netLogic.GetNetworkProperty(pack.PropertyId);

				if (property == null)
				{
					throw new Exception("Property return null");
				}

				if (pack.Fetch != ulong.MinValue)
				{
					property.Push(pack.Fetch);
				}
				else
				{
					property.SetValue(pack.data);
				}
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Entity: {pack.EntityId} - Property ID: {pack.PropertyId} - Class: {pack.LogicType}: Failed to route data \n{e.ToString()}");
			}
		}

		/// <summary>
		/// This function is ment to only be triggered internally 
		/// Bad things will happen if used
		/// </summary>
		/// <param name="data"></param>
		public void SetValue(byte[] data)
		{
			try
			{
				T val = value;
				value = MyAPIGateway.Utilities.SerializeFromBinary<T>(data);

				if (NetworkAPI.LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] new value: {value} --- old value: {value}");
				}

				ValueChanged?.Invoke(val, value);
				ValueChangedByNetwork?.Invoke(val, value);
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Failed to deserialize network property data\n{e.ToString()}");
			}
		}

		/// <summary>
		/// Forces a network sync.
		/// This call is only made by clients
		/// </summary>
		public void Fetch()
		{
			if (!MyAPIGateway.Multiplayer.IsServer)
			{
				if (MyAPIGateway.Session?.Player != null)
				{
					SendValue(MyAPIGateway.Session.Player.SteamUserId);
				}
				else
				{
					MyLog.Default.Error($"[NetworkAPI] Could not fetch property data. No player entity");
				}
			}

		}

		/// <summary>
		/// Broadcast an update
		/// </summary>
		public void Push(ulong sender = ulong.MinValue)
		{
			SendValue(sendTo: sender);
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
		private List<IProperty> NetworkProperties = new List<IProperty>();

		public int AddNetworkProperty(IProperty property)
		{
			NetworkProperties.Add(property);
			return NetworkProperties.Count - 1;
		}

		public IProperty GetNetworkProperty(int i)
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
			foreach (IProperty prop in NetworkProperties)
			{
				if (prop.SyncOnLoad)
				{
					prop.Fetch();
				}
			}
		}
	}
}
