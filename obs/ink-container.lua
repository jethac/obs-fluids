-- Ink Container is a native Vulkan window, not a Browser Source.
--
-- 1. Run InkContainer.exe --obs  (borderless 1920×1080)
-- 2. OBS → Add → Game Capture → window "Ink Container"
--
-- ENERGY while it runs:
--   POST http://127.0.0.1:17331/energy
--   {"delta": 1}   or   {"energy": 0.7}

obs = obslua

function script_description()
  return [[Ink Container
Native Vulkan GPU fluid sim (Maya 2D-container look).

This is not a Browser Source. Start the native host, then capture it:

  InkContainer.exe --obs

In OBS: Add → Game Capture (preferred) or Window Capture
on the window titled "Ink Container".

ENERGY dial: see logi/README.md
  POST http://127.0.0.1:17331/energy  {"delta":1}]]
end
