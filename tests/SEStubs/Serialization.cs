using System;
using System.IO;
using ProtoBuf;

namespace SEStubs
{
	/// <summary>
	/// Stand-in for MyAPIGateway.Utilities.SerializeToBinary/SerializeFromBinary.
	///
	/// This is a line-for-line copy of what the shipped game does in
	/// Sandbox.ModAPI.MyAPIUtilities:
	///
	///     byte[] IMyUtilities.SerializeToBinary&lt;T&gt;(T obj)
	///     {
	///         using MemoryStream memoryStream = new MemoryStream();
	///         Serializer.Serialize(memoryStream, obj);
	///         return memoryStream.ToArray();
	///     }
	///
	/// Space Engineers ships a fork of protobuf-net (ProtoBuf.Net.Core). It and
	/// the stock package agree on root-level values: an int is 2 bytes, a float
	/// 5, "hello" 7, in both. Note that protobuf's Serialize is a no-op for a
	/// null instance, so serializing null yields an empty array rather than
	/// throwing -- that behaviour is reproduced here too.
	/// </summary>
	public static class StubSerializer
	{
		public static byte[] Serialize<T>(T value)
		{
			using (MemoryStream stream = new MemoryStream())
			{
				Serializer.Serialize(stream, value);
				return stream.ToArray();
			}
		}

		public static T Deserialize<T>(byte[] data)
		{
			using (MemoryStream stream = new MemoryStream(data))
			{
				return Serializer.Deserialize<T>(stream);
			}
		}
	}
}
