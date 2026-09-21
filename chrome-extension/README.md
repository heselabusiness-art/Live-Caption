# Live Caption Assistant (Chrome sidebar)

Chrome **sidebar** port of Live Caption Viewer. It reads **Windows 11 Live Captions** (`Win+Ctrl+L`) through a native host that uses the same UI Automation capture as the desktop app, then sends segments into ChatGPT or Claude.

A Chrome extension cannot see the Live Captions window by itself. The native host is required.

## Load in Chrome (this is the working method)

Modern Chrome blocks silent CRX / registry installs. Load the unpacked folder:

1. Double-click `chrome-extension\Load-Extension.cmd`  
   (rebuilds the host, starts it, copies the folder path, opens `chrome://extensions`)
2. Turn **Developer mode** on
3. Click **Load unpacked** and select `D:\Dev\Live Caption\chrome-extension`
4. Confirm the ID is `nkcgnofkagfllgbfompiejcdadpbbikp`
5. Click the extension icon to open the **sidebar**
6. Press `Win+Ctrl+L`, then click **Start**

If Chrome already has an older copy, Remove it first, then Load unpacked again.

## Shortcuts

| Desktop app | Extension default | Action |
|---|---|---|
| Home | Alt+1 | Capture segment and send question (`THEY ASKED` on Onboarding) |
| End | Alt+2 | Capture segment and send pushback |
| PageUp | Alt+3 | Capture segment, display only |
| PageDown | Alt+4 | Send `Reminder.txt`, or the onboarding prompt if that tab is selected |
| Insert | (assign yourself) | Capture the selected tab screenshot and attach it |
| Delete | (assign yourself) | Capture segment and send as a coding question |

Chrome allows only 4 default shortcuts. Assign Insert/Delete in `chrome://extensions/shortcuts`. Set **Global** if you want them while another window is focused.

## After you edit prompts

Copy changed `Prompt.txt`, `Reminder.txt`, `Individual Prompt V2/`, and `onboarding prompt/` into `chrome-extension/prompts/`, then reload the extension.
