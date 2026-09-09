# Meeting Evidence Recorder

## Product Requirements Document (PRD)

**文件版本：** v0.1
**日期：** 2026-09-09
**狀態：** Draft
**產品類型：** Cross-platform desktop application
**目標平台：** Windows 10/11、Apple Silicon macOS
**建議技術棧：** .NET 10 LTS + C# + Avalonia
**相關系統：** Meeting Recording Processor (MRP)

---

# 1. Executive Summary

Meeting Evidence Recorder 是一個輕量級、local-first 嘅 desktop meeting recorder。

產品主要用途係錄製 online meeting、presentation、software demonstration 或 screen-sharing session，同時容許使用者喺錄影期間按 global hotkey，即時將當前錄影片段嘅畫面保存為 screenshot。

每張 screenshot 必須與 recording 使用同一條 timeline，並記錄精確 timestamp。

Audio capture 應接近 Microsoft ZoomIt Recording 嘅使用體驗：

* 使用者唔需要自行處理 audio loopback；
* 系統自動錄取 computer system audio；
* 系統自動錄取 microphone audio；
* Recorder 負責處理 platform-specific audio capture、mixing、sync 同 encoding；
* 使用者只需要選擇是否啟用 microphone，以及必要時選擇 microphone device。

一次 recording 完成後，Recorder 產生一個 self-contained **Meeting Evidence Bundle**，包含：

* meeting video；
* system audio；
* microphone audio；
* timestamped screenshots；
* session metadata；
* timestamped event metadata。

Evidence Bundle 可直接交畀現有 Meeting Recording Processor 作後續處理：

1. audio extraction；
2. ASR transcription；
3. SRT generation；
4. screenshot / transcript timestamp alignment；
5. optional OCR；
6. evidence timeline generation；
7. LLM final document generation。

Recorder 本身不負責 transcription、OCR 或 meeting summary。

---

# 2. Background

目前 Meeting Recording Processor 主要處理已存在嘅 meeting recording。

原有 video-aware workflow 設想係：

```text
Meeting Video
      │
      ├── Audio → ASR → Transcript / SRT
      │
      └── Video → Keyframe extraction
                       │
                       ▼
                  OCR / VLM
                       │
                       ▼
               Evidence Timeline
```

然而全自動 keyframe extraction 有一個根本限制：

> 系統只能估計邊一刻嘅畫面重要，但 meeting participant 本身往往喺 recording 當刻已經知道邊一頁、邊一個 UI、邊一張圖值得保存。

因此新方案將人工 visual marker 移到 recording 階段。

使用者只需要喺重要畫面出現時按一個 hotkey，Recorder 即時保存與 recording timeline 對齊嘅 screenshot。

Audio 方面，Recorder 應盡量提供接近 Microsoft ZoomIt Recording 嘅簡單體驗。使用者唔應該需要理解：

* audio loopback；
* virtual audio device；
* system audio routing；
* manual audio patching；
* system audio 同 microphone 嘅同步處理。

Recorder 應由 platform-specific backend 自動取得 system audio 同 microphone audio，並將兩者可靠地寫入 recording output。

原有 Meeting Recording Processor 已確立以下重要原則：

* visual evidence 必須保留 provenance；
* OCR 不應靜默覆寫 spoken transcript；
* audio transcript、visual evidence 同整理稿應彼此分離；
* timeline 應作為不同 evidence 間嘅共同基礎；
* workflow 應保持 local-first；
* raw artifacts 必須可追溯及可重做。

Meeting Evidence Recorder 將延續以上原則。

---

# 3. Product Vision

建立一個接近 Microsoft ZoomIt Recording 使用體驗嘅簡單 desktop utility，但增加：

> **「錄影期間以 hotkey 捕捉重要畫面，並將 screenshot 與錄影時間精確同步」**

Recorder 唔應該變成完整 meeting platform。

產品應保持：

* quick to start；
* low cognitive overhead；
* unobtrusive during meetings；
* deterministic；
* local-first；
* portable；
* processor-independent。

Audio capture 應遵循以下使用原則：

> 使用者按 Start Recording 後，Recorder 自動處理 system audio 同 microphone audio；除非使用者需要停用 microphone，否則唔需要額外設定 audio routing。

理想使用模式：

```text
Start Recorder
      ↓
Select screen
      ↓
Enable / disable microphone if needed
      ↓
Join / continue meeting
      ↓
Important screen appears
      ↓
Press screenshot hotkey
      ↓
Continue meeting
      ↓
Press screenshot hotkey
      ↓
Stop recording
      ↓
Evidence Bundle ready
      ↓
MRP processes recording
```

---

# 4. Product Goals

## 4.1 Primary Goals

### G1 — Reliable meeting recording

Recorder 必須可靠錄製：

* screen；
* computer system audio；
* microphone audio。

System audio 同 microphone audio 應由 Recorder 自動處理，使用者唔需要自行建立或管理 audio loopback。

---

### G2 — Automatic system audio and microphone capture

Recorder 應提供接近 Microsoft ZoomIt Recording 嘅 audio workflow：

