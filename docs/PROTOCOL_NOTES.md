# Protocol notes — Apex Pro TKL (64734)

Living document. Everything here is either CONFIRMED (from the device scan)
or UNKNOWN (needs a capture). Nothing is guessed and presented as fact.

## Device identity — CONFIRMED

- VID: `0x1038` (SteelSeries)
- PID: `0x1614`
- Windows enumerates it as **10 HID sub-interfaces** under one composite USB
  device (interfaces `mi_00` through `mi_04`, several with multiple
  collections).

## Interface map — CONFIRMED (from report descriptors)

| Interface | Usage page | Role | Notes |
|---|---|---|---|
| `mi_00 col01` | Generic Desktop / Mouse (0x0002) | Volume roller reported as a mouse wheel | Standard trick, not interesting |
| `mi_00 col02` (`\kbd`) | Keyboard (0x0006), 256-bit NKRO bitmap | Actual key input (full NKRO) | Exclusively claimed by Windows' keyboard driver — expected, don't touch |
| `mi_00 col03` | Consumer Control (0x0C) | Media keys | Standard |
| `mi_01` | **Vendor-defined `0xFFC0`** | **Command/control channel** | 64-byte input, 64-byte output, **642-byte feature report**. Opens successfully — not claimed by Windows. **This is almost certainly where SteelSeries GG sends everything**: actuation, per-key actuation, RGB, macros, profiles, OLED frames. |
| `mi_02 col01/02/03` | Duplicates of mi_00's collections | Same roles as above, second logical unit (possibly N-key rollover redundancy or the boot-protocol fallback) | Not interesting |
| `mi_03 col01/02` | Consumer Control | More media-key reporting | Not interesting |
| `mi_04` | **Vendor-defined `0xFFC1`** | **Input-only, 64 bytes, no output/feature** | **Never seen to carry anything** — in the hardware dump run it delivered 0 reports. The device→host acks/replies come from `mi_01`'s own 64-byte input report (see below). |

## Transport — CONFIRMED (from `actuation-set-min.pcapng` / `actuation-set-max.pcapng`)

`mi_01` traffic is entirely **USB control transfers**, `SET_REPORT`
(`bmRequestType 0x21`, `bRequest 0x09`), `wIndex 1`. Two shapes seen:

- **Feature report**, `wValue 0x0300`, `wLength 642` — a big blob, opcode in
  the first 1–3 bytes.
- **Output report**, `wValue 0x0200`, `wLength 64` — smaller, used for a
  periodic chunked transfer (see below).

Opcodes identified in the 642-byte Feature blob so far:
- `03 00 00 00 …` — a profile-info struct: contains the profile name as
  ASCII (`"Config 1"` seen), plus what looks like a keymap/key-list table.
  **Byte-for-byte identical** between the min and max captures, so actuation
  is not encoded here.
- `3a 57 04 …` — per-key RGB color table (`[key-index][R][G][B]` repeating).
  **Out of scope, not being pursued further** (RGB/OLED cut from the
  project — see README).
- `61 …` — looks like an RGB "chase"/bitmask effect frame. Same scope note
  as above.
- `31 47 …` — **actuation** (next section).

The 64-byte Output-report sequence (`88 00 01 02…` → `05 00 01 02 [counter]…`
→ `eb 01…`, repeated with `01 03`) is **NOT an OLED heartbeat** (that was an
early misreading): it is the control channel of the profile-write
transaction described under "Macros" below.

## Actuation (global and per-key) — CONFIRMED

Opcode `31 47`, sent as a Feature report (same SET_REPORT transport as
above), followed by one 3-byte triplet per key: `[key][value-lo][value-hi]`.

- **key** is a standard USB HID keyboard usage code (`0x04` = A … `0x16` =
  S … `0x1d` = Z, `0x1e`–`0x27` = 1–0, `0x28` = Enter, etc.).
- **value** is a little-endian u16.
- Keys GG addresses, in wire order: `0x04`–`0x28`, `0x2a`–`0x39`, `0x64`,
  `0x87`–`0x8b`, `0xe0`–`0xe7`, `0xf0` (68 triplets, 206 bytes, rest of the
  642 is zero padding). **Not in the frame:** Escape (`0x29`), F-row,
  arrows, navigation cluster — whether those have separate/fixed actuation
  is unknown.
