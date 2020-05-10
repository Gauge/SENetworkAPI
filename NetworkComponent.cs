using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game.Components;
using VRage.Utils;

namespace SENetworkAPI
{
	/// <summary>
	/// Use as a replacement for MyGameLogicComponent
	/// </summary>
	public class MyNetworkGameLogicComponent : MyGameLogicComponent
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

		/// <summary>
		/// Be sure to call base.OnAddedToScene(); within this function
		/// </summary>
		public override void OnAddedToScene()
		{
			base.OnAddedToScene();

			if (SessionTools.Ready)
			{
				SyncOnLoad();
			}
			else
			{
				SessionTools.WhenReady += SyncOnLoad;
			}
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

			SessionTools.WhenReady -= SyncOnLoad;
		}
	}

	/// <summary>
	/// Use as a replacement for MySessionComponentBase
	/// </summary>
	public class MyNetworkSessionComponent : MySessionComponentBase
	{
		public NetworkAPI Network => NetworkAPI.Instance;
		private List<NetSync> NetworkProperties = new List<NetSync>();

		public MyNetworkSessionComponent()
		{
			SessionTools.WhenReady += SyncOnLoad;
		}

		public int AddNetworkProperty(NetSync property)
		{
			NetworkProperties.Add(property);
			return NetworkProperties.Count - 1;
		}

		public NetSync GetNetworkProperty(int i)
		{
			return NetworkProperties[i];
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

			SessionTools.WhenReady -= SyncOnLoad;
		}
	}

	[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
	public class SessionTools : MySessionComponentBase
	{
		public static bool Ready { get; private set; }
		public static Action WhenReady;

		public override void LoadData()
		{
			MyAPIGateway.Session.OnSessionReady += OnReady;
		}

		private void OnReady()
		{
			if (NetworkAPI.LogNetworkTraffic)
			{
				MyLog.Default.Info("[SENetworkAPI] SessionTools: Sending Ready Signal");
			}

			Ready = true;
			WhenReady?.Invoke();
			MyAPIGateway.Session.OnSessionReady -= OnReady;

		}
	}
}
