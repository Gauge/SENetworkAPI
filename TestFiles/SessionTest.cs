using Sandbox.ModAPI;
using SENetworkAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;

namespace WeaponsOverhaul
{
	[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
	public class Core : MySessionComponentBase
	{
		public const ushort ModId = 12144;
		public const string ModName = "NetworkAPITest";
		public const string ModKeyword = "test";

		NetSync<string> SessionTest;

		public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
		{
			NetworkAPI.LogNetworkTraffic = true;

			if (!NetworkAPI.IsInitialized)
			{
				NetworkAPI.Init(ModId, ModName, ModKeyword);
			}

			MyLog.Default.WriteLine($"[Session Test] <InitializeSessionComponent>");
			SessionTest = new NetSync<string>(this, TransferType.Both);

		}
	}
}

