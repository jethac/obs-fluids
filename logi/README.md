# MX Creative Console → Energy

The aluminum dial on the MX Creative Console drives **ENERGY** on Ink Container: 0 is a still pond, 5 is the authored preset, 10 is a storm.

Three ways to wire it, in order of how likely they are to just work.

## 1. Logi Options+ key ticks (always works)

Options+ usually owns the HID device, so WebHID cannot. Map the dial to the keys the sim already listens for.

1. Open **Logi Options+**
2. Select the **MX Creative Console Dialpad**
3. Make a profile for **Ink Container** (or Desktop, if you want it everywhere including the screensaver)
4. Add **System → Dial Adjustment**
5. Turn left → `[`  turn right → `]`
6. Optional: Shift+those keys for fine steps (hold Shift while turning if your mapping allows)

`[` / `]` are ignored by the screensaver exit handler, so you can ride the dial without waking the machine.

## 2. Direct HID (close Options+ for the console, or don’t let it claim the dial)

In the Ink Container panel: **Arm MX Console**. Chrome/Edge will ask you to pick the Dialpad (`046d:bc00`) or Keypad (`046d:c354`). After that, ticks go straight into ENERGY.

The desktop host (`InkContainer.exe` / `.scr`) also opens those PIDs itself via HidSharp, so the screensaver gets analog ticks with no browser prompt — **if Options+ is not holding the device exclusive**.

If Arm MX Console says “HID blocked” or “No dial claimed”, Options+ still has it. Use mapping (1).

## 3. MIDI CC

**Arm MIDI** in the panel. Absolute CC 7 / 11 / 16 / 17 map 0–127 onto energy. Any other CC is treated as a 0–127 fader the first time it moves.

If you have a MIDI plugin for the MX Console, send CC 16.

## HTTP (Logi Actions plugin / anything else)

While `InkContainer.exe` or the screensaver is running:

```http
POST http://127.0.0.1:17331/energy
Content-Type: application/json

{"delta": 1}
```

or `{"energy": 0.7}` for absolute 0–1.

A Logi Actions SDK adjustment can POST that on every dial tick:

```js
applyAdjustment(_param, diff) {
  fetch("http://127.0.0.1:17331/energy", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ delta: diff })
  });
}
```

## Also

| | |
|---|---|
| On-screen brass pot | drag, or wheel over it |
| Mouse wheel on the sim | energy (Shift = fine) |
| `window.ink.setEnergy(0.8)` | from the devtools console |