1. 自動偵測可用 system audio capture capability；
2. 自動取得 computer system audio；
3. 自動偵測預設 microphone；
4. microphone 預設可以啟用或由使用者選擇停用；
5. 自動同步 system audio 同 microphone audio；
6. 自動將 audio 寫入 recording output；
7. 對使用者隱藏 platform-specific audio routing 複雜性。

---

### G3 — Timestamped manual visual evidence

使用者可以喺 recording 期間按 global hotkey。

每次操作必須：

1. capture 當前 recording frame；
2. 保存 screenshot；
3. 記錄相對 recording start 嘅 timestamp；
4. 將 event 寫入 machine-readable metadata。

---

### G4 — Single authoritative timeline

Video、audio、screenshots 同 events 必須共享同一個 recording timeline。

Screenshot timestamp 不應主要依賴：

```text
wall-clock current time - recording start time
```

應採用 recording / monotonic timeline。

System audio 同 microphone audio 亦必須與 video 使用同一條 authoritative timeline，避免後續 ASR 出現 audio drift 或 timestamp mismatch。

---

### G5 — Self-contained Evidence Bundle

每次 recording 完成後，所有 artifacts 應放入同一 session directory。

Evidence Bundle 應可以：

* copy；
* archive；
* move；
* hand off to MRP；
* process without Recorder installed。

---

### G6 — Cross-platform desktop support

核心產品功能應同時支援：

* Windows；
* Apple Silicon macOS。

UI、session management、event model、bundle format 等盡量共用。

OS-specific capture implementation 可以分開。

---

### G7 — Local-first

Recorder 不得自動：

* upload video；
* upload audio；
* upload screenshot；
* upload metadata；
* call cloud AI APIs。

---

# 5. Non-Goals

以下功能不屬 MVP：

### NG1 — ASR

Recorder 不負責 speech recognition。

---

### NG2 — SRT generation

Subtitle generation 由 Meeting Recording Processor 處理。

---

### NG3 — OCR

Recorder 不需要理解 screenshot 內容。

---

### NG4 — LLM summarization

Recorder 不生成：

* meeting notes；
* summary；
* action items；
* final report。

---

### NG5 — Speaker diarization

不在 Recorder 執行。

---

### NG6 — Automatic keyframe selection

MVP 不需要自動分析 video scene changes。

日後可由 MRP 補充 automatic evidence。

---

### NG7 — Meeting-platform integration

MVP 不需要直接整合：

* Microsoft Teams；
* Zoom；
* Google Meet；
* Webex。

Recorder 應以 generic screen recorder 形式工作。

---

### NG8 — Manual audio loopback configuration

MVP 不要求使用者：

* 建立 virtual audio device；
* 手動設定 audio loopback；
* 手動 routing system audio；
* 手動將 system audio 同 microphone 接駁；
* 以第三方 audio mixer 配置 recording input。

Recorder 應由 platform-specific backend 自動處理 system audio capture。

---

### NG9 — Cloud sync

MVP 不提供：

* cloud storage；
* account；
* cross-device sync。

---

# 6. Target Users

## 6.1 Primary User

需要經常參與：

* online meetings；
* software demonstrations；
* system walkthroughs；
* technical briefings；
* presentations；

並希望會後自動整理成文件嘅 knowledge worker。

---

## 6.2 Typical Use Cases

### UC1 — Online meeting

使用者錄製 Teams / Zoom meeting。

Recorder 自動錄取：

* meeting window / display；
* meeting system audio；
* 使用者 microphone audio。

當 presenter 顯示：

* diagram；
* table；
* configuration；
* decision slide；

使用者按 hotkey 保存畫面。

---

### UC2 — Software demo

Presenter 示範 application。

使用者保存：

* important screen；
* settings page；
* error message；
* configuration state；
* workflow step。

Recorder 同時保存 presenter 講解聲音及使用者補充說明。

---

### UC3 — Technical briefing

Meeting 期間大量討論：

* database；
* code；
* architecture；
* reporting；
* system configuration。

Screenshot 可以提供 ASR 無法可靠辨認嘅：

* identifiers；
* table names；
* UI labels；
* URLs；
* configuration values。

System audio 同 microphone audio 會一併保存，方便 MRP 後續進行完整 transcription。

---

### UC4 — Presentation capture

使用者唔需要保存每張 slide。

只標記真正值得放入 final document 嘅頁面。

---

# 7. Product Principles

## 7.1 Human-selected evidence first

Manual screenshot 係 high-value evidence。

未來如果加入 automatic frame extraction，優先順序應為：

```text
Manual screenshot
        ↓
Automatically detected important frame
        ↓
Interval fallback frame
```

---

## 7.2 Evidence, not interpretation

Recorder 記錄：

> 發生咗乜。

而唔係判斷：

> 呢件事代表乜。

---

## 7.3 Raw evidence must remain intact

後續 processor 可以：

* OCR；
* normalize；
* summarize；

但 Recorder output 本身應保持原始 evidence。

---

## 7.4 Timeline over filename

Filename 方便人閱讀。

Metadata timestamp 先係 authoritative value。

---

