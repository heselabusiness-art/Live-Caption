# Live Caption Viewer

A Windows 11 desktop overlay that

1. reads the built-in **Live Captions** (`Win + Ctrl + L`) through UI Automation,
2. keeps track of what was said since you last captured it, and
3. types the captured segment into **ChatGPT** or **Claude**, running in an embedded WebView2 browser, with one global hotkey.

A Chrome side-panel version of the same workflow lives in [chrome-extension/](chrome-extension/README.md).

## Requirements

| What | Why |
|---|---|
| Windows 11 with **Live Captions** (Settings → Accessibility → Captions) | the text source |
| **.NET 10 SDK** — <https://dotnet.microsoft.com/download/dotnet/10.0> | build and run (the app targets `net10.0-windows`; .NET 8 reaches end of support in Nov 2026) |
| **WebView2 Evergreen Runtime** (preinstalled on Windows 11) | the embedded browser |

## Build and run

```powershell
dotnet run                          # from the repo root
dotnet build LiveCaption.slnx -c Release   # app + native host + tests
dotnet test tests/LiveCaptionViewer.Tests  # unit tests (also run on Linux/macOS)
```

Visual Studio 2022 17.13+ or 2026 can open `LiveCaption.slnx` directly. The projects set
`EnableWindowsTargeting`, so they also restore and compile on a non-Windows machine; only running the
WPF app needs Windows.

## Using the app

1. Press `Win + Ctrl + L` and let Live Captions start transcribing. Capture starts together with the app (the **Start** / **Stop** buttons are still there); a hotkey pressed while capture is stopped starts it.
2. In the embedded browser open `chatgpt.com` or `claude.ai` and sign in.
3. On the **Pre-interview** tab fill in the fields and click **Send** once to load the setup prompt.
4. During the conversation use the hotkeys:

| Key | Headset | Action |
|---|---|---|
| `Home` | Volume up | capture the new caption text and send it as a question (`ROUND: …  QUESTION: "…"`) |
| `End` | Volume down | capture and send it as a pushback |
| `PageUp` | Mute | capture only, nothing is sent |
| `PageDown` | | send the FULL REMINDER block of `Reminder.txt` |
| `Insert` | | Tech Interview tab: screenshot the selected window and attach it to the chat |
| `Delete` | | capture and send as a coding question |
| `Ctrl+W` | | toggle click-through mode |

If a hotkey could not be registered (another app owns it) the status line under the captions says which.

## How it works

```
Live Captions window ──UI Automation events──► LiveCaptionsCaptureService
                                                       │ full visible text
                                                       ▼
                                             SegmentCaptureTracker  ◄── "what was said since the last capture?"
                                                       │ segment
 hotkey ─► HotkeyManager ─► MainWindow ─► PromptBuilder (trigger text) ─► PromptSender (queue)
                                                                              │
                                                  ChatGpt/ClaudeWebAutomationService (WebChatAutomationBase)
                                                                              ▼
                                                              chatgpt.com / claude.ai in WebView2
```

