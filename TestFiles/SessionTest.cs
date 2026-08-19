using Sandbox.ModAPI;
using SENetworkAPI;
using System;
using System.Text;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;

namespace SENetworkAPITest
{
	/// <summary>
	/// In game test harness for SENetworkAPI. Everything else in this repository
	/// is verified against a stub of the ModAPI; this is the only thing that
	/// runs inside Space Engineers, so it is what proves the engine actually
	/// behaves the way the unit tests assume.
	///
	/// Put this mod in a world, place a "Test Block", and drive it from chat:
	///
	///   test help      what each command does
	///   test local     checks that need only this machine
	///   test reset     zero the counters on every machine
	///   test report    what this machine has seen since the last reset
	///   test burst     run the network scenarios, then read the other side's report
	///
	/// The interesting checks need two machines. Run "test burst" on one and
	/// "test report" on the other.
	/// </summary>
	[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
	public class Core : MySessionComponentBase
	{
		public const ushort ModId = 12144;
		public const string ModName = "NetworkAPITest";
		public const string ModKeyword = "test";

		public static Core Instance;

		/// <summary>
		/// Every packet this mod receives on its channel, counted by a second
		/// raw handler registered alongside the API's own. This is the only way
		/// to see actual packet counts from inside the game, and it is what
		/// makes the batching claims checkable rather than assumed.
		/// </summary>
		public static int PacketsReceived;

		/// <summary>Property updates seen arriving from the network.</summary>
		public static int PropertyUpdatesReceived;

		private NetSync<int> SessionValue;

		// Declared once, on both machines, so their ids line up. The local
		// checks drive them with SetValue, which changes the value without
		// putting anything on the wire.
		private NetSync<int> localDeduped;
		private NetSync<int> localAlways;
		private NetSync<string> localText;

		private static Action<byte[]> counter;

		public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
		{
			Instance = this;
			NetworkAPI.LogNetworkTraffic = false;

			if (!NetworkAPI.IsInitialized)
			{
				NetworkAPI.Init(ModId, ModName, ModKeyword);
			}

			counter = CountPacket;
			MyAPIGateway.Multiplayer.RegisterMessageHandler(ModId, counter);

			SessionValue = new NetSync<int>(this, TransferType.ServerToClient, 0);
			SessionValue.ValueChangedByNetwork += (o, n, s) => PropertyUpdatesReceived++;

			localDeduped = new NetSync<int>(this, TransferType.Both, 0, false);
			localAlways = new NetSync<int>(this, TransferType.Both, 0, false).AlwaysSend();
			localText = new NetSync<string>(this, TransferType.Both, null, false);

			NetworkAPI.Instance.RegisterChatCommand(string.Empty, Chat_Help);
			NetworkAPI.Instance.RegisterChatCommand("help", Chat_Help);
			NetworkAPI.Instance.RegisterChatCommand("local", Chat_Local);
			NetworkAPI.Instance.RegisterChatCommand("reset", Chat_Reset);
			NetworkAPI.Instance.RegisterChatCommand("report", Chat_Report);
			NetworkAPI.Instance.RegisterChatCommand("burst", Chat_Burst);

			NetworkAPI.Instance.RegisterNetworkCommand("reset", Network_Reset);

			MyLog.Default.Info($"[{ModName}] ready. API version {NetworkAPI.Version}, running as {NetworkAPI.Instance.NetworkType}");
		}

		protected override void UnloadData()
		{
			if (counter != null)
			{
				MyAPIGateway.Multiplayer.UnregisterMessageHandler(ModId, counter);
				counter = null;
			}

			Instance = null;
		}

		private static void CountPacket(byte[] packet)
		{
			PacketsReceived++;
		}

		public static void Say(string text)
		{
			MyAPIGateway.Utilities.ShowMessage(ModName, text);
		}

		private static void Check(StringBuilder report, string name, bool passed, string detail)
		{
			report.AppendLine($"{(passed ? "PASS" : "FAIL")}  {name}  ({detail})");
		}

		private void Chat_Help(string arguments)
		{
			Say("local  - checks that need only this machine");
			Say("reset  - zero the counters everywhere");
			Say("report - what this machine has seen");
			Say("burst  - run the network scenarios, then read the other side's report");
		}

		/// <summary>
		/// Checks that hold on a single machine. These are the behaviours most
		/// likely to surprise a mod that was written against the old API.
		/// </summary>
		private void Chat_Local(string arguments)
		{
			StringBuilder report = new StringBuilder();
			report.AppendLine($"SENetworkAPI {NetworkAPI.Version} on {NetworkAPI.Instance.NetworkType}");

			int changes = 0;
			Action<int, int> countChange = (o, n) => changes++;
			localDeduped.ValueChanged += countChange;
			localDeduped.SetValue(localDeduped.Value + 1);
			localDeduped.SetValue(localDeduped.Value);
			localDeduped.SetValue(localDeduped.Value);
			localDeduped.ValueChanged -= countChange;
			Check(report, "unchanged assignments are dropped", changes == 1, $"{changes} change(s) from 3 assignments");

			int alwaysChanges = 0;
			Action<int, int> countAlways = (o, n) => alwaysChanges++;
			localAlways.ValueChanged += countAlways;
			localAlways.SetValue(7);
			localAlways.SetValue(7);
			localAlways.SetValue(7);
			localAlways.ValueChanged -= countAlways;
			Check(report, "AlwaysSend keeps every assignment", alwaysChanges == 3, $"{alwaysChanges} change(s) from 3 assignments");

			localText.SetValue(null);
			int textChanges = 0;
			Action<string, string> countText = (o, n) => textChanges++;
			localText.ValueChanged += countText;
			localText.SetValue("hello");
			localText.ValueChanged -= countText;
			Check(report, "a null valued property accepts a value", textChanges == 1 && localText.Value == "hello", $"value is {localText.Value ?? "null"}");

			bool castable = NetworkAPI.Instance.NetworkType == NetworkTypes.Client || NetworkAPI.Instance is Server;
			Check(report, "NetworkType agrees with the instance", castable, NetworkAPI.Instance.GetType().Name);

			foreach (string line in report.ToString().Split('\n'))
			{
				if (line.Trim().Length > 0)
				{
					Say(line.Trim());
				}
			}
		}

		private void Chat_Reset(string arguments)
		{
			ResetCounters();
			NetworkAPI.Instance.SendCommand("reset");
			Say("counters reset here and asked the other side to reset");
		}

		private void Network_Reset(ulong steamId, string command, byte[] data, DateTime sent)
		{
			ResetCounters();
		}

		public static void ResetCounters()
		{
			PacketsReceived = 0;
			PropertyUpdatesReceived = 0;
			TestBlock.ResetCounters();
		}

		private void Chat_Report(string arguments)
		{
			Say($"packets received: {PacketsReceived}");
			Say($"property updates received: {PropertyUpdatesReceived + TestBlock.UpdatesReceived}");
			Say(TestBlock.Report());
		}

		/// <summary>
		/// Drives the scenarios whose result is only visible on the other
		/// machine: run this on one side, then "test report" on the other.
		/// </summary>
		private void Chat_Burst(string arguments)
		{
			if (TestBlock.Blocks.Count == 0)
			{
				Say("place a Test Block first");
				return;
			}

			foreach (TestBlock block in TestBlock.Blocks)
			{
				block.RunBurst();
			}

			SessionValue.Value = SessionValue.Value + 1;

			Say($"burst sent from {TestBlock.Blocks.Count} block(s).");
			Say("run 'test report' on the other machine.");
			Say("expect: one packet for the coalesced group, one for the batch,");
			Say("and one property update per value that actually changed.");
		}
	}
}
