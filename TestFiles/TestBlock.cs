using ProtoBuf;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using SENetworkAPI;
using System.Collections.Generic;
using System.Text;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace SENetworkAPITest
{
	/// <summary>A reference type payload, the way mods send request objects.</summary>
	[ProtoContract]
	public class TestRequest
	{
		[ProtoMember(1)] public long IdentityId { get; set; }
		[ProtoMember(2)] public string Note { get; set; }
	}

	/// <summary>
	/// Carries one of every kind of synced property so a single block exercises
	/// the whole surface. Search "Test Block" in the G menu and place one.
	///
	/// The counters are what make this useful: they are read back with
	/// "test report" on the other machine, which is the only place the effect
	/// of batching is visible.
	/// </summary>
	[MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), true, "TestBlock")]
	public class TestBlock : MyGameLogicComponent
	{
		public static readonly List<TestBlock> Blocks = new List<TestBlock>();

		public static int UpdatesReceived;
		private static int batchUpdates;
		private static int coalescedUpdates;
		private static int lossyUpdates;
		private static int dedupedUpdates;
		private static int alwaysUpdates;
		private static int requestUpdates;
		private static bool controlsInitialized;

		/// <summary>Twelve plain properties: what a grid streaming in asks for.</summary>
		private readonly NetSync<int>[] batch = new NetSync<int>[12];

		/// <summary>Four batched into one packet per frame.</summary>
		private readonly NetSync<float>[] coalesced = new NetSync<float>[4];

		private NetSync<int> lossy;
		private NetSync<int> deduped;
		private NetSync<int> always;
		private NetSync<TestRequest> request;

		private int burst;

		public override void Init(MyObjectBuilder_EntityBase objectBuilder)
		{
			for (int i = 0; i < batch.Length; i++)
			{
				batch[i] = new NetSync<int>(this, TransferType.Both, i);
				batch[i].ValueChangedByNetwork += (o, n, s) => { batchUpdates++; UpdatesReceived++; };
			}

			for (int i = 0; i < coalesced.Length; i++)
			{
				coalesced[i] = new NetSync<float>(this, TransferType.Both, 0f).Coalesce();
				coalesced[i].ValueChangedByNetwork += (o, n, s) => { coalescedUpdates++; UpdatesReceived++; };
			}

			lossy = new NetSync<int>(this, TransferType.Both, 0).Lossy();
			lossy.ValueChangedByNetwork += (o, n, s) => { lossyUpdates++; UpdatesReceived++; };

			deduped = new NetSync<int>(this, TransferType.Both, 0);
			deduped.ValueChangedByNetwork += (o, n, s) => { dedupedUpdates++; UpdatesReceived++; };

			always = new NetSync<int>(this, TransferType.Both, 0).AlwaysSend();
			always.ValueChangedByNetwork += (o, n, s) => { alwaysUpdates++; UpdatesReceived++; };

			request = new NetSync<TestRequest>(this, TransferType.ClientToServer, new TestRequest(), false);
			request.ValueChangedByNetwork += (o, n, s) => { requestUpdates++; UpdatesReceived++; };

			Blocks.Add(this);
			NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
		}

		public override void Close()
		{
			Blocks.Remove(this);
		}

		public static void ResetCounters()
		{
			UpdatesReceived = 0;
			batchUpdates = 0;
			coalescedUpdates = 0;
			lossyUpdates = 0;
			dedupedUpdates = 0;
			alwaysUpdates = 0;
			requestUpdates = 0;
		}

		public static string Report()
		{
			StringBuilder text = new StringBuilder();
			text.Append($"batch {batchUpdates}, coalesced {coalescedUpdates}, lossy {lossyUpdates}, ");
			text.Append($"deduped {dedupedUpdates} (expect 1), always {alwaysUpdates} (expect 3), request {requestUpdates}");
			return text.ToString();
		}

		/// <summary>
		/// One pass of every scenario. What the other machine sees afterwards is
		/// the actual test.
		/// </summary>
		public void RunBurst()
		{
			burst++;

			// Twelve separate properties changing together.
			for (int i = 0; i < batch.Length; i++)
			{
				batch[i].Value = burst * 100 + i;
			}

			// Four batched properties: one packet, not four.
			for (int i = 0; i < coalesced.Length; i++)
			{
				coalesced[i].Value = burst + (i * 0.25f);
			}

			lossy.Value = burst;

			// Three assignments, one change: the other side should count one.
			deduped.Value = burst;
			deduped.Value = burst;
			deduped.Value = burst;

			// Three assignments, three changes.
			always.Value = burst;
			always.Value = burst;
			always.Value = burst;

			// A reference type is always sent, identical contents or not.
			if (NetworkAPI.Instance.NetworkType == NetworkTypes.Client)
			{
				request.Value = new TestRequest { IdentityId = MyAPIGateway.Session.Player.IdentityId, Note = "zip" };
				request.Value = new TestRequest { IdentityId = MyAPIGateway.Session.Player.IdentityId, Note = "zip" };
			}

			MyLog.Default.Info($"[{Core.ModName}] burst {burst} sent from {NetworkAPI.Instance.NetworkType}");
		}

		public override void UpdateOnceBeforeFrame()
		{
			if (controlsInitialized || MyAPIGateway.TerminalControls == null)
			{
				return;
			}

			controlsInitialized = true;

			IMyTerminalControlButton button = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("SENetworkAPITestBurst");
			button.Title = MyStringId.GetOrCompute("Run Network Test");
			button.Tooltip = MyStringId.GetOrCompute("Sends one pass of every scenario. Read the result with 'test report' on the other machine.");
			button.Visible = (block) => block.GameLogic.GetAs<TestBlock>() != null;
			button.Action = (block) =>
			{
				TestBlock logic = block.GameLogic.GetAs<TestBlock>();

				if (logic != null)
				{
					logic.RunBurst();
					Core.Say("burst sent. Run 'test report' on the other machine.");
				}
			};

			MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(button);
		}
	}
}
