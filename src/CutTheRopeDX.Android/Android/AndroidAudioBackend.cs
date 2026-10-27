using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Android.Content.Res;
using Android.Media;

using CutTheRopeDX.Framework.Media;

namespace CutTheRopeDX.Android
{
    /// <summary>One decoded sound effect in Android's low-latency sound pool.</summary>
    internal sealed class AndroidSoundEffect : ISoundEffect
    {
        private readonly SoundPool _soundPool;
        private bool _disposed;

        internal AndroidSoundEffect(SoundPool soundPool, int soundId, TimeSpan duration)
        {
            _soundPool = soundPool;
            SoundId = soundId;
            Duration = duration;
        }

        internal int SoundId { get; }

        internal TimeSpan Duration { get; }

        /// <inheritdoc />
        public ISoundInstance CreateInstance()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new AndroidSoundInstance(_soundPool, this);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = _soundPool.Unload(SoundId);
        }
    }

    /// <summary>A single playing voice of a pooled effect.</summary>
    internal sealed class AndroidSoundInstance : ISoundInstance
    {
        private static readonly TimeSpan CompletionTolerance = TimeSpan.FromMilliseconds(100);

        private readonly SoundPool _soundPool;
        private readonly AndroidSoundEffect _effect;
        private readonly Stopwatch _clock = new();
        private int _streamId;
        private AudioPlaybackState _state = AudioPlaybackState.Stopped;
        private float _volume = 1f;
        private bool _disposed;

        internal AndroidSoundInstance(SoundPool soundPool, AndroidSoundEffect effect)
        {
            _soundPool = soundPool;
            _effect = effect;
        }

        /// <inheritdoc />
        public bool IsLooped { get; set; }

        /// <inheritdoc />
        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0f, 1f);
                if (_streamId != 0)
                {
                    _soundPool.SetVolume(_streamId, _volume, _volume);
                }
            }
        }

        /// <inheritdoc />
        public AudioPlaybackState State
        {
            get
            {
                // SoundPool has no per-stream completion query. The packaged duration lets Core
                // retire completed one-shots instead of accumulating their managed instances.
                if (_state == AudioPlaybackState.Playing
                    && !IsLooped
                    && _clock.Elapsed >= _effect.Duration + CompletionTolerance)
                {
                    _streamId = 0;
                    _clock.Reset();
                    _state = AudioPlaybackState.Stopped;
                }

                return _state;
            }
        }

        /// <inheritdoc />
        public void Play()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Stop();
            int streamId = _soundPool.Play(
                _effect.SoundId,
                _volume,
                _volume,
                1,
                IsLooped ? -1 : 0,
                1f);
            if (streamId == 0)
            {
                return;
            }

            _streamId = streamId;
            _state = AudioPlaybackState.Playing;
            _clock.Restart();
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (_streamId != 0)
            {
                _soundPool.Stop(_streamId);
            }

            _streamId = 0;
            _clock.Reset();
            _state = AudioPlaybackState.Stopped;
        }

        /// <inheritdoc />
        public void Pause()
        {
            if (_state != AudioPlaybackState.Playing)
            {
                return;
            }

            if (_streamId != 0)
            {
                _soundPool.Pause(_streamId);
            }

            _clock.Stop();
            _state = AudioPlaybackState.Paused;
        }

        /// <inheritdoc />
        public void Resume()
        {
            if (_state != AudioPlaybackState.Paused)
            {
                return;
            }

            if (_streamId != 0)
            {
                _soundPool.Resume(_streamId);
            }

            _clock.Start();
            _state = AudioPlaybackState.Playing;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
        }
    }

    /// <summary>A streamed Android music track.</summary>
    internal sealed class AndroidMusicTrack : IMusicTrack
    {
        internal AndroidMusicTrack(string contentPath, TimeSpan duration)
        {
            ContentPath = contentPath;
            Duration = duration;
        }

        internal string ContentPath { get; }

        /// <inheritdoc />
        public TimeSpan Duration { get; }
    }

    /// <summary>Native Android audio for the shared game sound manager.</summary>
    internal sealed class AndroidAudioBackend : Java.Lang.Object,
        IAudioBackend,
        SoundPool.IOnLoadCompleteListener,
        MediaPlayer.IOnCompletionListener,
        MediaPlayer.IOnErrorListener
    {
        private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);

        private readonly AndroidContentStore _content;
        private readonly SoundPool _soundPool;
        private readonly Dictionary<int, TaskCompletionSource<bool>> _loads = [];
        private readonly object _loadLock = new();
        private readonly Dictionary<string, AndroidMusicTrack> _music = [];
        private MediaPlayer _musicPlayer;
        private int _musicState = (int)AudioPlaybackState.Stopped;
        private bool _disposed;

        public AndroidAudioBackend(AndroidContentStore content)
        {
            _content = content;
            using AudioAttributes attributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Game)
                .SetContentType(AudioContentType.Sfx)
                .Build();
            _soundPool = new SoundPool.Builder()
                .SetMaxStreams(24)
                .SetAudioAttributes(attributes)
                .Build();
            _soundPool.SetOnLoadCompleteListener(this);
        }

        /// <inheritdoc />
        public ISoundEffect LoadSound(string contentPath)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string assetPath = contentPath + ".ogg";
            using AssetFileDescriptor descriptor = _content.OpenFileDescriptor(assetPath);
            TimeSpan duration = ReadDuration(descriptor);

            int soundId;
            TaskCompletionSource<bool> completion;
            lock (_loadLock)
            {
                soundId = _soundPool.Load(descriptor, 1);
                if (soundId == 0)
                {
                    throw new InvalidOperationException($"SoundPool could not load '{assetPath}'.");
                }

                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _loads.Add(soundId, completion);
            }

            if (!completion.Task.Wait(LoadTimeout) || !completion.Task.Result)
            {
                lock (_loadLock)
                {
                    _ = _loads.Remove(soundId);
                }

                _ = _soundPool.Unload(soundId);
                throw new InvalidOperationException($"SoundPool did not decode '{assetPath}' successfully.");
            }

            return new AndroidSoundEffect(_soundPool, soundId, duration);
        }

        /// <inheritdoc />
        public IMusicTrack LoadMusic(string contentPath)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_music.TryGetValue(contentPath, out AndroidMusicTrack cached))
            {
                return cached;
            }

            using AssetFileDescriptor descriptor = _content.OpenFileDescriptor(contentPath + ".ogg");
            AndroidMusicTrack track = new(contentPath, ReadDuration(descriptor));
            _music.Add(contentPath, track);
            return track;
        }

        /// <inheritdoc />
        public void PlayMusic(IMusicTrack track, bool repeating)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (track is not AndroidMusicTrack androidTrack)
            {
                return;
            }

            StopMusic();
            using AssetFileDescriptor descriptor = _content.OpenFileDescriptor(androidTrack.ContentPath + ".ogg");
            _musicPlayer ??= CreateMusicPlayer();
            _musicPlayer.Reset();
            using (AudioAttributes attributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)
                .SetContentType(AudioContentType.Music)
                .Build())
            {
                _musicPlayer.SetAudioAttributes(attributes);
            }

            _musicPlayer.SetDataSource(
                descriptor.FileDescriptor,
                descriptor.StartOffset,
                descriptor.Length);
            _musicPlayer.Looping = repeating;
            _musicPlayer.Prepare();
            _musicPlayer.Start();
            Volatile.Write(ref _musicState, (int)AudioPlaybackState.Playing);
        }

        /// <inheritdoc />
        public void StopMusic()
        {
            if (_musicPlayer is not null)
            {
                // Reset is valid from prepared, started, paused and completed states, and also
                // covers a stop requested just as the native completion callback arrives.
                _musicPlayer.Reset();
            }

            Volatile.Write(ref _musicState, (int)AudioPlaybackState.Stopped);
        }

        /// <inheritdoc />
        public void PauseMusic()
        {
            if (MusicState != AudioPlaybackState.Playing || _musicPlayer is null)
            {
                return;
            }

            if (_musicPlayer.IsPlaying)
            {
                _musicPlayer.Pause();
                Volatile.Write(ref _musicState, (int)AudioPlaybackState.Paused);
            }
            else
            {
                Volatile.Write(ref _musicState, (int)AudioPlaybackState.Stopped);
            }
        }

        /// <inheritdoc />
        public void ResumeMusic()
        {
            if (MusicState == AudioPlaybackState.Paused && _musicPlayer is not null)
            {
                _musicPlayer.Start();
                Volatile.Write(ref _musicState, (int)AudioPlaybackState.Playing);
            }
        }

        /// <inheritdoc />
        public AudioPlaybackState MusicState => (AudioPlaybackState)Volatile.Read(ref _musicState);

        /// <inheritdoc />
        public void OnLoadComplete(SoundPool soundPool, int sampleId, int status)
        {
            TaskCompletionSource<bool> completion;
            lock (_loadLock)
            {
                if (!_loads.Remove(sampleId, out completion))
                {
                    return;
                }
            }

            _ = completion.TrySetResult(status == 0);
        }

        /// <inheritdoc />
        public void OnCompletion(MediaPlayer mp)
        {
            Volatile.Write(ref _musicState, (int)AudioPlaybackState.Stopped);
        }

        /// <inheritdoc />
        public bool OnError(MediaPlayer mp, MediaError what, int extra)
        {
            Volatile.Write(ref _musicState, (int)AudioPlaybackState.Stopped);
            global::Android.Util.Log.Warn(
                "CutTheRopeDX",
                $"Android music playback failed: {what}, extra={extra}.");
            return true;
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _musicPlayer?.Release();
                _musicPlayer = null;
                _soundPool.Release();
                lock (_loadLock)
                {
                    foreach (TaskCompletionSource<bool> load in _loads.Values)
                    {
                        _ = load.TrySetResult(false);
                    }

                    _loads.Clear();
                }
            }

            base.Dispose(disposing);
        }

        private MediaPlayer CreateMusicPlayer()
        {
            MediaPlayer player = new();
            player.SetOnCompletionListener(this);
            player.SetOnErrorListener(this);
            return player;
        }

        private static TimeSpan ReadDuration(AssetFileDescriptor descriptor)
        {
            try
            {
                using MediaMetadataRetriever retriever = new();
                retriever.SetDataSource(
                    descriptor.FileDescriptor,
                    descriptor.StartOffset,
                    descriptor.Length);
                string duration = retriever.ExtractMetadata(MetadataKey.Duration);
                return long.TryParse(duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out long milliseconds)
                    ? TimeSpan.FromMilliseconds(Math.Max(1, milliseconds))
                    : TimeSpan.FromSeconds(1);
            }
            catch (Exception failure)
            {
                // Duration only retires one-shot managed handles; playback itself does not depend
                // on a device exposing it through MediaMetadataRetriever.
                global::Android.Util.Log.Warn(
                    "CutTheRopeDX",
                    $"Could not read audio duration: {failure.Message}");
                return TimeSpan.FromSeconds(1);
            }
        }
    }
}
