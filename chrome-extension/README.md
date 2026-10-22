# Live Caption Assistant (Chrome sidebar)

Chrome **sidebar** port of Live Caption Viewer. It reads **Windows 11 Live Captions** (`Win+Ctrl+L`) through a native host that uses the same UI Automation capture as the desktop app, then sends segments into ChatGPT or Claude.

A Chrome extension cannot see the Live Captions window by itself. The native host is required.

## Load in Chrome (this is the working method)

Modern Chrome blocks silent CRX / registry installs. Load the unpacked folder:

1. Double-click `chrome-extension\Load-Extension.cmd`  
   (rebuilds the host, starts it, copies the folder path, opens `chrome://extensions`)
2. Turn **Developer mode** on
3. Click **Load unpacked** and select the `chrome-extension` folder of this repo (Load-Extension.cmd copies its full path to the clipboard)
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

## Reliability notes

* **Hotkeys survive Chrome stopping the extension.** Chrome stops idle MV3 service workers after ~30 s and starts a fresh one for the next shortcut. The transcript, read head and running flag are mirrored into `chrome.storage.session`, the side panel keeps the worker alive while it is open, and every shortcut first makes sure capture is running and pulls one fresh snapshot from the host, so a shortcut captures what is on the Live Captions window right now. An empty capture is reported in the panel (and a red `0` badge) instead of doing nothing.
* **Sends are queued.** Pressing a hotkey while the previous prompt is still going out waits for its turn (up to 3 queued) instead of being dropped.
* **Delivery is verified.** The content script checks the text really landed in the composer (with a simulated paste as fallback), waits for the Send button to be enabled, presses Enter if there is no Send button, and confirms the page took the message before it could click twice. A reply still streaming after 4 s is stopped so the new question goes out.
* **When the sites change their markup**, override the selectors without editing the extension. In `chrome://extensions` → *Live Caption Assistant* → *service worker* → Console:

  ```js
  chrome.storage.local.set({ selectorOverrides: {
    ChatGPT: { input: "#prompt-textarea", sendButton: "button[data-testid='send-button']" },
    Claude:  { stopButton: "button[aria-label='Stop response']" }
  }})
  ```

  Keys: `input`, `sendButton`, `stopButton` (CSS selectors).
* Tests: `node --test "tests/*.test.js"` from this folder.