## 7.5 Recording and screenshot must share capture source where practical

Screenshot 應優先由 recorder 正在使用嘅 frame stream / frame buffer 取得。

避免：

```text
video capture API
+
independent OS screenshot API
```

造成 timing mismatch。

---

## 7.6 Audio capture should be automatic

使用者唔應該需要理解或處理 audio loopback。

Recorder 應自動：

* 偵測 system audio capture；
* 偵測 microphone；
* 建立 audio capture pipeline；
* 對齊 audio 同 video timeline；
* 處理 system audio 同 microphone 嘅混音或多 track output。

---

## 7.7 Audio failure must be explicit

如果 system audio 或 microphone 無法取得，Recorder 必須清楚顯示狀態。

不得將 audio capture failure 靜默當成成功錄音。

---

# 8. MVP Scope

MVP 包含：

1. recording source selection；
2. automatic system audio capture；
3. automatic microphone capture；
4. microphone enable / disable；
5. optional microphone device selection；
6. automatic system audio + microphone synchronization；
7. start recording；
8. pause / resume；
9. stop recording；
10. global screenshot hotkey；
11. screenshot file output；
12. timestamped screenshot event；
13. session metadata；
14. recording duration；
15. tray/menu-bar recording indicator；
16. screenshot count；
17. configurable output directory；
18. configurable screenshot hotkey；
19. Evidence Bundle generation；
20. graceful error handling；
21. Windows support；
22. Apple Silicon macOS support。

MVP 不要求使用者自行設定：

* audio loopback；
* virtual audio device；
* audio mixer；
* system audio routing。

---

# 9. Functional Requirements

## FR-001 — Create Recording Session

使用者按 Start Recording 後，系統建立唯一 session。

每個 session 至少包含：

```text
session_id
started_at
recording timeline
capture source
audio configuration
application version
platform
```

---

## FR-002 — Screen Source Selection

使用者可以選擇：

* display；
* window。

MVP 至少必須支援 display capture。

Window capture 可以視開發成本列入 MVP 或 MVP+1。

---

## FR-003 — Automatic System Audio Capture

Recorder 應自動錄製 computer system audio，使用者唔需要自行處理 audio loopback 或建立 virtual audio device。

System audio capture 應包括由電腦播放嘅聲音，例如：

* Microsoft Teams；
* Zoom；
* Google Meet；
* browser；
* media player；
* system notification；
* other application audio。

Recorder 應：

1. 自動偵測平台可用嘅 system audio capture capability；
2. 優先使用 OS-native system audio capture；
3. 將 system audio 與 video 使用同一條 recording timeline；
4. 對 system audio capture 狀態提供清楚 UI；
5. 如果 system audio capture 不可用，於開始錄影前明確提示；
6. 不要求使用者手動設定 audio loopback。

System audio capture 嘅實際 implementation 可以因平台而異，但使用者體驗應保持一致。

---

## FR-004 — Automatic Microphone Capture

Recorder 應自動偵測系統預設 microphone，並提供簡單 microphone control。

MVP 應支援：

* 自動使用系統預設 microphone；
* 使用者啟用或停用 microphone；
* 使用者選擇其他可用 microphone；
* recording 開始前顯示目前 microphone 狀態；
* microphone capture 與 system audio 使用同一條 recording timeline。

預設行為應盡量接近：

> 使用者按 Start Recording 後，Recorder 自動錄取 system audio 同 microphone，毋須額外 audio routing 設定。

如果 microphone permission 未授予、device unavailable 或 capture 初始化失敗，Recorder 應：

* 清楚顯示原因；
* 允許使用者 retry；
* 允許使用者選擇停用 microphone 後繼續錄影；
* 在 session metadata 記錄實際 audio capture 狀態。

---

## FR-005 — Automatic Audio Synchronization and Output

Recorder 必須自動處理 system audio 同 microphone audio 嘅同步及輸出。

MVP 應支援以下其中一種 output strategy：

### Option A — Mixed Audio Track

Recorder 將 system audio 同 microphone audio mix 成一條 audio track，並與 video 一同寫入 `recording.mp4`。

適合：

* 簡化 MRP input；
* 接近一般 screen recorder 使用體驗；
* 直接交畀 ASR pipeline。

---

### Option B — Separate Audio Tracks

Recorder 將 system audio 同 microphone audio 保存為獨立 audio tracks，但兩者必須與 video 使用同一條 timeline。

例如：

```text
recording.mp4
├── video track
├── system audio track
└── microphone audio track
```

---

### MVP Output Decision

MVP 優先採用：

> **Mixed Audio Track 作為預設輸出，並保留未來支援 separate tracks 嘅 schema extension。**

如果 engineering implementation 可以低成本提供 separate tracks，則可以額外保存，但不得令 MRP 必須依賴特定 platform 或 audio routing configuration。

Recorder 必須：

1. 自動同步 system audio 同 microphone；
2. 避免明顯 audio drift；
3. 避免 system audio 或 microphone 單方面延遲；
4. 將 audio capture configuration 寫入 `session.json`；
5. 將實際 capture 狀態寫入 metadata；
6. 確保 MRP 可以可靠提取 ASR 所需 audio；
7. 不要求使用者自行處理 audio loopback。

