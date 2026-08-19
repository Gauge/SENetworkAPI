using System;
using System.Collections.Generic;
using System.Linq;
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
		public void Command_RoundTripsAnInlineProperty()
		{
			Command original = new Command {
				IsProperty = true,
				SteamId = 7,
				Property = new SyncData { Id = 3, EntityId = 99, SyncType = SyncType.Post, Data = new byte[] { 1, 2 } },
			};

			Command copy = StubSerializer.Deserialize<Command>(StubSerializer.Serialize(original));

			Assert.NotNull(copy.Property);
			Assert.Equal(3, copy.Property.Id);
			Assert.Equal(99, copy.Property.EntityId);
			Assert.Equal(SyncType.Post, copy.Property.SyncType);
			Assert.Equal(new byte[] { 1, 2 }, copy.Property.Data);
			Assert.Null(copy.Properties);
		}

		[Fact]
		public void Command_RoundTripsABatchOfProperties()
		{
			Command original = new Command {
				IsProperty = true,
				Properties = new List<SyncData> {
					new SyncData { Id = 1, SyncType = SyncType.Broadcast, Data = new byte[] { 1 } },
					new SyncData { Id = 2, SyncType = SyncType.Broadcast, Data = new byte[] { 2 } },
					new SyncData { Id = 3, SyncType = SyncType.Broadcast, Data = new byte[] { 3 } },
				},
			};

			Command copy = StubSerializer.Deserialize<Command>(StubSerializer.Serialize(original));

			Assert.Equal(3, copy.Properties.Count);
			Assert.Equal(new long[] { 1, 2, 3 }, copy.Properties.Select(p => p.Id).ToArray());
			Assert.Null(copy.Property);
		}

		[Fact]
		public void AnInlinePropertyIsCheaperThanEncodingItSeparately()
		{
			// The reason the layout changed: nesting the message costs one
			// encode pass, wrapping its bytes costs two.
			SyncData update = new SyncData { Id = 1, SyncType = SyncType.Broadcast, Data = new byte[] { 1, 2, 3, 4 } };

			int inline = StubSerializer.Serialize(new Command { IsProperty = true, Property = update }).Length;
			int wrapped = StubSerializer.Serialize(new Command { IsProperty = true, Data = StubSerializer.Serialize(update) }).Length;

			Assert.True(inline <= wrapped, $"inline {inline} bytes should not exceed wrapped {wrapped}");
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
