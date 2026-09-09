# Meeting Evidence Bundle Specification

**文件版本：** v0.1
**日期：** 2026-09-09
**狀態：** Draft
**Schema Major Version：** 1
**相關文件：**

* `PRD.md`
* `SDD.md`
* Meeting Recording Processor documentation

---

# 1. Purpose

Meeting Evidence Bundle 係 Meeting Evidence Recorder 同 Meeting Recording Processor（MRP）之間嘅正式 filesystem contract。

Bundle 必須能夠：

* 喺 Recorder 完全停止後獨立存在；
* copy 去另一個 directory；
* copy 去另一部機；
* archive；
* backup；
* hand off 畀 MRP；
* 喺無 Recorder installed 嘅情況下讀取；
* 喺唔依賴 Windows/macOS-specific API 嘅情況下處理。

Bundle consumer 唔應需要知道 recording 當時係由：

* Windows；
* macOS；
* Avalonia；
* .NET；
* FFmpeg；
* ScreenCaptureKit；
* Windows.Graphics.Capture；

邊一種 implementation 產生。

---

# 2. Scope

本 specification 定義：

1. bundle directory layout；
2. `session.json`；
3. `events.jsonl`；
4. screenshot asset rules；
5. media file contract；
6. timestamp semantics；
7. status semantics；
8. path rules；
9. schema versioning；
10. producer requirements；
11. consumer requirements；
12. validation rules；
13. incomplete/recovery semantics；
14. forward compatibility rules。

本 specification 不定義：

* Recorder UI；
* ASR；
* OCR；
* subtitle format；
* meeting note format；
* native capture implementation；
* FFmpeg invocation；
* screenshot hotkey；
* exact codec preset；
* encoder bitrate。

---

# 3. Design Principles

## 3.1 Bundle Is Self-Contained

所有 required artifact 必須存在 bundle directory 內。

不得要求 consumer 去：

* registry；
* app settings；
* external database；
* recorder cache；
* operating-system metadata；

先可以理解 bundle。

---

## 3.2 Relative Paths Only

Bundle metadata 內所有 asset path 必須係相對 bundle root 嘅 relative path。

Allowed：

```text
recording.mp4
screenshots/shot_00-12-34.083.png
```

Forbidden：

```text
/Users/user/meeting/recording.mp4
C:\Users\User\recording.mp4
../../../secret.txt
```

---

## 3.3 One Canonical Timeline

所有 timestamp 都使用同一條：

> **Canonical Recording Timeline**

定義：

> Timestamp 表示 final recording media 嘅 playback position。

因此：

```text
timestamp 00:10:00
```

代表：

> final recording 播放至第 10 分鐘嘅位置。

---

## 3.4 Wall Clock Is Metadata Only

例如：

```json
"created_at": "2026-09-09T10:30:00+08:00"
```

只表示 session 發生嘅實際日期時間。

唔可以用作：

* screenshot alignment；
* audio alignment；
* SRT alignment。

---

## 3.5 Raw Evidence Must Remain Traceable

Manual screenshot：

```text
manual evidence
```

不得喺 bundle level 被改寫成：

```text
OCR result
LLM interpretation
document summary
```

Derived artifacts 應由 MRP 或後續 pipeline 另行產生。

---

# 4. Bundle Root

每一個 meeting session 係一個 directory。

Recommended naming：

```text
meeting-YYYYMMDD-HHMMSS/
```

Example：

```text
meeting-20260909-103000/
```

Directory name：

* 方便人類識別；
* 唔係 canonical session identity；
* consumer 不得依賴 directory name parsing。

真正 identity：

```text
session_id
```

---

# 5. Completed Bundle Layout

Schema v1 minimum completed bundle：

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
    └── shot_00-42-19.114.png
