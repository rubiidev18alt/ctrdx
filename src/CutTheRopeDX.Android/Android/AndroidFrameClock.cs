using System;

namespace CutTheRopeDX.Android
{
    /// <summary>Fixed-step timing for the display-driven Android renderer.</summary>
    internal sealed class AndroidFrameClock
    {
        internal const double StepMilliseconds = 1000d / 60d;
        internal const int MaximumCatchUpSteps = 5;

        private const double TimingToleranceMs = StepMilliseconds * 0.000001d;

        private double _lastTimestampMs;
        private double _accumulatorMs;
        private bool _hasTimestamp;
        private bool _suspended;

        /// <summary>Whether simulation updates are currently suspended.</summary>
        public bool Suspended => _suspended;

        /// <summary>Returns how many fixed updates the next frame should run.</summary>
        /// <remarks>
        /// Long frames are capped rather than replayed in full. A backwards timestamp is treated
        /// as zero elapsed time, and suspending clears all debt so background time is never caught
        /// up when the activity returns.
        /// </remarks>
        public int Advance(double timestampMs)
        {
            if (_suspended)
            {
                Reset();
                return 0;
            }

            double elapsed = !_hasTimestamp
                ? StepMilliseconds
                : timestampMs - _lastTimestampMs;
            _lastTimestampMs = timestampMs;
            _hasTimestamp = true;
            _accumulatorMs += Math.Clamp(elapsed, 0d, StepMilliseconds * MaximumCatchUpSteps);

            int steps = 0;
            // Repeated subtraction of 1/60 can leave the fifth interval a few ulps below the
            // threshold. The tolerance is nanoseconds, not an extra gameplay-speed allowance.
            while (_accumulatorMs + TimingToleranceMs >= StepMilliseconds
                && steps < MaximumCatchUpSteps)
            {
                _accumulatorMs = Math.Max(0d, _accumulatorMs - StepMilliseconds);
                steps++;
            }

            return steps;
        }

        /// <summary>Suspends or resumes stepping and drops elapsed time from the transition.</summary>
        public void SetSuspended(bool value)
        {
            _suspended = value;
            Reset();
        }

        /// <summary>Clears the clock before the next display frame.</summary>
        public void Reset()
        {
            _lastTimestampMs = 0d;
            _accumulatorMs = 0d;
            _hasTimestamp = false;
        }
    }
}
