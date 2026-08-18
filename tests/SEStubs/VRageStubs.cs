// ---------------------------------------------------------------------------
//  SEStubs -- a minimal stand-in for the Space Engineers ModAPI.
//
//  SENetworkAPI is compiled against the game's assemblies (VRage.*, Sandbox.*),
//  which are Windows-only, licensed, and impossible to load in a unit-test
//  runner. This assembly re-declares *only* the surface SENetworkAPI actually
//  touches, keeping the same namespaces, type names and member signatures, so
//  the production sources compile unmodified against it.
//
//  Everything here is deliberately dumb: state is public and settable so tests
//  can drive the "game" directly. See SEStubs/FakeGame.cs for the control seam.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ProtoBuf;

namespace VRageMath
{
	/// <summary>Double precision world-space vector.</summary>
	public struct Vector3D
	{
		public double X, Y, Z;

		public Vector3D(double x, double y, double z)
		{
			X = x;
			Y = y;
			Z = z;
		}

		public Vector3D(double xyz) : this(xyz, xyz, xyz) { }

		public static readonly Vector3D Zero = new Vector3D(0, 0, 0);

		public double LengthSquared() => (X * X) + (Y * Y) + (Z * Z);
		public double Length() => Math.Sqrt(LengthSquared());

		public static Vector3D operator -(Vector3D a, Vector3D b) => new Vector3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
		public static Vector3D operator +(Vector3D a, Vector3D b) => new Vector3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
		public static Vector3D operator *(Vector3D a, double s) => new Vector3D(a.X * s, a.Y * s, a.Z * s);

		public override string ToString() => $"{{X:{X} Y:{Y} Z:{Z}}}";
	}
}

namespace VRage.Utils
{
	/// <summary>Interned string handle. Only ToString()/equality are used by the API.</summary>
	public struct MyStringHash
	{
		private readonly string _value;
		private MyStringHash(string value) { _value = value; }
		public static MyStringHash GetOrCompute(string value) => new MyStringHash(value);
		public string String => _value ?? string.Empty;
		public override string ToString() => _value ?? string.Empty;
	}

	public struct MyStringId
	{
		private readonly string _value;
		private MyStringId(string value) { _value = value; }
		public static MyStringId GetOrCompute(string value) => new MyStringId(value);
		public override string ToString() => _value ?? string.Empty;
	}

	public enum LogSeverity { Info, Warning, Error }

	public sealed class LogEntry
	{
		public LogSeverity Severity;
		public string Message;
		public override string ToString() => $"[{Severity}] {Message}";
	}

	/// <summary>
	/// Stand-in for the game log. Every line is retained so tests can assert on
	/// the error/warning paths that SENetworkAPI reports *only* through logging.
	/// </summary>
	public class MyLog
	{
		public static MyLog Default = new MyLog();

		public readonly List<LogEntry> Entries = new List<LogEntry>();

		public void Info(string message) => Write(LogSeverity.Info, message);
		public void Warning(string message) => Write(LogSeverity.Warning, message);
		public void Error(string message) => Write(LogSeverity.Error, message);
		public void WriteLine(string message) => Write(LogSeverity.Info, message);

		private void Write(LogSeverity severity, string message)
		{
			lock (Entries)
			{
				Entries.Add(new LogEntry { Severity = severity, Message = message });
			}
		}

		public void Clear()
		{
			lock (Entries) { Entries.Clear(); }
		}

		/// <summary>All messages logged at the given severity.</summary>
		public IReadOnlyList<string> Messages(LogSeverity severity)
		{
			lock (Entries)
			{
				return Entries.FindAll(e => e.Severity == severity).ConvertAll(e => e.Message);
			}
		}

		public bool Contains(LogSeverity severity, string fragment)
		{
			lock (Entries)
			{
				return Entries.Exists(e => e.Severity == severity && e.Message != null && e.Message.Contains(fragment));
			}
		}
	}
}

namespace VRage
{
	/// <summary>
	/// The game uses a proprietary block compressor; GZip is behaviourally
	/// equivalent for our purposes (round-trips bytes, changes the length).
	/// </summary>
	public static class MyCompression
	{
		public static int CompressCallCount;
		public static int DecompressCallCount;

		public static byte[] Compress(byte[] data)
		{
			CompressCallCount++;
			using (MemoryStream output = new MemoryStream())
			{
				using (GZipStream gzip = new GZipStream(output, CompressionLevel.Fastest, true))
				{
					gzip.Write(data, 0, data.Length);
				}

				return output.ToArray();
			}
		}

		public static byte[] Decompress(byte[] data)
		{
			DecompressCallCount++;
			using (MemoryStream input = new MemoryStream(data))
			using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
			using (MemoryStream output = new MemoryStream())
			{
				gzip.CopyTo(output);
				return output.ToArray();
			}
		}
	}
}

namespace VRage.ObjectBuilders
{
	public class MyObjectBuilder_Base { }
}

