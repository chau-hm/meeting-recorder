# Meeting Evidence Recorder

## System Design Document (SDD)

**文件版本：** v0.1
**日期：** 2026-09-09
**狀態：** Draft
**對應 PRD：** Meeting Evidence Recorder PRD v0.1
**目標平台：** Windows 10/11 x64、Apple Silicon macOS
**主要技術棧：** .NET 10 LTS + C# + Avalonia
**相關系統：** Meeting Recording Processor (MRP)

---

# 1. Purpose

本文件定義 Meeting Evidence Recorder 嘅系統架構、component boundaries、runtime data flow、platform abstraction、recording timeline、audio synchronization、media output、evidence persistence、failure recovery、testing strategy 同 deployment model。

SDD 目的係令 implementation 可以由 Phase 0 開始逐步落地，而唔需要每一個 PR 重新決定：

* authoritative timeline；
* capture abstraction；
* audio mixing strategy；
* screenshot semantics；
* Evidence Bundle format；
* crash recovery；
* Windows/macOS responsibility boundary；
* MRP integration contract。

本文件不重新定義 product requirements。

如本 SDD 與 PRD 衝突：

> **PRD 優先。**

---

# 2. System Context

Meeting Evidence Recorder 位於整個 Meeting Recording Processor workflow 嘅最前端。

```text
┌───────────────────────────────────────────────┐
│ Meeting Evidence Recorder                    │
│                                               │
│ Screen Capture                               │
│ System Audio Capture                         │
│ Microphone Capture                           │
│ Screenshot Hotkey                            │
│ Recording Clock                              │
│ Evidence Events                              │
└──────────────────────┬────────────────────────┘
                       │
                       ▼
             Meeting Evidence Bundle
                       │
                       ▼
┌───────────────────────────────────────────────┐
│ Meeting Recording Processor                  │
│                                               │
│ Audio Extraction                             │
│ ASR                                          │
│ SRT                                          │
│ OCR                                          │
│ Automatic Frames                             │
│ Timeline Alignment                           │
└──────────────────────┬────────────────────────┘
                       │
                       ▼
                Evidence Timeline
                       │
                       ▼
┌───────────────────────────────────────────────┐
│ Document Generation Agent                    │
│                                               │
│ Interpretation                               │
│ Summary                                      │
│ Decisions / Actions                          │
│ Screenshot Selection                         │
│ Final Document                               │
└───────────────────────────────────────────────┘
```

Recorder 不應知道：

* 使用邊個 ASR model；
* transcript 格式點整理；
* screenshot OCR 結果；
* LLM prompt；
* meeting final document structure。

MRP 亦不應需要：

* Recorder process；
* Avalonia；
* Windows audio device；
* macOS capture API；
* virtual audio routing。

兩者之間唯一正式 contract 係：

> **Meeting Evidence Bundle。**

---

# 3. Design Goals

系統設計優先順序：

```text
1. Recording reliability
2. Timeline correctness
3. Audio/video synchronization
4. Evidence durability
5. Failure visibility
6. Cross-platform consistency
7. UI simplicity
8. Performance optimization
```

如果需要 trade-off：

> recording integrity 永遠優先於 UI responsiveness、preview quality 或 optional diagnostics。

---

# 4. Architectural Principles

## 4.1 Shared Core, Native Capture

跨平台唔代表所有 code 都必須跨平台。

採用：

```text
Shared .NET application/core
+
platform-specific native capture adapters
```

Shared layer 負責：

* session lifecycle；
* recording state machine；
* authoritative timeline；
* audio normalization；
* audio mixing；
* event model；
* Evidence Bundle；
* persistence；
* validation；
* UI view models。

Platform layer 負責：

* screen acquisition；
* system audio acquisition；
* microphone acquisition；
* native permissions；
* hotkey registration；
* native device enumeration；
* OS-specific tray/menu integration where required。

Avalonia 目前 Windows backend 直接使用 Win32，而 macOS 使用自身 native Objective-C++ backend；desktop app 可以 target .NET 10。

---

## 4.2 One Authoritative Media Timeline

所有 media/evidence 必須落入同一時間座標：

```text
T = 0
Recording starts
```

之後：

```text
video frame PTS
system audio samples
microphone samples
screenshot events
pause/resume boundaries
```

全部最終表示為：

```text
recording timeline timestamp
```

不得將 wall-clock time 當 media timeline。

---

## 4.3 Capture and Encoding Are Separate Concerns

```text
Capture
   ↓
Timestamp
   ↓
Normalize
   ↓
Synchronize
   ↓
Encode / Mux
```

Capture backend 不應負責：

* Evidence Bundle naming；
* screenshot event persistence；
* final session metadata；
* business state machine。

---

## 4.4 Screenshot Is an Event Referencing a Frame

Screenshot 唔係另一套獨立 screen-capture workflow。

概念上係：

```text
Recording frame stream
          │
          ├──► Encoder
          │
          └──► Latest valid frame
                        │
                   hotkey event
                        │
                        ▼
                      PNG
```

因此 screenshot 應盡可能係 recording stream 真正出現過嘅 frame。

---

## 4.5 Append Before Finalize

Recording 期間可以持續保存嘅 metadata 應持續 append，而唔等 Stop 先寫。

因此：

```text
events.jsonl
```

採用 append-only。

Session completion metadata 則 finalization 時寫入。

---

## 4.6 Fail Explicitly

以下情況不得 silent degradation：

* system audio unavailable；
* microphone requested but failed；
* disk failure；
* encoder failure；
* screenshot write failure；
* permission unavailable；
* timeline discontinuity；
* audio synchronization failure。

---

# 5. High-Level Architecture

```text
┌──────────────────────────────┐
│ Avalonia Presentation Layer  │
│                              │
│ Main Window                  │
│ Tray/Menu UI                 │
│ Settings                     │
│ Status / Diagnostics         │
└───────────────┬──────────────┘
                │
                ▼
┌──────────────────────────────┐
│ Application Layer            │
│                              │
│ RecorderController           │
│ RecordingStateMachine        │
│ SessionCoordinator           │
│ ScreenshotCoordinator        │
│ PermissionCoordinator        │
└───────────────┬──────────────┘
                │
                ▼
┌────────────────────────────────────────────┐
│ Recording Core                             │
│                                            │
│ RecordingClock                             │
│ VideoPipeline                              │
│ AudioPipeline                              │
│ Synchronizer                               │
│ Mixer                                      │
│ MediaWriter                                │
│ EvidenceEventWriter                        │
│ SessionManifestWriter                      │
└───────────┬──────────────────┬─────────────┘
            │                  │
            ▼                  ▼
┌───────────────────┐   ┌────────────────────┐
│ Platform Services │   │ Persistence        │
│                   │   │                    │
│ Screen Capture    │   │ Session Directory  │
│ System Audio      │   │ Work Artifacts     │
│ Microphone        │   │ JSON / JSONL       │
│ Hotkey            │   │ Screenshots        │
│ Permissions       │   │ Final MP4          │
└─────────┬─────────┘   └────────────────────┘
          │
     ┌────┴─────┐
     ▼          ▼
 Windows      macOS
```

