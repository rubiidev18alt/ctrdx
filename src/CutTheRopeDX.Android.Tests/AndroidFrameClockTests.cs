using CutTheRopeDX.Android;

using Xunit;

namespace CutTheRopeDX.Android.Tests
{
    public class AndroidFrameClockTests
    {
        [Fact]
        public void FirstFrameRunsOneStepAndPartialFramesAccumulate()
        {
            AndroidFrameClock clock = new();

            Assert.Equal(1, clock.Advance(1000d));
            Assert.Equal(0, clock.Advance(1000d + (AndroidFrameClock.StepMilliseconds * 0.75d)));
            Assert.Equal(1, clock.Advance(1000d + (AndroidFrameClock.StepMilliseconds * 1.25d)));
        }

        [Fact]
        public void ZeroIsAValidTimestampRatherThanARestartMarker()
        {
            AndroidFrameClock clock = new();

            Assert.Equal(1, clock.Advance(0d));
            Assert.Equal(0, clock.Advance(0d));
            Assert.Equal(1, clock.Advance(AndroidFrameClock.StepMilliseconds));
        }

        [Fact]
        public void LongFrameIsCappedAtFiveCatchUpSteps()
        {
            AndroidFrameClock clock = new();
            _ = clock.Advance(1000d);

            Assert.Equal(
                AndroidFrameClock.MaximumCatchUpSteps,
                clock.Advance(1000d + (AndroidFrameClock.StepMilliseconds * 100d)));
            Assert.Equal(0, clock.Advance(1000d + (AndroidFrameClock.StepMilliseconds * 100d)));
        }

        [Fact]
        public void BackwardsTimestampDoesNotCreateNegativeDebt()
        {
            AndroidFrameClock clock = new();
            _ = clock.Advance(1000d);

            Assert.Equal(0, clock.Advance(900d));
            Assert.Equal(1, clock.Advance(900d + AndroidFrameClock.StepMilliseconds));
        }

        [Fact]
        public void SuspensionDropsBackgroundAndExistingDebt()
        {
            AndroidFrameClock clock = new();
            _ = clock.Advance(1000d);
            _ = clock.Advance(1000d + (AndroidFrameClock.StepMilliseconds * 0.9d));

            clock.SetSuspended(true);
            Assert.True(clock.Suspended);
            Assert.Equal(0, clock.Advance(900000d));

            clock.SetSuspended(false);
            Assert.False(clock.Suspended);
            Assert.Equal(1, clock.Advance(950000d));
            Assert.Equal(0, clock.Advance(950000d + (AndroidFrameClock.StepMilliseconds * 0.9d)));
        }
    }
}
