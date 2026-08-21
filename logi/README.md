# MX Creative Console → Energy

The aluminum dial on the MX Creative Console drives **ENERGY** on Ink Container: 0 is a still pond, 5 is the authored preset, 10 is a storm.

Three ways to wire it, in order of how likely they are to just work.

## 1. Logi Options+ key ticks (always works)

Options+ usually owns the HID device, so the native host cannot. Map the dial to the keys the sim already listens for.

1. Open **Logi Options+**
2. Select the **MX Creative Console Dialpad**
3. Make a profile for **Ink Container** (or Desktop, if you want it everywhere including the screensaver)
4. Add **System → Dial Adjustment**
5. Turn left → `[`  turn right → `]`

`[` / `]` are ignored by the screensaver exit handler, so you can ride the dial without waking the machine.

## 2. Direct HID (close Options+ for the console, or don’t let it claim the dial)

The native host (`InkContainer.exe` / `.scr`) opens Logitech vendor `046d` product `bc00` (Dialpad) or `c354` (Keypad) via hidapi. Screensaver and desktop both get analog ticks with no browser prompt — **if Options+ is not holding the device exclusive**.

If the dial does nothing, Options+ still has it. Use mapping (1).

## 3. HTTP (Logi Actions plugin / anything else)

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
| Mouse wheel on the sim | energy |
| `[` `]` `-` `=` `,` `.` PgUp/PgDn | energy |