---

# 6. Proposed Solution Structure

```text
MeetingEvidenceRecorder.sln

src/
├── MeetingEvidenceRecorder.App/
│   ├── App.axaml
│   ├── Views/
│   ├── ViewModels/
│   ├── Services/
│   └── Program.cs
│
├── MeetingEvidenceRecorder.Application/
│   ├── RecorderController.cs
│   ├── SessionCoordinator.cs
│   ├── ScreenshotCoordinator.cs
│   ├── PermissionCoordinator.cs
│   └── RecordingStateMachine.cs
│
├── MeetingEvidenceRecorder.Core/
│   ├── Recording/
│   │   ├── RecordingClock.cs
│   │   ├── RecordingSession.cs
│   │   ├── RecordingConfiguration.cs
│   │   └── RecordingState.cs
│   │
│   ├── Video/
│   │   ├── VideoFrame.cs
│   │   └── VideoFormat.cs
│   │
│   ├── Audio/
│   │   ├── AudioFrame.cs
│   │   ├── AudioFormat.cs
│   │   ├── AudioMixer.cs
│   │   └── AudioSynchronizer.cs
│   │
│   ├── Evidence/
│   │   ├── RecordingEvent.cs
│   │   ├── ScreenshotEvent.cs
│   │   └── EvidenceBundle.cs
│   │
│   └── Abstractions/
│       ├── IScreenCaptureBackend.cs
│       ├── ISystemAudioCaptureBackend.cs
│       ├── IMicrophoneCaptureBackend.cs
│       ├── IGlobalHotkeyService.cs
│       ├── IPermissionService.cs
│       └── IMediaWriter.cs
│
├── MeetingEvidenceRecorder.Infrastructure/
│   ├── Media/
│   │   ├── FfmpegMediaWriter.cs
│   │   ├── AudioResampler.cs
│   │   └── PngFrameWriter.cs
│   │
│   ├── Persistence/
│   │   ├── EvidenceBundleWriter.cs
│   │   ├── EventJournal.cs
│   │   ├── ManifestWriter.cs
│   │   └── SessionRecoveryService.cs
│   │
│   └── Diagnostics/
│       └── RecorderLogger.cs
│
├── MeetingEvidenceRecorder.Platform.Windows/
│   ├── WindowsScreenCaptureBackend.cs
│   ├── WasapiSystemAudioBackend.cs
│   ├── WindowsMicrophoneBackend.cs
│   ├── WindowsGlobalHotkeyService.cs
│   └── WindowsPermissionService.cs
│
└── MeetingEvidenceRecorder.Platform.MacOS/
    ├── MacScreenCaptureBackend.cs
    ├── MacSystemAudioBackend.cs
    ├── MacMicrophoneBackend.cs
    ├── MacGlobalHotkeyService.cs
    └── MacPermissionService.cs

tests/
├── MeetingEvidenceRecorder.Core.Tests/
├── MeetingEvidenceRecorder.Application.Tests/
├── MeetingEvidenceRecorder.Infrastructure.Tests/
├── MeetingEvidenceRecorder.Platform.Windows.Tests/
├── MeetingEvidenceRecorder.Platform.MacOS.Tests/
└── MeetingEvidenceRecorder.IntegrationTests/
```

---

# 7. Dependency Direction

Dependency 必須單向：

```text
App
 ↓
Application
 ↓
Core
 ↑
Infrastructure
 ↑
Platform implementations
```

Core 不得 reference：

* Avalonia；
* Windows namespaces；
* macOS native framework；
* FFmpeg executable wrapper；
* filesystem-specific implementation。

Platform adapters implement Core abstractions。

---

# 8. Core Interfaces

## 8.1 Screen Capture

```csharp
public interface IScreenCaptureBackend : IAsyncDisposable
{
    Task<IReadOnlyList<CaptureSource>> GetSourcesAsync(
        CancellationToken cancellationToken);

    Task StartAsync(
        CaptureSource source,
        VideoCaptureOptions options,
        CancellationToken cancellationToken);

    IAsyncEnumerable<VideoFrame> ReadFramesAsync(
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
```

`VideoFrame`：

```csharp
public sealed record VideoFrame(
    long Sequence,
    TimeSpan SourceTimestamp,
    ReadOnlyMemory<byte> Data,
    VideoFormat Format
);
```

`SourceTimestamp` 係 native capture timestamp。

Application/Core layer 將其 normalize 成 recording timeline。

---

# 9. Windows Screen Capture

Windows implementation 優先使用：

```text
Windows.Graphics.Capture
```

Microsoft 官方 API 可以取得 display/window frames，並用於 continuous stream 或 snapshot。

High-level flow：

```text
Capture Source
     ↓
GraphicsCaptureItem
     ↓
Direct3D11CaptureFramePool
     ↓
GraphicsCaptureSession
     ↓
FrameArrived
     ↓
VideoFrame
```

Platform backend 必須處理：

* capture support detection；
* source close；
* display resize；
* graphics device loss；
* capture restart；
* frame pool recreation。

---

# 10. macOS Screen Capture

macOS implementation 採用：

```text
ScreenCaptureKit
```

ScreenCaptureKit 會將 screen/audio 以 timestamped sample buffers 提供畀 app，適合作為 native capture backend。

High-level flow：

```text
SCShareableContent
      ↓
SCContentFilter
      ↓
SCStreamConfiguration
      ↓
SCStream
      ↓
CMSampleBuffer
      ↓
VideoFrame
```

MVP target：

```text
Apple Silicon macOS
```

---

# 11. Audio Architecture

Audio pipeline 必須將：

```text
System Audio
+
Microphone
```

視為兩個獨立 capture streams。

即使 MVP final output 係 mixed audio：

> capture stage 仍然唔應直接將兩個來源視為一個 opaque stream。

Architecture：

```text
System Audio Backend
        │
        ▼
   Audio Frames
        │
        ├───────────────┐
                        ▼
                  Audio Normalize
                        │
                        ▼
                   Synchronizer
                        ▲
                        │
        ┌───────────────┘
        │
Microphone Backend
        │
        ▼
   Audio Frames
                        │
                        ▼
                     Mixer
                        │
                        ▼
                Mixed PCM Stream
                        │
                        ▼
                  Media Writer
```

---

