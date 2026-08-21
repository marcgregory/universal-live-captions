=====================================================================
 Universal Live Captions
 Chrome-Live-Caption-style live captions for any Windows application
=====================================================================

WHAT THIS IS
---------------------------------------------------------------------
Universal Live Captions captures the system audio your apps play and
shows real-time captions in a small always-on-top window. It uses
WASAPI loopback capture (no virtual audio cable needed). Speech
recognition and translation run in a Gemini Live session, so an
internet connection and a Gemini API key are required at runtime.

No Python installation is required. No .NET installation is required.
No speech or translation models are bundled — everything runs in the
Gemini session.

=====================================================================
 INSTALL (Windows 10 64-bit, recommended)
=====================================================================

1. Download UniversalCaptions for Windows.
2. Run UniversalCaptions-Setup-*.exe and follow the installer.
3. Launch UniversalCaptions from the Start Menu (or the Desktop
   shortcut created during install).
4. Add your Gemini API key in the control window (get one free at
   https://aistudio.google.com/apikey).
5. Press START in the control window.

That's it. The installer bundles the .NET runtime; the app talks to
Gemini directly using your key.

=====================================================================
 PORTABLE INSTALL (no installer, advanced users)
=====================================================================

Prefer no installer? Download UniversalCaptions-*-win-x64-full.zip,
extract it anywhere, and run UniversalCaptions.App.exe.

The portable and installed versions are identical.

=====================================================================
 USING THE APP
=====================================================================

- The CONTROL WINDOW opens automatically. Use it to:
    - Choose the audio source (system default output, or pick one).
    - Choose the speech language.
    - Turn translation on or off and pick the target language.
    - Adjust the overlay position, opacity, and font size.
    - Press START to begin captions; press STOP to end.

- The CAPTION OVERLAY floats above the task bar. Drag it to move,
  hover it to reveal collapse / hide controls.

- No microphone is used. Only what your apps play is captioned.

=====================================================================
 PRIVACY
=====================================================================

- No microphone capture.
- No raw audio is saved to disk.
- Your Gemini API key is stored in Windows Credential Manager.
- Audio is streamed to Google's Gemini API for transcription and
  translation — it is not sent anywhere else and is not persisted.

=====================================================================
 UNINSTALL
=====================================================================

If you used the Setup.exe: open Windows "Add or remove programs"
and uninstall Universal Captions.

If you used the portable ZIP: delete the folder.

Your settings (audio source, language, overlay position) are stored
under your user profile and are removed by either uninstall method.

=====================================================================
 TROUBLESHOOTING
=====================================================================

- No captions appear? Make sure audio is playing through the
  selected Windows output device. The app captures what you hear,
  not what your microphone hears.

- Captions will not start? Add or update your Gemini API key in the
  control window, and check your internet connection.

- The app will not start? Verify you are on Windows 10 64-bit
  (build 1809 or later). The installer bundles its own .NET
  runtime; you do not need a separate one.

- For developer build instructions, environment-variable knobs, and
  advanced troubleshooting, see docs/DEVELOPER_SETUP.md in the
  project source.
