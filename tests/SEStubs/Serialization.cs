using System;
using System.IO;
using ProtoBuf;

namespace SEStubs
{
	/// <summary>
	/// Stand-in for MyAPIGateway.Utilities.SerializeToBinary/SerializeFromBinary.
	///
	/// The game serializes with protobuf-net, and so do we. Values are wrapped in
	/// a single-field envelope so that bare primitives (int, float, string ...)
	/// round-trip too -- protobuf has no top-level representation for those, and
	/// SENetworkAPI serializes raw T values in NetSync&lt;T&gt;.
	/// </summary>
	public static class StubSerializer
	{
		[ProtoContract]
		private class Envelope<T>
		{
			[ProtoMember(1)]
			public T Value { get; set; }
		}

		public static byte[] Serialize<T>(T value)
		{
			using (MemoryStream stream = new MemoryStream())
			{
				Serializer.Serialize(stream, new Envelope<T> { Value = value });
				return stream.ToArray();
			}
		}

		public static T Deserialize<T>(byte[] data)
		{
			if (data == null)
			{
				throw new ArgumentNullException(nameof(data));
			}

			using (MemoryStream stream = new MemoryStream(data))
			{
				return Serializer.Deserialize<Envelope<T>>(stream).Value;
			}
		}
	}
}