# 12. Windows Audio Capture

## 12.1 System Audio

Windows system audio 使用：

```text
WASAPI loopback
```

WASAPI loopback 可以取得 render endpoint 嘅 system mix，而且唔要求使用者啟用「Stereo Mix」之類 hardware loopback device。

Implementation：

```text
Default render endpoint
        ↓
WASAPI shared-mode loopback
        ↓
IAudioCaptureClient
        ↓
System Audio Frames
```

---

## 12.2 Microphone

Microphone 使用 Windows audio capture endpoint。

Logical flow：

```text
Default capture endpoint
        ↓
Audio client
        ↓
Microphone PCM Frames
```

MVP 必須：

* auto-select default microphone；
* enumerate alternatives；
* detect device removal；
* report capture failure。

---

# 13. macOS Audio Capture

目前 ScreenCaptureKit configuration 可同時配置：

```text
capturesAudio
captureMicrophone
microphoneCaptureDeviceID
```

Apple 官方 sample 亦展示同一 stream configuration 同時設定 screen audio 與 microphone capture。

因此 macOS MVP 優先採用：

```text
ScreenCaptureKit
├── screen
├── system audio
└── microphone
```

而唔要求：

```text
BlackHole
Loopback
Soundflower
virtual audio device
```

---

# 14. Canonical Audio Format

Platform capture formats 可以不同。

進入 shared mixer 前統一成 canonical format。

MVP 建議：

```text
PCM
48,000 Hz
32-bit float internal representation
stereo mix bus
```

原因：

* 48 kHz 係 video production 常見 sample rate；
* float mixing 減少 intermediate clipping / conversion complexity；
* system audio 多數 stereo；
* microphone mono 可以上混到 stereo bus。

Final codec 可由 media writer encode。

---

# 15. Audio Normalization

每個 source 進入 mixer 前：

```text
Native PCM
    ↓
Sample format conversion
    ↓
Sample-rate conversion
    ↓
Channel mapping
    ↓
Canonical AudioFrame
```

例如：

```text
Microphone
48 kHz mono
    ↓
48 kHz stereo
```

或者：

```text
Device
44.1 kHz float
    ↓
48 kHz float
```

---

# 16. Audio Synchronization

System audio 同 microphone 唔可以假設：

```text
callback arrival time == media timestamp
```

每個 `AudioFrame` 必須帶：

```csharp
public sealed record AudioFrame(
    AudioSourceKind Source,
    TimeSpan SourceTimestamp,
    int SampleCount,
    AudioFormat Format,
    ReadOnlyMemory<float> Samples
);
```

Synchronizer 負責：

```text
Source timestamp
      ↓
map to RecordingClock
      ↓
detect early / late buffer
      ↓
small correction / buffering
      ↓
aligned mixer frame
```

---

# 17. Audio Drift Policy

同步系統需要區分：

### Short-term jitter

以 bounded buffer 吸收。

### Persistent clock drift

記錄 drift：

```text
system audio timeline
vs
microphone timeline
vs
recording timeline
```

如果差異逐步增加，AudioSynchronizer 可以作非常細微 resampling correction。

不可：

```text
突然 drop 大段 audio
```

或：

```text
silent timestamp reset
```

所有 significant discontinuity 必須：

* log；
* session diagnostic；
* 如影響 output integrity，標示 warning。

---

# 18. Audio Mixing

MVP output：

> Mixed Audio Track

Mixer：

```text
System Audio ────┐
                 ├──► Mixer ──► mixed PCM
Microphone ──────┘
```

初版 mixer 唔需要：

* compressor；
* noise reduction；
* echo cancellation；
* automatic gain control；
* voice enhancement。

只需要：

* deterministic summing；
* configurable gain；
* clipping prevention。

建議 default：

```text
system audio gain = 1.0
microphone gain   = 1.0
```

必要時使用 limiter / safe normalization。

---

# 19. Future Separate Tracks

Core model 必須由開始就識別：

```text
AudioSourceKind.System
AudioSourceKind.Microphone
AudioSourceKind.Mixed
```

因此日後可以將 media writer 擴展成：

```text
recording.mp4

Track 0 Video
Track 1 Mixed Audio
Track 2 System Audio
Track 3 Microphone
```

但 MVP Evidence Bundle 只要求：

```text
Video
+
Mixed Audio
```

---

# 20. Recording Clock

## 20.1 Requirements

RecordingClock 必須：

* monotonic；
* high resolution；
* 不受 wall-clock adjustment；
* pause-aware；
* thread-safe；
* process-local。

Logical API：

```csharp
public interface IRecordingClock
{
    TimeSpan Elapsed { get; }

    bool IsRunning { get; }

    void Start();

    void Pause();

    void Resume();

    void Stop();
}
```

---

# 21. Timeline Semantics

Canonical timestamp：

```text
T = playback position in final recording
```

例：

```text
Recording starts    T=0
Record 10 min       T=10:00
Pause 5 min
Resume              T remains 10:00
Record 20 min
Stop                duration = 30:00
```

因此 pause wall-clock time：

```text
不進入 canonical media timeline
```

---

# 22. Source Timestamp Mapping

Native sources 各自可能有 timestamp。

初始化 recording 時建立：

```text
source timestamp origin
→
recording clock origin
```

其後：

```text
NormalizedTimestamp =
SourceTimestamp
- SourceOrigin
- accumulated paused duration
```

實際 implementation 必須避免只用 callback arrival time。

---

# 23. Screenshot Architecture

Global hotkey flow：

```text
User presses hotkey
        ↓
IGlobalHotkeyService
        ↓
ScreenshotCoordinator
        ↓
RecordingClock timestamp request
        ↓
LatestFrameProvider
        ↓
copy current valid video frame
        ↓
PNG encoder
        ↓
atomic file write
        ↓
append events.jsonl
        ↓
UI notification
```

---

# 24. Latest Frame Buffer

唔需要保存大量 frames。

只維持：

```text
Latest completed frame
+
optional previous frame
```

例如：

```csharp
public interface ILatestFrameProvider
{
    bool TryAcquireLatest(out CapturedFrameLease frame);
}
```

Frame ownership 必須清楚，避免 capture thread overwrite screenshot 正在 encode 嘅 memory。

建議：

```text
reference-counted / copied frame lease
```

而唔係共享 mutable buffer。

---

# 25. Screenshot Timestamp Semantics

Screenshot event timestamp 應對應：

> **被保存 frame 本身嘅 normalized frame timestamp**

而唔係：

> hotkey callback arrival time。

流程：

```text
hotkey at T=754.091
latest rendered frame PTS=754.083
```

保存：

```json
{
  "timestamp_ms": 754083
}
```

因為 PNG 真正代表：

