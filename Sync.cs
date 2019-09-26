using ProtoBuf;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.Utils;

namespace ModNetworkAPI
{
	public enum TransferType { ServerToClient, ClientToServer, Both }

	[ProtoContract]
	public class PropertyData
	{
		[ProtoMember(1)]
		public long EntityId;
		[ProtoMember(2)]
		public string LogicType;
		[ProtoMember(3)]
		public int PropertyId;
		[ProtoMember(4)]
		public byte[] data;
	}

	public interface IProperty
	{
		int Id { get; }
		void SetValue(byte[] data);
	}

	public class NetSync<T> : IProperty
	{
		public int Id { get; private set; }
		public TransferType TransferType { get; private set; }

		public Action<T> ValueChanged;
		public Action<T> ValueChangedByNetwork;

		//private bool IsLogicComponent => LogicComponent != null;

		private MyNetworkAPIGameLogicComponent LogicComponent { get; set; }
		//private MyNetworkAPISessionComponent SessionComponent { get; set; }



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

		public NetSync(MyNetworkAPIGameLogicComponent logic, TransferType tt)
		{
			LogicComponent = logic;
			TransferType = tt;

			Id = logic.AddNetworkProperty(this);
		}

		//public NetSync(MyNetworkAPISessionComponent session, TransferType tt)
		//{
		//	SessionComponent = session;
		//	TransferType = tt;

		//	Id = session.AddNetworkProperty(this);
		//}

		private void SendValue()
		{
			PropertyData data = new PropertyData() {
				EntityId = LogicComponent.Entity.EntityId,
				LogicType = LogicComponent.GetType().ToString(),
				PropertyId = Id,
				data = MyAPIGateway.Utilities.SerializeToBinary(value)
			};
		}

		private static void RouteMessage(PropertyData pack)
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

				property.SetValue(pack.data);

			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Entity: {pack.EntityId} - Class: {pack.LogicType} - Property: {pack.PropertyId}: Failed to route data \n{e.ToString()}");
			}
		}

		public void SetValue(byte[] data)
		{
			switch (Type.GetTypeCode(typeof(T)))
			{
				case TypeCode.Boolean:
					(this as NetSync<bool>).Value = MyAPIGateway.Utilities.SerializeFromBinary<bool>(data);
					break;
				case TypeCode.Int32:
					(this as NetSync<int>).Value = MyAPIGateway.Utilities.SerializeFromBinary<int>(data);
					break;
				case TypeCode.Int64:
					(this as NetSync<long>).Value = MyAPIGateway.Utilities.SerializeFromBinary<long>(data);
					break;
				case TypeCode.Double:
					(this as NetSync<double>).Value = MyAPIGateway.Utilities.SerializeFromBinary<double>(data);
					break;
				case TypeCode.Single:
					(this as NetSync<float>).Value = MyAPIGateway.Utilities.SerializeFromBinary<float>(data);
					break;
				case TypeCode.String:
					(this as NetSync<string>).Value = MyAPIGateway.Utilities.SerializeFromBinary<string>(data);
					break;
				case TypeCode.Int16:
					(this as NetSync<short>).Value = MyAPIGateway.Utilities.SerializeFromBinary<short>(data);
					break;
				case TypeCode.Char:
					(this as NetSync<char>).Value = MyAPIGateway.Utilities.SerializeFromBinary<char>(data);
					break;
				case TypeCode.UInt32:
					(this as NetSync<uint>).Value = MyAPIGateway.Utilities.SerializeFromBinary<uint>(data);
					break;
				case TypeCode.UInt64:
					(this as NetSync<ulong>).Value = MyAPIGateway.Utilities.SerializeFromBinary<ulong>(data);
					break;
				case TypeCode.UInt16:
					(this as NetSync<ushort>).Value = MyAPIGateway.Utilities.SerializeFromBinary<ushort>(data);
					break;
				default:
					throw new Exception($"[NetworkAPI] The property type send is not supported: {Type.GetTypeCode(typeof(T))}");
			}

			ValueChangedByNetwork?.Invoke(value);
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

	//public class MyNetworkAPISessionComponent : MySessionComponentBase
	//{
	//	private List<IProperty> NetworkProperties = new List<IProperty>();

	//	public int AddNetworkProperty(IProperty property)
	//	{
	//		NetworkProperties.Add(property);
	//		return NetworkProperties.Count - 1;
	//	}

	//	public IProperty GetNetworkProperty(int i)
	//	{
	//		return NetworkProperties[i];
	//	}
	//}
}
