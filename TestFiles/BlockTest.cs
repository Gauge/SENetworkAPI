using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using SENetworkAPI;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace WeaponsOverhaul
{
    /// <summary>
    /// To run the test search "Test Block" in the G menu and place in the world.
    /// </summary>
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), true, "TestBlock")]
    public class CoreBlock : MyGameLogicComponent
    {
        private static bool ControlsInitialized = false;

        NetSync<int> sync;
        NetSync<float> serverToClient;
        NetSync<double> clientToServer;
        NetSync<string> gamelogic;
        NetSync<string> imyentity;
        NetSync<string> myentity;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {

            MyLog.Default.WriteLine($"[Block Test] {Status()} <InitializeGameLogicComponent>");
            gamelogic = new NetSync<string>(this, TransferType.Both, string.Empty);

            MyLog.Default.WriteLine($"[Block Test] {Status()} <InitializeIMyEntity>");
            imyentity = new NetSync<string>(Entity, TransferType.Both, string.Empty);

            MyLog.Default.WriteLine($"[Block Test] {Status()} <InitializeMyEntity>");
            myentity = new NetSync<string>(Entity as MyEntity, TransferType.Both, string.Empty);

            sync = new NetSync<int>(this, TransferType.Both);

            serverToClient = new NetSync<float>(Entity as MyEntity, TransferType.ServerToClient);

            clientToServer = new NetSync<double>(Entity as MyEntity, TransferType.ClientToServer);

            NeedsUpdate |= VRage.ModAPI.MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        bool IsFirstFrame = true;
        public override void UpdateOnceBeforeFrame()
        {
            if (ControlsInitialized)
                return;

            if (IsFirstFrame)
            {
                IsFirstFrame = false;
                NeedsUpdate |= VRage.ModAPI.MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
                return;
            }

            IMyTerminalControlButton button = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("NetworkAPITestButton");
            button.Title = MyStringId.GetOrCompute("Run Tests");
            button.Visible = (block) => { return block.GameLogic.GetAs<CoreBlock>() != null; };
            button.Action = RunTests;

            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(button);

        }

        private void RunTests(IMyTerminalBlock terminalBlock)
        {
            CoreBlock block = terminalBlock.GameLogic.GetAs<CoreBlock>();

            MyLog.Default.WriteLine($"[Block Test] {Status()} <AssignNewValue>");
            block.AssignNewValue();

            MyLog.Default.WriteLine($"[Block Test] {Status()} <AssignNewValue>");
            block.SetNewValue();

            MyLog.Default.WriteLine($"[Block Test] {Status()} <ServerToClient>");
            block.ServerToClient();

            MyLog.Default.WriteLine($"[Block Test] {Status()} <ClientToServer>");
            block.ClientToServer();
        }

        public void AssignNewValue()
        {
            sync.Value = 100;
            sync.Value += 25;
            sync.Value *= 2;
        }

        public void SetNewValue()
        {
            sync.SetValue(100);
            sync.SetValue(sync.Value + 25, SyncType.Broadcast);
            sync.SetValue(sync.Value * 2, SyncType.None);
            sync.SetValue(1);
            sync.Push();
        }

        public void ServerToClient() 
        {
            serverToClient.Value = 1.54f;
        }

        public void ClientToServer() 
        {
            clientToServer.Value = 12.322d;
        }


        public static string Status()
        {
            return $"_{(MyAPIGateway.Multiplayer.IsServer ? "SERVER" : "CLIENT")}_";
        }
    }
}