```text
T = 754.083
```

---

# 26. Screenshot Naming

Human-readable filename：

```text
shot_HH-MM-SS.mmm.png
```

例如：

```text
shot_00-12-34.083.png
```

Canonical identity 唔依賴 filename。

`events.jsonl`：

```json
{
  "event_id": "evt-000023",
  "type": "screenshot",
  "timestamp_ms": 754083,
  "asset": "screenshots/shot_00-12-34.083.png"
}
```

如果同一 millisecond 多次 capture：

```text
shot_00-12-34.083_02.png
```

---

# 27. Global Hotkey Abstraction

```csharp
public interface IGlobalHotkeyService
{
    Task RegisterAsync(
        HotkeyDefinition hotkey,
        Func<Task> callback);

    Task UnregisterAsync();
}
```

Platform implementations：

```text
WindowsGlobalHotkeyService
MacGlobalHotkeyService
```

UI 不應直接處理 platform-specific shortcut registration。

---

# 28. Recording State Machine

```text
               ┌────────────┐
               │    Idle    │
               └─────┬──────┘
                     │ Start
                     ▼
               ┌────────────┐
               │  Starting  │
               └─────┬──────┘
                     │ initialized
                     ▼
               ┌────────────┐
          ┌────│ Recording  │───────┐
          │    └────────────┘       │
      Pause│                         │Stop
          ▼                         ▼
    ┌────────────┐           ┌────────────┐
    │   Paused   │           │  Stopping  │
    └─────┬──────┘           └─────┬──────┘
          │ Resume                  │ finalized
          └──────► Recording        ▼
                              ┌────────────┐
                              │ Completed  │
                              └────────────┘
```

任意 active state：

```text
        fatal error
            ↓
      Error / Incomplete
```

---

# 29. Startup Transaction

Start Recording 唔應只係：

```text
state = Recording
```

而係一個 staged initialization transaction。

Sequence：

```text
Create Session Directory
        ↓
Validate disk
        ↓
Validate permissions
        ↓
Initialize screen capture
        ↓
Initialize system audio
        ↓
Initialize microphone if enabled
        ↓
Initialize media writer
        ↓
Initialize event journal
        ↓
Start authoritative clock
        ↓
Start capture streams
        ↓
Register recording state
```

任何 mandatory stage fail：

```text
rollback initialized resources
```

---

# 30. Start Recording Sequence

```text
User
 │
 │ Start
 ▼
RecorderController
 │
 ├── SessionCoordinator.Create()
 │
 ├── PermissionCoordinator.Validate()
 │
 ├── ScreenBackend.Initialize()
 │
 ├── SystemAudio.Initialize()
 │
 ├── Microphone.Initialize()
 │
 ├── MediaWriter.Initialize()
 │
 ├── EventJournal.Open()
 │
 ├── RecordingClock.Start()
 │
 ├── CaptureBackends.Start()
 │
 └── State = Recording
```

---

# 31. Stop Recording Sequence

```text
User
 │
 │ Stop
 ▼
RecorderController
 │
 ├── State = Stopping
 ├── unregister screenshot hotkey
 ├── stop accepting new events
 ├── stop capture backends
 ├── drain audio/video queues
 ├── stop recording clock
 ├── flush mixer
 ├── close work media
 ├── finalize/remux recording.mp4
 ├── validate recording
 ├── finalize session.json
 ├── fsync/close events.jsonl
 └── State = Completed
```

---

# 32. Media Pipeline

```text
Screen Capture
      │
      ▼
VideoFrame Queue
      │
      ├───────────────► LatestFrameProvider
      │                       │
      │                    Screenshot
      ▼
Video Encoder
      │
      │
System Audio Capture
      │
      ▼
Normalize ─┐
           │
           ▼
        Synchronizer
           │
Mic Capture│
      │    │
      ▼    │
Normalize ─┘
           │
           ▼
          Mixer
           │
           ▼
      Audio Encoder
           │
           ├────────────┐
Video Encoder───────────┤
                        ▼
                    Media Mux
```

---

# 33. Queue Strategy

Capture callbacks 必須保持短。

Capture callback 不應：

* PNG encode；
* JSON filesystem flush；
* FFmpeg blocking write；
* UI update。

使用 bounded channels：

```text
Video Capture
   ↓
Bounded Video Channel
   ↓
Video Consumer

Audio Capture
   ↓
Bounded Audio Channel
   ↓
Audio Consumer
```

如果 consumer 跟唔上：

### Video

可有限度 drop intermediate frames。

### Audio

唔應一般性 drop。

Audio queue pressure 係 reliability issue，應發 warning / abort，而唔係 silent loss。

---

# 34. Backpressure Policy

優先順序：

```text
Audio integrity
>
video continuity
>
preview smoothness
>
UI refresh
```

如果 system overloaded：

1. disable/reduce preview；
2. reduce nonessential UI updates；
3. allow video frame dropping；
4. preserve audio；
5. if encoder cannot sustain recording, fail explicitly。

---

# 35. Encoding and Media Writer

`IMediaWriter`：

```csharp
public interface IMediaWriter : IAsyncDisposable
{
    Task InitializeAsync(
        MediaWriterConfiguration configuration,
        CancellationToken cancellationToken);

    ValueTask WriteVideoAsync(
        EncodedOrRawVideoFrame frame,
        CancellationToken cancellationToken);

    ValueTask WriteAudioAsync(
        AudioFrame frame,
        CancellationToken cancellationToken);

    ValueTask AdvanceVideoWatermarkAsync(
        TimeSpan safeThrough,
        CancellationToken cancellationToken);

    Task FinalizeAsync(
        CancellationToken cancellationToken);
}
```

During active recording, the application supplies this watermark from the canonical
`RecordingClock` after the bounded video reorder allowance has elapsed. The media writer may
repeat the last committed video frame only for CFR slots strictly before that watermark; audio
timestamps must not advance the watermark directly. Finalization remains the authoritative point
for extending the remaining static video tail to the canonical recording end.

首個 implementation：

```text
FfmpegMediaWriter
```

FFmpeg 可以以 bundled binary 或 managed process wrapper 方式使用。

Core 不依賴 FFmpeg。

---

# 36. Proposed MVP Media Format

Product-level contract：

```text
recording.mp4
```

SDD 建議初始 defaults：

```text
Container:
MP4

Video:
H.264

Audio:
AAC

Audio sample rate:
48 kHz

Audio mode:
mixed

Frame rate target:
30 fps
```

但：

> codec/profile 屬 implementation configuration，唔應寫死入 Evidence Bundle schema version 1。

---

# 37. Recoverable Recording Strategy

直接持續寫普通 MP4 有一個 reliability 問題：

