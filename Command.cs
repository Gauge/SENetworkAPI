using ProtoBuf;
using System.Collections.Generic;

namespace SENetworkAPI
{
	[ProtoContract]
	internal class Command
	{
		[ProtoMember(1)]
		public ulong SteamId { get; set; }
		[ProtoMember(2)]
		public string CommandString { get; set; }
		[ProtoMember(3)]
		public string Message { get; set; }
		[ProtoMember(4)]
		public byte[] Data { get; set; }
		[ProtoMember(5)]
		public long Timestamp { get; set; }
		[ProtoMember(6)]
		public bool IsProperty { get; set; }
		[ProtoMember(7)]
		public bool IsCompressed { get; set; }

		/// <summary>
		/// A property update carried directly on the envelope.
		///
		/// The original layout put a serialized SyncData in <see cref="Data"/>,
		/// which cost a whole extra encode pass - protobuf charges by the call,
		/// not by the payload. Nested messages cost nothing extra, so the update
		/// rides along in the same pass. Packets in the old layout are still
		/// understood on receive.
		/// </summary>
		[ProtoMember(8)]
		public SyncData Property { get; set; }

		/// <summary>
		/// Several property updates batched into one packet. Set instead of
		/// <see cref="Property"/> when more than one property changed in the
		/// same frame and coalescing is enabled.
		/// </summary>
		[ProtoMember(9)]
		public List<SyncData> Properties { get; set; }
	}
}
