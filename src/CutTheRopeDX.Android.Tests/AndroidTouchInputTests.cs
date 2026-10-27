using System.Linq;
using System.Numerics;

using CutTheRopeDX.Android;
using CutTheRopeDX.Framework.Core;

using Xunit;

namespace CutTheRopeDX.Android.Tests
{
    public class AndroidTouchInputTests
    {
        [Fact]
        public void PointerIdsMapToStableNonzeroEngineSlots()
        {
            AndroidTouchInput input = new();

            TouchLocation first = Assert.Single(input.Press(41, 10f, 20f));
            TouchLocation second = Assert.Single(input.Press(7, 30f, 40f));

            Assert.Equal(1, first.Id);
            Assert.Equal(TouchLocationState.Pressed, first.State);
            Assert.Equal(new Vector2(10f, 20f), first.Position);
            Assert.Equal(2, second.Id);
            Assert.Equal(2, input.ActiveCount);

            TouchLocation moved = Assert.Single(input.Move(41, 11f, 22f));
            Assert.Equal(first.Id, moved.Id);
            Assert.Equal(TouchLocationState.Moved, moved.State);
            Assert.Equal(new Vector2(11f, 22f), moved.Position);
        }

        [Fact]
        public void ReleasedSlotIsReusedWithoutDisturbingOtherPointers()
        {
            AndroidTouchInput input = new();
            _ = input.Press(100, 0f, 0f);
            _ = input.Press(200, 0f, 0f);

            TouchLocation released = Assert.Single(input.Release(100, 5f, 6f));
            TouchLocation replacement = Assert.Single(input.Press(300, 7f, 8f));

            Assert.Equal(1, released.Id);
            Assert.Equal(TouchLocationState.Released, released.State);
            Assert.Equal(new Vector2(5f, 6f), released.Position);
            Assert.Equal(1, replacement.Id);
            Assert.Equal(2, Assert.Single(input.Move(200, 1f, 1f)).Id);
        }

        [Fact]
        public void DuplicateUnknownAndExcessPointersAreIgnored()
        {
            AndroidTouchInput input = new();
            _ = input.Press(9, 0f, 0f);

            Assert.Empty(input.Press(9, 1f, 1f));
            Assert.Empty(input.Move(10, 1f, 1f));
            Assert.Empty(input.Release(10, 1f, 1f));
            for (int pointer = 10; pointer < 14; pointer++)
            {
                _ = Assert.Single(input.Press(pointer, pointer, pointer));
            }

            Assert.Equal(AndroidTouchInput.MaximumTouches, input.ActiveCount);
            Assert.Empty(input.Press(99, 0f, 0f));
            Assert.Equal(5, input.CancelAll().Length);
            TouchLocation restarted = Assert.Single(input.Press(99, 0f, 0f));
            Assert.Equal(1, restarted.Id);
        }

        [Fact]
        public void CancellationReleasesEverySlotOffscreenInStableOrder()
        {
            AndroidTouchInput input = new();
            for (int pointer = 0; pointer < AndroidTouchInput.MaximumTouches; pointer++)
            {
                _ = input.Press(50 + pointer, pointer, pointer);
            }

            TouchLocation[] cancelled = input.CancelAll();

            Assert.Equal([1, 2, 3, 4, 5], cancelled.Select(touch => touch.Id).ToArray());
            Assert.All(cancelled, touch =>
            {
                Assert.Equal(TouchLocationState.Released, touch.State);
                Assert.Equal(AndroidTouchInput.CancelledPosition, touch.Position);
            });
            Assert.Equal(0, input.ActiveCount);
            Assert.Empty(input.CancelAll());
        }
    }
}