---

## FR-006 — Start Recording

按 Start 後：

1. initialize screen capture；
2. initialize automatic system audio capture；
3. initialize microphone capture；
4. initialize audio synchronization / mixing pipeline；
5. initialize authoritative recording clock；
6. create session directory；
7. start video/audio writing；
8. UI 顯示 recording state；
9. UI 顯示 system audio 同 microphone capture 狀態。

如果 system audio capture 初始化失敗，Recorder 應於開始前明確提示使用者。

如果 microphone capture 初始化失敗，Recorder 可以提供：

* retry；
* disable microphone and continue；
* cancel recording。

---

## FR-007 — Global Screenshot Hotkey

Recording active 時，使用者可以喺其他 application 使用 global hotkey。

預設可考慮：

```text
Ctrl/Cmd + Shift + configurable key
```

實際 default key 必須避免常見 OS/application shortcut conflict。

---

## FR-008 — Screenshot Capture

Hotkey 觸發後：

1. capture 當前 recording frame；
2. 讀取 recording timestamp；
3. 保存 PNG；
4. 建立 event；
5. 更新 screenshot count；
6. 提供短暫 visual confirmation。

Screenshot 操作不得明顯 interrupt recording。

---

## FR-009 — Screenshot Timestamp

每張 screenshot 至少包含：

```json
{
  "event_id": "...",
  "type": "screenshot",
  "timestamp_ms": 754083,
  "asset": "screenshots/shot_00-12-34.083.png"
}
```

`timestamp_ms` 係 canonical timestamp。

Filename timestamp 只作人類識別用途。

---

## FR-010 — Screenshot Feedback

Capture 成功後，UI 可以短暫顯示：

```text
Screenshot captured
00:12:34.083
```

不應：

* steal focus；
* pause meeting；
* 彈出大型 dialog。

---

## FR-011 — Pause Recording

如果產品提供 Pause：

Pause period 不應產生 video/audio content。

Resume 後 timeline semantics 必須清晰一致。

建議 canonical timeline 表示：

> 實際 output media playback timeline。

即 Pause 期間唔計入 media timestamp。

System audio 同 microphone capture 亦應喺 Pause 期間停止寫入 output，Resume 後重新與 recording timeline 對齊。

---

## FR-012 — Stop Recording

Stop 後：

1. stop screen capture；
2. stop system audio capture；
3. stop microphone capture；
4. flush audio mixer / synchronizer；
5. flush encoder；
6. finalize media container；
7. finalize metadata；
8. validate required artifacts；
9. mark session complete。

---

## FR-013 — Crash / Interrupted Session Recovery

如果 app 非正常終止：

應盡可能：

* 保存已寫 metadata；
* 保存可恢復 media；
* 保存已完成嘅 screenshot；
* 保存已記錄嘅 audio capture state；
* session 標示 incomplete。

不得將 incomplete recording 偽裝成 successfully completed session。

---

## FR-014 — Evidence Bundle

成功 recording 至少輸出：

