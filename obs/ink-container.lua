-- Ink Container
-- The supported OBS path is native: run InkContainer.exe --obs and Game Capture it.
-- This script remains as an optional Browser Source helper for the HTML demo.

obs = obslua

local preset = "demo"
local quality = "high"
local alpha = false
local width = 1920
local height = 1080

local function html_url()
  local dir = script_path()
  -- obs/ -> repo root -> web/fluid.html
  local path = dir .. "../web/fluid.html"
  path = path:gsub("\\", "/")
  if not path:match("^/") and not path:match("^%a:") then
    -- leave as-is
  end
  local q = "?mode=obs&preset=" .. preset .. "&quality=" .. quality
  if alpha then q = q .. "&alpha=1" end
  -- Browser Source local_file ignores query strings; use a file URL instead.
  if path:match("^%a:") then
    return "file:///" .. path .. q
  end
  return "file://" .. path .. q
end

function script_description()
  return [[Ink Container
GPU fluid simulation (Maya 2D-container look) as a Browser Source.

Click the button to add it to the current scene.
Turn OFF "Shutdown source when not visible" and
"Refresh browser when scene becomes active" on the source
so the sim keeps running.

alpha = overlay with density as transparency.]]
end

function script_properties()
  local props = obs.obs_properties_create()

  local p = obs.obs_properties_add_list(props, "preset", "Preset", obs.OBS_COMBO_TYPE_LIST, obs.OBS_COMBO_FORMAT_STRING)
  obs.obs_property_list_add_string(p, "Tech Demo", "demo")
  obs.obs_property_list_add_string(p, "Ink", "ink")
  obs.obs_property_list_add_string(p, "Cloud", "cloud")
  obs.obs_property_list_add_string(p, "Fire", "fire")
  obs.obs_property_list_add_string(p, "Fog", "fog")

  local q = obs.obs_properties_add_list(props, "quality", "Quality", obs.OBS_COMBO_TYPE_LIST, obs.OBS_COMBO_FORMAT_STRING)
  obs.obs_property_list_add_string(q, "Low", "low")
  obs.obs_property_list_add_string(q, "Medium", "medium")
  obs.obs_property_list_add_string(q, "High", "high")
  obs.obs_property_list_add_string(q, "Ultra", "ultra")

  obs.obs_properties_add_bool(props, "alpha", "Transparent overlay")
  obs.obs_properties_add_int(props, "width", "Width", 320, 3840, 1)
  obs.obs_properties_add_int(props, "height", "Height", 240, 2160, 1)
  obs.obs_properties_add_button(props, "create", "Add to current scene", create_source)
  return props
end

function script_defaults(settings)
  obs.obs_data_set_default_string(settings, "preset", "demo")
  obs.obs_data_set_default_string(settings, "quality", "high")
  obs.obs_data_set_default_bool(settings, "alpha", false)
  obs.obs_data_set_default_int(settings, "width", 1920)
  obs.obs_data_set_default_int(settings, "height", 1080)
end

function script_update(settings)
  preset = obs.obs_data_get_string(settings, "preset")
  quality = obs.obs_data_get_string(settings, "quality")
  alpha = obs.obs_data_get_bool(settings, "alpha")
  width = obs.obs_data_get_int(settings, "width")
  height = obs.obs_data_get_int(settings, "height")
end

function create_source(props, prop)
  local settings = obs.obs_data_create()
  obs.obs_data_set_bool(settings, "is_local_file", false)
  obs.obs_data_set_string(settings, "url", html_url())
  obs.obs_data_set_int(settings, "width", width)
  obs.obs_data_set_int(settings, "height", height)
  obs.obs_data_set_int(settings, "fps", 60)
  obs.obs_data_set_bool(settings, "shutdown", false)
  obs.obs_data_set_bool(settings, "restart_when_active", false)
  obs.obs_data_set_bool(settings, "reroute_audio", false)

  local source = obs.obs_source_create("browser_source", "Ink Container", settings, nil)
  local current = obs.obs_frontend_get_current_scene()
  if current ~= nil then
    local scene = obs.obs_scene_from_source(current)
    if scene ~= nil then
      obs.obs_scene_add(scene, source)
    end
    obs.obs_source_release(current)
  end
  obs.obs_source_release(source)
  obs.obs_data_release(settings)
  return true
end