> process crash 前如果 container 未 finalize，可能失去重要 metadata/index。

因此設計採用：

```text
Capture
   ↓
Recoverable work media
   ↓
normal Stop
   ↓
Finalize / remux
   ↓
recording.mp4
```

Session recording 期間：

```text
.work/
└── recording.partial.*
```

正常完成：

```text
.work/recording.partial.*
        ↓
validation
        ↓
recording.mp4
```

之後刪除 work media。

---

# 38. Recovery Work Format

MVP 不喺 SDD v0.1 鎖死：

```text
Matroska
fragmented MP4
segmented MP4
```

Engineering spike 必須比較：

* crash recoverability；
* FFmpeg support；
* disk overhead；
* final remux cost；
* A/V timestamp preservation；
* Windows/macOS consistency。

Required behavior：

> work artifact 即使未 clean finalize，亦應盡量可 recover。

---

# 39. Evidence Bundle Layout

Completed：

```text
meeting-20260909-103000/
│
├── recording.mp4
├── session.json
├── events.jsonl
│
└── screenshots/
    ├── shot_00-03-18.420.png
    ├── shot_00-12-41.083.png
    └── ...
```

Active/incomplete session 可以額外：

```text
.work/
logs/
```

例如：

```text
meeting-20260909-103000/
│
├── session.json
├── events.jsonl
├── screenshots/
├── .work/
│   └── recording.partial.mkv
└── logs/
    └── recorder.log
```

Completed session 可以選擇移除 `.work/`。

---

# 40. Atomic File Strategy

`session.json` 更新使用：

```text
write session.json.tmp
fsync where appropriate
atomic replace
```

`events.jsonl`：

```text
append one complete JSON line
flush periodically
```

Screenshot：

```text
write .tmp
close
rename to final .png
append event only after successful rename
```

因此：

> `events.jsonl` 不應引用一張尚未成功寫完嘅 screenshot。

---

# 41. Event Journal

MVP：

```json
{"event_id":"evt-000001","type":"screenshot","timestamp_ms":198420,"asset":"screenshots/shot_00-03-18.420.png"}
```

內部 schema：

```csharp
public abstract record RecordingEvent(
    string EventId,
    TimeSpan Timestamp
);
```

Screenshot：

```csharp
public sealed record ScreenshotEvent(
    string EventId,
    TimeSpan Timestamp,
    string AssetPath
) : RecordingEvent(EventId, Timestamp);
```

---

# 42. Event Ordering

Event journal 必須：

```text
monotonically ordered by append time
```

但 event timestamp 理論上可以相同。

Processor 不應假設：

```text
timestamp unique
```

Identity 由：

```text
event_id
```

決定。

---

# 43. Session Manifest

Recommended `session.json`：

```json
{
  "schema_version": "1.0",
  "session_id": "7e177...", 
  "created_at": "2026-09-09T10:30:00+08:00",
  "duration_ms": 3675421,
  "status": "completed",

  "recording": {
    "file": "recording.mp4",

    "video": {
      "source_type": "display",
      "width": 2560,
      "height": 1440,
      "fps": 30
    },

    "audio": {
      "system_audio": true,
      "microphone": true,
      "microphone_device": "MacBook Microphone",
      "output_mode": "mixed",
      "sample_rate": 48000,
      "system_audio_capture_mode": "os_native",
      "synchronized_to_recording_timeline": true
    }
  },

  "events_file": "events.jsonl",

  "application": {
    "name": "Meeting Evidence Recorder",
    "version": "0.1.0"
  },

  "platform": {
    "os": "macOS",
    "architecture": "arm64"
  }
}
```

---

# 44. Manifest Status

Allowed v1 states：

```text
initializing
recording
paused
finalizing
completed
incomplete
failed
```

Evidence consumer 必須：

```text
status == completed
```

先視為完整 bundle。

MRP 可以 optional support：

```text
incomplete
```

作 recovery/import。

---

# 45. Schema Versioning

Evidence Bundle 使用獨立 semantic schema version：

```text
schema_version
```

唔應直接等同 app version。

例如：

```text
App 1.5
still writes schema 1.0
```

Breaking schema change：

```text
2.0
```

Backward-compatible field：

```text
仍可以保持 1.x
```

MRP 必須對 unknown major version fail clearly。

---

# 46. Permissions Architecture

```csharp
public interface IPermissionService
{
    Task<PermissionStatus> GetScreenCaptureStatusAsync();

    Task<PermissionStatus> GetMicrophoneStatusAsync();

    Task RequestScreenCaptureAsync();

    Task RequestMicrophoneAsync();

    Task OpenSystemSettingsAsync(
        PermissionKind permission);
}
```

Shared UI 只識：

```text
Granted
Denied
Restricted
NotDetermined
Unsupported
```

---

# 47. Permission Preflight

Start 前：

```text
Screen capture permission
       ↓
System audio capability
       ↓
Microphone permission if enabled
```

Recording UI 必須先顯示：

```text
Screen: Ready
System Audio: Ready
Microphone: Ready
```

再容許 normal Start。

---

# 48. Platform Capability Model

每個 backend startup 時產生：

```csharp
public sealed record PlatformCapabilities(
    bool SupportsDisplayCapture,
    bool SupportsWindowCapture,
    bool SupportsSystemAudio,
    bool SupportsMicrophone,
    bool SupportsGlobalHotkey
);
```

不得 hard-code：

```text
OS == Windows → everything supported
```

Runtime capability check 仍然需要。

Windows.Graphics.Capture 本身亦提供 capture support check。

---

# 49. Error Model

所有重大 runtime error 使用 structured type：

```csharp
public sealed record RecorderError(
    string Code,
    RecorderErrorSeverity Severity,
    string UserMessage,
    string DiagnosticMessage,
    Exception? Exception
);
```

Severity：

```text
Info
Warning
Recoverable
Fatal
```

---

# 50. Error Codes

例如：

```text
CAPTURE_SCREEN_PERMISSION_DENIED
CAPTURE_SCREEN_UNAVAILABLE

AUDIO_SYSTEM_UNAVAILABLE
AUDIO_SYSTEM_DEVICE_LOST

AUDIO_MIC_PERMISSION_DENIED
AUDIO_MIC_DEVICE_LOST

AUDIO_SYNC_DRIFT_EXCEEDED

MEDIA_ENCODER_FAILED
MEDIA_MUX_FAILED

DISK_SPACE_LOW
DISK_WRITE_FAILED

SCREENSHOT_WRITE_FAILED

HOTKEY_REGISTRATION_FAILED
```

UI 唔應直接顯示 raw exception。

---

# 51. Partial Failure Policy

## Microphone fails

如果 system audio + video 正常：

```text
continue allowed
```

但：

