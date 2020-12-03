using Sandbox.ModAPI;
using System;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;

namespace SENetworkAPI
{
	[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
	public class SessionTools : MySessionComponentBase
	{
		public static bool Ready { get; private set; }
		public static Action WhenReady;

		public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
		{
			MyAPIGateway.Session.OnSessionReady += OnReady;
		}

		protected override void UnloadData()
		{
			NetworkAPI.Dispose();
		}

		private void OnReady()
		{
			if (MyAPIGateway.Session.IsServer)
			{
				ReadyUp();
			}
			else
			{
				MyAPIGateway.Parallel.StartBackground(() => { MyAPIGateway.Parallel.Sleep(10000); }, ReadyUp);
			}
		}

		private void ReadyUp() 
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