namespace VRage.Game
{
	using VRage.ObjectBuilders;
	using VRage.Utils;

	public enum MyOnlineModeEnum { OFFLINE, PRIVATE, FRIENDS, PUBLIC }

	public struct MyDefinitionId
	{
		public MyStringHash SubtypeId;
		public string TypeIdString;

		public MyDefinitionId(string typeId, string subtypeId)
		{
			TypeIdString = typeId;
			SubtypeId = MyStringHash.GetOrCompute(subtypeId);
		}

		public override string ToString() => $"{TypeIdString}/{SubtypeId}";
	}

	public class MyObjectBuilder_EntityBase : MyObjectBuilder_Base { }
	public class MyObjectBuilder_SessionComponent : MyObjectBuilder_Base { }
}

namespace VRage.ModAPI
{
	/// <summary>Root of the game's entity interface tree.</summary>
	public interface IMyEntity
	{
		long EntityId { get; }
	}

	[Flags]
	public enum MyEntityUpdateEnum
	{
		NONE = 0,
		BEFORE_NEXT_FRAME = 1,
		EACH_FRAME = 2,
		EACH_10TH_FRAME = 4,
		EACH_100TH_FRAME = 8,
	}
}

namespace VRage.Game.Entity
{
	using VRage.Game;
	using VRage.ModAPI;
	using VRageMath;

	/// <summary>Position/orientation component of an entity.</summary>
	public class MyPositionComponentBase
	{
		public Vector3D Position;
		public Vector3D GetPosition() => Position;
		public void SetPosition(Vector3D position) => Position = position;
	}

	/// <summary>
	/// Concrete entity stub. <see cref="Close"/> and <see cref="AddToScene"/>
	/// raise the lifecycle events NetSync hooks into.
	/// </summary>
	public class MyEntity : IMyEntity
	{
		private static long _nextEntityId = 1;

		public long EntityId { get; set; }
		public MyDefinitionId? DefinitionId { get; set; }
		public MyPositionComponentBase PositionComp { get; set; } = new MyPositionComponentBase();

		public event Action<MyEntity> OnClose;
		public event Action<MyEntity> AddedToScene;

		public MyEntity()
		{
			EntityId = _nextEntityId++;
		}

		/// <summary>Raises AddedToScene, as the game does when the entity enters the world.</summary>
		public void AddToScene() => AddedToScene?.Invoke(this);

		/// <summary>Raises OnClose, as the game does when the entity is removed.</summary>
		public void Close() => OnClose?.Invoke(this);

		/// <summary>Number of live AddedToScene subscribers (used to assert unsubscription).</summary>
		public int AddedToSceneSubscriberCount => AddedToScene?.GetInvocationList().Length ?? 0;
		public int OnCloseSubscriberCount => OnClose?.GetInvocationList().Length ?? 0;

		internal static void ResetIdCounter() => _nextEntityId = 1;
	}
}

namespace Sandbox.Game.Entities
{
	using VRage.Game.Entity;

	public class MyCubeGrid : MyEntity
	{
		public string DisplayName { get; set; } = "Static Grid";
	}

	public class MyCubeBlock : MyEntity
	{
		public MyCubeGrid CubeGrid { get; set; } = new MyCubeGrid();
	}
}

namespace VRage.Game.ModAPI
{
	using VRageMath;

	public interface IMyPlayer
	{
		ulong SteamUserId { get; }
		string DisplayName { get; }
		Vector3D GetPosition();
	}
}

namespace VRage.Game.Components
{
	using System;
	using VRage.Game;
	using VRage.ModAPI;

	public enum MyUpdateOrder { NoUpdate, BeforeSimulation, Simulation, AfterSimulation }

	[AttributeUsage(AttributeTargets.Class)]
	public class MySessionComponentDescriptor : Attribute
	{
		public MySessionComponentDescriptor(MyUpdateOrder updateOrder) { }
		public MySessionComponentDescriptor(MyUpdateOrder updateOrder, int priority) { }
	}

	[AttributeUsage(AttributeTargets.Class)]
	public class MyEntityComponentDescriptor : Attribute
	{
		public MyEntityComponentDescriptor(Type objectBuilderType, bool useEntityUpdate, params string[] subtypes) { }
	}

	public abstract class MyComponentBase { }

	public abstract class MySessionComponentBase : MyComponentBase
	{
		public virtual void Init(MyObjectBuilder_SessionComponent sessionComponent) { }
		public virtual void LoadData() { }
		protected virtual void UnloadData() { }

		/// <summary>Test hook: drives the protected UnloadData the game would call.</summary>
		public void SimulateUnload() => UnloadData();
	}

	public abstract class MyGameLogicComponent : MyComponentBase
	{
		public IMyEntity Entity { get; set; }
		public MyEntityUpdateEnum NeedsUpdate { get; set; }

		public virtual void Init(MyObjectBuilder_EntityBase objectBuilder) { }
		public virtual void UpdateOnceBeforeFrame() { }
	}
}
