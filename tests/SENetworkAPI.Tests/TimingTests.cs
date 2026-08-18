using System;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>The timestamp helpers used to age incoming packets.</summary>
	public class TimingTests
	{
		[Fact]
		public void GetDeltaMilliseconds_ForNow_IsEssentiallyZero()
		{
			float delta = NetworkAPI.GetDeltaMilliseconds(DateTime.UtcNow.Ticks);

			Assert.InRange(delta, 0, 50);
		}

		[Fact]
		public void GetDeltaMilliseconds_MeasuresTheAgeOfATimestamp()
		{
			long quarterSecondAgo = DateTime.UtcNow.Ticks - (250 * TimeSpan.TicksPerMillisecond);

			float delta = NetworkAPI.GetDeltaMilliseconds(quarterSecondAgo);

			Assert.InRange(delta, 250, 300);
		}

		[Fact]
		public void GetDeltaMilliseconds_ForAFutureTimestamp_IsNegative()
		{
			long inTheFuture = DateTime.UtcNow.Ticks + (1000 * TimeSpan.TicksPerMillisecond);

			Assert.True(NetworkAPI.GetDeltaMilliseconds(inTheFuture) < 0);
		}

		[Fact]
		public void GetDeltaMilliseconds_HasWholeMillisecondResolution()
		{
			// The subtraction is integer division on ticks, so the float result
			// never carries a fraction -- sub-millisecond timing is not available.
			float delta = NetworkAPI.GetDeltaMilliseconds(DateTime.UtcNow.Ticks - 15_000);

			Assert.Equal(delta, (float)Math.Floor(delta));
		}

		[Fact]
		public void GetDeltaFrames_ConvertsMillisecondsToSixtyHertzFrames()
		{
			long oneSecondAgo = DateTime.UtcNow.Ticks - TimeSpan.TicksPerSecond;

			int frames = NetworkAPI.GetDeltaFrames(oneSecondAgo);

			Assert.InRange(frames, 60, 63);
		}

		[Fact]
		public void GetDeltaFrames_RoundsUp()
		{
			// 1ms is a fraction of a frame but still counts as one.
			long justNow = DateTime.UtcNow.Ticks - TimeSpan.TicksPerMillisecond;

			Assert.InRange(NetworkAPI.GetDeltaFrames(justNow), 1, 2);
		}

		[Fact]
		public void GetDeltaFrames_ForNow_IsZero()
		{
			Assert.InRange(NetworkAPI.GetDeltaFrames(DateTime.UtcNow.Ticks), 0, 1);
		}
	}
}