```text
Microphone: Failed
```

session metadata 必須反映。

---

## System audio fails before recording

Default：

```text
block normal Start
```

使用者可以明確選：

```text
Continue without system audio
```

先開始。

---

## Screenshot fails

```text
recording continues
```

---

## Media writer fails

```text
fatal
```

因為再錄落去已經冇可靠 output。

---

# 52. Disk Space

Start 前：

* verify output path writable；
* query free space；
* warn below configurable threshold。

Recording 期間：

```text
periodic free-space monitor
```

Threshold 建議：

```text
Warning threshold
Critical threshold
```

到 critical：

> graceful stop 優先於寫到 disk full 先 crash。

---

# 53. Session Recovery

Application startup 時掃描 output root：

```text
status != completed
+
.work exists
```

標示：

```text
Recoverable Session
```

RecoveryService 可以嘗試：

1. inspect partial media；
2. repair/remux；
3. validate duration；
4. preserve events；
5. preserve screenshots；
6. create recovered MP4；
7. set `status = incomplete` 或 `completed_recovered`。

`completed_recovered` 是否加入 public schema 留待 Phase 0 contract 決定。

---

# 54. UI Architecture

採用 MVVM：

```text
View
 ↓
ViewModel
 ↓
Application Service
 ↓
Core
```

View 不應：

* directly start ScreenCaptureKit；
* directly call WASAPI；
* write session.json；
* manage FFmpeg process。

---

# 55. Main ViewModel

Responsibilities：

```text
Capture source selection
Microphone selection
Microphone enabled state
System audio readiness
Hotkey configuration
Output directory
Start command
Permission status
```

---

# 56. Recording ViewModel

Expose：

```text
Recording duration
Recording state
System audio status
Microphone status
Screenshot count
Pause
Resume
Stop
```

UI refresh timer：

```text
~4–10 Hz
```

唔需要跟 frame rate refresh。

---

# 57. System Tray / Menu Bar

Shared abstraction：

```csharp
public interface IRecorderShellService
{
    void ShowRecordingState(...);

    void ShowScreenshotCaptured(...);

    void ShowAudioError(...);
}
```

Platform-specific UI integration 可以由 Avalonia capability + native fallback 實現。

---

# 58. Configuration

User settings 建議：

```json
{
  "output_directory": "...",
  "microphone_enabled": true,
  "preferred_microphone_id": "...",
  "screenshot_hotkey": "...",
  "video": {
    "fps": 30
  }
}
```

不得保存：

* meeting content；
* participant information；
* recording filenames history；

除非未來 product requirement 明確加入。

---

# 59. Logging

Local diagnostic log：

```text
logs/
└── recorder.log
```

可記錄：

```text
2026-09-09T10:30:00 session created
10:30:00 screen backend ready
10:30:00 system audio active
10:30:00 microphone active
10:34:18 screenshot evt-0001
...
```

不得 log：

```text
audio content
video frame content
OCR
meeting transcript
window text
screen contents
```

---

# 60. Metrics

純 local diagnostics 可計：

```text
video frames captured
video frames encoded
video frames dropped

system audio frames
microphone frames

max audio queue depth
measured drift

screenshots requested
screenshots written
screenshots failed

encoder duration
finalization duration
```

MVP 不上傳 metrics。

---

# 61. Security and Privacy

Default：

```text
No network required
```

Recorder runtime 不應：

* call cloud service；
* auto upload；
* send screenshots；
* send filenames；
* send meeting metadata。

Dependencies 亦應審核，避免 telemetry SDK。

---

# 62. File Permissions

Evidence Bundle 應使用 current user normal file permissions。

不需要：

* admin；
* root；
* system service。

Native capture permission 只要求 OS 明確指定嘅 user consent。

---

# 63. Process Model

MVP：

```text
Single desktop process
```

唔需要：

* Windows service；
* macOS helper daemon；
* privileged helper；
* background server。

FFmpeg 如果使用 executable：

```text
child process
```

由 Recorder lifecycle 管理。

---

# 64. FFmpeg Process Management

如果採 external process：

Recorder 必須：

* locate bundled executable；
* pass pipes securely；
* monitor exit code；
* capture diagnostics；
* terminate on abort；
* avoid shell interpolation；
* use argument list API。

不得：

```text
cmd.exe /c arbitrary-string
```

或：

```text
/bin/sh -c arbitrary-string
```

---

# 65. Packaging

## Windows

Target：

```text
win-x64
self-contained
```

.NET self-contained deployment 會將所需 runtime 隨 application 一齊發布，使用者唔需要預先安裝 matching .NET runtime。

Artifact：

```text
MeetingEvidenceRecorder-win-x64.zip
```

---

## macOS

Target：

```text
osx-arm64
self-contained
.app bundle
```

Artifact：

```text
Meeting Evidence Recorder.app
```

正式 distribution：

```text
code signing
notarization
```

---

# 66. Native AOT

MVP：

```text
Not required
```

優先：

```text
normal self-contained .NET
```

原因：

* native interop 已複雜；
* capture pipeline validation 優先；
* AOT 對 long-running recorder startup benefit 有限。

日後另開 optimization ADR。

---

# 67. Dependency Policy

Third-party dependencies 應遵循：

1. actively maintained；
2. compatible with .NET 10；
3. suitable license；
4. no mandatory cloud service；
5. no hidden telemetry；
6. no unnecessary native runtime；
7. platform abstraction ownership清楚。

---

# 68. Testing Strategy

Testing 分五層：

```text
Unit
Component
Platform
Integration
End-to-End
```

---

# 69. Unit Tests

Pure .NET components：

### RecordingClock

* monotonic；
* pause；
* resume；
* elapsed；
* double pause；
* stop。

### EventJournal

* ordering；
* JSONL；
* duplicate timestamps；
* atomic writes。

### SessionManifest

* completed；
* incomplete；
* disabled microphone；
* failed system audio。

### Audio

* mono → stereo；
* sample conversion；
* mixer；
* drift model。

---

# 70. Timeline Tests

建立 deterministic fake sources：

```text
Fake video: 30 fps
Fake system audio: 48 kHz
Fake mic: 48 kHz
```

模擬：

```text
T=0 start
T=10.000 screenshot
T=20 pause
5 sec wall time
resume
T=25 screenshot
```

驗證：

```text
final timestamp excludes paused duration
```

---

# 71. Audio Synchronization Tests

模擬 microphone clock：

```text
+50 ppm drift
```

System audio：

```text
-30 ppm drift
```

驗證：

* drift detected；
* correction bounded；
* long recording 不累積成 seconds-level error。

---

# 72. Screenshot Tests

驗證：

```text
hotkey event at 10.050
latest frame at 10.033

screenshot timestamp == 10.033
```

