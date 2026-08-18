using System;
using SEStubs;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>
	/// The Command envelope is the only thing that ever goes on the wire.
	/// Every field must survive a protobuf round trip.
	/// </summary>
	public class CommandTests : NetworkTestBase
	{
		[Fact]
		public void Command_RoundTripsEveryField()
		{
			Command original = new Command {
				SteamId = 76561198000000001,
				CommandString = "update player 3",
				Message = "hello world",
				Data = new byte[] { 1, 2, 3, 250 },
				Timestamp = 637000000000000000,
				IsProperty = true,
				IsCompressed = true,
			};

			Command copy = StubSerializer.Deserialize<Command>(StubSerializer.Serialize(original));

			Assert.Equal(original.SteamId, copy.SteamId);
			Assert.Equal(original.CommandString, copy.CommandString);
			Assert.Equal(original.Message, copy.Message);
			Assert.Equal(original.Data, copy.Data);
			Assert.Equal(original.Timestamp, copy.Timestamp);
			Assert.True(copy.IsProperty);
			Assert.True(copy.IsCompressed);
		}

		[Fact]
		public void Command_DefaultsRoundTripAsNulls()
		{
			Command copy = StubSerializer.Deserialize<Command>(StubSerializer.Serialize(new Command()));

			Assert.Null(copy.CommandString);
			Assert.Null(copy.Message);
			Assert.Null(copy.Data);
			Assert.Equal(0UL, copy.SteamId);
			Assert.False(copy.IsProperty);
			Assert.False(copy.IsCompressed);
		}

		[Fact]
		public void SyncData_RoundTripsEveryField()
		{
			SyncData original = new SyncData {
				Id = 7,
				EntityId = 123456789,
				Data = new byte[] { 9, 8, 7 },
				SyncType = SyncType.Fetch,
			};

			SyncData copy = StubSerializer.Deserialize<SyncData>(StubSerializer.Serialize(original));

			Assert.Equal(original.Id, copy.Id);
			Assert.Equal(original.EntityId, copy.EntityId);
			Assert.Equal(original.Data, copy.Data);
			Assert.Equal(SyncType.Fetch, copy.SyncType);
		}
	}
}
