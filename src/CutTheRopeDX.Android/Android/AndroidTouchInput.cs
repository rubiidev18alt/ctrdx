using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using CutTheRopeDX.Framework.Core;

namespace CutTheRopeDX.Android
{
    /// <summary>Maps Android pointer ids to the five stable touch slots used by gameplay.</summary>
    /// <remarks>
    /// This type deliberately has no Android dependency. A view forwards its current pointer
    /// coordinates, while the game thread remains the only thread that sees the returned touches.
    /// </remarks>
    internal sealed class AndroidTouchInput
    {
        /// <summary>Gameplay has five touch slots; zero is its empty-slot sentinel.</summary>
        internal const int MaximumTouches = 5;

        /// <summary>Releases a cancelled gesture outside every button, matching the desktop host.</summary>
        internal static readonly Vector2 CancelledPosition = new(-100000f, -100000f);

        private readonly Dictionary<int, (int EngineId, Vector2 Position)> _pointers = [];

        /// <summary>Number of Android pointers currently tracked.</summary>
        public int ActiveCount => _pointers.Count;

        /// <summary>Records a new pointer, or returns an empty batch when all slots are occupied.</summary>
        public TouchLocation[] Press(int pointerId, float x, float y)
        {
            if (_pointers.ContainsKey(pointerId) || _pointers.Count >= MaximumTouches)
            {
                return [];
            }

            int engineId = FirstFreeEngineId();
            Vector2 position = new(x, y);
            _pointers.Add(pointerId, (engineId, position));
            return [new TouchLocation(engineId, TouchLocationState.Pressed, position)];
        }

        /// <summary>Moves a tracked pointer.</summary>
        public TouchLocation[] Move(int pointerId, float x, float y)
        {
            if (!_pointers.TryGetValue(pointerId, out (int EngineId, Vector2 Position) pointer))
            {
                return [];
            }

            Vector2 position = new(x, y);
            _pointers[pointerId] = (pointer.EngineId, position);
            return [new TouchLocation(pointer.EngineId, TouchLocationState.Moved, position)];
        }

        /// <summary>Releases a tracked pointer.</summary>
        public TouchLocation[] Release(int pointerId, float x, float y)
        {
            if (!_pointers.TryGetValue(pointerId, out (int EngineId, Vector2 Position) pointer))
            {
                return [];
            }

            _ = _pointers.Remove(pointerId);
            return [new TouchLocation(
                pointer.EngineId,
                TouchLocationState.Released,
                new Vector2(x, y))];
        }

        /// <summary>Ends every tracked gesture without activating the element underneath it.</summary>
        public TouchLocation[] CancelAll()
        {
            TouchLocation[] cancelled = [.. _pointers.Values
                .OrderBy(pointer => pointer.EngineId)
                .Select(pointer => new TouchLocation(
                    pointer.EngineId,
                    TouchLocationState.Released,
                    CancelledPosition))];
            _pointers.Clear();
            return cancelled;
        }

        private int FirstFreeEngineId()
        {
            for (int id = 1; id <= MaximumTouches; id++)
            {
                if (_pointers.Values.All(pointer => pointer.EngineId != id))
                {
                    return id;
                }
            }

            // Press checks the slot bound before reaching here.
            throw new System.InvalidOperationException("No Android touch slot is available.");
        }
    }
}
