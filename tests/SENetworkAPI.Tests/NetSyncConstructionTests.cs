using System;
using SEStubs;
using Sandbox.Game.Entities;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>
	/// NetSync construction: id assignment, registry bookkeeping and the
	/// sync-on-load hook.
	/// </summary>
	public class NetSyncConstructionTests : NetworkTestBase
	{
		private class TestLogic : MyGameLogicComponent { }

		private class TestSessionComponent : MySessionComponentBase { }

		[Fact]
		public void NullEntity_Throws()
		{
			GivenClient();

			Exception ex = Assert.Throws<Exception>(() => new NetSync<int>((MyEntity)null, TransferType.Both));

			Assert.Contains("MyEntity was null", ex.Message);
		}

		[Fact]
		public void NullIMyEntity_Throws()
		{
			GivenClient();

			Assert.Throws<Exception>(() => new NetSync<int>((IMyEntity)null, TransferType.Both));
		}

		[Fact]
		public void NullGameLogic_Throws()
		{
			GivenClient();

			Exception ex = Assert.Throws<Exception>(() => new NetSync<int>((MyGameLogicComponent)null, TransferType.Both));

			Assert.Contains("MyGameLogicComponent was null", ex.Message);
		}

		[Fact]
		public void GameLogicWithoutAnEntity_Throws()
		{
			GivenClient();

			Assert.Throws<Exception>(() => new NetSync<int>(new TestLogic(), TransferType.Both));
		}

		[Fact]
		public void NullSessionComponent_Throws()
		{
			GivenClient();

			Exception ex = Assert.Throws<Exception>(() => new NetSync<int>((MySessionComponentBase)null, TransferType.Both));

			Assert.Contains("MySessionComponentBase was null", ex.Message);
		}

		[Fact]
		public void GameLogicConstructor_BindsToTheOwningEntity()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			TestLogic logic = new TestLogic { Entity = entity };

			NetSync<int> property = new NetSync<int>(logic, TransferType.Both, 5);

			Assert.Same(property, NetSync.PropertiesByEntity[entity][0]);
			Assert.Equal(5, property.Value);
		}

		[Fact]
		public void EntityProperties_AreIdentifiedByTheirDeclarationOrder()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();

			NetSync<int> first = new NetSync<int>(entity, TransferType.Both);
			NetSync<int> second = new NetSync<int>(entity, TransferType.Both);
			NetSync<int> third = new NetSync<int>(entity, TransferType.Both);

			Assert.Equal(0, first.Id);
			Assert.Equal(1, second.Id);
			Assert.Equal(2, third.Id);
			Assert.Equal(3, NetSync.PropertiesByEntity[entity].Count);
		}

		[Fact]
		public void EachEntity_HasItsOwnIdSequence()
		{
			GivenClient();
			MyEntity a = Game.CreateEntity();
			MyEntity b = Game.CreateEntity();

			NetSync<int> first = new NetSync<int>(a, TransferType.Both);
			NetSync<int> second = new NetSync<int>(b, TransferType.Both);

			Assert.Equal(0, first.Id);
			Assert.Equal(0, second.Id);
		}

		[Fact]
		public void SessionProperties_UseAGloballyGeneratedId()
		{
			GivenClient();

			NetSync<int> first = new NetSync<int>(new TestSessionComponent(), TransferType.Both);
			NetSync<int> second = new NetSync<int>(new TestSessionComponent(), TransferType.Both);

			Assert.Equal(1, first.Id);
			Assert.Equal(2, second.Id);
			Assert.Same(first, NetSync.PropertyById[1]);
			Assert.Same(second, NetSync.PropertyById[2]);
		}

		[Fact]
		public void EntityProperties_AreNotAddedToThePropertyByIdRegistry()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();

			new NetSync<int>(entity, TransferType.Both);

			Assert.Empty(NetSync.PropertyById);
		}

		[Fact]
		public void ConstructorFlags_ArePreserved()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();

			NetSync<int> property = new NetSync<int>(entity, TransferType.ServerToClient, 9, syncOnLoad: false, limitToSyncDistance: false);

			Assert.Equal(TransferType.ServerToClient, property.TransferType);
			Assert.False(property.SyncOnLoad);
			Assert.False(property.LimitToSyncDistance);
			Assert.Equal(9, property.Value);
		}

		[Fact]
		public void EntityProperty_WithSyncOnLoad_DoesNotFetchUntilTheEntityEntersTheScene()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			Game.ClearTraffic();

			new NetSync<int>(entity, TransferType.Both);

			Assert.Empty(Game.Sent);
			Assert.Equal(1, entity.AddedToSceneSubscriberCount);
		}

		[Fact]
		public void EntityProperty_FetchesOnceTheEntityEntersTheScene()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			new NetSync<int>(entity, TransferType.Both);
			Game.ClearTraffic();

			entity.AddToScene();

			SyncData sync = TheOnlySyncDataSent();
			Assert.Equal(SyncType.Fetch, sync.SyncType);
			Assert.Equal(entity.EntityId, sync.EntityId);
		}

		[Fact]
		public void EntityProperty_UnsubscribesAfterItsFirstSceneEntry()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			new NetSync<int>(entity, TransferType.Both);

			entity.AddToScene();
			Game.ClearTraffic();
			entity.AddToScene();

			Assert.Equal(0, entity.AddedToSceneSubscriberCount);
			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void EntityProperty_WithoutSyncOnLoad_NeverHooksTheScene()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();

			new NetSync<int>(entity, TransferType.Both, syncOnLoad: false);
			entity.AddToScene();

			Assert.Equal(0, entity.AddedToSceneSubscriberCount);
			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void SessionProperty_WithSyncOnLoad_FetchesImmediately()
		{
			GivenClient();
			Game.ClearTraffic();

			new NetSync<int>(new TestSessionComponent(), TransferType.Both);

			SyncData sync = TheOnlySyncDataSent();
			Assert.Equal(SyncType.Fetch, sync.SyncType);
			Assert.Equal(0, sync.EntityId);
			Assert.Equal(1, sync.Id);
		}

		[Fact]
		public void SessionProperty_WithoutSyncOnLoad_StaysQuiet()
		{
			GivenClient();
			Game.ClearTraffic();

			new NetSync<int>(new TestSessionComponent(), TransferType.Both, syncOnLoad: false);

			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void SyncOnLoad_OnTheServer_SendsNothing_BecauseServersNeverFetch()
		{
			GivenServer();
			Game.ClearTraffic();

			new NetSync<int>(new TestSessionComponent(), TransferType.Both);

			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void ClosingAnEntity_UnregistersTheMatchingPropertyId()
		{
			// Note the asymmetry: Entity_OnClose only touches PropertyById, which
			// entity-scoped properties never live in. See docs/known-issues.md.
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			NetSync<int> property = new NetSync<int>(entity, TransferType.Both);

			entity.Close();

			Assert.True(NetSync.PropertiesByEntity.ContainsKey(entity));
			Assert.Contains(property, NetSync.PropertiesByEntity[entity]);
		}

		[Fact]
		public void ClosingAnEntity_CanEvictAnUnrelatedSessionProperty()
		{
			// The entity property's Id is a per-entity index (0, 1, 2 ...) while
			// session property ids come from a global counter (1, 2, 3 ...).
			// The two key spaces collide inside PropertyById on close.
			GivenClient();
			NetSync<int> sessionProperty = new NetSync<int>(new TestSessionComponent(), TransferType.Both);
			Assert.Equal(1, sessionProperty.Id);

			MyEntity entity = Game.CreateEntity();
			new NetSync<int>(entity, TransferType.Both);   // Id 0
			new NetSync<int>(entity, TransferType.Both);   // Id 1 -- collides
			entity.Close();

			Assert.False(NetSync.PropertyById.ContainsKey(1));
		}

		[Fact]
		public void Descriptor_ForASessionProperty_NamesTheComponent()
		{
			GivenClient();

			NetSync<string> property = new NetSync<string>(new TestSessionComponent(), TransferType.Both, string.Empty);

			Assert.Equal($"<TestSessionComponent_String.{property.Id}>", property.Descriptor());
		}

		[Fact]
		public void Descriptor_ForAnEntityProperty_NamesTheSubtypeAndEntityId()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity(subtypeId: "TestBlock");

			NetSync<int> property = new NetSync<int>(entity, TransferType.Both);

			Assert.Equal($"<TestBlock.{entity.EntityId}_Int32.0>", property.Descriptor());
		}

		[Fact]
		public void Descriptor_WithoutADefinition_FallsBackToTheTypeName()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();

			NetSync<int> property = new NetSync<int>(entity, TransferType.Both);

			Assert.Equal($"<MyEntity.{entity.EntityId}_Int32.0>", property.Descriptor());
		}

		[Fact]
		public void Descriptor_ForABlock_IncludesTheGridName()
		{
			GivenClient();
			MyCubeBlock block = new MyCubeBlock { DefinitionId = new VRage.Game.MyDefinitionId("MyObjectBuilder_UpgradeModule", "TestBlock") };
			block.CubeGrid.DisplayName = "Big Red";
			Game.Entities.Add(block);

			NetSync<int> property = new NetSync<int>(block, TransferType.Both);

			Assert.Equal($"<Big Red_TestBlock.{block.EntityId}_Int32.0>", property.Descriptor());
		}
	}
}
