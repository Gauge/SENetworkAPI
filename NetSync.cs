using ProtoBuf;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.Utils;

namespace SENetworkAPI
{
	public enum TransferType { ServerToClient, ClientToServer, Both }

	[ProtoContract]
	public class SyncData
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
		public bool Fetch;
	}

	public interface IProperty
	{
		/// <summary>
		/// The automatically assigned id
		/// </summary>
		int Id { get; }

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
		void Push();
	}

	public class NetSync<T> : IProperty
	{
		public int Id { get; private set; }
		public TransferType TransferType { get; private set; }

		public Action<T> ValueChanged;
		public Action<T> ValueChangedByNetwork;

		private MyNetworkAPIGameLogicComponent LogicComponent { get; set; }

		private T value;
		public T Value
		{
			get { return value; }
			set
			{
				this.value = value;
				if (TransferType == TransferType.ServerToClient)
				{
					if (NetworkAPI.NetworkType == NetworkTypes.Server)
					{
						SendValue();
					}
				}
				else if (TransferType == TransferType.ClientToServer)
				{
					if (NetworkAPI.NetworkType == NetworkTypes.Server)
					{
						SendValue();
					}
				}
				else if (TransferType == TransferType.Both)
				{
					SendValue();
				}

				ValueChanged?.Invoke(value);

			}
		}

		public NetSync(MyNetworkAPIGameLogicComponent logic, TransferType tt, T startingValue = default(T))
		{
			LogicComponent = logic;
			TransferType = tt;
			value = startingValue;

			Id = logic.AddNetworkProperty(this);
		}

		private void SendValue(bool fetch = false)
		{
			SyncData data = new SyncData() {
				EntityId = LogicComponent.Entity.EntityId,
				LogicType = LogicComponent.GetType().ToString(),
				PropertyId = Id,
				data = MyAPIGateway.Utilities.SerializeToBinary(value),
				Fetch = fetch
			};

			if (NetworkAPI.IsInitialized)
			{
				NetworkAPI.Instance.SendCommand(new Command() { IsProperty = true, Data = MyAPIGateway.Utilities.SerializeToBinary(data) });
			}
		}

		internal static void RouteMessage(SyncData pack)
		{
			try
			{
				IMyEntity entity = MyAPIGateway.Entities.GetEntityById(pack.EntityId);

				if (entity == null)
				{
					throw new Exception("Could not locate game entity");
				}

				MyGameLogicComponent logic = (entity.GameLogic as MyCompositeGameLogicComponent).GetAs(pack.LogicType);

				if (logic == null)
				{
					throw new Exception("Didn't find a game component of specified class type");
				}

				MyNetworkAPIGameLogicComponent netComp = logic as MyNetworkAPIGameLogicComponent;

				if (netComp == null)
				{
					throw new Exception("The inherited \"MyGameLogicComponent\" needs to be replaced with \"MyNetworkAPIGameLogicComponent\"");
				}

				IProperty property = netComp.GetNetworkProperty(pack.PropertyId);

				if (pack.Fetch)
				{
					property.Push();
				}
				else
				{
					property.SetValue(pack.data);
				}
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Entity: {pack.EntityId} - Class: {pack.LogicType} - Property: {pack.PropertyId}: Failed to route data \n{e.ToString()}");
			}
		}

		public void SetValue(byte[] data)
		{
			try
			{
				Value = MyAPIGateway.Utilities.SerializeFromBinary<T>(data);

				ValueChangedByNetwork?.Invoke(value);
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Failed to deserialize network property data\n{e.ToString()}");
			}
		}

		public void Fetch()
		{
			SendValue(true);
		}

		public void Push()
		{
			SendValue();
		}
	}

	/// <summary>
	/// This class should be used inplace of MyGameLogicComponent for all block mods that have network properties
	/// </summary>
	public class MyNetworkAPIGameLogicComponent : MyGameLogicComponent
	{
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
	}
}