| Area | Code |
|---|---|
| Caption capture | [Services/LiveCaptionsCaptureService.cs](Services/LiveCaptionsCaptureService.cs) subscribes to UI Automation `Name` / structure change events on the Live Captions window, so new words show up immediately. A 1.5 s safety poll stays on, and plain 500 ms polling takes over if events are refused. |
| Segments | [Services/SegmentCaptureTracker.cs](Services/SegmentCaptureTracker.cs) keeps a word-level transcript and aligns every caption snapshot with its end (tolerating the recogniser's revisions of the last words), so a capture returns exactly the words spoken since the previous one however the Live Captions window scrolls. A hotkey first reads the window **at that instant** (`LiveCaptionsCaptureService.ReadNow`), then captures. |
| Trigger prompts | [Services/PromptBuilder.cs](Services/PromptBuilder.cs) builds `ROUND/QUESTION`, pushback and coding messages. Quotes are neutralised, line breaks collapsed, and an over-long segment is cut to its most recent 4000 characters. |
| Sending | [Services/PromptSender.cs](Services/PromptSender.cs) serialises sends: a second press **waits for its turn** (up to 3 queued) instead of being dropped. [Services/WebChatAutomationBase.cs](Services/WebChatAutomationBase.cs) types into the page and checks that it worked, see below. |
| Hotkeys / window | [Infrastructure/HotkeyManager.cs](Infrastructure/HotkeyManager.cs) (`RegisterHotKey`, headset `WM_APPCOMMAND`, low-level keyboard hook), [Infrastructure/WindowEffects.cs](Infrastructure/WindowEffects.cs) (opacity, click-through), [Infrastructure/NativeMethods.cs](Infrastructure/NativeMethods.cs). |
| UI | `MainWindow.*.cs`: one partial class per concern (shell, browser, capture, send, hotkeys, pre-interview, tech interview). |

### How a prompt is delivered

`WebChatAutomationBase.SendPromptAsync` does not just click and hope:

1. If the previous reply is still streaming it waits 4 s, then presses **Stop** so the new question goes out at once.
2. It puts the text into the composer through the browser's real input pipeline (CDP `Input.insertText`, with `execCommand` as fallback) and **verifies** the composer holds the text. If not, it retries once.
3. It waits for the Send button to be enabled and clicks it. If **no** Send button exists for 2.5 s (the site changed its markup) it presses **Enter** instead.
4. It confirms the page took the message (reply started streaming, or the composer emptied) before it could ever click again, so nothing is sent twice.

Every step is logged, and failures end up in the send-status line with the page state.

## When ChatGPT or Claude change their page

The CSS selectors are the fragile part. Without rebuilding, create
`%LocalAppData%\LiveCaptionViewer\selectors.json` (re-read on every send, no restart needed):

```json
{
  "ChatGPT": {
    "input": "document.querySelector('#prompt-textarea')",
    "sendButton": "document.querySelector('button[data-testid=\"send-button\"]')"
  },
  "Claude": {
    "stopButton": "document.querySelector('button[aria-label=\"Stop response\"]')"
  }
}
```

Keys: `input`, `sendButton`, `isGenerating`, `stopButton`. Each value is a JavaScript expression returning
the element (for `isGenerating`: a truthy value). Find the right selector with the browser's DevTools
(right-click → Inspect in the embedded browser).

## Logs and settings

All per-user data is in `%LocalAppData%\LiveCaptionViewer\`:

| File | Content |
|---|---|
| `logs\app-YYYYMMDD.log` | everything the app did: hotkeys, captures, every send attempt and its outcome (7 days kept) |
| `settings.json` | proxy settings |
| `selectors.json` | optional selector overrides (see above) |
| `WebView2\` | browser profile (cookies, logins) |

## Tests and CI

* `tests/LiveCaptionViewer.Tests`: xUnit tests for the pure logic (segment tracker, prompt builder, selector overrides, ingestion parsing, proxy arguments). They link the source files, so they run anywhere.
* `chrome-extension/tests`: `node --test "chrome-extension/tests/*.test.js"` for the extension's tracker and trigger builders.
* `.github/workflows/ci.yml` builds the solution on `windows-latest`, runs both test suites and syntax-checks the extension scripts.

## Docker (Windows containers)

The app is WPF, so the image can only be built with **Windows containers** (Docker Desktop → Switch to Windows containers). It packages a self-contained `win-x64` publish; run the published exe on a Windows desktop for the GUI.

```powershell
docker build -t live-caption-viewer .
# or
docker compose --profile build build
```

## Project layout

```
LiveCaption.slnx
├── LiveCaptionViewer.csproj        WPF app (net10.0-windows)
├── App.xaml(.cs)                   theme, logging, global exception handlers
├── MainWindow.xaml                 UI
├── MainWindow*.cs                  window logic, one partial per concern
├── Infrastructure/                 logging, Win32 interop, hotkeys, window effects
├── Models/                         settings and form data
├── Services/                       capture, tracking, prompts, chat-page automation, docx export
├── tests/LiveCaptionViewer.Tests/  unit tests
├── chrome-extension/               side-panel version + native host (net10.0-windows)
├── Prompt.txt, Reminder.txt, "Individual Prompt V2", "pre interview prompt"   prompt files copied next to the exe
└── Dockerfile, docker-compose.yml
```

When you edit the prompt files, copy them into `chrome-extension/prompts/` too (the extension ships its own copy).
