using System;
using System.Collections.Generic;
using System.Linq;
using SEStubs;
using VRage;
using VRage.Game.Components;
using Xunit;

namespace SENetworkAPI.Tests
{
	public class CompactBatchTests : NetworkTestBase
	{
		private class Session : MySessionComponentBase { }

		private void EnableCompact()
		{
			NetworkAPI.UseCompactBatches = true;
			NetworkAPI.CompactBatchThreshold = 0;
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void SharedEntityBatchesRoundTripWithOrWithoutCompression(bool compress)
		{
			GivenClient();
			EnableCompact();
			NetworkAPI.CompressionThreshold = compress ? 0 : int.MaxValue;
			var original = Enumerable.Range(0, 80).Select(i => new SyncData {
				Id = i, EntityId = long.MinValue + 42, SyncType = SyncType.Broadcast,
				Data = StubSerializer.Serialize(new string('a', 40))
			}).ToList();
			var command = new Command { IsProperty = true, Properties = original };
			int before = StubSerializer.Serialize(command).Length;
			NetworkAPI.Compress(command);
			Assert.Equal(1, command.BatchFormat);
			Assert.Equal(compress, command.IsCompressed);
			Assert.True(StubSerializer.Serialize(command).Length < before);
			var decoded = CompactBatch.Decode(compress ? MyCompression.Decompress(command.Data) : command.Data);
			AssertUpdatesEqual(original, decoded);
			Assert.All(original, u => Assert.Equal(long.MinValue + 42, u.EntityId));
		}

		[Fact]
		public void MixedEntitiesTypesAndNullEmptyValuesRetainTheirOrder()
		{
			GivenClient();
			EnableCompact();
			NetworkAPI.CompressionThreshold = 0;
			var original = Enumerable.Range(0, 80).Select(i => new SyncData {
				Id = i, EntityId = i % 2 == 0 ? 0 : long.MaxValue,
				SyncType = (SyncType)(i % 3),
				Data = i % 3 == 0 ? null : i % 3 == 1 ? new byte[0] : new byte[50]
			}).ToList();
			var command = new Command { IsProperty = true, Properties = original };
			NetworkAPI.Compress(command);
			Assert.Equal(1, command.BatchFormat);
			var decoded = CompactBatch.Decode(command.IsCompressed ? MyCompression.Decompress(command.Data) : command.Data);
			AssertUpdatesEqual(original, decoded);
		}

		[Fact]
		public void FetchBatchesNeedNoValueArrayOrLengths()
		{
			GivenClient();
			EnableCompact();
			NetworkAPI.CompressionThreshold = int.MaxValue;
			var command = new Command { IsProperty = true, Properties = Enumerable.Range(0, 50).Select(i => new SyncData {
				Id = i, EntityId = 900000000000, SyncType = SyncType.Fetch
			}).ToList() };
			NetworkAPI.Compress(command);
			Assert.Equal(1, command.BatchFormat);
			var batch = StubSerializer.Deserialize<CompactBatch>(command.Data);
			Assert.Null(batch.Lengths);
			Assert.Null(batch.Values);
			Assert.All(CompactBatch.Decode(command.Data), u => { Assert.Null(u.Data); Assert.Equal(SyncType.Fetch, u.SyncType); });
		}

		[Fact]
		public void TinyOrLargerEncodingsStayOnTheLegacyLayout()
		{
			GivenClient();
			EnableCompact();
			var updates = new List<SyncData> { new SyncData(), new SyncData() };
			var command = new Command { IsProperty = true, Properties = updates };
			NetworkAPI.Compress(command);
			Assert.Equal(0, command.BatchFormat);
			Assert.Same(updates, command.Properties);
			Assert.Null(command.Data);

			NetworkAPI.CompactBatchThreshold = 256;
			command.Properties = new List<SyncData> {
				new SyncData { Id = 1, EntityId = long.MaxValue },
				new SyncData { Id = 2, EntityId = long.MaxValue }
			};
			NetworkAPI.Compress(command);
			Assert.Equal(0, command.BatchFormat);
		}

		[Fact]
		public void IncompressibleBatchDoesNotKeepAnExpandedCompressedCopy()
		{
			GivenClient();
			EnableCompact();
			NetworkAPI.CompressionThreshold = 0;
			var random = new Random(7);
			var original = new List<SyncData>();
			for (int i = 0; i < 2; i++)
			{
				byte[] value = new byte[7000];
				random.NextBytes(value);
				original.Add(new SyncData { Id = i, EntityId = long.MaxValue, Data = value });
			}
			var command = new Command { IsProperty = true, Properties = original };
			int originalLength = StubSerializer.Serialize(command).Length;
			NetworkAPI.Compress(command);
			Assert.False(command.IsCompressed);
			Assert.True(StubSerializer.Serialize(command).Length <= originalLength);
			if (command.BatchFormat == 1) AssertUpdatesEqual(original, CompactBatch.Decode(command.Data));
		}

		[Fact]
		public void ClientAcceptsCompactBatchesEvenWhenCompactSendingIsDisabled()
		{
			GivenServer();
			EnableCompact();
			for (int i = 0; i < 40; i++)
				new NetSync<int>(new Session(), TransferType.ServerToClient, 0, syncOnLoad: false).Coalesce().Value = i + 1;
			Game.NextFrame();
			byte[] wire = Assert.Single(Game.Sent).Data;
			Assert.Equal(1, StubSerializer.Deserialize<Command>(wire).BatchFormat);
			Restart();
			GivenClient();
			var properties = Enumerable.Range(0, 40).Select(i => new NetSync<int>(new Session(), TransferType.ServerToClient, 0, syncOnLoad: false)).ToList();
			Receive(wire);
			Assert.False(NetworkAPI.UseCompactBatches);
			Assert.Equal(Enumerable.Range(1, 40), properties.Select(p => p.Value));
		}

		[Fact]
		public void CompactClientBatchUsesTransportIdentityAndDirectionChecks()
		{
			GivenServer();
			EnableCompact();
			var writable = new NetSync<int>(new Session(), TransferType.Both, 0, syncOnLoad: false);
			var protectedValue = new NetSync<int>(new Session(), TransferType.ServerToClient, 0, syncOnLoad: false);
			ulong sender = 0;
			writable.ValueChangedByNetwork += (oldValue, newValue, id) => sender = id;
			var command = new Command { IsProperty = true, SteamId = 999, Properties = Enumerable.Range(0, 50).Select(i => new SyncData {
				Id = i % 2 == 0 ? writable.Id : protectedValue.Id, Data = StubSerializer.Serialize(i + 1), SyncType = SyncType.Post
			}).ToList() };
			NetworkAPI.Compress(command);
			Assert.Equal(1, command.BatchFormat);
			Receive(StubSerializer.Serialize(command), senderId: ClientId);
			Assert.Equal(ClientId, sender);
			Assert.Equal(49, writable.Value);
			Assert.Equal(0, protectedValue.Value);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void ByteBudgetsAndAllValuesSurviveCompactBatchSplitting(bool lossy)
		{
			GivenClient();
			EnableCompact();
			for (int i = 0; i < 100; i++)
				new NetSync<string>(new Session(), TransferType.Both, "", syncOnLoad: false).Coalesce().Lossy(lossy).Value = new string('x', 250);
			Game.NextFrame();
			Assert.True(Game.Sent.Count > 1);
			Assert.All(Game.Sent, p => {
				Assert.InRange(p.Data.Length, 1, lossy ? NetworkAPI.UnreliableMessageLimit : NetSync.ReliableBatchByteLimit);
				Assert.Equal(!lossy, p.Reliable);
			});
			var updates = Game.Sent.SelectMany(DecodeSyncDataList).ToList();
			Assert.Equal(100, updates.Count);
			Assert.All(updates, u => Assert.Equal(new string('x', 250), StubSerializer.Deserialize<string>(u.Data)));
		}

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		[InlineData(4)]
		[InlineData(5)]
		public void MalformedCompactBatchIsRejectedBeforeApplyingAnyValues(int fault)
		{
			GivenClient();
			var property = new NetSync<int>(new Session(), TransferType.Both, 7, syncOnLoad: false);
			var value = StubSerializer.Serialize(99);
			var batch = new CompactBatch {
				Ids = new List<long> { property.Id, property.Id },
				Lengths = new List<int> { value.Length + 1, 0 },
				Values = value
			};
			if (fault == 0) batch.Lengths.Add(0);
			if (fault == 1) batch.Lengths[1] = -1;
			if (fault == 2) batch.Lengths[1] = 10;
			if (fault == 3) batch.EntityIds = new List<long> { 0 };
			if (fault == 4) batch.SyncTypes = new List<SyncType> { SyncType.Post, SyncType.None };
			if (fault == 5) batch.Ids = Enumerable.Repeat(property.Id, 501).ToList();
			Receive(StubSerializer.Serialize(new Command { IsProperty = true, BatchFormat = 1, Data = StubSerializer.Serialize(batch) }));
			Assert.Equal(7, property.Value);
			Assert.True(LoggedError("Failure in message processing"));
		}

		[Theory]
		[InlineData(2, false)]
		[InlineData(1, true)]
		public void UnknownOrAmbiguousBatchFormatsAreRejected(int version, bool inline)
		{
			GivenClient();
			var property = new NetSync<int>(new Session(), TransferType.Both, 7, syncOnLoad: false);
			var update = new SyncData { Id = property.Id, Data = StubSerializer.Serialize(99) };
			Receive(StubSerializer.Serialize(new Command {
				IsProperty = true, BatchFormat = version,
				Property = inline ? update : null,
				Data = new byte[0]
			}));
			Assert.Equal(7, property.Value);
			Assert.True(LoggedError("Failure in message processing"));
		}

		private static void AssertUpdatesEqual(List<SyncData> expected, List<SyncData> actual)
		{
			Assert.Equal(expected.Count, actual.Count);
			for (int i = 0; i < expected.Count; i++)
			{
				Assert.Equal(expected[i].Id, actual[i].Id);
				Assert.Equal(expected[i].EntityId, actual[i].EntityId);
				Assert.Equal(expected[i].SyncType, actual[i].SyncType);
				Assert.Equal(expected[i].Data, actual[i].Data);
			}
		}
	}
}