而唔係：

```text
10.050
```

---

# 73. Crash Tests

強制 terminate：

```text
5 min
30 min
60 min
```

驗證：

* event journal readable；
* screenshots intact；
* partial media exists；
* session not completed；
* recovery path detectable。

---

# 74. Platform Tests — Windows

Minimum：

* Windows 10 supported baseline；
* Windows 11；
* one display；
* multi-display；
* default speakers；
* USB headset；
* Bluetooth audio；
* default microphone；
* microphone disconnected during recording；
* system audio device switch。

---

# 75. Platform Tests — macOS

Minimum：

```text
Apple Silicon
```

測試：

* Screen Recording permission granted；
* permission denied；
* microphone permission granted；
* denied；
* built-in audio；
* AirPods/headset；
* external microphone；
* multi-display；
* display sleep/wake；
* active window switching。

---

# 76. End-to-End Acceptance Fixture

建立一條 deterministic test session：

```text
0:00 recording starts
0:10 tone A
0:15 spoken test phrase
0:20 screenshot marker
0:30 tone B
0:40 screenshot marker
1:00 stop
```

輸出：

```text
recording.mp4
session.json
events.jsonl
screenshots/
```

交畀 MRP：

* extract audio；
* ASR；
* SRT；
* align screenshot；
* validate expected timestamps。

---

# 77. Performance Targets

MVP engineering targets：

### Recording duration

```text
120 min
```

正常支援。

### Screenshot

Hotkey 本身不可 block capture path。

### Memory

不保存整段 raw recording。

### Frame buffering

small bounded queue。

### Audio

streaming only。

---

# 78. Performance Measurement

Benchmark：

```text
1080p30
1440p30
4K30 where feasible
```

觀察：

* CPU；
* GPU；
* memory；
* encoded bitrate；
* dropped frames；
* audio drift；
* screenshot latency。

4K support 是否成為 formal MVP requirement 由後續 acceptance 決定。

---

# 79. Recording Quality Profiles

MVP 可只提供一個預設 profile：

```text
Meeting
30 fps
H.264
AAC
48 kHz
```

內部 config 應允許未來：

```text
Low bandwidth
Standard
High quality
Software demo
```

但第一版 UI 唔需要 expose。

---

# 80. Source Change Policy

MVP recording 中：

> capture source 不允許動態切換。

如果 selected display/source 消失：

```text
warning / fatal
```

而唔自動切去另一 display。

避免 output evidence provenance 模糊。

---

# 81. Device Change Policy

Microphone disconnect：

```text
continue system audio
mark microphone failed
```

System audio endpoint change：

Implementation 可嘗試 reconnect。

如果無法可靠 reconnect：

```text
visible warning
metadata update
```

不得 silent switch 而不記錄。

---

# 82. Concurrency Model

主要 logical workers：

```text
UI thread

Screen capture callback/thread
System audio capture thread
Microphone capture thread

Video processing worker
Audio synchronization worker
Audio mixing worker
Media writer worker

Screenshot writer worker
Event journal writer
```

Workers 之間透過：

```text
Channel<T>
```

或 equivalent bounded async queue。

---

# 83. Cancellation

整個 recording session 使用 root：

```text
CancellationTokenSource
```

但 graceful stop 同 abort 要分開。

### Graceful Stop

```text
stop producing
drain queues
finalize
```

### Abort

```text
cancel immediately
preserve recoverable files
mark incomplete
```

---

# 84. Resource Ownership

每個 native resource 必須 deterministic dispose：

```text
capture sessions
audio clients
native buffers
graphics textures
FFmpeg pipes/process
file streams
hotkeys
```

優先使用：

```text
IAsyncDisposable
```

確保 shutdown order 正確。

---

# 85. Shutdown Ordering

正常：

```text
Disable new hotkey events
        ↓
Stop capture producers
        ↓
Drain queues
        ↓
Stop audio mixer
        ↓
Flush encoder
        ↓
Close muxer
        ↓
Finalize media
        ↓
Finalize manifest
```

唔可以：

```text
kill encoder
then stop capture
```

---

# 86. Evidence Bundle Validation

Finalize 後執行：

```text
recording.mp4 exists
recording.mp4 readable
duration > 0

session.json valid
events.jsonl valid

every screenshot event asset exists
every screenshot asset is inside bundle

timestamps >= 0
timestamps <= recording duration + tolerance
```

失敗：

```text
status != completed
```

---

# 87. Path Security

Bundle path 只允許 relative paths：

```text
screenshots/shot_...
```

禁止：

```text
../../../...
/absolute/path
C:\...
```

MRP 亦可以安全 consume bundle。

---

# 88. MRP Import Contract

MRP 應以 directory 為 input：

```text
mrp extract /path/to/meeting-20260909-103000/
```

Importer：

```text
detect session.json
        ↓
validate schema
        ↓
locate recording
        ↓
read events
        ↓
extract audio
        ↓
ASR
        ↓
align visual evidence
```

---

# 89. Timeline Alignment Contract

MRP 不需要重新猜 screenshot timestamp。

直接：

```text
events.jsonl.timestamp_ms
```

例如：

```text
SRT segment:
748200 → 761700

Screenshot:
754083
```

判斷：

```text
748200 <= 754083 <= 761700
```

則 visual evidence attach 到該 segment。

---

# 90. Screenshot Provenance

MRP merge 後：

```json
{
  "type": "manual_capture",
  "source_event_id": "evt-00023",
  "timestamp_ms": 754083,
  "file": "screenshots/shot_00-12-34.083.png"
}
```

不得將 manual screenshot 混淆成：

```text
automatic_keyframe
```

---

# 91. Architecture Decision Records

建議 repo 建立：

```text
docs/adr/
```

首批：

```text
ADR-001-shared-dotnet-native-platform-backends.md
ADR-002-authoritative-recording-timeline.md
ADR-003-mixed-audio-mvp.md
ADR-004-evidence-bundle-schema.md
ADR-005-recoverable-media-workfile.md
ADR-006-screenshot-from-recording-frame.md
ADR-007-ffmpeg-media-layer.md
```

---

# 92. Key Design Decisions

## D1 — .NET 10 + Avalonia

Accepted。

理由：

* shared desktop core；
* Windows/macOS；
* self-contained distribution；
* C# domain model；
* native backend 可隔離。

.NET 10 目前係 LTS，支援期至 2028 年 11 月。

---

## D2 — Native capture APIs

Accepted。

Windows：

```text
Windows.Graphics.Capture
WASAPI
```

macOS：

```text
ScreenCaptureKit
```

不採用 generic FFmpeg desktop capture 作主要 capture abstraction。

---

## D3 — Mixed audio output in MVP