```

Required：

```text
session.json
events.jsonl
recording media
```

`screenshots/`：

* 可以存在但為空；
* 如果 session 完全冇 screenshot event，consumer 不應視為 error。

---

# 6. Optional Producer Working Files

Recording active 或 interrupted session 可以包含：

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

Consumer：

* 不應依賴 `.work/`；
* 不應依賴 `logs/`；
* completed bundle 可以完全冇呢啲 directory。

---

# 7. Required Files

## 7.1 `session.json`

用途：

> 描述 session、recording、audio/video configuration、bundle status 同 producer metadata。

---

## 7.2 `events.jsonl`

用途：

> 保存 recording timeline 上發生嘅 discrete evidence events。

Schema v1 主要 event：

```text
screenshot
```

---

## 7.3 Recording Media

Schema v1 standard filename：

```text
recording.mp4
```

實際 path 仍應由：

```json
recording.file
```

指定。

Consumer 應以 metadata 為 authoritative source。

---

# 8. Encoding Rules

所有 JSON / JSONL：

```text
UTF-8
```

不得依賴：

```text
UTF-16
system code page
locale-specific encoding
```

JSON files 不需要 BOM。

Recommended：

```text
UTF-8 without BOM
```

---

# 9. JSON Naming Convention

Schema v1 使用：

```text
snake_case
```

Example：

```json
{
  "schema_version": "1.0",
  "session_id": "...",
  "created_at": "...",
  "duration_ms": 1234
}
```

---

# 10. `session.json` Top-Level Schema

Required top-level fields：

```json
{
  "schema_version": "1.0",
  "session_id": "UUID",
  "created_at": "RFC3339 timestamp",
  "duration_ms": 3675421,
  "status": "completed",
  "recording": {},
  "events_file": "events.jsonl",
  "application": {},
  "platform": {}
}
```

---

# 11. `schema_version`

Type：

```text
string
```

Example：

```json
"schema_version": "1.0"
```

格式：

```text
MAJOR.MINOR
```

Example：

```text
1.0
1.1
2.0
```

Consumer 必須：

* parse major；
* unknown major → fail clearly；
* known major + newer minor → attempt forward-compatible parsing。

---

# 12. `session_id`

Type：

```text
string
```

Recommended：

```text
UUID
```

Example：

```json
"session_id": "7e177f69-1d91-43ef-b554-d9529148f121"
```

Requirements：

* 每次 recording session 唯一；
* directory rename 不改 session ID；
* copy bundle 不改 session ID。

---

# 13. `created_at`

Type：

```text
string
```

Format：

```text
RFC 3339 / ISO 8601 with timezone offset
```

Example：

```json
"created_at": "2026-09-09T10:30:00+08:00"
```

不得：

```text
omit timezone
```

Recommended precision：

```text
seconds or milliseconds
```

---

# 14. `duration_ms`

Type：

```text
integer
```

Unit：

```text
milliseconds
```

Meaning：

> canonical final media playback duration。

Example：

```json
"duration_ms": 3675421
```

Pause period 不包括在內。

If status：

```text
recording
initializing
```

`duration_ms` 可以：

```text
null
```

但 `completed` 必須係 non-null positive integer。

---

# 15. `status`

Schema v1 allowed values：

```text
initializing
recording
paused
finalizing
completed
incomplete
failed
```

---

# 16. Status Semantics

## `initializing`

Session directory 已建立，但 capture 尚未完全開始。

---

## `recording`

Recording actively producing media。

---

## `paused`

Recording session 存在，但 canonical media timeline 暫停。

---

## `finalizing`

Capture 已停止，但 media/container/bundle 仲未完成 finalization。

---

## `completed`

所有 required final artifacts 已成功：

* written；
* finalized；
* validated。

只有 `completed` bundle 可以被 consumer 默認視為完整 input。

---

## `incomplete`

Session 曾開始 recording，但未能正常完成。

可能仍有：

* partial media；
* valid screenshot；
* valid events。

---

## `failed`

Session initialization 或 runtime failure，無法形成正常 recording。

---

# 17. `recording`

Required object：

```json
{
  "file": "recording.mp4",
  "video": {},
  "audio": {}
}
```

---

# 18. `recording.file`

Type：

```text
string
```

Required：

```text
completed
```

Example：

```json
"file": "recording.mp4"
```

Requirements：

* relative path；
* bundle-local；
* consumer 必須 resolve relative bundle root；
* 不得有 path traversal。

---

# 19. Video Metadata

Recommended schema：

```json
{
  "source_type": "display",
  "width": 2560,
  "height": 1440,
  "fps": 30
}
```

---

# 20. `video.source_type`

Schema v1 known values：

```text
display
window
region
```

MVP producer：

```text
display
```

Consumer 對 unknown future value：

```text
preserve / ignore unless operationally required
```

唔應因為新 source type 就拒絕整個 bundle。

---

# 21. Video Dimensions

```json
"width": 2560,
"height": 1440
```

Type：

```text
positive integer
```

Unit：

```text
pixels
```

---

# 22. `fps`

Type 可以係：

```text
integer
number
```

Example：

```json
"fps": 30
```

或者：

```json
"fps": 29.97
```

Consumer 不應用呢個 field 自己重新計 timeline。

真正 timestamps 應以 media PTS / bundle timestamp 為準。

---

# 23. Audio Metadata

Schema v1 recommended：

```json
{
  "system_audio": true,
  "microphone": true,
  "microphone_device": "MacBook Microphone",
  "output_mode": "mixed",
  "sample_rate": 48000,
  "system_audio_capture_mode": "os_native",
  "synchronized_to_recording_timeline": true
}
```

---

# 24. `system_audio`

Type：

```text
boolean
```

Meaning：

> final recording 是否實際包含 successfully captured system audio。

唔係：

> user originally requested system audio。

如果 capture failed：

```json
"system_audio": false
```

---

# 25. `microphone`

Type：

```text
boolean
```

Meaning：

> final recording 是否實際包含 microphone audio。

如果 user disable：

```json
"microphone": false
```

---

# 26. `microphone_device`

Type：

```text
string | null
```

Example：

```json
"microphone_device": "MacBook Microphone"
```

如果：

* disabled；
* unavailable；

則：

```json
"microphone_device": null
```

此 field 只作 provenance / diagnostics。

Consumer 不應依賴 device name 作 processing。

---

# 27. `output_mode`

Schema v1 known values：

```text
mixed
system_audio_only
microphone_only
none
```

Future：

```text
separate
mixed_and_separate
```

---

# 28. `mixed`

Meaning：

```text
system audio
+
microphone
→ one output audio track
```

---

# 29. `system_audio_only`

Meaning：

final recording audio track 只包含 system audio。

---

# 30. `microphone_only`

Meaning：

final recording audio track 只包含 microphone。

---

# 31. `none`

Meaning：

final recording 沒有 usable audio track。

Completed bundle technically可以存在：

```text
video-only
```

但 producer 必須明確寫：

```json
"output_mode": "none"
```

---

# 32. `sample_rate`

Type：

```text
integer | null
```

Unit：

```text
Hz
```

Recommended MVP：

```json
"sample_rate": 48000
```

如果 final recording 冇 audio：

```json
"sample_rate": null
```

---

# 33. `system_audio_capture_mode`

Schema v1：

```text
os_native
```

Example：

```json
"system_audio_capture_mode": "os_native"
```

Future 可能：

```text
virtual_device
external
unknown
```

但 MVP producer 應使用：

```text
os_native
```

---

# 34. `synchronized_to_recording_timeline`

Type：

```text
boolean
```

Completed normal session：

```json
true
```

如果 producer 知道 audio 存在重大 synchronization failure：

```json
false
```

Consumer 應：

* 可讀取；
* 顯示 warning；
* 不假設 ASR timestamps 完全可靠。

---

# 35. Optional Audio Diagnostics

Producer 可以加入：

```json
"diagnostics": {
  "max_observed_drift_ms": 12.4,
  "sync_warning": false
}
```

Consumer：

* 不應要求此 field；
* 不應因缺少就 fail。

---

# 36. `events_file`

Type：

```text
string
```

Required：

```text
all bundle states after initialization
```

Default：

```json
"events_file": "events.jsonl"
```

Path rules同 `recording.file` 一樣。

---

# 37. Application Metadata

Recommended：

```json
{
  "name": "Meeting Evidence Recorder",
  "version": "0.1.0"
}
```

用途：

* diagnostics；
* reproducibility；
* compatibility investigation。

Consumer 不應將 app version 當 schema version。

---

# 38. Platform Metadata

Recommended：

```json
{
  "os": "macOS",
  "architecture": "arm64"
}
```

Known OS values：

```text
Windows
macOS
```

Architecture：

```text
x64
arm64
```

Consumer 不應按 platform 改變基本 parsing logic。

---

# 39. Complete `session.json` Example

```json
{
  "schema_version": "1.0",
  "session_id": "7e177f69-1d91-43ef-b554-d9529148f121",
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

# 40. `events.jsonl`

格式：

```text
JSON Lines
```

即：

> 每一行係一個完整 JSON object。

Example：

```json
{"event_id":"evt-000001","type":"screenshot","timestamp_ms":198420,"asset":"screenshots/shot_00-03-18.420.png"}
{"event_id":"evt-000002","type":"screenshot","timestamp_ms":761083,"asset":"screenshots/shot_00-12-41.083.png"}
```

---

# 41. Event Required Fields

所有 schema v1 event 必須至少：

```json
{
  "event_id": "...",
  "type": "...",
  "timestamp_ms": 0
}
```

---

# 42. `event_id`

Type：

```text
string
```

Requirements：

* session 內唯一；
* immutable；
* 不依賴 timestamp uniqueness。

Recommended：

```text
evt-000001
evt-000002
```

UUID 亦可。

---

# 43. `type`

Type：

```text
string
```

Schema v1 defined：

```text
screenshot
```

Future examples：

```text
important
decision
action_item
question
bookmark
chapter
```

Consumer：

> 遇到 unknown event type，不應因此拒絕整個 bundle。

應：

```text
skip unknown event
+
optionally preserve metadata
```

---

# 44. `timestamp_ms`

Type：

```text
integer
```

Unit：

```text
milliseconds
```

Meaning：

> canonical recording timeline position。

Must satisfy：

```text
timestamp_ms >= 0
```

Completed bundle：

```text
timestamp_ms <= duration_ms + permitted tolerance
```

---

# 45. Timestamp Precision

Schema v1 canonical serialization：

```text
integer milliseconds
```

原因：

* JSON interoperability；
* SRT alignment 已足夠；
* human-readable；
* 避免 floating-point ambiguity。

Internal recorder 可以保留：

```text
higher-resolution native timestamps
```

但 bundle v1 serialize 為：

```text
ms
```

---

# 46. Timestamp Rounding

Producer 必須使用 deterministic conversion。

Recommended：

```text
round to nearest millisecond
```

唔應：

* 不同 event 用不同 rounding；
* 依 locale；
* 用 floating point string。

---

# 47. Canonical Pause Semantics

如果：

```text
record 10 min
pause 5 min
resume
```

Resume 後下一個 media timestamp 接住：

```text
10:00
```

而唔係：

```text
15:00
```

所以：

> Paused wall-clock duration 不進入 `timestamp_ms`。

---

# 48. Screenshot Event

Schema：

```json
{
  "event_id": "evt-000001",
  "type": "screenshot",
  "timestamp_ms": 198420,
  "asset": "screenshots/shot_00-03-18.420.png"
}
```

Required screenshot-specific field：

```text
asset
```

---

# 49. Screenshot Timestamp Semantics

Screenshot timestamp 應代表：

> **saved screenshot frame 本身嘅 recording-frame timestamp。**

例如：

```text
Hotkey received:
754091 ms

Latest valid recording frame:
754083 ms
```

Bundle 必須寫：

```json
"timestamp_ms": 754083
```

---

# 50. Screenshot Asset

Type：

```text
string
```

Rules：

* relative path；
* bundle-local；
* normally under `screenshots/`；
* referenced file 必須存在；
* case sensitivity 不應作 portable assumption。

---

# 51. Screenshot Format

Schema v1 producer default：

```text
PNG
```

Filename：

```text
*.png
```

Consumer 應至少支援：

```text
image/png
```

Future bundle minor version 可以加入其他 image format，但 schema v1 MVP producer固定 PNG。

---

# 52. Screenshot Naming

Recommended：

```text
shot_HH-MM-SS.mmm.png
```

Example：

```text
shot_00-12-34.083.png
```

Filename：

> 非 authoritative timestamp source。

Consumer 必須使用：

```json
timestamp_ms
```

---

# 53. Duplicate Screenshot Timestamps

Allowed。

Example：

```json
{"event_id":"evt-11","type":"screenshot","timestamp_ms":754083,"asset":"screenshots/shot_00-12-34.083.png"}
{"event_id":"evt-12","type":"screenshot","timestamp_ms":754083,"asset":"screenshots/shot_00-12-34.083_02.png"}
```

Consumer 不得將：

```text
timestamp
```

當 primary key。

---

# 54. Event Ordering

Producer 應：

> 按 event commit / append order 寫 JSONL。

通常 timestamp 亦會 non-decreasing。

Consumer 應容許：

```text
equal timestamps
```

如遇極少量 out-of-order timestamp：

* 可排序作 timeline processing；
* 不應改寫原 journal。

---

# 55. Failed Screenshot

如果 screenshot file write failed：

Producer 不應寫一個正常 screenshot event 指向不存在 asset。

Recommended：

```text
log failure
continue recording
```

如未來要將 failure 寫入 event journal，可用另一 event type，但不屬 schema v1 requirement。

---

# 56. Event Extensibility

Future event：

```json
{
  "event_id": "evt-0100",
  "type": "decision",
  "timestamp_ms": 1200000,
  "note": "Approved option B"
}
```

Schema evolution rule：

> Consumer 必須忽略自己唔識嘅 optional fields。

---

# 57. Recording Media Contract

Schema v1 completed bundle expected：

```text
MP4 container
```

Standard：

```text
recording.mp4
```

Recommended MVP codecs：

```text
Video: H.264
Audio: AAC
```

但：

> codecs 不屬 bundle schema compatibility identity。

Consumer 應透過 media probing 檢查真正 codec。

---

# 58. Recording Media Requirements

Completed media 必須：

* container finalized；
* readable；
* duration > 0；
* seekable where container permits；
* video track readable；
* audio track reflect metadata；
* timestamps usable。

---

# 59. Mixed Audio Contract

MVP：

```text
output_mode = mixed
```

代表 final ASR-ready audio track 已包含：

```text
system audio
+
microphone
```

MRP 可以直接提取呢條 mixed track。

---

# 60. Separate Track Forward Compatibility

Future bundle 可以加入：

```json
"audio": {
  "output_mode": "mixed_and_separate",
  "tracks": {
    "mixed": 1,
    "system": 2,
    "microphone": 3
  }
}
```

Schema v1 consumer如果唔識：

```text
tracks
```

應仍然可以根據：

```text
output_mode
```

同 media probe 做合理處理。

---

# 61. Completed Bundle Invariants

如果：

```json
"status": "completed"
```

以下必須成立：

1. `session.json` valid；
2. `schema_version` supported；
3. `session_id` valid；
4. `duration_ms > 0`；
5. recording file exists；
6. recording file readable；
7. events file exists；
8. every referenced screenshot exists；
9. event paths安全；
10. required paths bundle-local；
11. event timestamp non-negative；
12. screenshot timestamps大致喺 media duration範圍內；
13. recording audio metadata反映實際 capture state。

---

# 62. Screenshot Timestamp Tolerance

由於：

* container rounding；
* encoder final frame；
* millisecond rounding；

consumer validation 可以接受：

```text
timestamp_ms <= duration_ms + tolerance
```

Recommended tolerance：

```text
1000 ms
```

但 producer 應盡量保持：

```text
timestamp_ms <= duration_ms
```

---

# 63. Session Lifecycle Persistence

Recorder 建立 session 時：

```json
"status": "initializing"
```

Capture start：

```json
"status": "recording"
```

Pause：

```json
"status": "paused"
```

Stop：

```json
"status": "finalizing"
```

Successful validation：

```json
"status": "completed"
```

---

# 64. Interrupted Session

如果 process crash：

最後 filesystem 狀態可能係：

```json
"status": "recording"
```

但 process 已唔存在。

Consumer：

* 不應自動視為 completed；
* 可將佢交畀 recovery workflow；
* MRP normal import default應 warning / reject。

---

# 65. `incomplete`

Recovery service 或 Recorder restart 可以將 session 更新為：

```json
"status": "incomplete"
```

Meaning：

> Session 曾錄到 evidence，但唔符合 completed bundle invariant。

---

# 66. Failed Session

Example：

```json
{
  "schema_version": "1.0",
  "session_id": "...",
  "created_at": "...",
  "duration_ms": null,
  "status": "failed",
  ...
}
```

Failed bundle 不保證有 final recording。

---

# 67. Recovery Artifacts

`.work/`：

```text
implementation-specific
```

Producer 可以保存：

```text
partial media
temporary muxing data
recovery journal
```

此 spec 不要求 consumer理解 `.work/`。

Recovery 屬 Recorder implementation responsibility。

---

# 68. Consumer Import Modes

Recommended MRP modes：

## Strict

只接受：

```text
status == completed
```

---

## Recovery / Best Effort

可以接受：

```text
incomplete
recording
finalizing
```

但必須明確：

> input 未經完整 bundle validation。

---

# 69. Bundle Validation Levels

建議定義三級。

## Level 1 — Structural

驗證：

* required files；
* JSON syntax；
* required fields；
* safe paths。

---

## Level 2 — Referential

驗證：

* asset exists；
* recording exists；
* screenshot event assets exist。

---

## Level 3 — Media

驗證：

* media parse；
* duration；
* video/audio streams；
* timestamps大致一致。

`completed` producer 應通過 Level 3。

---

# 70. JSON Schema

建議 repo日後加入：

```text
schemas/
├── meeting-evidence-bundle-session-v1.schema.json
└── meeting-evidence-bundle-event-v1.schema.json
```

但：

> JSON Schema file係 machine-readable implementation of 本文件，而唔係取代本 specification。

如果兩者有矛盾：

```text
specification
```

應先修正，兩者保持一致。

---

# 71. Path Normalization

Metadata path serialization：

```text
/
```

作 separator。

Example：

```json
"asset": "screenshots/shot_00-12-34.083.png"
```

唔應寫：

```json
"asset": "screenshots\\shot_00-12-34.083.png"
```

即使 producer running on Windows。

---

# 72. Path Traversal Protection

Consumer resolve path 前必須：

1. combine bundle root；
2. normalize full path；
3. verify resolved path仍然位於 bundle root。

拒絕：

```text
../
../../
absolute path
UNC path
drive-qualified path
```

---

# 73. Symlink Policy

為避免 portable/security ambiguity：

MVP producer：

> 不應喺 Evidence Bundle 內建立 symlink 作 required asset。

Consumer可以選擇：

```text
reject symlink
```

或確保 resolved target仍然喺 bundle root。

---

# 74. Case Sensitivity

Windows/macOS filesystem behavior可能不同。

Producer：

* 不應產生只靠 case 區別嘅兩個 required files。

Bad：

```text
shot.png
Shot.png
```

Consumer：

* 不應假定 filesystem一定 case-sensitive。

---

# 75. Filename Safety

Producer filename應避免：

* OS reserved characters；
* control characters；
* newline；
* trailing dot；
* trailing space。

Recommended character set：

```text
A-Z
a-z
0-9
-
_
.
```

---

# 76. Timezone

Timezone只用於：

```text
created_at
```

Canonical media timestamp：

```text
timezone-free elapsed duration
```

---

# 77. Daylight Saving / Clock Changes

如果 recording期間：

* wall clock change；
* NTP adjustment；
* DST；
* timezone change；

不得影響：

```text
timestamp_ms
duration_ms
```

---

# 78. Duration Source of Truth

Producer completed session：

```text
duration_ms
```

應來自：

> final media timeline / authoritative recording timeline。

唔應只係：

```text
stop_wall_clock - start_wall_clock
```

---

# 79. Event Provenance

Manual screenshot event本身就代表：

```text
human-triggered evidence
```

MRP derived timeline應保留：

```text
manual_capture
```

Example derived output：

```json
{
  "type": "manual_capture",
  "source_event_id": "evt-000002",
  "timestamp_ms": 761083,
  "file": "screenshots/shot_00-12-41.083.png"
}
```

---

# 80. Derived Artifacts

MRP 可喺同一 bundle旁邊或 output directory產生：

```text
transcript.srt
transcript.json
visual-timeline.json
merged-timeline.json
meeting-notes.md
```

但呢啲：

> **不屬 Evidence Bundle schema v1 required source artifacts。**

---

# 81. Immutable Source Principle

後續 processor：

* 不應修改 `recording.mp4`；
* 不應修改 original screenshot；
* 不應修改 original event timestamp。

如果 correction：

> 寫入 derived artifact。

---

# 82. Bundle Copy Semantics

Bundle copy 後：

```text
session_id
```

保持不變。

原因：

> 同一 recording evidence 嘅複製品仍然係同一 session provenance。

如果重新開始 recording：

> 必須新 `session_id`。

---

# 83. Bundle Rename Semantics

Root directory 可以 rename。

Consumer不得依賴：

```text
meeting-YYYYMMDD-HHMMSS
```

判斷 timestamp/session identity。

---

# 84. Bundle Hashing

Schema v1 不要求 checksum。

Future 可以加入：

```json
"integrity": {
  "recording_sha256": "...",
  "events_sha256": "..."
}
```

但唔係 MVP。

---

# 85. Application Compatibility

Producer app可以由：

```text
0.1
0.2
1.0
```

都寫：

```json
"schema_version": "1.0"
```

只要 contract不變。

---

# 86. Minor Schema Evolution

例如 schema `1.1` 可以加入 optional：

```json
"recording": {
  "audio": {
    "diagnostics": {}
  }
}
```

1.0 consumer：

* ignore `diagnostics`；
* 繼續工作。

---

# 87. Major Schema Evolution

例如：

```text
2.0
```

如果：

* timestamp semantics改變；
* required field meaning改變；
* bundle organization fundamentally change；
* path semantics改變。

Consumer遇到 unknown major：

```text
must fail clearly
```

---

# 88. Unknown Field Handling

Consumer：

> 必須忽略 unknown fields，除非該 field係理解 core semantics所必需。

例如：

```json
{
  "future_feature": {
    "foo": "bar"
  }
}
```

唔應令 schema 1 consumer失敗。

---

# 89. Unknown Event Handling

例如 future：

```json
{"event_id":"evt-9","type":"question","timestamp_ms":1000}
```

old consumer：

* skip event；
* optionally log；
* 不 reject bundle。

---

# 90. Required Field Removal

同一 major version：

> 不得刪除現有 required field。

如必須：

```text
new major version
```

---

# 91. Null Handling

Required semantic value如果未有：

```text
null
```

只可以喺 status容許情況。

例如 active session：

```json
"duration_ms": null
```

Completed：

```text
duration_ms cannot be null
```

---

# 92. Boolean Semantics

例如：

```json
"microphone": false
```

必須代表：

> final captured result冇 microphone audio。

唔應代表模糊狀態：

```text
unknown
maybe
requested but failed
```

如果需要更詳細 failure provenance：

> optional diagnostic field處理。

---

# 93. Example — System Audio + Microphone

```json
{
  "audio": {
    "system_audio": true,
    "microphone": true,
    "microphone_device": "USB Microphone",
    "output_mode": "mixed",
    "sample_rate": 48000,
    "system_audio_capture_mode": "os_native",
    "synchronized_to_recording_timeline": true
  }
}
```

---

# 94. Example — System Audio Only

```json
{
  "audio": {
    "system_audio": true,
    "microphone": false,
    "microphone_device": null,
    "output_mode": "system_audio_only",
    "sample_rate": 48000,
    "system_audio_capture_mode": "os_native",
    "synchronized_to_recording_timeline": true
  }
}
```

---

# 95. Example — Microphone Only

```json
{
  "audio": {
    "system_audio": false,
    "microphone": true,
    "microphone_device": "MacBook Microphone",
    "output_mode": "microphone_only",
    "sample_rate": 48000,
    "system_audio_capture_mode": "os_native",
    "synchronized_to_recording_timeline": true
  }
}
```

---

# 96. Example — Video Only

```json
{
  "audio": {
    "system_audio": false,
    "microphone": false,
    "microphone_device": null,
    "output_mode": "none",
    "sample_rate": null,
    "system_audio_capture_mode": "os_native",
    "synchronized_to_recording_timeline": true
  }
}
```

MRP應：

> detect no audio and fail ASR clearly。

---

# 97. Example — Sync Warning

```json
{
  "audio": {
    "system_audio": true,
    "microphone": true,
    "microphone_device": "USB Mic",
    "output_mode": "mixed",
    "sample_rate": 48000,
    "system_audio_capture_mode": "os_native",
    "synchronized_to_recording_timeline": false,
    "diagnostics": {
      "max_observed_drift_ms": 742.5,
      "sync_warning": true
    }
  }
}
```

---

# 98. Event Journal Crash Safety

Producer應：

1. serialize完整 event；
2. append成一行；
3. newline；
4. flush according to durability policy。

不得先寫：

```text
半個 JSON
```

再等太耐先完成。

---

# 99. Corrupted Final JSONL Line

Crash可能令最後一行 incomplete。

Recovery/best-effort consumer：

* 可以忽略最後一個 malformed incomplete line；
* 不應忽略中間 malformed line而當 bundle healthy。

Strict completed validation：

> 任意 malformed line = invalid completed bundle。

---

# 100. Screenshot Write Ordering

Required sequence：

```text
Capture frame
    ↓
Encode PNG temp file
    ↓
Flush/close
    ↓
Atomic rename
    ↓
Append screenshot event
```

不得：

```text
append event
    ↓
then try write screenshot
```

---

# 101. Manifest Write Ordering

Completed finalization：

```text
Finalize media
    ↓
Validate media
    ↓
Validate events/assets
    ↓
Write status=completed
```

因此：

> `completed` 必須係 final commit signal。

---

# 102. Completed as Commit Marker

Consumer可以將：

```json
"status": "completed"
```

視為：

> Producer聲稱 bundle已完成 atomic logical commit。

Consumer仍然應做 validation，但可以用此作 fast precondition。

---

# 103. MRP Import Algorithm

Recommended：

```text
Open bundle directory
      ↓
Read session.json
      ↓
Parse schema_version
      ↓
Check supported major version
      ↓
Check status
      ↓
Resolve recording path safely
      ↓
Resolve events file safely
      ↓
Parse events
      ↓
Validate screenshot assets
      ↓
Probe recording media
      ↓
Extract audio
      ↓
ASR
      ↓
Align events by timestamp_ms
```

---

# 104. Screenshot ↔ SRT Alignment

Given：

```text
SRT:
748200 → 761700

Screenshot:
754083
```

Match：

```text
segment_start_ms
<=
event.timestamp_ms
<
segment_end_ms
```

Recommended interval convention：

```text
[start, end)
```

即：

* start inclusive；
* end exclusive。

---

# 105. Boundary Screenshot

如果 screenshot exactly：

```text
event.timestamp_ms == current_segment.end_ms
```

則應 match：

> next segment

如果 next segment存在。

呢個 convention避免 screenshot同時屬於兩個 segments。

---

# 106. Gap Alignment

如果 screenshot timestamp落喺兩段 SRT中間：

MRP可以：

* attach nearest segment；
  -建立 standalone visual evidence interval；
  -由 higher-level alignment strategy處理。

Evidence Bundle本身不指定呢個 policy。

---

# 107. Event-to-Media Validation

Consumer 可以 optional verify：

```text
screenshot image
```

同同 timestamp video frame visually similar。

但唔係 schema v1 required validation。

Canonical trust source：

```text
events.jsonl
```

provided producer completed validation。

---

# 108. Privacy

Bundle 可以包含高度敏感內容：

* meeting audio；
* screen content；
* personal names；
* confidential UI；
* credentials accidentally displayed；
* internal system information。

Spec本身不要求 encryption。

Producer/consumer：

> 不得自動 upload。

---

# 109. Logs

`logs/`：

* diagnostic only；
* not part of core bundle contract；
  -不得包含 raw meeting content。

Consumer不應需要 logs先可處理 bundle。

---

# 110. Optional Bundle Metadata

Future可以加入：

```json
"title": "Architecture Review"
```

或者：

```json
"tags": ["project-x"]
```

但呢類 user metadata：

* optional；
* 不影響 evidence parsing。

---

# 111. No Participant Identity Contract

Schema v1不保存：

```text
speaker identity
participant identity
meeting platform participant list
```

如未來加入：

> 必須另行定義 provenance/privacy semantics。

---

# 112. No Transcript Contract

Evidence Bundle source schema v1：

> 不要求 transcript。

Transcript係 derived artifact。

---

# 113. No OCR Contract

Screenshot：

```text
image evidence
```

OCR output唔寫入 source screenshot event。

---

# 114. No Semantic Marker Requirement

Schema v1 required event只有：

```text
screenshot
```

未來：

```text
decision
question
action_item
```

唔影響 v1 consumer。

---

# 115. Reference Producer Requirements

Recorder producer必須：

* write UTF-8；
* use safe relative paths；
* maintain authoritative timeline；
* persist canonical milliseconds；
* preserve screenshot provenance；
* append events safely；
* finalize media before completed；
* validate bundle before completed；
* accurately describe actual audio result；
* mark interrupted session non-completed。

---

# 116. Reference Consumer Requirements

MRP consumer必須：

* validate schema major；
* safely resolve paths；
* not trust filename timestamp；
* use `timestamp_ms`；
* ignore unknown optional fields；
* ignore unknown event types；
* reject unsupported major version clearly；
* distinguish completed/incomplete；
* preserve manual screenshot provenance；
* not modify source evidence。

---

# 117. Validation Error Examples

Recommended error codes：

```text
BUNDLE_SESSION_MISSING
BUNDLE_SESSION_INVALID_JSON
BUNDLE_SCHEMA_UNSUPPORTED

BUNDLE_STATUS_NOT_COMPLETED

BUNDLE_RECORDING_MISSING
BUNDLE_RECORDING_UNREADABLE

BUNDLE_EVENTS_MISSING
BUNDLE_EVENTS_INVALID

BUNDLE_ASSET_MISSING
BUNDLE_PATH_INVALID
BUNDLE_PATH_ESCAPE

BUNDLE_TIMESTAMP_INVALID
BUNDLE_DURATION_INVALID

BUNDLE_AUDIO_METADATA_INCONSISTENT
```

---

# 118. Warning Examples

Warnings：

```text
BUNDLE_AUDIO_SYNC_WARNING
BUNDLE_EVENT_OUT_OF_ORDER
BUNDLE_TIMESTAMP_NEAR_DURATION_BOUNDARY
BUNDLE_UNKNOWN_EVENT_TYPE
BUNDLE_UNKNOWN_OPTIONAL_FIELD
```

Warnings唔一定代表 import失敗。

---

# 119. Example Valid Bundle

```text
meeting-20260909-103000/
│
├── recording.mp4
├── session.json
├── events.jsonl
└── screenshots/
    ├── shot_00-03-18.420.png
    └── shot_00-12-41.083.png
```

`events.jsonl`：

```json
{"event_id":"evt-000001","type":"screenshot","timestamp_ms":198420,"asset":"screenshots/shot_00-03-18.420.png"}
{"event_id":"evt-000002","type":"screenshot","timestamp_ms":761083,"asset":"screenshots/shot_00-12-41.083.png"}
```

---

# 120. Example Invalid Bundle — Absolute Asset

Invalid：

```json
{
  "event_id": "evt-1",
  "type": "screenshot",
  "timestamp_ms": 1000,
  "asset": "/Users/example/Desktop/shot.png"
}
```

Reason：

```text
absolute paths forbidden
```

---

# 121. Example Invalid Bundle — Traversal

Invalid：

```json
{
  "asset": "../../shot.png"
}
```

Reason：

```text
path escapes bundle root
```

---

# 122. Example Invalid Bundle — Missing Asset

Event：

```json
{
  "event_id": "evt-1",
  "type": "screenshot",
  "timestamp_ms": 1000,
  "asset": "screenshots/shot.png"
}
```

但：

```text
screenshots/shot.png
```

不存在。

Completed validation：

```text
FAIL
```

---

# 123. Example Invalid Bundle — Completed But Missing Recording

```json
{
  "status": "completed",
  "recording": {
    "file": "recording.mp4"
  }
}
```

但 file不存在。

Result：

```text
FAIL
```

---

# 124. Example Incomplete Bundle

```text
meeting-20260909-103000/
│
├── session.json
├── events.jsonl
├── screenshots/
│   └── shot_00-01-12.000.png
└── .work/
    └── recording.partial.mkv
```

`session.json`：

```json
{
  "schema_version": "1.0",
  "session_id": "...",
  "created_at": "2026-09-09T10:30:00+08:00",
  "duration_ms": null,
  "status": "incomplete"
}
```

Normal strict MRP：

```text
reject / require explicit best-effort
```

---

# 125. Bundle Producer Atomicity Model

Evidence Bundle唔需要整個 directory filesystem-level atomic。

Logical atomicity由：

```text
status = completed
```

提供。

Producer流程：

```text
create directory
      ↓
write active artifacts
      ↓
finalize recording
      ↓
validate all references
      ↓
atomic write session.json
status = completed
```

---

# 126. Bundle Consumer Trust Model

Consumer不應：

```text
blindly trust status=completed
```

應至少做 Level 1/2 validation。

對正式 MRP pipeline建議：

```text
Level 3 validation
```

---

# 127. Schema v1 Compatibility Matrix

| Producer | Consumer | Expected                          |
| -------- | -------- | --------------------------------- |
| 1.0      | 1.0      | Full support                      |
| 1.1      | 1.0      | Support if additions optional     |
| 1.0      | 1.1      | Full support                      |
| 2.0      | 1.x      | Reject unsupported major          |
| 1.x      | 2.x      | Consumer-defined backward support |

---

# 128. Bundle MIME / Archive Format

Schema v1 filesystem directory係 canonical representation。

Optional archive：

```text
.zip
```

可以用作 transport。

解壓後必須還原相同 directory contract。

Spec v1不定義 custom：

```text
.meetingbundle
```

container。

---

# 129. Archive Safety

Consumer如果直接支援 ZIP import：

必須防止：

```text
zip-slip / path traversal
```

所有 archive entries必須 extract喺 target bundle root。

---

# 130. Future Manifest Integrity

Possible future：

```json
"integrity": {
  "algorithm": "sha256",
  "files": {
    "recording.mp4": "...",
    "events.jsonl": "...",
    "screenshots/shot_00-03-18.420.png": "..."
  }
}
```

此設計留待 schema 1.x extension或2.0。

---

# 131. Future Event Sequence Number

Current：

```text
event_id
```

已足夠。

如果需要 journal ordering independent identity：

future可以加：

```json
"sequence": 23
```

但 schema v1唔要求。

---

# 132. Future Sub-Millisecond Timestamp

如果日後 professional video workflow需要：

```text
microseconds
```

唔應重新解釋：

```text
timestamp_ms
```

而係新增：

```text
timestamp_us
```

或提升 schema major/minor並清楚定義 precedence。

---

# 133. Future Multiple Recordings

Schema v1：

```text
one session
→
one primary recording
```

如果日後支援：

* multiple monitors separately；
* separate camera feed；
* multiple recording segments；

應擴展 recording model，而唔破壞現有 `recording.file` semantics。

---

# 134. Future Pause Metadata

Schema v1只要求 canonical timeline壓縮 pause。

Future可加入：

```json
"pauses": [
  {
    "wall_started_at": "...",
    "wall_duration_ms": 300000,
    "recording_timestamp_ms": 600000
  }
]
```

但 MRP normal alignment唔需要知道 pause。

---

# 135. Future Recording Events

Possible：

```json
{
  "event_id": "evt-100",
  "type": "recording_warning",
  "timestamp_ms": 900000,
  "code": "MICROPHONE_DEVICE_LOST"
}
```

唔屬 MVP。

---

# 136. Contract Boundary Summary

Evidence Bundle owns：

```text
Raw recording
Raw screenshots
Session metadata
Event timestamps
Capture provenance
```

MRP owns：

```text
Audio extraction
ASR
SRT
OCR
Automatic frames
Timeline merge
```

Document Agent owns：

```text
Interpretation
Summary
Actions
Decisions
Final image selection
Final document
```

---

# 137. Normative Timestamp Rule

最重要嘅 schema invariant：

> `timestamp_ms` 永遠表示 final recording 嘅 canonical playback timeline，而唔係 wall-clock time、hotkey callback time、filesystem creation time 或 screenshot filename。

---

# 138. Normative Screenshot Rule

> Screenshot event timestamp 必須對應實際保存 screenshot frame 嘅 normalized recording timestamp。

---

# 139. Normative Completion Rule

> Producer 只可以喺 recording media、event references 同 required metadata 全部完成並通過 validation 後，將 session status 設為 `completed`。

---

# 140. Normative Path Rule

> Bundle內所有 machine-readable asset references 必須使用 bundle-relative safe paths，且不得 escape bundle root。

---

# 141. Normative Provenance Rule

> Bundle保存 evidence，而唔保存未經區分嘅 interpretation。Manual screenshot 必須保持可識別為 manual source evidence。

---

# 142. Normative Compatibility Rule

> Consumer 必須拒絕自己不支援嘅 schema major version，但應容忍同一 major version內新增嘅 optional fields同未知 event types。

---

# 143. Phase 0 Implementation Deliverables

在開始 native Windows/macOS recorder 前，Phase 0 應完成：

* [ ] `session.json` C# model；
* [ ] `RecordingEvent` model；
* [ ] `ScreenshotEvent` model；
* [ ] JSON serialization；
* [ ] JSONL writer；
* [ ] Bundle path resolver；
* [ ] path traversal validation；
* [ ] bundle validator；
* [ ] completed/incomplete state handling；
* [ ] fake bundle producer；
* [ ] fake bundle consumer fixture；
* [ ] schema compatibility tests；
* [ ] timestamp boundary tests；
* [ ] duplicate timestamp tests；
* [ ] malformed event tests；
* [ ] missing screenshot tests；
* [ ] interrupted journal tests。

---

# 144. Recommended Machine-Readable Schemas

Phase 0完成時應額外產生：

```text
schemas/
├── meeting-evidence-session-v1.schema.json
└── meeting-evidence-event-v1.schema.json
```

並由 automated tests驗證 example fixtures。

---

# 145. Recommended Fixtures

```text
tests/fixtures/bundles/
├── valid-completed/
├── valid-no-screenshots/
├── valid-system-audio-only/
├── valid-microphone-only/
├── valid-video-only/
├── incomplete-session/
├── invalid-missing-recording/
├── invalid-missing-screenshot/
├── invalid-path-traversal/
├── invalid-event-json/
├── invalid-unsupported-schema/
└── valid-unknown-event/
```

---

# 146. Definition of Done

Evidence Bundle Specification v1 implementation可以視為完成，當：

* [ ] Recorder可以產生 schema 1.0 bundle；
* [ ] MRP-style validator可以讀取 bundle；
* [ ] bundle copy/rename後仍可處理；
* [ ] consumer完全唔依賴 platform-specific information；
* [ ] `timestamp_ms`與 canonical recording timeline一致；
* [ ] screenshot只引用成功寫入嘅 asset；
* [ ] completed bundle有 readable media；
* [ ] incomplete session不會誤標 completed；
* [ ] unknown optional fields不破壞 consumer；
* [ ] unknown event type不破壞 consumer；
* [ ] unsupported major version會明確拒絕；
* [ ] path traversal會被拒絕；
* [ ] malformed JSONL會被偵測；
* [ ] audio metadata反映實際 capture結果；
* [ ] MRP可直接從 bundle取得 recording同 manual screenshot timeline；
* [ ] source evidence不會被 processor靜默改寫。

---

# 147. Final Contract

Schema v1最小完整 contract：

```text
Meeting Evidence Bundle
│
├── session.json
│     ├── schema_version
│     ├── session_id
│     ├── created_at
│     ├── duration_ms
│     ├── status
│     ├── recording
│     │      ├── file
│     │      ├── video
│     │      └── audio
│     ├── events_file
│     ├── application
│     └── platform
│
├── events.jsonl
│     └── RecordingEvent[]
│             ├── event_id
│             ├── type
│             └── timestamp_ms
│
├── recording.mp4
│
└── screenshots/
      └── screenshot assets
```

核心 invariant：

```text
Recording Media
+
Timestamped Events
+
Referenced Assets
+
Stable Metadata
+
One Canonical Timeline
```

必須可以單獨、可攜、可驗證、可追溯地由 Recorder hand off 到任何符合 specification 嘅 consumer。
