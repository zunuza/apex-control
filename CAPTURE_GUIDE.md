# Capture guide — isolating the actuation protocol

Goal: watch SteelSeries GG talk to the keyboard's `mi_01` interface
(VID `0x1038`, PID `0x1614`) while performing exactly one action, so the
resulting bytes are unambiguous. SteelSeries GG needs to stay installed for
this step only — it comes off again once we've extracted what we need.

## One-time setup

1. Install [Wireshark](https://www.wireshark.org/download.html). During
   install, make sure **USBPcap** is checked (it's an optional component in
   the installer) — this is what lets Wireshark see raw USB traffic on
   Windows, since Windows doesn't expose that natively.
2. Make sure SteelSeries GG is installed and can already control the
   keyboard normally (actuation slider etc. works in its Engine app).

## Capturing one action

Do this once per action — **one small change per capture**, not a whole
session, or the resulting data is hard to isolate.

1. Plug in the Apex Pro TKL.
2. Open Wireshark. In the interface list you'll see one or more entries
   named `USBPcap1`, `USBPcap2`, etc. — one per USB root hub. If you're not
   sure which one the keyboard is on, you can start with `USBPcap1` and
   switch if you see nothing.
3. Start the capture (double-click the interface, or the blue shark-fin
   button).
4. In the display filter bar at the top, type:
   ```
   usb.idVendor == 0x1038
   ```
   and press Enter. This hides everything except SteelSeries traffic, which
   makes the rest much easier to read even before you save anything.
5. Open SteelSeries GG's Engine app, go to the actuation setting for the
   Apex Pro TKL.
6. Change **one thing** — e.g. drag the actuation slider to its minimum
   value (should be 0.1mm or similar) and let it apply. Don't touch anything
   else.
7. Stop the Wireshark capture (red square button).
8. File → Save As → save as a `.pcapng` file, named after what you just did,
   e.g. `actuation-set-min.pcapng`.
9. Repeat steps 3–8 for a second, clearly different value — e.g. actuation
   set to its **maximum** (probably 3.6mm or similar). Save as
   `actuation-set-max.pcapng`.

That's enough for a first pass — two captures that differ in exactly one
value let us find precisely which byte(s) encode the actuation point by
diffing them.

## What to send back

Send both `.pcapng` files. If you'd rather not send the raw capture (it can
technically contain other USB traffic if the filter didn't catch everything
locally), you can instead:

1. With the filter above still applied, select all filtered packets
   (Edit → Select All, with the filter active — or Ctrl+A after clicking a
   filtered packet).
2. File → Export Specified Packets → save as a new `.pcapng` containing only
   the filtered (SteelSeries) packets.

Either way, two small files (a few KB each) round-trip much faster than
trying to paste hex by hand.

## After actuation

Once we've confirmed the actuation command from these two captures, we'll
repeat the same one-action-per-capture process for:
- Per-key actuation (one specific key changed)
- A single macro assigned to one key
- Profile switch

(RGB and OLED are out of scope for this project — not captured.)

Same idea every time: smallest possible change, saved as its own file, named
for what it does.
