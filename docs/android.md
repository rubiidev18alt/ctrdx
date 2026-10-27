# Android build

The Android head runs the same `CutTheRopeDX.Core` game as the desktop and browser heads. It uses SkiaSharp over OpenGL ES, Android `SoundPool`/`MediaPlayer` for audio, and a native `VideoView` for cutscenes. Android 5.0 (API 21) or later is required.

## Prerequisites

- The .NET SDK version from `global.json`, with the Android workload:

  ```bash
  dotnet workload install android
  ```

- A configured Android SDK and a JDK supported by that .NET Android workload. Android Studio's SDK setup is sufficient. Set `ANDROID_HOME` if the SDK is not in the default location.
- Python 3.11 or later and the content-conversion packages:

  ```bash
  python3 -m pip install -r scripts/requirements.txt
  ```

- FFmpeg 9 with the `libopus` and `libvpx-vp9` encoders. The conversion refuses an FFmpeg without the required encoders. To pin an exact binary, set `CTRDX_FFMPEG` to its absolute path and optionally set `CTRDX_FFMPEG_SHA256` to its SHA-256.

The original game assets are downloaded and verified by the existing content builder; they are not stored in this repository. Original game assets belong to ZeptoLab.

## Build an APK

For a current 64-bit ARM device or emulator:

```bash
dotnet publish src/CutTheRopeDX.Android/CutTheRopeDX.Android.csproj \
    -c Release -f net10.0-android -r android-arm64
```

The APK is written under:

```text
src/CutTheRopeDX.Android/bin/Release/net10.0-android/android-arm64/publish/
```

The project also supports `android-arm`, `android-x86`, and `android-x64`. Omit `-r` to publish all configured RIDs, or substitute the one needed by the target device.

A normal build performs both existing content stages automatically:

1. Fetch and prepare the original content.
2. Convert it into the Android payload in `src/CutTheRopeDX.Android/Assets/content`: WebP images, Ogg audio, WebM video, fonts, maps, and tier-0 metadata.

The conversion is incremental. `--require-video` is always passed by the Android target, so a missing cutscene encoder fails the build instead of silently producing an incomplete game. The generated payload is ignored by Git.

To reuse an already converted payload without fetching or rebuilding content:

```bash
dotnet publish src/CutTheRopeDX.Android/CutTheRopeDX.Android.csproj \
    -c Release -f net10.0-android -r android-arm64 \
    -p:BuildContent=false -p:DeployContent=false
```

Do not use `DeployAndroidContent=false` for a playable build; that option is for compile-only CI checks.

## Build an APK with GitHub Actions

Run the **Build Android APK** workflow manually from the Actions tab. Each run downloads and verifies the original assets through the shared content workflow, installs JDK 21, the Android SDK/build tools, the .NET Android workload, and pinned FFmpeg, then converts the real game content once. It builds and uploads two signed APK artifacts: `ctrdx-android-arm64` for 64-bit ARM devices and `ctrdx-android-arm` for 32-bit ARMv7 devices. Both APKs use the standard generated Android debug key with v1/v2 compatibility signatures so they can be installed for testing on API 21+ devices without configuring secrets; use your own signing properties for a distribution build.

## Install and debug

With platform-tools on `PATH` and a device connected:

```bash
adb install -r path/to/the-built.apk
adb logcat -s CutTheRopeDX
```

The application ID is `page.yell0wsuit.cuttherope.dx`. A development APK can use the normal Android debug signing setup. A Release APK intended for distribution must be signed with your own upload/install key using the standard .NET Android signing properties. Keep keystores, aliases, and passwords outside Git.

Saves are JSON files in the app-private `files/saves` directory. Android backup is enabled by the manifest; no desktop save-directory probing or external-storage permission is used.

## Validation

Run the platform-independent Android helper tests with:

```bash
dotnet test src/CutTheRopeDX.Android.Tests/CutTheRopeDX.Android.Tests.csproj
```

They cover stable five-slot/nonzero touch IDs, cancellation, fixed-step timing, suspension, and content-path containment. PR quality CI also installs the Android workload, compiles the Android head, and publishes an APK containing tiny synthetic assets. That package smoke test verifies that Ogg and WebM entries are uncompressed, as required by `AssetManager.OpenFd`; it does not substitute synthetic files for real game content in a playable build.
