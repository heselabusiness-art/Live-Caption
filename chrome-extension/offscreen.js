/** Web Speech API host — service workers cannot use speech recognition. */

let recognition = null;
let committed = "";
let running = false;

function SpeechCtor() {
  return window.SpeechRecognition || window.webkitSpeechRecognition;
}

function emit(text, status, error) {
  chrome.runtime.sendMessage({
    type: "LC_SPEECH_UPDATE",
    text: text || "",
    status: status || "monitoring",
    error: error || null
  }).catch(() => {});
}

function startRecognition() {
  const Ctor = SpeechCtor();
  if (!Ctor) {
    emit("", "error", "Web Speech API is not available in this browser.");
    return;
  }

  stopRecognition();
  committed = "";
  running = true;

  recognition = new Ctor();
  recognition.continuous = true;
  recognition.interimResults = true;
  recognition.lang = "en-US";

  recognition.onresult = (event) => {
    let finals = "";
    let interims = "";
    for (let i = event.resultIndex; i < event.results.length; i++) {
      const piece = event.results[i][0].transcript;
      if (event.results[i].isFinal) finals += `${piece} `;
      else interims += `${piece} `;
    }
    committed += finals;
    emit(`${committed}${interims}`.trim(), "monitoring");
  };

  recognition.onerror = (event) => {
    if (event.error === "no-speech" || event.error === "aborted") return;
    emit(committed, "error", event.error);
  };

  recognition.onend = () => {
    if (!running) {
      emit(committed, "stopped");
      return;
    }
    try {
      recognition.start();
    } catch {
      setTimeout(() => {
        if (running) {
          try { recognition.start(); } catch { /* ignore */ }
        }
      }, 250);
    }
  };

  try {
    recognition.start();
    emit(committed, "monitoring");
  } catch (err) {
    emit("", "error", err.message || String(err));
  }
}

function stopRecognition() {
  running = false;
  if (recognition) {
    try { recognition.stop(); } catch { /* ignore */ }
    recognition = null;
  }
}

chrome.runtime.onMessage.addListener((msg) => {
  if (msg?.type === "LC_SPEECH_START") startRecognition();
  if (msg?.type === "LC_SPEECH_STOP") stopRecognition();
});