- GG always sends the **full table**. Global actuation = the same value in
  every triplet; **per-key = the same frame with one or more triplets
  holding a different value.** Confirmed with a capture where every key was
  2.0mm and only S (`0x16`) was 0.1mm.
- `0x32`, `0x64`, `0x87`–`0x8b` always carry a fixed sentinel `0x1f23`
  (wire bytes `23 1f`) regardless of the slider — these are the ISO /
  international keys (Non-US `#`, Non-US `\`, International 1–5) that this
  ANSI board doesn't physically have.

(An earlier reading of this frame as `31 47 04` + `[lo][hi][key]` produced
byte-identical output but misattributed keys by one position; the
key-first reading is the correct one — it puts the changed triplet on S in
the per-key capture, and puts the sentinel on the ISO keys.)

The write is sent **live while dragging the slider** (settled, not per
pixel) — not on GG's "Save" button, which only persists locally. It's a
single rare frame among ~30/sec `3a5704` RGB refresh frames, which is why it
was hard to spot until a capture had RGB minimized.

### Value → millimeters

Little-endian u16, **not linear** in mm (consistent with a Hall-effect
sensor's non-linear response). Firm anchors, each confirmed by independent
captures:

| mm | raw | evidence |
|---|---|---|
| 0.1 | `0x0606` (1542) | original "min" capture; S in the per-key capture |
| 2.0 | `0x373b` (14139) | calibration capture; the global value in the per-key capture |
| 3.6 | `0xc0c8` (49352) | calibration capture |
| 4.0 (true max) | `0xd4d9` (54489) | calibration capture; original "max" capture |

Between 0.2 and 3.6mm the labeled sweep gives 35 consecutive real values in
GG's 0.1mm steps (write #19 = 2.0mm and #35 = 3.6mm landed exactly 16 steps
apart, which supports uniform 0.1mm steps). Labels other than the four
anchors are inferred from that; 3.7–3.9mm were never captured.

**Correction history:** an earlier version of these notes labeled `0x373b`
as 0.1mm, `0xc0c8` as 2.0mm and `0xd4d9` as 3.6mm — every label shifted by
one. The calibration run's "starting at .1" state produces no write, so
The author's three moves (2.0, 3.6, 4.0) produced the three writes, which I
misread as (0.1, 2.0, 3.6). The per-key capture (known global 2.0mm, S set
to 0.1mm) exposed it. That also explains the early hardware misbehavior:
what was requested as "1.5mm" was actually ~3.3mm, and "1.53mm" was actually
3.2mm.

## Macros — PARTIALLY decoded (captures: `macro-single-q-on-f9`, `macro-double-qw-on-f10`)

**Macros are not a small dedicated command.** They live inside the
**profile struct** (the `03 00 00 00` / `03 00 00 02` Feature-report family
that earlier looked like a fixed handshake). When GG saves, it sends a
~2.7s burst of 104 of these frames (52 of each type — the profile written
out as pages) — one burst, at Save. Earlier "identical" 03-family captures
were just profiles with no macros in them.

What changed when a macro was saved:

- **Keymap table** (in the `03 00 00 00` `"Config 1"` frame, ~offset
  60–330): 5-byte entries, one per physical key position.
  `51 <HID code> 00 00 00` = key sends its normal code (`0x42` = F9,
  `0x43` = F10). Binding a macro turns the entry into
  `71 00 00 <offset> 00` (`0x71` = macro; `<offset>` = the macro record's
  position in the macro area, in 4-byte units — F9 → `00`, F10 → `05`,
  and F10's record is 20 bytes = 5 units after F9's).
- **Macro area** (in the `03 00 00 02 00 02 ff ff …` frame, starting at
  offset 384), 4-byte units. A record is: header `77 01 <n> 00`
  (`n` = number of event units after the settings unit: 3 for the 1-key
  macro, 5 for the 2-key one), a settings unit `01 01 0f 00` (meaning
  unknown), then events:
  - `02 <HID key> 01 01` — key down, `02 <HID key> 01 00` — key up
  - `04 00 <lo> <hi>` — delay (probably ms; recorded press durations were
    `0xac` = 172 and `0x8d` = 141)

  Single Q on F9: `77010300 01010f00 02140101 0400ac00 02140100`
  (Q down, wait 172, Q up). Two-key on F10: `77010500 01010f00 021a0101
  02140101 04008d00 021a0100 02140100` — decodes as W down, Q down, wait
  141, W up, Q up — a simultaneous chord. Confirmed: the author pressed both
  keys at the same time when recording, so downs are listed back-to-back
  with no delay unit between them, and the single delay is the hold time.
  **Timeline macros — CONFIRMED against GG (capture `macro-sequence-q-300-w-on-f11`,
  reference `docs/reference/tx-macro-seq-q300w.txt`):** GG's macro editor shows a
  macro as a flat timeline of blocks (key down, wait, key up, ...). the author built
  F11 = Q down, 300 ms, Q up, 300 ms, W down, 300 ms, W up and saved; the record GG
  wrote is
  `77010700 01010f00 02140101 04002c01 02140100 04002c01 021a0101 04002c01
  021a0100`, byte-for-byte what the app's encoder produces (`n` = number of
  events, one 4-byte unit each, settings unit `01 01 0f 00`; waits sit between
  events exactly like the holds in the earlier captures). Applying our edit to the
  keyboard's prior state (a dump with no macros) gives GG's saved image except for
  the actuation area (offsets 2375-2516) and the CRC that covers it: GG's save also
  rewrote the actuation table from its own copy, so a GG "Save" overrides actuation set
  by our tool. Whole-image checks in `ApexMacro verify` (check 12): GG's image
  minus F11 plus our F11 == GG's image, CRC included. In this capture the `74`
  command comes after the two `90`s (as in the baseline capture); the earlier
  macro captures put it before them, so its position is not significant.
  The app refuses timelines that leave a key held, release an unpressed key, wait
  0 or over 5000 ms, or exceed 60 events (its own safety rules, not GG's).
- **Write transaction (CONFIRMED from the merged timeline of macro
  capture A):** the 104 Feature frames are **two 512-byte halves of 1 KB
  pages** (frame byte 3 = offset within the page in 256-byte units: `00` =
  first half, `02` = second half; bytes 4–5 = `00 02` = length 0x0200 BE),
  interleaved with Output-report control commands on `mi_01`, with the
  keyboard's acks coming back as `mi_01`'s **input report** (64 bytes; the
  interrupt IN endpoint I earlier mis-attributed to `mi_04`):
  1. `O 88 00 01 02 …` (begin region 02) → device replies `88 01`
  2. **40 pages**: Feature(half 0), Feature(half 2), then
     `O 05 00 01 02 <off> 00 00 04 00 …` where `<off>` steps `00,04,08,…,9c`
     (page address in 256-byte units; `0400` = 1024-byte page length) →
     device replies `05 01`
  3. `O eb 01 …` → reply `01 00`
  4. `O 88 00 01 03 …` (begin region 03) → reply `88 01`
  5. **12 pages** the same way with `O 05 00 01 03 <off>` (`00,04,…,2c`)
  6. `O 89 01`, `O 41 00`, `O 74 00 01 00`, `O 90 00` (reply begins
     `34 2e 31 36 2e 38` = ASCII `4.16.8`, the firmware version), `O 90 01`
  (Output reports are 64 bytes via `SET_REPORT` `wValue 0x0200`.) The whole
  thing is a ~52 KB config image written page by page: ~2.7 s for the
  Feature frames, ~3.7 s for the whole transaction. This sequence is
  byte-identical across every capture except for the page contents.
  **GG never reads the profile back during a session** — it keeps the
  image in its own database. No read path is known yet (GG's *startup*
  sync, before our captures began, is the likely place to find one).
- **Slots and regions (CONFIRMED from `gg-startup.pcapng`):** the keyboard
  stores **5 profile slots** ("Config 1".."Config 5"). Every write/read
  command carries `00 <slot 01–05> <region 02|03>`: the commit was
  `05 00 01 02 …` = slot 1, region 02 (and `… 01 03` = slot 1, region 03).
  Region 02 = the profile (name, keymap, macros, actuation…; 13 pages, CRC
  in page 12); region 03 = 12 more pages of constant content. GG's macro
  writes targeted slot 1.
- **READ protocol (CONFIRMED):** per 1 KB page, GG sends Output report
  `85 00 <slot> <region> 00 <page-addr> 00 00 <len LE16 = 00 04>` (acked
  `85 01` as an input report on `mi_01`), then for each 512-byte half: Feature `SET_REPORT`
  `83 00 00 <half 00|02> 00 02` (i.e. "send me 512 bytes at half-offset")
  followed by a `GET_REPORT` (Feature, wLength 642) whose response is the
  **raw data, no header**, 512 bytes then zero padding. Page addresses in
  256-byte units: `00,04,…,30` (13 pages) for region 02 and `00,04,…,2c`
  (12 pages) for region 03. A first `85 … 02 00` / `83 … 02 00` read of 2
  bytes returns `0f 00` (a header word). GG runs the full sweep for all 5
  slots at startup (~10 s) and **writes nothing** at startup.
- **Read-back validation:** the checksum algorithm above reproduces the
  stored CRC in all 5 keyboard-read slot images (in addition to the 7 write
  captures), and slot 1's region 03 read-back is byte-identical to what GG
  wrote. Flash beyond the CRC in page 12 reads back `0xFF` (erased) even
  though GG's write padded it with zeros — a writer must still replicate
  GG's transaction exactly, zeros included.
- The "bank 00 / bank 02" split used below is really "first half / second
  half of each page". Page *p*'s halves are stream-chunk *p* of each bank;
  the CRC lives in page 12, and the last 4 bytes of the image (frame 104)
  look like a second, constant CRC for region 03's (constant) contents.
- **Burst structure (CONFIRMED):** 104 Feature-report frames, alternating
  bank `00` (byte 3 = `00`) and bank `02` (byte 3 = `02`), 52 of each, in
  time order (frame 1 = bank 00 "Config 1", frame 2 = bank 02, ...). Each
  frame is `03 00 00 <bank> 00 02` + a **512-byte payload chunk** (bytes
  6–517) + zero padding to 642. Each bank is therefore a 26,624-byte buffer.
  Most chunks are empty. Bank 00 holds the profile name, keymap table and the
  per-key actuation table (stream offset ~1351+); bank 02 holds the macro area
  (stream offset 1402+).
- **Checksum — CRACKED and verified (7/7 captures, 5 distinct contents):**
  4 bytes at payload bytes 268–271 of **frame 25** (frame bytes 274–277),
  preceded by `01 00 00`. It is CRC-32, poly `0x04C11DB7`, **not reflected**,
  init `0xFFFFFFFF`, xorout `0`, computed over **32-bit little-endian words**
  (i.e. each 4-byte group byte-swapped — the STM32-style hardware CRC), of
  the payload bytes (frame bytes 6–517) of frames 1–24 followed by frame 25's
  payload bytes up to (not including) the checksum field — 12,556 bytes, in
  **transmission order** (the two banks interleaved as sent). The result is
  stored **little-endian**. Because the interleaved stream includes bank-02
  chunks, changing a macro (bank 02) changes this checksum.
- Bank 02's last 4 payload bytes (frame 104, frame bytes 514–517) are the
  constant `1b 6d f6 ec` in every capture, macros or not — a fixed
  marker, not a content checksum.

**Macros are stored on the keyboard.** Confirmed by quitting GG entirely:
the macros keep working. **Decision: macro writing is REQUIRED for this
project** (the author's call — I suggested dropping it and was overruled). The
integrity bytes have to be cracked offline first; nothing gets sent to the
keyboard until an offline checksum reproduces GG's captured values exactly.

**Offline builder — VERIFIED (`tools/ApexMacro verify`, 11/11 checks).**
Coordinates below are *payload* offsets in the region-02 image (frame byte =
payload + 6):
- keymap entries: 5-byte, from offset 85, stride 5 (F9 entry at 210, F10 at
  250); macro area base 2938; CRC field at 12556; the actuation table is in
  page 2's first half (2375–2512 differ between captures).
- Rebuilding a transaction from a captured image reproduces every one of
  the 164 host frames byte-for-byte (macro A, macro B, baseline).
- `baseline + [F9 = Q, hold 172ms]` == captured macro A, and
  `macro A + [F10 = W+Q chord, hold 141ms]` == captured macro B, both
  byte-for-byte including the recomputed CRC. (The baseline capture had
  different actuation settings than A/B, so the test first copies only the
  page-2 actuation bytes from the reference and reports how many.)
- Only known unknown in the builder: the `74 00 01 00` command sits before
  the two `90` commands in macro captures but after them in the baseline
  capture; the builder uses the macro-capture order (it is what GG did while
  saving macros). Macro-area capacity is also unknown — the tool refuses to
  write past 512 bytes of macro area until that's measured.
- Hardware sync rule for a future sender: wait for the ack on `mi_01`'s input report
  (`88 01` after begin, `05 01` after each commit, `01 00` after `eb`)
  before sending the next step; GG paces ~15 ms between Feature frames.

**Why this matters:** writing a macro means rewriting the *whole profile*
(name, keymap, macro area, per-frame integrity bytes), not one small
frame. Getting the integrity bytes wrong could produce a profile the
keyboard rejects or, worse, half-accepts. Nothing macro-related is
implemented in the tool, deliberately.

**Hardware read — VERIFIED (2026-09-19, `ApexMacro dump`).** Our own code
read all 5 slots from the real keyboard: firmware `4.16.8`, header word
`0f 00`, every slot's CRC valid, and all 10 images (5 slots × regions 02/03)
are **byte-identical** to what GG read at startup. Slot 1 = "Config 1" with 2
macros (F9 ptr 0, F10 ptr 5); slots 2–5 have none. Findings from getting
there:
- Replies/acks arrive on `mi_01`'s input report, not `mi_04` (see the
  correction above) — 131 replies seen on `mi_01`, 0 on `mi_04`.
- **Pacing matters at close-out:** after `89 01` the keyboard is busy for
  ~0.3 s; a `90` version query sent immediately is ignored (timeout). GG waits
  ~0.58 s after the last read before `69` and ~0.31 s between `89 01` and
  `41`; the tool now does the same. Data is saved before the close-out.
- Backups are in `backups/<timestamp>/` (`slotN-region02.bin` 13,312 B,
  `slotN-region03.bin` 12,288 B, `summary.txt`).

**First hardware write — VERIFIED (2026-09-19, `bind F11 Q --hold 100`).**
Compared a fresh full dump (`backups/20260918-222201`) against the original
dump byte-for-byte:
- slots 2–5 (both regions) and slot 1's region 03: **identical**.
- slot 1 region 02: keymap entry @95 `51 44 00 00 00` → `71 00 00 0c 00`; new
  record @2986 `77 01 03 00 01 01 0f 00 02 14 01 01 04 00 64 00 02 14 01 00`
  (Q down, 100 ms, Q up); CRC `364ee3b9` → `603c2b87` (valid; exactly the
  dry-run prediction). F9/F10 entries and records byte-identical to before.
  F11 types `q` on the keyboard.
- **Unexplained:** the 752 bytes after the CRC (offsets 12560–13311) were
  `0xFF` in every earlier dump (including after GG's own macro saves, whose
  frames carry zeros there) but read back `0x00` after *our* write, i.e. the
  keyboard stored exactly the zeros we sent. Outside the CRC-covered area and
  nothing visibly changed, but it means our write does not leave flash
  identical to a GG write. Candidate causes not yet tested: GG's later
  activity, firmware behavior around `89 01`, or something in how GG frames
  the tail. **Decision (author): always write `0xFF` there.** Implemented:
  `Writer.ImageFromDump` forces the 752 post-CRC bytes to `0xFF`; the diff
  display reports the tail explicitly; read-back verification now checks all
  13 KB of region 02 including the tail. To normalize the keyboard's current
  zeros, `restore` the post-write dump (`backups/20260918-222201`) — **done
  and read-back verified, F9/F10/F11 macros all working** — its
  CRC-covered data is written unchanged and the tail becomes `0xFF`.
  Pages 13–39 of region 02 (which GG also writes, as zeros) have never been
  read back by anyone, so their current content is unknown; the tool still
  sends zeros for them, exactly as GG does.

**Removing / replacing macros — verified offline AND on hardware
(2026-09-19: `unbind F11` worked; `bind F9 Q --hold 200` replace/re-pack
read back VERIFIED — F10 moved to ptr 0, F9 appended at ptr 7, CRC valid).
Hand-verified: F9/F10 behave correctly, and GG (reopened) displays the
edited state — F9 hold 200 ms, F10 intact, F11's macro gone — so GG parses
our records and syncs from the keyboard. Still uncaptured: GG's own
behavior when it deletes a macro.** `ApexMacro unbind <KEY>` and `bind <KEY> <CHORD>` on an
already-bound key (= replace). Design:
- A macro-bound keymap entry no longer contains its key code, so the key →
  entry offset comes from a fixed table (`KeymapLayout.cs`: 112 entries read
  from default-state slots 2/3/5, identical in all three, no duplicates;
  slot 1's default entries agree with it exactly).
- Remove = restore `51 <key> 00 00 00`, delete the record, **re-pack** the
  macro area contiguously (records keep their order), rewrite every macro
  entry's pointer, zero the freed bytes, recompute the CRC. Replace = remove
  then append (the replaced macro moves to the end).
- The editor refuses anything it doesn't fully understand: orphan/shared
  records, malformed records, unknown entry states, keys not in the table.
- **What the offline proofs establish:** removal is the exact inverse of
  adding — `macro-B minus F10` equals GG's captured `macro-A` (CRC included),
  and on the real keyboard `post-F11-write dump minus F11 macro` equals the
  `pre-F11-write dump`. Re-packing after removing the *first* macro,
  replacement, and refusals are checked with invariants (pointers, contiguous
  area, zeroed tail, valid CRC).
- **What is NOT established:** GG's own behavior when deleting/replacing a
  macro has never been captured. Removing the *last* macro is the exact
  inverse of a verified add; removing a *middle/first* macro (re-packing) is
  our design, not observed GG behavior. Also unknown whether GG re-syncs its
  own database from the keyboard, so editing with GG open/afterwards may
  overwrite our changes (GG rewrites whole profiles from its own copy).
- Observed side fact: in slot 1, 20 keymap entries that slots 2–5 have as
  defaults (PrintScreen `0x46`, ScrollLock `0x47`, Pause `0x48`, and keypad
  keys `0x53`–`0x63`) are blank (`00`). Not touched by any of our edits.

## Profile switching — CONFIRMED (captures: `profile-switch`, `profile-switch-2`, `gg-startup-config3`)

Switching the active profile (GG: double-click a profile in the on-board
profiles sidebar; no Save) is **only Output commands — no profile data, no
reads**:

    [69]   89 <slot>   (~0.31 s)   41   90   90 01

- **`89 <slot>` selects the active profile** (`89 01` … `89 05`; all five
  observed). `41` after a ~0.31 s pause, then the two version queries
  `90` / `90 01` (replies `34 2e 31 36 2e 38` = `4.16.8`).
- **The leading `69` is sent exactly when slot 1 is the source or the target
  of the switch** — 9 of 9 observed switches fit (1→2 ✓, 2→3 ✗, 3→1 ✓, 1→4 ✓,
  4→5 ✗, 5→3 ✗, 3→2 ✗, 2→5 ✗, 5→1 ✓). GG also sends `69` in every read-session
  close-out (`69, 89 <active>, 41, 90, 90 01`), including with Config 3 active.
  What `69` means is unknown.
- **The active profile cannot be read back.** With Config 3 active, GG's
  startup sees: `f4` reply still `00 01`, 2-byte header word still `0f 00`,
  and slots 2–5 plus every region 03 byte-identical to the startup taken with
  Config 1 active. GG simply re-selects the profile *it* remembers
  (`69, 89 03` at the end of its startup read). So there is no known
  "which profile is active?" query.
- **Consequences for our tools:** the closing `89 01` of `dump` / `bind` /
  `unbind` / `restore` (and the write transaction's own final `89 01`)
  activates Config 1 regardless of what was active before. `dump --activate N`
  chooses what a read-only dump leaves active; `profile N` switches.
- `tools/ApexMacro profile <1-5>` always sends the leading `69` (we cannot know
  the source slot; `69, 89 N` is GG-proven for targets 1–4 and `89 05` alone
  for 5). Offline-verified (`verify`, checks 11): its plan equals all 11
  captured GG sequences (9 switches + 2 session close-outs) byte-for-byte, and
  the `69` rule holds across both switch chains. **Hardware-verified in both
  directions:** `profile 2` (macros stopped, Config 2's lighting took over) and
  `profile 1` (macros back).
  Physical check: in the current setup only Config 1 has macros (F9 types q,
  F10 presses W+Q together).
## Implementation — `tools/ApexActuation`

`ApexActuation <mm> [KEY=mm ...]` (e.g. `ApexActuation 2.0 S=0.1`),
`--list` to print the table, `--dry-run` to print the report bytes without
sending. Verified: the dry-run output for `2.0 S=0.1` is byte-identical to
GG's captured per-key frame.

**Safety rule:** only raw values GG itself has been observed sending are
ever written. A first version interpolated between anchors; on hardware,
the synthesized values misbehaved (sluggish at one, key-repeat spam at
another). A requested mm now snaps to the nearest table entry.

**Observed-but-bad value:** `0x909c` (36988, actually 3.2mm) is real GG
traffic but caused key-repeat spam when set as a settled value. Excluded.
If the spam symptom shows up on another value, drop that entry rather than
troubleshoot — the table is built to fail safe by losing a value.

**Hardware-tested so far:** global mode with the old (mislabeled) table —
i.e. raw values for 2.0–3.6mm plus 4.0mm, all fine except 3.2mm. **Per-key
confirmed working:** `ApexActuation 2.0 S=0.1` made S trigger on a very
light press while every other key stayed at 2.0mm. Also confirmed
`ApexActuation 4.0 S=0.1` (everything else at the 4.0mm max, S light) —
a clean way to test individual low values by varying only S. **All table
values (0.1–3.6mm except the excluded 3.2mm, plus 4.0mm) have now been
hardware-tested on the real keyboard and work.**

## What's still UNKNOWN

- Whether the low end (0.1–0.3mm; note 0.2mm's raw is *below* 0.1mm's)
  behaves cleanly on hardware, and whether those labels are exactly right.
- 3.7–3.9mm raw values (never captured; **deliberately skipped** — author: nobody
  will use actuation points past the ones already supported).
- Whether Escape / F-row / arrows / nav keys have adjustable actuation via
  some other command, or are fixed.
- `mi_04` delivered 0 reports in a full 388-step session; unknown whether it ever emits (the earlier "heartbeat" theory came from mis-attributing `mi_01`'s input endpoint). Was: does it emit anything actuation-related, or is it purely the
  heartbeat/firmware-version notifications seen so far?
- Macros and profile switching — not yet captured.

None of this can come from public documentation — it needs a live capture of
SteelSeries GG actually using `mi_01`. See `CAPTURE_GUIDE.md`.

## SOCD ("Rapid Tap" in GG) - decoded (captures: socd-baseline, -a-d-last-input, -a-d-priority-key-1, -pair, -a-d-priority-key-2, -off, -no-pair, -pair-w-s-priority)

Reference transactions: `docs/reference/tx-socd-*.txt` (`ApexMacro verify`, checks 13-14). Every capture is an ordinary
profile save (the same 164-step write as a macro save). **The whole setting lives in region 02 at offsets 2887..2912**;
region 03 is identical in every capture. GG applies pairs and behaviours on **Save**.

Layout (all eight captures agree; applying any captured configuration to any other captured image reproduces GG's
save byte-for-byte, CRC included - 64/64 combinations):

- **2887 = on/off flag**: `01` on, `00` off (seen in `socd-off` and `socd-no-pair`).
- **2888 + 5*n (n = 0..4) = pair n**, five bytes: `[key 1 HID][key 2 HID][behaviour][00][00]`. Up to 5 pairs; an unused
  entry is all zeros. The last two bytes were `00` in every capture (meaning unknown, written as `00`).
- **behaviour**: `00` = Last Input Priority, `01` = key 1 priority, `02` = key 2 priority (GG's dropdown had these three).
  The second pair's behaviour byte sits at 2895 (`socd-pair-w-s-priority`: W+S with priority key 1 = `1A 16 01`).
- **Deleting the only pair**: GG switches the feature OFF (2887 = `00`) and leaves a placeholder pair Q + W
  (`14 1A 00`) in the first entry; we write exactly that for "no pairs".
- **Live command:** when the on/off switch is flipped GG first sends an Output command `1a 00` (off) or `1a 01` (on),
  no acknowledgement seen, and then does its normal save (`socd-off`, `socd-no-pair` have `1a 00`; `socd-a-d-last-input`
  has `1a 00` then `1a 01`; the saves that did not touch the switch have none). The app sends the same command just before
  its write when the flag changes. Whether the save alone would also switch the feature is untested.

Still unknown: the meaning of the two trailing bytes per entry; whether profiles 2-5 use the same block (the app only
writes Config 1); whether GG's dropdown has further options than the three seen (the app offers only these three).

## Writing other profiles - CONFIRMED for slot 2 (captures `profile2-macro-save`, `profile2-actuation-socd`)

GG saving Config 2 (references `docs/reference/tx-profile2-*.txt`) is the slot-1 transaction with the slot number
substituted: `88 00 02 02` begin, 40 page commits `05 00 02 02 ...`, `eb 02`, `88 00 02 03`, 12 commits
`05 00 02 03 ...`, then `89 02` (Config 2 is left active), `41`, `74 00 01 00`, `90`, `90 01`. The device's acks
are the same as for slot 1 (`88 01`, `05 01` per page, `01 00` after eb). `Transaction.Build(img, slot)` reproduces
both captures frame for frame (`ApexMacro verify`), and our editor applied to GG's slot 2 images reproduces GG's next image
(V's macro added / removed) including the CRC.
- GG writes the tail after the CRC as zeros (we keep writing 0xFF, as for slot 1) and rebuilds region 03 (lighting) from its own copy.
- The live actuation frame `31 47` is sent first when actuation changed, then the profile is saved with the same table inside it. **CONFIRMED**
  from `profile2-actuation-multikey` (eight keys, eight values; `docs/reference/tx-profile2-actuation-multikey.txt`): the table is two
  70-byte arrays in region 02, low bytes of each key's raw u16 at 2375+i and high bytes at 2445+i, with i = the key's rank among the 68
  adjustable keys ordered by the offset of their keymap-table entry (Q 15, W 16, E 17, A 29, S 30, LShift 42, V 47, Space 60). Entries 68/69
  are zero. Entry 57 (left Windows key, E3) always holds 0x3A3F in GG's saves and is left alone (E3 is sent live only). Applying our
  editor to GG's previous image with the table from GG's last live frame reproduces GG's next image byte-for-byte, CRC included
  (`ApexMacro verify`). The app's *Save to Config N...* sends GG's live frame, then writes the slot with these arrays changed.
- Our live actuation frames never appeared in slot 1's stored image (checked across the pre-write snapshots).
- SOCD on/off on Config 2 (captures `profile2-socd-on` / `-off`, `docs/reference/tx-profile2-socd-*.txt`): GG sends `1a 01` /`1a 00`
  first (an Output command, no reply), then the ordinary slot 2 save. The two images differ only in the flag byte (2887) and the CRC.
  Same as Config 1, so the app sends the live command before saving on any profile (`verify`: the planner reproduces both images,
  and the live frame, from GG's opposite-state image).