Accepted。

Internal pipeline 保留 separate source identity。

Final MVP：

```text
mixed audio
```

Future：

```text
separate tracks optional
```

---

## D4 — Screenshot from recording frame

Accepted。

避免獨立 OS screenshot API 同 recording frame timestamp mismatch。

---

## D5 — JSONL event journal

Accepted。

理由：

* appendable；
* crash-friendly；
* extensible；
* human-readable。

---

## D6 — Recoverable intermediate media

Accepted principle。

實際 work container format：

```text
TBD engineering spike
```

---

# 93. Explicit Non-Decisions

SDD v0.1 暫時唔鎖：

1. exact video bitrate；
2. exact H.264 profile；
3. FFmpeg bundled version；
4. work container係 MKV 定 fragmented MP4；
5. exact default screenshot hotkey；
6. Windows ARM64；
7. Intel macOS；
8. window capture MVP timing；
9. audio level meter；
10. separate track release timing。

---

# 94. Phase 0 — Contract Foundation

第一個 engineering phase 唔錄真正 meeting。

Deliverables：

```text
Solution scaffold
Core models
Interfaces
RecordingClock
Session state machine
Evidence Bundle schema
Event journal
Manifest
Fake capture sources
Fake media writer
Recovery abstraction
Tests
```

---

# 95. Phase 0 Acceptance

必須可以完全用 fake backend 跑：

```text
Start
 ↓
fake video/audio
 ↓
screenshot event
 ↓
Pause
 ↓
Resume
 ↓
Stop
 ↓
Evidence Bundle
```

並驗證：

```text
session.json
events.jsonl
timeline semantics
```

先進入 native capture。

---

# 96. Phase 1 — Windows MVP

Deliverables：

```text
Windows.Graphics.Capture
WASAPI system audio
microphone
audio normalization
mixer
media writer
global hotkey
screenshot
portable build
```

---

# 97. Phase 2 — macOS MVP

Deliverables：

```text
ScreenCaptureKit
system audio
microphone
permissions
global hotkey
screenshot
.app packaging
```

ScreenCaptureKit 現行 API 已提供 screen、audio 以及 microphone-related configuration，適合呢個統一 native backend 方向。

---

# 98. Phase 3 — MRP Integration

MRP 增加：

```text
EvidenceBundleImporter
```

不得令 Recorder reference MRP package。

Dependency：

```text
Recorder ──X──► MRP
```

只透過 filesystem contract integration。

---

# 99. Repository Documentation

建議：

```text
README.md
docs/
├── PRD.md
├── SDD.md
├── EVIDENCE_BUNDLE_SPEC.md
├── DEVELOPMENT.md
├── TESTING.md
└── adr/
```

其中下一份最重要文件：

```text
EVIDENCE_BUNDLE_SPEC.md
```

將 schema 同 app implementation 完全分離。

---

# 100. Risks

## R1 — Cross-platform audio timing

Windows/macOS native timestamp semantics 不完全一致。

Mitigation：

```text
platform timestamp adapter
+
shared normalized recording timeline
+
long-run drift tests
```

---

## R2 — Native interop complexity

Avalonia shared UI 唔會消除 native capture complexity。

Mitigation：

```text
strict platform interfaces
+
native code isolated in platform projects
```

---

## R3 — Crash-corrupted media

Mitigation：

```text
recoverable work media
+
append-only event journal
+
final MP4 only after validation
```

---

## R4 — Screenshot stalls encoder

Mitigation：

```text
latest-frame lease
+
async PNG writer
```

---

## R5 — Audio device changes

Mitigation：

```text
device state monitoring
+
explicit degradation
+
session metadata
```

---

## R6 — Long recording drift

Mitigation：

```text
timestamp-based synchronization
+
bounded buffering
+
drift measurement
+
integration benchmark
```

---

# 101. Definition of Done — Recorder MVP Architecture

Architecture 可視為完成，當以下均成立：

* [ ] shared Core 無 Windows/macOS dependency；
* [ ] Windows/macOS capture backend 經 interface 注入；
* [ ] authoritative RecordingClock 有 automated tests；
* [ ] pause/resume semantics deterministic；
* [ ] system audio + microphone 有 separate internal streams；
* [ ] mixed audio output 同 video synchronized；
* [ ] screenshot 使用 recording-frame timestamp；
* [ ] `events.jsonl` recording 期間 append；
* [ ] interrupted session 唔會標示 completed；
* [ ] media 有 recovery strategy；
* [ ] completed bundle 有 `recording.mp4`；
* [ ] Evidence Bundle validator 通過；
* [ ] MRP 可只依賴 filesystem contract import；
* [ ] 60–120 分鐘 recording integration test 無顯著 drift；
* [ ] Windows 不要求 virtual audio cable；
* [ ] macOS 不要求 virtual audio device；
* [ ] meeting content 不經 network 傳送。

---

# 102. Final Architecture Summary

Meeting Evidence Recorder 採用：

```text
.NET 10
+
Avalonia UI
+
Shared Recorder Core
+
Native Windows/macOS Capture Backends
+
Single Authoritative Recording Timeline
+
Separate Internal System/Microphone Streams
+
Synchronized Mixed Audio MVP
+
FFmpeg-compatible Media Writer
+
Recording-frame Screenshot Capture
+
Append-only Evidence Events
+
Recoverable Work Media
+
Self-contained Evidence Bundle
```

核心 runtime flow：

```text
             ┌── Screen Capture ──► Video Frames ──────────────┐
             │                                                  │
             │                                ┌──► Screenshot   │
             │                                │                 │
Recording ───┼── System Audio ─► Normalize ───┐                 │
Clock        │                                ├─► Sync ─► Mix   │
             └── Microphone ───► Normalize ───┘          │      │
                                                          │      │
                                                          ▼      ▼
                                                     Media Writer
                                                          │
                                                          ▼
                                                Recoverable Work Media
                                                          │
                                                       Stop
                                                          │
                                                          ▼
                                                    recording.mp4

Hotkey
  │
  ▼
Latest Recording Frame
  │
  ├──► PNG
  │
  └──► events.jsonl

                    ↓

            Meeting Evidence Bundle

                    ↓

          Meeting Recording Processor
```

整個 architecture 最重要嘅 invariant 係：

> **任何一項 evidence，只要聲稱發生於 recording 某一 timestamp，就必須可以用同一條 canonical media timeline 同其他 evidence 對齊。**

Recorder 嘅責任係可靠捕捉 evidence。

MRP 嘅責任係理解同整合 evidence。

Document Agent 嘅責任係將 evidence 轉化成可讀文件。

三層之間以穩定、可追溯、processor-independent 嘅 Evidence Bundle contract 連接。