```text
meeting-YYYYMMDD-HHMMSS/
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

---

# 10. Evidence Bundle Contract

## 10.1 Directory Naming

預設：

```text
meeting-YYYYMMDD-HHMMSS/
```

將來可以支援 user-provided session title。

---

## 10.2 `session.json`

建議 schema：

```json
{
  "schema_version": "1.0",
  "session_id": "uuid",
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

Audio metadata 應反映實際狀態。

例如 microphone 被停用時：

```json
{
  "audio": {
    "system_audio": true,
    "microphone": false,
    "microphone_device": null,
    "output_mode": "system_audio_only",
    "system_audio_capture_mode": "os_native",
    "synchronized_to_recording_timeline": true
  }
}
```

如果 system audio capture 不可用，session 不應將 `system_audio` 標示為 `true`。

---

## 10.3 `events.jsonl`

每一行代表一個 event。

Example：

```json
{"event_id":"evt-001","type":"screenshot","timestamp_ms":198420,"asset":"screenshots/shot_00-03-18.420.png"}
{"event_id":"evt-002","type":"screenshot","timestamp_ms":761083,"asset":"screenshots/shot_00-12-41.083.png"}
```

JSONL 優點：

* recording 期間可 append；
* crash 時較容易保留；
* future event types 容易增加。

---

# 11. Event Model

MVP 只要求：

```text
screenshot
```

但 schema 應避免 hard-code 成 screenshot-only。

基礎概念應為：

```text
RecordingEvent
```

未來可能加入：

```text
important
decision
action-item
question
bookmark
chapter
```

例如：

```json
{
  "event_id": "evt-003",
  "type": "decision",
  "timestamp_ms": 1124000
}
```

呢啲 marker 不屬 MVP UI requirement，但 data model 應容許擴展。

---

# 12. Meeting Recording Processor Integration

Recorder 唔應直接 call ASR。

標準 workflow：

```text
Evidence Bundle
       │
       ▼
Meeting Recording Processor
       │
       ├── recording.mp4
       │       ↓
       │      Audio extraction
       │       ↓
       │      ASR
       │       ↓
       │ transcript.json / SRT
       │
       ├── events.jsonl
       │
       └── screenshots/
               │
               ▼
       Timeline alignment
               │
               ▼
        Evidence Timeline
```

如果 `recording.mp4` 使用 mixed audio track，MRP 應直接提取該 audio track 作 ASR。

如果未來 Evidence Bundle 支援 separate audio tracks，MRP 可以根據 `session.json` 選擇：

* mixed track；
* system audio track；
* microphone track；
* processor-specific audio mix。

Recorder 不應要求 MRP 依賴 OS-specific audio device 或 loopback configuration。

---

# 13. Evidence Timeline Integration

假設：

```text
Screenshot:
00:12:34.083
```

SRT：

```text
00:12:28 → 00:12:41
「而家呢度可以見到 Department Summary……」
```

Processor 應產生：

```json
{
  "start": 748.2,
  "end": 761.7,

  "spoken_text": "而家呢度可以見到 Department Summary……",

  "visual_evidence": [
    {
      "type": "manual_capture",
      "timestamp": 754.083,
      "file": "screenshots/shot_00-12-34.083.png"
    }
  ]
}
```

Manual screenshot 必須被標示為：

```text
manual_capture
```

以區別將來 processor 自動抽取嘅：

```text
scene_change
interval_capture
llm_requested_frame
```

Audio transcript 應以 recording timeline 為基礎，與 screenshot event 對齊。

---

# 14. User Experience Requirements

## 14.1 Main Window

MVP 建議：

```text
┌─────────────────────────────────┐
│ Meeting Evidence Recorder       │
│                                 │
│ Screen                           │
│ [ Display 1                 ▼ ] │
│                                 │
│ System Audio        [Auto ✓]    │
│                                 │
│ Microphone                       │
│ [ MacBook Microphone        ▼ ] │
│ [✓] Record microphone            │
│                                 │
│ Screenshot Hotkey                │
│ [ Cmd + Shift + M ]              │
│                                 │
│ Output                           │
│ ~/MeetingRecordings              │
│                                 │
│        ● Start Recording         │
└─────────────────────────────────┘
```

System Audio UI 應以簡單狀態為主，不應要求使用者選擇 loopback device。

例如：

```text
System Audio: Ready
Microphone: Ready
```

如果 system audio capture 需要 permission：

```text
System Audio: Permission required
[Open Settings]
```

---

# 15. Recording UI

Recording 開始後 UI 應盡量 unobtrusive。

例如 tray/menu bar：

```text
● REC  00:34:18   🔊  🎙  📷 7
```

Menu：

```text
Recording: 00:34:18
System Audio: On
Microphone: On
Screenshots: 7

Capture Screenshot
Mute Microphone
Pause
Stop Recording
```

如果 microphone 被停用：

```text
● REC  00:34:18   🔊  🎙 Off  📷 7
```

如果 system audio capture 出現問題，UI 必須清楚顯示：

```text
System Audio: Error
```

---

# 16. Recording State Model

至少需要以下 states：

```text
Idle
  │
  ▼
Starting
  │
  ▼
Recording
  │
  ├──► Paused
  │       │
  │       └──► Recording
  │
  ▼
Stopping
  │
  ▼
Completed
```

Error path：

```text
Starting
Recording
Stopping
   │
   ▼
Error / Incomplete
```

Audio capture 狀態應獨立記錄，因為 video recording 可能成功但某一個 audio source 可能失敗。

例如：

```text
Recording State: Recording
System Audio: Active
Microphone: Failed
```

---

# 17. Platform Requirements

## 17.1 Windows

目標：

```text
Windows 10 / Windows 11
x64 initially
ARM64 optional / later
```

OS-specific capability 至少包括：

* screen capture；
* OS-native system audio capture；
* microphone capture；
* automatic system audio + microphone synchronization；
* global hotkey；
* tray integration。

Windows implementation 應優先使用 OS-native system audio capture，避免要求使用者安裝或設定 virtual audio device。

---

## 17.2 macOS

目標：

```text
Apple Silicon macOS
```

OS-specific capability：

* screen capture；
* OS-native system audio capture；
* microphone capture；
* automatic system audio + microphone synchronization；
* global hotkey；
* menu-bar integration；
* Screen Recording permission；
* Microphone permission；
* keyboard/global-event related permission where applicable。

macOS implementation 應優先使用 OS-native system audio capture，避免要求使用者自行建立或選擇 audio loopback device。

---

# 18. Cross-Platform Architecture Requirement

產品唔要求所有 implementation 都 cross-platform。

要求係：

```text
Shared application behavior
+
platform-specific capture backends
```

建議 boundary：

```text
                 Shared .NET Core
                       │
       ┌───────────────┼───────────────┐
       ▼               ▼               ▼
Screen Capture     Audio Capture    Global Hotkey
       │               │               │
 ┌─────┴─────┐    ┌────┴────┐    ┌─────┴─────┐
 ▼           ▼    ▼         ▼    ▼           ▼
Windows    macOS Windows   macOS Windows     macOS
```

Audio Capture backend 應提供一致嘅 shared abstraction：

```text
IAudioCaptureBackend
```

至少需要支援：

```text
InitializeSystemAudioCapture()
InitializeMicrophoneCapture()
Start()
Pause()
Resume()
Stop()
GetCaptureStatus()
GetAudioFormat()
```

Shared layer 負責：

* audio source lifecycle；
* recording clock；
* synchronization；
* mixing policy；
* output metadata；
* error state；
* session persistence。

Platform backend 負責：

* OS-native system audio capture；
* microphone device access；
* permission handling；
* platform-specific audio format；
* platform-specific device enumeration。

---

# 19. Technology Constraints

目前產品方向採用：

```text
Runtime:
.NET 10 LTS

Language:
C#

Desktop UI:
Avalonia

Encoding / muxing:
FFmpeg or equivalent tested media layer

Windows native integration:
Windows capture / OS-native system audio APIs / microphone APIs

macOS native integration:
ScreenCaptureKit / macOS system audio APIs / microphone APIs
```

以上屬 engineering constraint，而唔係 Evidence Bundle contract。

未來 Recorder implementation 即使更換 framework，Evidence Bundle 應保持 compatible。

---

# 20. Portability Requirements

## Windows

應提供 self-contained distribution。

使用者唔應需要手動安裝：

```text
.NET Runtime
```

Preferred packaging：

```text
MeetingRecorder-win-x64.zip
```

使用者亦唔應需要額外安裝：

```text
Virtual audio cable
Audio loopback driver
Third-party audio mixer
```

---

## macOS

Preferred：

```text
Meeting Recorder.app
```

正式 distribution 應考慮：

* signing；
* notarization；
* application permissions。

使用者亦唔應需要額外安裝或設定：

```text
Virtual audio device
Manual audio routing
Third-party audio mixer
```

---

# 21. Performance Requirements

Recorder 必須優先確保 recording reliability。

### PR-001

Screenshot capture 不應造成可感知 recording pause。

---

### PR-002

Hotkey 至 screenshot event creation 應足夠快速，避免錯過使用者想保存嘅畫面。

---

### PR-003

長時間 recording 不應將所有 raw video frames 保留喺 RAM。

---

### PR-004

Screenshot count 應可支援至少數百張，而唔明顯影響 recording。

---

### PR-005

60–120 分鐘 meeting 應屬正常使用範圍。

---

### PR-006

System audio 同 microphone audio 應保持可接受嘅同步誤差。

MVP 應避免出現會影響 ASR 或 transcript alignment 嘅明顯 audio drift。

---

### PR-007

Audio capture 應以 streaming 方式處理，不應將長時間 raw audio 全部保留喺 RAM。

---

# 22. Reliability Requirements

Recording reliability 高於：

* UI animation；
* fancy visual effects；
* live preview quality；
* advanced settings。

如果資源不足，系統應優先：

1. preserve recording；
2. preserve audio timeline；
3. preserve event timestamps；
4. preserve screenshot；
5. degrade non-critical UI。

如果 microphone capture 失敗但 system audio 同 video 仍然正常，Recorder 應盡量繼續 recording，並清楚標示 microphone unavailable。

如果 system audio capture 失敗，Recorder 應：

* 於開始前阻止 silent degraded recording；
* 清楚提示使用者；
* 允許使用者 retry；
* 允許使用者選擇是否以 system-audio-disabled mode 繼續，前提係 UI 明確顯示；
* 將實際狀態寫入 session metadata。

---

# 23. Privacy Requirements

所有 recording content 都可能包含敏感資料。

Recorder 必須：

* local-first；
* 不自動 upload；
* 不自動 telemetry 傳送 meeting content；
* 不自動刪除原始 recording；
* 不將 screenshot 發送第三方；
* 明確顯示 output location；
* 明確顯示 microphone 是否正在錄音；
* 明確顯示 system audio 是否正在錄音。

如果日後加入 telemetry：

不得包含：

* audio；
* video；
* screenshot；
* transcript；
* file names；
* meeting title；

除非使用者明確 opt-in。

---

# 24. Error Handling

至少要處理：

### Capture permission denied

顯示清楚：

```text
Screen recording permission is required.
```

---

### System audio capture unavailable

顯示清楚：

```text
System audio capture is unavailable.
No audio loopback configuration is required, but this platform or permission does not currently allow automatic system audio capture.
```

提供：

* retry；
* open system settings；
* cancel recording；
* 如產品允許，明確選擇以 system-audio-disabled mode 繼續。

不得 silently 錄成冇 system audio 嘅影片。

---

### Microphone unavailable

使用者可以：

* retry；
* select another microphone；
* disable microphone；
* continue with system audio only。

---

### Microphone permission denied

顯示清楚：

```text
Microphone permission is required to record microphone audio.
```

使用者可以選擇：

* open system settings；
* retry；
* continue without microphone。

---

### Audio synchronization failure

如果 system audio 同 microphone 無法可靠同步：

* 顯示 warning；
* 優先保存 video；
* 優先保存可用 audio；
* session 標示 audio synchronization status；
* 不得將未同步 audio 靜默標示為 fully synchronized。

---

### Output disk unavailable

Start 前盡量檢查。

Recording 期間如果 disk exhausted：

* fail visibly；
* preserve existing artifacts；
* mark session incomplete。

---

### Screenshot write failed

Recording 應繼續。

Event 應標示 failure，而唔係令整個 meeting recording terminate。

---

# 25. Observability

Recorder 應建立 local diagnostic log。

例如：

```text
logs/
└── recorder.log
```

Log 可以包含：

* app start；
* capture initialization；
* selected screen；
* system audio capture initialization；
* microphone device；
* audio capture status；
* recording state changes；
* screenshot events；
* encoder failures；
* permission errors；
* audio synchronization warnings。

不得將 meeting spoken/visual content 寫入 log。

---

# 26. MVP Acceptance Criteria

## Recording

* [ ] Windows 可錄製 display；
* [ ] Apple Silicon macOS 可錄製 display；
* [ ] recording 可產生可播放 video；
* [ ] Windows 可自動錄製 system audio，而唔需要 virtual audio device；
* [ ] Apple Silicon macOS 可自動錄製 system audio，而唔需要手動 audio loopback；
* [ ] microphone 可自動偵測；
* [ ] microphone 可以啟用或停用；
* [ ] system audio + microphone 可同時使用；
* [ ] system audio 同 microphone 與 video 使用同一條 recording timeline；
* [ ] 60 分鐘 recording 穩定完成。

---

## Audio User Experience

* [ ] 使用者唔需要自行設定 audio loopback；
* [ ] 使用者唔需要安裝 virtual audio cable；
* [ ] 使用者唔需要使用第三方 audio mixer；
* [ ] Start Recording 前 UI 顯示 system audio 狀態；
* [ ] Start Recording 前 UI 顯示 microphone 狀態；
* [ ] microphone permission denied 時有清楚提示；
* [ ] system audio capture unavailable 時有清楚提示；
* [ ] audio capture failure 會寫入 session metadata；
* [ ] MRP 可以從 recording output 提取可供 ASR 使用嘅 audio。

---

## Screenshot Evidence

* [ ] recording 期間 global hotkey 可工作；
* [ ] hotkey 不需要 Recorder window focus；
* [ ] 每次 hotkey 產生 screenshot；
* [ ] screenshot 使用 recording timeline；
* [ ] screenshot filename 包含 human-readable timestamp；
* [ ] event metadata 保存 canonical timestamp；
* [ ] screenshot 不明顯 interrupt video recording；
* [ ] screenshot count 即時更新。

---

## Evidence Bundle

* [ ] 每次 session 有獨立 directory；
* [ ] 包含 `recording.mp4`；
* [ ] 包含 `session.json`；
* [ ] 包含 `events.jsonl`；
* [ ] 包含 `screenshots/`；
* [ ] session status 明確；
* [ ] audio capture configuration 寫入 `session.json`；
* [ ] interrupted session 不會標示 completed。

---

## Processor Compatibility

* [ ] MRP 可以讀 `session.json`；
* [ ] MRP 可以讀 `events.jsonl`；
* [ ] MRP 可以從 `recording.mp4` 提取 audio；
* [ ] screenshot timestamp 可以對齊 SRT；
* [ ] audio transcript 可以與 screenshot event 使用同一條 timeline；
* [ ] MRP 不依賴 Recorder runtime；
* [ ] MRP 不依賴 OS-specific audio loopback；
* [ ] Evidence Bundle copy 到另一部機仍可處理。

---

## Privacy

* [ ] app offline 可正常使用；
* [ ] recording 不會自動 upload；
* [ ] screenshot 不會自動 upload；
* [ ] meeting content 不出現在 telemetry；
* [ ] UI 清楚顯示 microphone 是否啟用；
* [ ] UI 清楚顯示 system audio 是否啟用。

---

# 27. MVP+1 Candidates

完成核心 MVP 後優先考慮：

### 27.1 Window Capture

除 display 外，可以只錄指定 window。

---

### 27.2 Region Capture

指定 screen region。

---

### 27.3 Multiple Marker Types

例如：

```text
Screenshot
Important
Decision
Action Item
Question
```

---

### 27.4 User Annotation

Capture screenshot 後可以 optional 加一行短 note：

```text
"Check this configuration later"
```

但不得 interrupt normal recording workflow。

---

### 27.5 Pre/Post Screenshot Context

Processor 可喺 manual screenshot 前後自動取得：

```text
-2 sec
exact marker
+2 sec
```

作 context comparison。

---

### 27.6 Automatic Scene Change Supplement

如果兩個 manual screenshots 中間長時間冇 marker：

processor 可以補充 automatic frame extraction。

---

### 27.7 OCR

Processor 對 manual screenshots 優先執行 OCR。

---

### 27.8 Recording Profiles

例如：

```text
Meeting
Presentation
Software Demo
Audio-first
```

---

### 27.9 Separate Audio Tracks

除 mixed audio track 外，保存：

```text
system audio track
microphone audio track
```

方便日後：

* speaker separation；
* audio debugging；
* independent ASR；
* source-specific processing。

---

### 27.10 Audio Device Diagnostics

提供簡單 audio diagnostics：

* system audio level meter；
* microphone level meter；
* permission status；
* capture backend status；
* synchronization status。

---

# 28. Future Vision

長遠 Recorder 不只係 screen recorder，而係：

> **Meeting Evidence Capture Client**

可以記錄：

```text
Audio evidence
Video evidence
Visual evidence
Human markers
User notes
Meeting events
```

所有資料使用共同 timeline。

Audio capture 應維持低認知負擔：

```text
Start Recording
      ↓
System audio automatically captured
      ↓
Microphone automatically captured
      ↓
No manual loopback configuration
```

未來 pipeline：

```text
Meeting Evidence Recorder
           │
           ▼
     Evidence Bundle
           │
           ▼
Meeting Recording Processor
           │
    ┌──────┼──────┐
    ▼      ▼      ▼
   ASR    OCR   Auto Frames
    │      │      │
    └──────┼──────┘
           ▼
    Evidence Timeline
           │
           ▼
Document Generation Agent
           │
           ▼
     Final Document
```

---

# 29. Product Boundary

最重要嘅 architecture boundary：

## Recorder owns

```text
Screen Capture
System Audio Capture
Microphone Capture
Audio Synchronization
Recording Clock
Events
Screenshots
Evidence Bundle
```

## Processor owns

```text
Audio extraction
ASR
SRT
OCR
Automatic keyframes
Evidence alignment
```

## Document Agent owns

```text
Interpretation
Summarization
Document structure
Screenshot selection for publication
Final writing
```

三層不應互相取代。

Recorder 負責將 system audio 同 microphone audio 可靠保存，但唔負責：

* speaker identification；
* transcript；
* semantic interpretation；
* audio content classification。

---

# 30. Success Criteria

產品成功唔係由「AI 功能有幾多」判斷。

第一階段成功標準係：

> 使用者可以像使用普通 screen recorder 一樣錄完整場 meeting，只需要按 Start Recording；system audio 同 microphone 會自動錄取，毋須處理 audio loopback；使用者只需要喺重要畫面按一下 hotkey；會後 Meeting Recording Processor 可以準確知道「講到呢段內容嗰陣，使用者特別保存咗呢張畫面」。

如果呢條 workflow 穩定可靠，後續 OCR、ASR、VLM 同 LLM 都可以逐步疊加，而毋須改變最底層 evidence contract。

---

# 31. Proposed Delivery Phases

## Phase 0 — Contract Foundation

* Evidence Bundle schema；
* session schema；
* event schema；
* recording clock semantics；
* audio metadata semantics；
* mixed audio output contract；
* platform abstractions；
* sample fixture。

---

## Phase 1 — Windows Recorder MVP

* display capture；
* OS-native system audio capture；
* automatic microphone capture；
* audio synchronization；
* mixed audio output；
* recording；
* global screenshot hotkey；
* evidence bundle；
* portable self-contained build。

---

## Phase 2 — macOS Recorder MVP

* ScreenCaptureKit backend；
* OS-native system audio capture；
* automatic microphone capture；
* audio synchronization；
* mixed audio output；
* global hotkey；
* permissions flow；
* `.app` packaging。

---

## Phase 3 — MRP Integration

* detect Evidence Bundle；
* read manual events；
* extract mixed audio；
* ASR；
* SRT alignment；
* merged evidence timeline。

---

## Phase 4 — Evidence Enrichment

* OCR manual screenshots；
* scene-change fallback；
* automatic frame extraction；
* evidence confidence / provenance；
* optional separate audio tracks。

---

## Phase 5 — Document Generation

* final meeting document；
* timestamp-aware screenshot insertion；
* decisions；
* actions；
* technical terms；
* traceable source evidence。

---

# 32. Open Product Decisions

以下問題唔阻礙開始 Phase 0，但應喺實作期間逐步決定：

1. MVP 是否必須支援 window capture，定 display-only 已足夠；
2. MVP mixed audio track 嘅具體 codec、sample rate 同 channel layout；
3. separate audio tracks 是否於 MVP+1 提供；
4. pause/resume 是否納入第一版；
5. default screenshot hotkey；
6. screenshot image format 固定 PNG 定可選 JPEG；
7. video encoder / codec default；
8. Windows ARM64 是否首版支援；
9. output directory naming 是否加入 optional meeting title；
10. tray/menu-bar UI 嘅具體行為；
11. system audio capture permission flow；
12. microphone 預設係啟用定停用；
13. system audio capture unavailable 時，是否允許使用者明確選擇繼續錄影；
14. audio synchronization status 應否顯示於 recording UI；
15. 是否保存 audio capture diagnostics；
16. 是否於 MVP 提供 system audio / microphone level meter。

以上均不應改變：

```text
Recording
+
System Audio Capture
+
Microphone Capture
+
Automatic Audio Synchronization
+
Authoritative Timeline
+
Timestamped Events
+
Evidence Bundle
```

呢七個核心 product contract。
