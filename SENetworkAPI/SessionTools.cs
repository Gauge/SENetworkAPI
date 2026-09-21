using Sandbox.ModAPI;
using System;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;

namespace SENetworkAPI
{
	/// <summary>Tears the API down when the world unloads. Ships with the API.</summary>
	[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
	public class SessionTools : MySessionComponentBase
	{
		/// <summary>Disposes the API when the world unloads.</summary>
		protected override void UnloadData()
		{
			NetworkAPI.Dispose();
		}
	}
}
