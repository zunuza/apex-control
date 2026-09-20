APEX CONTROL
============
A small, local-only replacement for SteelSeries GG, for the wired Apex Pro TKL.
No account, no internet, no telemetry: it only talks to your keyboard over USB.

Apex Control is an independent project. It is not made by or affiliated with SteelSeries.


WHAT YOU NEED
-------------
  - The wired SteelSeries Apex Pro TKL (USB ID 1038:1614). Only this model is supported for editing.
  - Windows 10 or 11, 64-bit.
  - Nothing to install. If you were sent "ApexControl-standalone.exe", that one file is everything.
    (The small "ApexControl.exe" build needs the .NET 8 Desktop Runtime, x64, installed first.)


GETTING STARTED
---------------
  1. Quit SteelSeries GG completely, including its Engine (right-click its tray icon -> Quit).
     Apex Control will not write to the keyboard while GG is running, so the two never fight.
  2. Run the exe. Windows SmartScreen may say "Windows protected your PC" because the file is not
     code-signed. Click "More info", then "Run anyway".
  3. Plug in the keyboard. The header shows whether it was found.


WHAT IT CAN DO
--------------
  Macros      Click a key, then build a macro: record it live by typing, or edit the timeline of
              Key down / Key up / Wait blocks and change the timings. Save writes it to the keyboard.
              A key can be un-selected by clicking it again.
  Actuation   Set how far each key has to be pressed before it registers. Only the values that
              have been confirmed against the real keyboard are offered. "Send to keyboard" changes it right now;
              "Save to Config N..." also writes it into that profile on the keyboard (about 15 seconds, you
              confirm first), the way GG's Save does. A saved table stays with its
              profile on the keyboard. Each profile also remembers the table you last sent live, and a
              box on the tab can re-send that when you switch to it (off by default). The left Windows key can only be sent live; the profile has no slot for it.
  SOCD        Choose what happens when two opposite keys (for example A and D) are held at once:
              last input wins, or a priority key wins.
  Profiles    Switch between the profile buttons. You can rename them; the names are only stored
              on this PC, not on the keyboard. Double-click a profile to switch to it: the Macros and SOCD tabs then read and write that profile (the label at the top right says which).
  Messages    A green pop-up at the top says when something was saved, sent or read (red if it failed);
              click it to dismiss it. Switching profile shows that profile's own macros and SOCD straight
              away (the app reads it once the first time), so nothing goes blank.
  Backups     "Save backup" stores a copy of Config 1. "Restore" writes a saved copy back.
              A backup is also made automatically before every write.
  Tray        Closing the window hides Apex Control in the system tray. Right-click the tray
              icon to open it or quit. Only one copy can run at a time.
  Startup     "Start with Windows" (left panel) launches it hidden in the tray at sign-in.

Not included: lighting (RGB) and OLED settings. Use GG for those.


IF SOMETHING GOES WRONG
-----------------------
  - "GG is running": quit GG and its Engine fully, then try again.
  - A write looks wrong: use Restore and pick the backup made just before it.
  - The keyboard behaves oddly: unplug it and plug it back in.
  - Only the profile named at the top right is written by Macros/SOCD, and each write is read back to check it. Actuation is sent live to the keyboard's active profile.


WILL IT WORK ON MY KEYBOARD?
----------------------------
It was reverse-engineered from USB captures of GG on an Apex Pro TKL with firmware 4.16.8.
Other firmware versions or other Apex models may differ. Use "Compatibility..." in the header:

  1. It lists the SteelSeries devices Windows sees and compares them with the TKL. This sends
     nothing to any keyboard.
  2. Optionally, "Read this keyboard's profile" reads Config 1 the way GG does at startup and checks
     whether the profile has the TKL's layout. It writes nothing. GG must be closed. Only try it on
     a keyboard you are happy to experiment with; models other than the TKL have never been tested.
  3. Optionally, "Create export" saves one .zip on your PC with the report and Config 1's key
     layout, so the developer can look at your model. Nothing is uploaded, and you decide whether to
     send the file. Your profile name, your macros and which keys have macros are left out unless
     you tick "Include my macros and profile name".

Where things are kept:  %LOCALAPPDATA%\ApexControl   (backups, profile names, compatibility reports)
Type that into the Explorer address bar to open it.


PRIVACY
-------
No network access, accounts or analytics. Everything stays on your PC. The compatibility report
contains model IDs and format checks only: no serial numbers or device paths.


CREDITS
-------
  Source code, updates and compatibility reports: https://github.com/zunuza/apex-control
  Made by zunuza. If it helped you: https://ko-fi.com/zunuza  ("buy me a coffee!")
  Font: Hauora Sans (SIL Open Font License, included with the app source).
  USB library: HidSharp (Apache License 2.0, James Bellinger).
  "SteelSeries", "Apex" and "GG" belong to their owners.
