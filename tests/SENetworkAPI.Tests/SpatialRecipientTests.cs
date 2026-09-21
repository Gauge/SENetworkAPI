using System;
using System.Linq;
using SEStubs;
using VRageMath;
using Xunit;

namespace SENetworkAPI.Tests
{
	public class SpatialRecipientTests : NetworkTestBase
	{
		[Theory]
		[InlineData(0, 10)]
		[InlineData(1, 10)]
		[InlineData(2, 10)]
		[InlineData(0, 20)]
		[InlineData(1, 20)]
		[InlineData(2, 20)]
		[InlineData(0, 40)]
		[InlineData(1, 40)]
		[InlineData(2, 40)]
		[InlineData(0, 256)]
		[InlineData(1, 256)]
		[InlineData(2, 256)]
		public void IndexedQueriesMatchFullSphereScanIncludingOrder(int axis, int players)
		{
			Server server = GivenDedicatedServer();
			var random = new Random(100 + axis);
			for (int i = 0; i < players; i++)
			{
				double a = random.NextDouble() * 100000 - 50000;
				double b = random.NextDouble() * 1000 - 500;
				double c = random.NextDouble() * 1000 - 500;
				Game.Players.Add((ulong)(i + 1), axis == 0 ? new Vector3D(a, b, c) : axis == 1 ? new Vector3D(b, a, c) : new Vector3D(b, c, a));
			}
			for (int i = 0; i < 100; i++)
			{
				Vector3D point = Game.Players.AllPlayers[i % players].GetPosition();
				double radius = i % 10 == 0 ? 1000000 : (i % 3 == 0 ? -1200 : 1200);
				ulong excluded = (ulong)(i % players + 1);
				var expected = Game.Players.AllPlayers.Where(p => p.SteamUserId != excluded && (p.GetPosition() - point).LengthSquared() < radius * radius).Select(p => p.SteamUserId).ToArray();
				var actual = server.CollectRecipients(point, radius, 0, excluded);
				Assert.Equal(expected, actual);
				server.ReleaseRecipients(actual);
			}
		}

		[Fact]
		public void DenseQueriesStillRespectChangedQueryPositionRadiusAndExclusion()
		{
			Server server = GivenDedicatedServer();
			for (int i = 0; i < 40; i++) Game.Players.Add((ulong)(i + 1), new Vector3D(i, 0, 0));
			for (int i = 0; i < 32; i++)
			{
				var all = server.CollectRecipients(Vector3D.Zero, 1000, 0, 0);
				Assert.Equal(40, all.Count);
				server.ReleaseRecipients(all);
			}
			var distant = server.CollectRecipients(new Vector3D(10000, 0, 0), 1000, 0, 0);
			Assert.Empty(distant);
			server.ReleaseRecipients(distant);
			var limited = server.CollectRecipients(Vector3D.Zero, 20, 0, 5);
			Assert.Equal(Enumerable.Range(1, 20).Where(id => id != 5).Select(id => (ulong)id), limited);
			server.ReleaseRecipients(limited);
			var allExceptSender = server.CollectRecipients(Vector3D.Zero, 1000, 0, 5);
			Assert.Equal(39, allExceptSender.Count);
			Assert.DoesNotContain(5UL, allExceptSender);
			server.ReleaseRecipients(allExceptSender);
		}

		[Fact]
		public void PlayerCountDoesNotCapTheNumberOfRecipients()
		{
			Server server = GivenDedicatedServer();
			for (int i = 0; i < 5000; i++) Game.Players.Add((ulong)(i + 1), Vector3D.Zero);
			for (int i = 0; i < 32; i++)
			{
				var recipients = server.CollectRecipients(Vector3D.Zero, 1000, 0, 0);
				Assert.Equal(5000, recipients.Count);
				Assert.Equal(Enumerable.Range(1, 5000).Select(id => (ulong)id), recipients);
				server.ReleaseRecipients(recipients);
			}
		}

		[Fact]
		public void IndexRefreshesMovementJoinsLeavesAndQueryPositions()
		{
			Server server = GivenDedicatedServer();
			for (int i = 0; i < 100; i++) Game.Players.Add((ulong)(i + 1), new Vector3D(i * 1000, 0, 0));
			for (int i = 0; i < 32; i++) server.ReleaseRecipients(server.CollectRecipients(Vector3D.Zero, 100, 0, 0));
			var moved = (FakePlayer)Game.Players.AllPlayers[1];
			moved.Position = Vector3D.Zero;
			Game.Players.AllPlayers.RemoveAt(0);
			Game.Players.Add(999, Vector3D.Zero);
			Game.NextFrame();
			for (int i = 0; i < 32; i++)
			{
				var recipients = server.CollectRecipients(Vector3D.Zero, 100, 0, 0);
				Assert.Equal(new ulong[] { 2, 999 }, recipients);
				server.ReleaseRecipients(recipients);
			}
			var elsewhere = server.CollectRecipients(new Vector3D(5000, 0, 0), 100, 0, 0);
			Assert.Equal(new ulong[] { 6 }, elsewhere);
			server.ReleaseRecipients(elsewhere);
		}

		[Fact]
		public void IndexKeepsExactDistanceBoundaryAndTinyRadiusAtLargeCoordinates()
		{
			Server server = GivenDedicatedServer();
			for (int i = 0; i < 100; i++) Game.Players.Add((ulong)(i + 1), new Vector3D(i * 1000, 0, 0));
			Game.Players.Add(999, new Vector3D(1e16, 0, 0));
			for (int i = 0; i < 32; i++)
			{
				var boundary = server.CollectRecipients(Vector3D.Zero, 1000, 0, 0);
				Assert.Equal(new ulong[] { 1 }, boundary);
				server.ReleaseRecipients(boundary);
			}
			var tiny = server.CollectRecipients(new Vector3D(1e16, 0, 0), .01, 0, 0);
			Assert.Equal(new ulong[] { 999 }, tiny);
			server.ReleaseRecipients(tiny);
		}

		[Fact]
		public void InvalidPositionsAndInfiniteRadiiKeepLinearSemantics()
		{
			Server server = GivenDedicatedServer();
			for (int i = 0; i < 100; i++) Game.Players.Add((ulong)(i + 1), new Vector3D(i * 1000, 0, 0));
			Game.Players.Add(999, new Vector3D(double.NaN, 0, 0));
			for (int i = 0; i < 32; i++)
			{
				var recipients = server.CollectRecipients(Vector3D.Zero, double.PositiveInfinity, 0, 0);
				Assert.Equal(100, recipients.Count);
				server.ReleaseRecipients(recipients);
			}
		}
	}
}
