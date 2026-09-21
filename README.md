# Live Caption Viewer

A small Windows 11 WPF app that captures text from the built-in **Live Captions**
window using **UI Automation** and mirrors it inside its own UI in near real time.

Live Captions has no public API, so this app inspects its window via
`System.Windows.Automation`, walks the automation tree for text-bearing
elements, and polls them a few times a second.

## Project layout

```
LiveCaption/
├── LiveCaptionViewer.csproj
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs
├── Models/
│   └── CapturedCaption.cs
└── Services/
    └── LiveCaptionsCaptureService.cs
```

## Prerequisites

- **Windows 11** with the **Live Captions** feature installed
  (Settings → Accessibility → Captions → Live captions, or `Win + Ctrl + L`).
- **.NET 8 SDK** — download from <https://dotnet.microsoft.com/download/dotnet/8.0>.
  After install, open a *new* terminal and verify with `dotnet --version`.

## Build & run

From the project root (`d:\Dev\Live Caption`):

```powershell
dotnet restore
dotnet build
dotnet run
```

Or open `LiveCaptionViewer.csproj` in Visual Studio 2022 (17.8+) and press F5.

## Using the app

1. Press `Win + Ctrl + L` to open Windows Live Captions and let it start
   transcribing audio.
2. Launch this app and click **Start**.
3. The status row shows what the service is doing:
   - *Searching for Live Captions...* — looking for the window.
   - *Live Captions not found* — open it and the service will keep retrying.
   - *Monitoring Live Captions* — receiving text.
   - *Stopped* / *Error* — self-explanatory.
4. Click **Stop** to end monitoring, **Clear** to wipe the captions view.

If Live Captions is closed and reopened mid-session, the service detects the
disappearing automation element and automatically re-acquires the new window.

## How it works

`LiveCaptionsCaptureService`:

1. Finds the `LiveCaptions.exe` process and grabs its main window via
   `AutomationElement.FromHandle`. Falls back to scanning top-level windows by
   class name (`LiveCaptionsDesktopWindow`) or display name (`Live Captions`).
2. Searches descendants for `Document`, `Text`, and `Edit` control types.
3. Reads each element's content, preferring `TextPattern`, then `ValuePattern`,
   then the element's `Name`.
4. Normalizes whitespace and compares against the last emitted text; only fires
   `CaptionUpdated` when the text actually changed.
5. Polls every ~400 ms — low CPU, good responsiveness. Adjust
   `PollInterval` / `SearchInterval` in the service if you want different
   timings.

Threading: the polling loop runs on a background `Task`; events are marshaled
to the WPF UI thread via `Dispatcher.Invoke`.

## Roadmap / nice-to-haves

- Subscribe to UI Automation `StructureChangedEvent` / `AutomationPropertyChangedEvent`
  on the caption element so updates are event-driven instead of polled.
- Persist transcript to disk (txt / srt with timestamps).
- Diff-based append so long captions don't repeat the rolling window every tick.
- Settings panel for poll interval, font, theme.
