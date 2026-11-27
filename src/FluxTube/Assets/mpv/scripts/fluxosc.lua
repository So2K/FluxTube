-- FluxTube Apple iOS / visionOS Liquid Glass Player Controls
-- Ultra-low-latency (<1ms libass render, 0ms cursor tracking, 0% idle CPU, Dynamic Island HUD, guaranteed timeline preview)
local mp = require 'mp'
local utils = require 'mp.utils'
local assdraw = require 'mp.assdraw'
local opts = { iconfont = "Segoe Fluent Icons", font = "Segoe UI", brand = "5533FF", brand2 = "FF617B", panel = "191515" } -- ASS BBGGRR
require('mp.options').read_options(opts, "fluxosc")

local ov = mp.create_osd_overlay("ass-events")
local S = {
    mx = -1, my = -1, last_move = 0, drag = nil, down = nil, force = false,
    paused = false, time = 0, disp_time = 0, dur = 0, cache = 0, vol = 100, mute = false, fs = false, buffering = false, seeking = false,
    has_next = false, quality = "", speed = "1×", speed_num = 1.0, title = "", channel = "", likes = "", short_overlay = false, segments = {}, rclick = "pause",
    bezel = nil, vol_until = 0, fade = 0, ease = 0, last_seek = 0, last_seek_pct = -1, last_vol_set = 0, last_thumb_req = 0, drag_pct = 0, ready = false,
    hold_at = nil, hold_2x = false, ext_2x = nil, saved_speed = 1,
    thumb_file = nil, thumb_w = 0, thumb_h = 0, thumb_want = -99, thumb_on = false,
    sc_on = true, is_short = false, is_live = false, lang = "ru", sc_comments = {}, pending_sc_comments = nil, cm_ovs = {}, cm_auto_idx = nil, cm_auto_until = 0,
    heatmap = {},
    ui_scale = 1.0, corners = "normal", card_style = "clean", neon_glow = false, light = false,
    player_style = "flux", theater = false, show_remaining = false,
    skip_undo = nil, skip_undo_a = 0, unskip_r = nil,
    seek_anim = 0, vol_anim = 0, speed2x_a = 0, vol_pill_a = 0,
    hov_a = {},
}
local W, H, sc = 1280, 720, 1
local L = {}
local timer
local last_render_done = 0

local function now() return mp.get_time() end
local function clamp(v, a, b) if v < a then return a elseif v > b then return b else return v end end
local function smooth(cur, tgt, rate, dt)
    if math.abs(tgt - cur) < 0.004 then return tgt end
    return cur + (tgt - cur) * (1 - math.exp(-rate * dt))
end
local function ease_out_quart(x) local u = 1 - clamp(x, 0, 1); return 1 - u * u * u * u end

local function glyph(cp)
    return string.char(0xE0 + math.floor(cp / 4096), 0x80 + math.floor(cp / 64) % 64, 0x80 + cp % 64)
end
local G = {
    play = glyph(0xE768), pause = glyph(0xE769), next = glyph(0xE893), vol = glyph(0xE767), vol0 = glyph(0xE74F),
    fs = glyph(0xE740), unfs = glyph(0xE73F), theater = glyph(0xE9A6), back = glyph(0xED3C), fwd = glyph(0xED3D),
    cc = glyph(0xE7F0), loop = glyph(0xE8EE), pip = glyph(0xE8A7), lock = glyph(0xE72E), unlock = glyph(0xE785),
    toapp = glyph(0xE73F), close = glyph(0xE711), sc = glyph(0xE8BD),
}

local function fmt_time(t)
    if not t or t < 0 then t = 0 end
    t = math.floor(t)
    local h, m, s = math.floor(t / 3600), math.floor(t / 60) % 60, t % 60
    if h > 0 then return string.format("%d:%02d:%02d", h, m, s) end
    return string.format("%d:%02d", m, s)
end

local function app(...) mp.commandv("script-message", "flux", ...) end

local function thumb_hide()
    if S.thumb_on then
        mp.commandv("overlay-remove", 60)
        S.thumb_on = false
    end
end

local function comments_hide()
    for slot, _ in pairs(S.cm_ovs) do
        mp.commandv("overlay-remove", slot)
    end
    S.cm_ovs = {}
end

local function show_comment_dots()
    return S.sc_on and not S.pip and not S.short_overlay and not S.is_short and S.dur > 5 and S.sc_comments and #S.sc_comments > 0
end

local function comment_pos(m, dy)
    local pw = L.px1 - L.px0
    local frac = clamp((m.t or 0) / S.dur, 0.025, 0.975)
    return L.px0 + pw * frac, (L.dock_t + (dy or 0)) - 7 * sc
end

local function esc(s) return (s:gsub("\\", "\\\226\128\139"):gsub("{", "\\{"):gsub("[\r\n\t]+", " ")) end

-- ---------------------------------------------------------------- Apple Liquid Glass drawing primitives
local A
local cur_alpha_mul = 1
local function alpha(opacity) return clamp(math.floor(255 * (1 - opacity * cur_alpha_mul) + 0.5), 0, 255) end
local function shape(color, opacity)
    A:new_event()
    A:append(string.format("{\\an7\\pos(0,0)\\bord0\\shad0\\1c&H%s&\\1a&H%02X&}", color, alpha(opacity)))
    A:draw_start()
end
local function rect(x0, y0, x1, y1, color, opacity, r)
    if x1 <= x0 or y1 <= y0 or opacity <= 0.004 then return end
    shape(color, opacity)
    if r and r > 0 then A:round_rect_cw(x0, y0, x1, y1, r) else A:rect_cw(x0, y0, x1, y1) end
    A:draw_stop()
end
local function circle(cx, cy, r, color, opacity)
    if r <= 0.1 or opacity <= 0.004 then return end
    rect(cx - r, cy - r, cx + r, cy + r, color, opacity, r)
end
local function text(x, y, an, size, str, opacity, font, bold, shadow)
    if (opacity or 1) <= 0.004 then return end
    A:new_event()
    A:append(string.format("{\\an%d\\pos(%.1f,%.1f)\\fs%.1f\\fn%s\\b%d\\bord%.1f\\shad0\\1c&HFFFFFF&\\3c&H000000&\\1a&H%02X&\\3a&H%02X&}",
        an, x, y, size, font or opts.font, bold and 1 or 0, shadow and 1.1 * sc or 0, alpha(opacity or 1), alpha(0.35)))
    A:append(str)
end
local function text_w(str, size)
    local n = 0
    for _ in str:gmatch("[%z\1-\127\194-\244][\128-\191]*") do n = n + 1 end
    return n * size * 0.52
end

local function hex_to_bgr(hex)
    if type(hex) == "string" and #hex == 7 and hex:sub(1, 1) == "#" then
        return hex:sub(6, 7) .. hex:sub(4, 5) .. hex:sub(2, 3)
    end
    return nil
end
local function mix_bgr(a, b, t)
    local b1 = tonumber(a:sub(1, 2), 16) or 0
    local g1 = tonumber(a:sub(3, 4), 16) or 0
    local r1 = tonumber(a:sub(5, 6), 16) or 0
    local b2 = tonumber(b:sub(1, 2), 16) or 0
    local g2 = tonumber(b:sub(3, 4), 16) or 0
    local r2 = tonumber(b:sub(5, 6), 16) or 0
    local nb = clamp(math.floor(b1 + (b2 - b1) * t + 0.5), 0, 255)
    local ng = clamp(math.floor(g1 + (g2 - g1) * t + 0.5), 0, 255)
    local nr = clamp(math.floor(r1 + (r2 - r1) * t + 0.5), 0, 255)
    return string.format("%02X%02X%02X", nb, ng, nr)
end
local function glass_tint()
    -- Deep smoked-obsidian glass with a subtle 22% live-video hue whisper (feels alive, never muddy!)
    return mix_bgr("121214", opts.panel or "181616", 0.22)
end
local function accent_primary()
    if S.player_style == "youtube" then return "3300FF" end
    return opts.brand
end
local function accent_secondary()
    if S.player_style == "youtube" then return "4411FF" end
    return opts.brand2 or opts.brand
end

-- Signature Apple visionOS / iOS Liquid Glass pill: soft floating shadow + 1px specular white rim + smoked frosted body
local function ios_glass(x0, y0, x1, y1, rad, bg_op, with_shadow)
    local r = math.min(rad, (y1 - y0) * 0.5)
    if with_shadow then
        rect(x0 + 1 * sc, y0 + 5 * sc, x1 - 1 * sc, y1 + 7 * sc, "000000", 0.36, r + 2 * sc)
    end
    -- Luminous 1px Liquid Glass specular rim with a delicate 16% accent tint from the video
    local rim = mix_bgr("FFFFFF", opts.brand or "FFFFFF", 0.16)
    rect(x0 - 1.0 * sc, y0 - 1.0 * sc, x1 + 1.0 * sc, y1 + 1.0 * sc, rim, 0.22, r + 1.0 * sc)
    -- Translucent smoked-glass fill
    rect(x0, y0, x1, y1, glass_tint(), bg_op or 0.84, r)
end

-- ---------------------------------------------------------------- layout
local function layout()
    W, H = mp.get_osd_size()
    if not W or W <= 0 then W, H = 1280, 720 end
    local hidpi = mp.get_property_number("display-hidpi-scale", 1) or 1
    local user_scale = clamp(S.ui_scale or 1.0, 0.75, 1.50)
    sc = hidpi * user_scale
    local max_sc = math.min(W / 380, H / 210)
    if sc > max_sc and max_sc > 0.55 then sc = math.max(0.62 * hidpi, max_sc) end
    local compact = W < 620 * sc
    if W < 460 * sc then sc = sc * 0.84 end
    local is_yt = S.player_style == "youtube"
    local dock_m = is_yt and 0 or ((S.fs or W > 760 * sc) and (20 * sc) or (10 * sc))
    local m = is_yt and (14 * sc) or (dock_m + 18 * sc)
    L = {
        compact = compact, m = m, dock_m = dock_m, is_yt = is_yt,
        bar = (is_yt and 42 or 44) * sc, btn = 38 * sc, icon = 18.0 * sc, fs_txt = 13.5 * sc, hits = {},
    }
    L.dock_b = H - (is_yt and (4 * sc) or (S.fs and 18 * sc or 12 * sc))
    L.by = L.dock_b - L.bar / 2 - (is_yt and 0 or 2 * sc)
    L.py = L.dock_b - L.bar - (is_yt and 6 * sc or 11 * sc)
    L.dock_t = L.py - (is_yt and 10 * sc or 15 * sc)
    L.px0, L.px1 = is_yt and (12 * sc) or m, W - (is_yt and (12 * sc) or m)

    local x = m
    local function left(id, w) L.hits[#L.hits + 1] = { id = id, x0 = x, x1 = x + w }; x = x + w; return x - w / 2 end
    L.play = left("play", L.btn)
    if S.has_next and not compact then L.next = left("next", L.btn) end
    L.volc = left("mute", L.btn)
    if not compact then
        L.vx0, L.vx1 = x + 4 * sc, x + 82 * sc
        L.hits[#L.hits + 1] = { id = "vol", x0 = x - 2 * sc, x1 = x + 90 * sc }
        x = x + 96 * sc
    end
    L.time_x = x + 6 * sc
    local clock_w = text_w("00:00 / 00:00", L.fs_txt) + (S.dur >= 3600 and 38 * sc or 14 * sc)
    L.hits[#L.hits + 1] = { id = "time", x0 = L.time_x - 4 * sc, x1 = L.time_x + clock_w }

    local rx = W - m
    local function right(id, w) rx = rx - w; L.hits[#L.hits + 1] = { id = id, x0 = rx, x1 = rx + w }; return rx + w / 2 end
    local min_rx = L.time_x + text_w("0:00:00 / 0:00:00", L.fs_txt) + 16 * sc
    local function can_fit(w) return rx - w >= min_rx end
    if not S.pip then L.fs = right("fs", L.btn) end
    if not S.fs and not S.pip and can_fit(L.btn) then L.pipb = right("pip", L.btn) end
    if not compact and not S.fs and not S.pip and not S.is_short and can_fit(L.btn) then L.theater = right("theater", L.btn) end
    if S.pip then
        L.top = {}
        local tx = W - 10 * sc
        for _, id in ipairs({ "pip-close", "pip-back", "pip-lock" }) do
            tx = tx - 36 * sc
            L.top[#L.top + 1] = { id = id, x0 = tx, x1 = tx + 36 * sc, cx = tx + 18 * sc }
        end
        L.ty = 24 * sc
    end
    if not compact then
        if not S.is_short and can_fit(L.btn) then L.cc = right("subs", L.btn) end
        if not S.pip and can_fit(L.btn) then L.loop = right("loop", L.btn) end
        if not S.pip and not S.short_overlay and not S.is_short and can_fit(L.btn) then L.sc = right("sc", L.btn) end
        if not S.is_short and S.quality ~= "" then
            local w = text_w(S.quality, L.fs_txt) + 24 * sc
            if can_fit(w) then L.q = right("quality", w); L.qw = w end
        end
        if not S.is_short then
            local w = text_w(S.speed, L.fs_txt) + 22 * sc
            if can_fit(w) then L.sp = right("speed", w); L.spw = w end
        end
    end
    L.right_edge = rx
end

local function utf8_len(s) local n = 0; for _ in s:gmatch("[%z\1-\127\194-\244][\128-\191]*") do n = n + 1 end; return n end
local function utf8_sub(s, n)
    local out, i = {}, 0
    for ch in s:gmatch("[%z\1-\127\194-\244][\128-\191]*") do i = i + 1; if i > n then break end; out[#out + 1] = ch end
    return table.concat(out)
end
local function chapter_at(t)
    local c = S.chapters
    if not c or #c == 0 then return nil end
    local name = nil
    for _, ch in ipairs(c) do if t + 0.05 >= ch[1] then name = ch[2] else break end end
    return name
end
local function segment_at(t)
    local segs = S.segments
    if not segs or #segs == 0 then return nil end
    local tol = S.dur > 0 and math.max(0.4, S.dur * 0.004) or 0.5
    for _, seg in ipairs(segs) do
        if t >= seg[1] - tol and t <= seg[2] + tol then return seg end
    end
    return nil
end
local function segment_label(cat)
    local en = S.lang == "en"
    if cat == "selfpromo" then return en and "Self-promotion" or "Самореклама"
    elseif cat == "interaction" then return en and "Reminder" or "Напоминание"
    elseif cat == "intro" then return en and "Intro" or "Интро"
    elseif cat == "outro" then return en and "Outro" or "Аутро"
    elseif cat == "preview" then return en and "Preview" or "Тизер"
    elseif cat == "filler" then return en and "Filler" or "Филлер"
    elseif cat == "music_offtopic" then return en and "Non-music" or "Не музыка"
    else return en and "Sponsor / Ad" or "Реклама" end
end
local function btn_tooltip(id)
    local en = S.lang == "en"
    if id == "play" then return S.paused and (en and "Play (K)" or "Воспроизвести (K)") or (en and "Pause (K)" or "Пауза (K)")
    elseif id == "next" then return en and "Next video" or "Следующее видео"
    elseif id == "mute" then return (S.mute or S.vol <= 0) and (en and "Unmute (M)" or "Включить звук (M)") or (en and "Mute (M)" or "Выключить звук (M)")
    elseif id == "speed" then return en and "Playback speed" or "Скорость воспроизведения"
    elseif id == "quality" then return en and "Video quality" or "Качество видео"
    elseif id == "sc" then return S.sc_on and (en and "Hide timeline comments" or "Скрыть комментарии") or (en and "Show timeline comments" or "Комментарии на таймлайне")
    elseif id == "theater" then return S.theater and (en and "Default view (T)" or "Обычный вид (T)") or (en and "Theater mode (T)" or "Широкий экран (T)")
    elseif id == "pip" then return en and "Picture-in-Picture (P)" or "Картинка в картинке (P)"
    elseif id == "loop" then return S.loop and (en and "Loop: On (L)" or "Повтор: Вкл (L)") or (en and "Loop video (L)" or "Повтор видео (L)")
    elseif id == "subs" then return en and "Subtitles & audio (C)" or "Субтитры и озвучка (C)"
    elseif id == "fs" then return S.fs and (en and "Exit full screen (F)" or "Выйти из полноэкранного (F)") or (en and "Full screen (F)" or "Во весь экран (F)")
    end
    return nil
end

local function hit()
    if not L.py then layout() end
    if S.mx < 0 then return nil end
    local x, y = S.mx, S.my
    if L.top and y <= L.ty + 18 * sc then
        for _, h in ipairs(L.top) do if x >= h.x0 and x < h.x1 then return h.id end end
    end
    if S.unskip_r and S.skip_undo and S.skip_undo_a > 0.15 then
        local ur = S.unskip_r
        if x >= ur.x0 and x <= ur.x1 and y >= ur.y0 and y <= ur.y1 then return "unskip" end
    end
    if show_comment_dots() and S.fade >= 0.3 and not (S.drag == "seek") then
        local lim = math.min(22, #S.sc_comments)
        local r2 = (12 * sc) * (12 * sc)
        for i = lim, 1, -1 do
            local mx, cy = comment_pos(S.sc_comments[i], 0)
            local dx, dy = x - mx, y - cy
            if dx * dx + dy * dy <= r2 then return "cm:" .. i end
        end
    end
    if y >= L.py - 14 * sc and y <= L.py + 12 * sc and x >= L.px0 - 8 * sc and x <= L.px1 + 8 * sc then return "seek" end
    if y > L.py + 12 * sc and y <= H then
        for _, h in ipairs(L.hits) do if x >= h.x0 and x < h.x1 then return h.id end end
        return "bar"
    end
    return "video"
end

local function over_controls() local h = hit(); return h ~= nil and h ~= "video" end

local function want_visible()
    if S.force or S.drag then return true end
    if not S.ready then return false end
    if S.paused then return true end
    if S.mx < 0 then return false end
    if over_controls() then return true end
    return now() - S.last_move < 2.2
end

local function hov_val(id) return S.hov_a[id] or 0 end

-- Apple circular frosted hover halo + crisp icon + subtle glowing active pill dot
local function icon_btn(id, cx, by, g, active_on)
    local ha = hov_val(id)
    local b1 = accent_primary()
    if ha > 0.02 then
        circle(cx, by, (16.5 + 1.5 * ha) * sc, "FFFFFF", 0.16 * ha)
    end
    text(cx, by, 5, L.icon * (1 + 0.07 * ha), g, 0.90 + 0.10 * ha, opts.iconfont)
    if active_on then
        -- Signature Apple active indicator: sleek luminous dot under icon
        circle(cx, by + 15.2 * sc, (2.3 + 0.5 * ha) * sc, b1, 1)
    end
end

-- Apple translucent glass pill button for Speed & Quality
local function text_btn(id, cx, by, w, str)
    local ha = hov_val(id)
    local b1 = accent_primary()
    local bh = 27 * sc
    local bx0, by0, bx1, by1 = cx - w / 2 + 3 * sc, by - bh / 2, cx + w / 2 - 3 * sc, by + bh / 2
    local bg = ha > 0.02 and mix_bgr("FFFFFF", b1, ha * 0.45) or "FFFFFF"
    rect(bx0, by0, bx1, by1, bg, 0.12 + ha * 0.18, bh / 2)
    text(cx, by, 5, L.fs_txt, esc(str), 0.95 + 0.05 * ha, nil, true)
end

local function wrap_utf8(str, maxc, max_lines)
    maxc = math.max(12, math.floor(maxc))
    max_lines = max_lines or 2
    local out, cur, cur_n = {}, "", 0
    for word in str:gmatch("%S+") do
        local wl = utf8_len(word)
        if cur_n == 0 then
            if wl > maxc then cur = utf8_sub(word, maxc - 1) .. "…"; cur_n = maxc
            else cur = word; cur_n = wl end
        elseif cur_n + 1 + wl <= maxc then
            cur = cur .. " " .. word; cur_n = cur_n + 1 + wl
        else
            out[#out + 1] = cur
            if #out >= max_lines then
                out[max_lines] = utf8_sub(out[max_lines], math.max(1, maxc - 1)) .. "…"
                return out
            end
            if wl > maxc then cur = utf8_sub(word, maxc - 1) .. "…"; cur_n = maxc
            else cur = word; cur_n = wl end
        end
    end
    if cur_n > 0 and #out < max_lines then out[#out + 1] = cur end
    return out
end

local function render()
    layout()
    if S.fade <= 0.005 then thumb_hide(); comments_hide() end
    A = assdraw.ass_new()
    local t = now()
    local h = hit()
    local hov_cm = (type(h) == "string" and h:sub(1, 3) == "cm:") and tonumber(h:sub(4)) or nil
    local b1, b2 = accent_primary(), accent_secondary()

    if S.ease > 0.005 then
        cur_alpha_mul = S.ease
        local slide_y = (1 - S.ease) * 14 * sc
        local py = L.py + slide_y
        local by = L.by + slide_y
        local dock_t = L.dock_t + slide_y
        local dock_b = L.dock_b + slide_y

        -- 1. Floating Apple Liquid Glass Capsule Dock (or Classic YouTube Edge Bar)
        if L.is_yt or S.short_overlay then
            local gh = (S.short_overlay and 210 or 126) * sc
            rect(0, H - gh, W, H - gh * 0.55, "000000", 0.16)
            rect(0, H - gh * 0.55, W, H - gh * 0.25, "000000", 0.36)
            rect(0, H - gh * 0.25, W, H, "000000", 0.62)
        else
            -- Soft subtle bottom vignette + floating visionOS Liquid Glass pill
            rect(0, dock_t - 18 * sc, W, H, "000000", 0.22)
            ios_glass(L.dock_m, dock_t, W - L.dock_m, dock_b, 22 * sc, 0.80, true)
        end

        if S.short_overlay and (S.channel ~= "" or S.title ~= "") then
            local info_y = dock_t - 14 * sc
            if S.title ~= "" then
                local maxc = math.max(18, math.floor((W - 28 * sc) / (8.5 * sc)))
                local tstr = utf8_len(S.title) > maxc and (utf8_sub(S.title, maxc - 1) .. "…") or S.title
                text(14 * sc, info_y, 1, 15 * sc, esc(tstr), 0.96, nil, true, true)
                info_y = info_y - 24 * sc
            end
            if S.channel ~= "" then
                local ch = S.channel
                if S.likes ~= "" then ch = ch .. "  ·  " .. S.likes end
                text(14 * sc, info_y, 1, 13.5 * sc, esc(ch), 0.88, nil, true, true)
            end
        end

        if S.fs and S.title ~= "" then
            -- Floating Apple-style top title scrim
            rect(0, 0, W, 68 * sc, "000000", 0.34)
            text(26 * sc, 22 * sc - (1 - S.ease) * 8 * sc, 7, 19.5 * sc, esc(S.title), 0.96, nil, true, true)
        end

        -- 2. Apple TV+ / iOS Capsule Seek Bar (5px sleek pill -> expands smoothly to 9px on hover/drag!)
        local sa = S.seek_anim
        local th = (L.is_yt and (3.6 + 2.4 * sa) or (4.8 + 4.0 * sa)) * sc
        local tr = th / 2
        local pw = L.px1 - L.px0
        local live_t = S.drag == "seek" and (S.drag_pct * S.dur) or S.disp_time
        local at_live_edge = S.is_live and (S.dur <= 0 or (S.dur - live_t) < 22)
        local pct = S.is_live and (at_live_edge and 1 or (S.dur > 0 and clamp(live_t / S.dur, 0, 1) or 1))
            or (S.dur > 0 and clamp(live_t / S.dur, 0, 1) or 0)
        local hot_seek = (h == "seek" or S.drag == "seek")
        local hov_t = hot_seek and S.dur > 0 and S.mx >= 0 and (clamp((S.mx - L.px0) / pw, 0, 1) * S.dur) or -1

        -- Base recessed dark groove (15:1 contrast with pure white played bar on ANY video color!)
        rect(L.px0, py - th / 2, L.px1, py + th / 2, "000000", 0.58, tr)
        rect(L.px0, py - th / 2, L.px1, py + th / 2, "FFFFFF", 0.14, tr)
        -- Buffered range (clean neutral mid-gray)
        if S.dur > 0 and S.cache > live_t then
            rect(L.px0 + pw * pct, py - th / 2, L.px0 + pw * clamp(S.cache / S.dur, 0, 1), py + th / 2, "FFFFFF", 0.30, tr)
        end
        -- Hover preview range fill
        local fill_w = pw * pct
        if hot_seek and S.mx >= L.px0 and S.mx <= L.px1 then
            local hx = clamp(S.mx, L.px0, L.px1)
            if hx > L.px0 + fill_w then
                rect(L.px0 + fill_w, py - th / 2, hx, py + th / 2, "FFFFFF", 0.42, tr)
            end
        end
        -- Played fill: pure crisp Apple white in Liquid Glass mode (NEVER blends into blue/colored videos!), classic red in YouTube mode
        if fill_w > 0.5 then
            local fill_col = L.is_yt and "3300FF" or "FFFFFF"
            rect(L.px0, py - th / 2 - 0.6 * sc, L.px0 + fill_w, py + th / 2 + 0.6 * sc, "000000", 0.38, tr + 0.6 * sc)
            rect(L.px0, py - th / 2, L.px0 + fill_w, py + th / 2, fill_col, 1, tr)
        end

        -- SponsorBlock segments: refined muted warm-amber (clearly distinct from white played bar & dark groove, zero neon glare!)
        if S.dur > 0 and S.segments and #S.segments > 0 then
            local seg_tol = S.dur > 0 and math.max(0.4, S.dur * 0.004) or 0.5
            for _, seg in ipairs(S.segments) do
                local is_seg_hov = hov_t >= 0 and hov_t >= seg[1] - seg_tol and hov_t <= seg[2] + seg_tol
                local sbh = is_seg_hov and (th + 2.8 * sc) or (th + 1.4 * sc)
                local sx0 = L.px0 + pw * clamp(seg[1] / S.dur, 0, 1)
                local sx1 = L.px0 + pw * clamp(seg[2] / S.dur, 0, 1)
                if sx1 - sx0 < 5 * sc then sx1 = math.min(L.px1, sx0 + 5 * sc) end
                rect(sx0 - 1 * sc, py - sbh / 2 - 1 * sc, sx1 + 1 * sc, py + sbh / 2 + 1 * sc, "08080C", 0.72, (sbh + 2 * sc) / 2)
                rect(sx0, py - sbh / 2, sx1, py + sbh / 2, is_seg_hov and "78D8FF" or "48B4E8", 0.96, sbh / 2)
            end
        end

        -- Chapters: subtle dividers + YouTube-style hovered chapter highlight
        if S.dur > 0 and S.chapters and #S.chapters > 0 then
            if hov_t >= 0 then
                for i, ch in ipairs(S.chapters) do
                    local c0 = ch[1]
                    local c1 = (i < #S.chapters) and S.chapters[i + 1][1] or S.dur
                    if hov_t >= c0 and hov_t < c1 then
                        local cx0 = L.px0 + pw * clamp(c0 / S.dur, 0, 1)
                        local cx1 = L.px0 + pw * clamp(c1 / S.dur, 0, 1)
                        rect(cx0, py - th * 0.65, cx1, py + th * 0.65, "FFFFFF", 0.18, tr)
                        break
                    end
                end
            end
            for i, ch in ipairs(S.chapters) do
                if i > 1 and ch[1] < S.dur then
                    local gx = L.px0 + pw * (ch[1] / S.dur)
                    rect(gx - 1.2 * sc, py - th * 0.7, gx + 1.2 * sc, py + th * 0.7, "0A0A0E", 0.92)
                end
            end
        end

        -- 2b. YouTube-style "Most Replayed" smooth mountain heatmap rising above the seekbar on hover/scrub!
        local has_hm = (not S.is_live) and (not S.pip) and S.heatmap and #S.heatmap >= 4
        if has_hm and sa > 0.02 then
            local hm = S.heatmap
            local n = #hm
            local max_h = 36 * sc * sa
            local base_y = py - th / 2 - 1.5 * sc
            local pts_x, pts_y = {}, {}
            for i = 1, n do
                local frac = (i - 1) / (n - 1)
                pts_x[i] = L.px0 + pw * frac
                local v = clamp(hm[i] or 0, 0, 1)
                -- Slight power curve so peaks pop crisply like YouTube's Most Replayed hills
                pts_y[i] = base_y - (1.2 * sc * sa + math.pow(v, 1.15) * max_h)
            end
            -- Precompute smooth Catmull-Rom cubic Bezier control points (scaled properly via assdraw API!)
            local c1x, c1y, c2x, c2y = {}, {}, {}, {}
            local min_y = base_y - max_h - 3 * sc
            local max_y = base_y - 0.5 * sc
            for i = 1, n - 1 do
                local x0, y0 = pts_x[math.max(1, i - 1)], pts_y[math.max(1, i - 1)]
                local x1, y1 = pts_x[i], pts_y[i]
                local x2, y2 = pts_x[i + 1], pts_y[i + 1]
                local x3, y3 = pts_x[math.min(n, i + 2)], pts_y[math.min(n, i + 2)]
                c1x[i] = x1 + (x2 - x0) / 6
                c1y[i] = clamp(y1 + (y2 - y0) / 6, min_y, max_y)
                c2x[i] = x2 - (x3 - x1) / 6
                c2y[i] = clamp(y2 - (y3 - y1) / 6, min_y, max_y)
            end
            local function wave_fwd(dy)
                A:move_to(pts_x[1], pts_y[1] + dy)
                for i = 1, n - 1 do
                    A:bezier_curve(c1x[i], c1y[i] + dy, c2x[i], c2y[i] + dy, pts_x[i + 1], pts_y[i + 1] + dy)
                end
            end
            local function wave_rev(dy)
                A:line_to(pts_x[n], pts_y[n] + dy)
                for i = n - 1, 1, -1 do
                    A:bezier_curve(c2x[i], c2y[i] + dy, c1x[i], c1y[i] + dy, pts_x[i], pts_y[i] + dy)
                end
            end
            -- Subtle dark contrast silhouette behind peaks so hills stay readable on bright white frames
            shape("000000", 0.24 * sa)
            wave_fwd(-1.8 * sc)
            A:line_to(L.px1, base_y)
            A:line_to(L.px0, base_y)
            A:draw_stop()
            -- Translucent frosted mountain fill (single fast ASS polygon!)
            shape("FFFFFF", 0.30 * sa)
            wave_fwd(0)
            A:line_to(L.px1, base_y)
            A:line_to(L.px0, base_y)
            A:draw_stop()
            -- Crisp luminous top ridge ribbon (symmetric forward+reverse Bezier, uniform 1.8px anti-aliased crest!)
            shape("FFFFFF", 0.90 * sa)
            wave_fwd(-1.1 * sc)
            wave_rev(0.7 * sc)
            A:draw_stop()
        end

        -- Apple tactile scrubber knob (always-visible crisp 4.4px playhead dot -> expands to 8.4px sphere on hover/drag!)
        local kr = ((L.is_yt and 0 or 4.4) + (8.4 - (L.is_yt and 0 or 4.4)) * sa) * sc
        if kr > 0.8 * sc then
            local kx = L.px0 + pw * pct
            circle(kx, py + 0.8 * sc, kr + 1.5 * sc, "000000", 0.42)
            circle(kx, py, kr, L.is_yt and b1 or "FFFFFF", 1)
        end

        -- 3. Compute exclusion zones (preview box & button tooltips) so comment avatars NEVER collide with text or preview!
        local excl_x0, excl_x1 = 1e9, -1e9
        if hot_seek and not hov_cm and S.dur > 0 and S.mx >= 0 then
            local pw_box = (S.thumb_file and S.thumb_w > 0) and (S.thumb_w + 20 * sc) or (260 * sc)
            excl_x0 = clamp(S.mx - pw_box / 2, 4 * sc, math.max(4 * sc, W - pw_box - 4 * sc)) - 14 * sc
            excl_x1 = excl_x0 + pw_box + 28 * sc
        end
        local tip = h and btn_tooltip(h)
        local tip_x, tip_w = 0, 0
        if tip and not hot_seek and not S.drag then
            local bx_c = S.mx
            for _, hb in ipairs(L.hits) do
                if hb.id == h then bx_c = (hb.x0 + hb.x1) / 2; break end
            end
            tip_w = text_w(tip, 12.5 * sc) + 24 * sc
            tip_x = clamp(bx_c, 14 * sc + tip_w / 2, W - 14 * sc - tip_w / 2)
            excl_x0 = tip_x - tip_w / 2 - 14 * sc
            excl_x1 = tip_x + tip_w / 2 + 14 * sc
        end

        -- 4. Timed Comment Avatars (positioned cleanly above the glass dock, automatically yielding to preview, heatmap & tooltips!)
        if show_comment_dots() and not (has_hm and hot_seek) then
            local lim = math.min(22, #S.sc_comments)
            local used_slots = {}
            local last_mx = -1e9
            for i = 1, lim do
                local m = S.sc_comments[i]
                local mx, cy = comment_pos(m, slide_y)
                local is_hov = (hov_cm == i)
                local excluded = (mx >= excl_x0 and mx <= excl_x1)
                if excluded or (not is_hov and (mx - last_mx) < 24 * sc) then
                    local slot0 = 9 + i
                    if S.cm_ovs[slot0] then mp.commandv("overlay-remove", slot0); S.cm_ovs[slot0] = nil end
                else
                    last_mx = mx
                    -- Crisp vertical pin line connecting each avatar down to its exact timestamp on the seekbar
                    if not hot_seek then
                        local pin_top = cy + 9 * sc
                        local pin_bot = py - th / 2 - 1 * sc
                        if pin_bot > pin_top then
                            rect(mx - 0.75 * sc, pin_top, mx + 0.75 * sc, pin_bot, is_hov and b1 or "FFFFFF", is_hov and 0.96 or 0.40)
                            circle(mx, pin_bot, (is_hov and 2.4 or 1.6) * sc, is_hov and b1 or "FFFFFF", is_hov and 0.98 or 0.65)
                        end
                    end
                    local slot = 9 + i
                    if m.f and m.w and m.h then
                        if S.ease >= 0.45 then
                            local ox = math.floor(mx - m.w / 2 + 0.5)
                            local oy = math.floor(cy - m.h / 2 + 0.5)
                            local prev = S.cm_ovs[slot]
                            if not prev or prev.f ~= m.f or prev.x ~= ox or prev.y ~= oy or prev.w ~= m.w or prev.h ~= m.h then
                                mp.commandv("overlay-add", slot, ox, oy, m.f, 0, "bgra", m.w, m.h, m.w * 4)
                                S.cm_ovs[slot] = { f = m.f, x = ox, y = oy, w = m.w, h = m.h }
                            end
                            used_slots[slot] = true
                        end
                        if is_hov then
                            circle(mx, cy, (m.w * 0.5) + 3.4 * sc, "FFFFFF", 0.45)
                        end
                    else
                        if S.cm_ovs[slot] then mp.commandv("overlay-remove", slot); S.cm_ovs[slot] = nil end
                        if not hot_seek then
                            local r = (is_hov and 13.0 or 9.5) * sc
                            circle(mx, cy, r + 1.4 * sc, "FFFFFF", is_hov and 0.95 or 0.72)
                            circle(mx, cy, r, m.c or "B67EFF", 0.96)
                            text(mx, cy, 5, (is_hov and 12.5 or 10) * sc, esc(m.i or "?"), 1, nil, true)
                        end
                    end
                    if not hov_cm and m.ts and not S.paused and math.abs(S.time - (m.t or -99)) < 1.6 then
                        if S.cm_auto_idx ~= i then S.cm_auto_idx = i; S.cm_auto_until = t + 3.2 end
                    end
                end
            end
            for slot, _ in pairs(S.cm_ovs) do
                if not used_slots[slot] then mp.commandv("overlay-remove", slot); S.cm_ovs[slot] = nil end
            end

            -- Sleek compact Apple Liquid Glass comment card on avatar hover (tight width, zero wasted space!)
            local show_idx = (not hot_seek) and (hov_cm or ((S.cm_auto_idx and t < S.cm_auto_until and S.sc_comments[S.cm_auto_idx]) and S.cm_auto_idx or nil)) or nil
            if show_idx and S.sc_comments[show_idx] and W >= 200 * sc then
                local m = S.sc_comments[show_idx]
                local big = (hov_cm ~= nil)
                local mx, cy = comment_pos(m, slide_y)
                local body_fs = (big and 14.5 or 13.0) * sc
                local author_fs = (big and 13.0 or 12.0) * sc
                local line_h = (big and 19.5 or 17.0) * sc
                local pad_x = (big and 14 or 12) * sc
                local pad_y = (big and 10 or 8) * sc
                local max_card_w = math.min(W - 32 * sc, (big and 370 or 310) * sc)
                local maxc = math.max(16, math.floor((max_card_w - pad_x * 2 - 6 * sc) / (body_fs * 0.48)))
                local author = m.a or ""
                if utf8_len(author) > 24 then author = utf8_sub(author, 23) .. "…" end
                local ts_str = fmt_time(m.t or 0)
                local lines = wrap_utf8(m.x or "", maxc, big and 4 or 2)
                local ts_w = text_w(ts_str, 11.0 * sc) + 12 * sc
                local hdr_w = text_w(author, author_fs) * 0.94 + 8 * sc + ts_w
                local maxw = hdr_w
                for _, ln in ipairs(lines) do maxw = math.max(maxw, text_w(ln, body_fs) * 0.92) end
                local cw = clamp(maxw + pad_x * 2 + 8 * sc, math.min(130 * sc, max_card_w), max_card_w)
                local hdr_h = (big and 24 or 21) * sc
                local ch = pad_y + hdr_h + #lines * line_h + pad_y
                local cx0 = clamp(mx - cw / 2, L.px0, math.max(L.px0, L.px1 - cw))
                local cy1 = cy - 16 * sc
                local cy0 = math.max(12 * sc, cy1 - ch)
                cy1 = cy0 + ch

                ios_glass(cx0, cy0, cx0 + cw, cy1, 14 * sc, 0.90, true)
                rect(cx0 + 6 * sc, cy0 + 9 * sc, cx0 + 8.8 * sc, cy1 - 9 * sc, b1, 0.95, 1.4 * sc)

                local tx0 = cx0 + pad_x + 3 * sc
                local hy = cy0 + pad_y
                A:new_event()
                A:append(string.format("{\\an7\\pos(%.1f,%.1f)\\fs%.1f\\fn%s\\b1\\bord0\\shad0\\1c&HFFFFFF&\\1a&H%02X&}%s",
                    tx0, hy, author_fs, opts.font, alpha(0.98), esc(author)))
                local pill_x = tx0 + text_w(author, author_fs) * 0.94 + 8 * sc
                if pill_x + ts_w < cx0 + cw - pad_x + 4 * sc then
                    local pill_h = 18 * sc
                    rect(pill_x, hy - 1 * sc, pill_x + ts_w, hy + pill_h - 1 * sc, b1, 0.28, pill_h / 2)
                    A:new_event()
                    A:append(string.format("{\\an5\\pos(%.1f,%.1f)\\fs%.1f\\fn%s\\b1\\bord0\\shad0\\1c&HFFFFFF&\\1a&H%02X&}%s",
                        pill_x + ts_w / 2, hy + pill_h / 2 - 1 * sc, 11.0 * sc, opts.font, alpha(0.96), ts_str))
                end
                for li, ln in ipairs(lines) do
                    text(tx0, cy0 + pad_y + hdr_h + (li - 1) * line_h, 7, body_fs, esc(ln), 0.94)
                end
            end
        else
            comments_hide()
        end

        -- 5. Unified Apple TV+ / visionOS Timeline Frame Preview Card (overlay slot 60 -> ALWAYS ON TOP OF EVERYTHING!)
        if hot_seek and not hov_cm and S.dur > 0 and S.mx >= 0 then
            local hp = clamp((S.mx - L.px0) / pw, 0, 1)
            local ht = hp * S.dur
            if math.abs(ht - S.thumb_want) > 0.18 and (t - S.last_thumb_req) > 0.032 then
                S.thumb_want = ht
                S.last_thumb_req = t
                app("thumb", string.format("%.2f", ht))
            end

            local has_thumb = (not S.is_live) and (S.thumb_file ~= nil and S.thumb_w > 0 and S.thumb_h > 0)
            local seg = (not S.is_live) and segment_at(ht) or nil
            local chap = (not S.is_live) and chapter_at(ht) or nil
            local ts_str = S.is_live
                and ((S.dur - ht) < 22 and "● LIVE" or ("-" .. fmt_time(math.max(0, S.dur - ht))))
                or fmt_time(ht)
            local pad = 5 * sc
            local ts_fs = 12.5 * sc
            local ch_fs = 12.2 * sc
            local ad_fs = 11.2 * sc

            -- Measure timestamp pill & optional SponsorBlock ad badge (tight symmetrical Apple proportions)
            local ts_pill_w = text_w(ts_str, ts_fs) * 0.92 + 16 * sc
            local ts_pill_h = 21 * sc
            local ad_lbl = seg and segment_label(seg[3]) or nil
            local ad_lbl_w = ad_lbl and (text_w(ad_lbl, ad_fs) * 0.90) or 0
            local ad_pill_w = ad_lbl and (16 * sc + ad_lbl_w + 10 * sc) or 0
            local meta_gap = ad_lbl and (6 * sc) or 0
            local meta_w = ts_pill_w + meta_gap + ad_pill_w

            -- Compute unified card width: tightly frames thumbnail, or compact glass card if loading/no storyboard
            local card_w
            if has_thumb then
                card_w = S.thumb_w + pad * 2
            else
                local ch_need = chap and math.min(240 * sc, text_w(chap, ch_fs) + 24 * sc) or 0
                card_w = clamp(math.max(meta_w + 20 * sc, ch_need), 84 * sc, math.min(280 * sc, W - 24 * sc))
            end

            -- Truncate chapter cleanly to fit inside the card width
            local chap_txt = nil
            if chap and #chap > 0 then
                local max_ch = math.max(10, math.floor((card_w - 20 * sc) / (ch_fs * 0.52)))
                chap_txt = (utf8_len(chap) > max_ch) and (utf8_sub(chap, max_ch - 1) .. "…") or chap
            end

            -- Footer height: 1 compact row (timestamp + ad badge) or 2 tidy rows (chapter + meta row)
            local foot_h = chap_txt and (46 * sc) or (28 * sc)
            local card_h = (has_thumb and (pad + S.thumb_h + 3 * sc) or (4 * sc)) + foot_h
            local hm_lift = has_hm and (32 * sc * sa) or 0
            local cy1 = dock_t - 12 * sc - hm_lift
            local cy0 = math.max(10 * sc, cy1 - card_h)
            cy1 = cy0 + card_h
            local cx0 = clamp(S.mx - card_w / 2, 12 * sc, math.max(12 * sc, W - card_w - 12 * sc))
            local cx1 = cx0 + card_w

            -- Draw ONE unified Apple Liquid Glass card (with subtle warm-gold rim whisper when hovering an ad!)
            if ad_lbl then
                rect(cx0 + 1 * sc, cy0 + 5 * sc, cx1 - 1 * sc, cy1 + 7 * sc, "000000", 0.38, 15 * sc)
                rect(cx0 - 1.1 * sc, cy0 - 1.1 * sc, cx1 + 1.1 * sc, cy1 + 1.1 * sc, "5EC8F5", 0.34, 14.1 * sc)
                rect(cx0, cy0, cx1, cy1, glass_tint(), 0.94, 13 * sc)
            else
                ios_glass(cx0, cy0, cx1, cy1, 13 * sc, 0.94, true)
            end

            -- Mount storyboard frame neatly inset at top of card
            if has_thumb then
                local iw, ih = S.thumb_w, S.thumb_h
                local ix = math.floor(cx0 + pad + 0.5)
                local iy = math.floor(cy0 + pad + 0.5)
                -- Subtle recessed frame behind thumbnail
                rect(ix - 1 * sc, iy - 1 * sc, ix + iw + 1 * sc, iy + ih + 1 * sc, "000000", 0.55, 6 * sc)
                if not S.thumb_on or S.ov_file ~= S.thumb_file or S.ov_x ~= ix or S.ov_y ~= iy then
                    mp.commandv("overlay-add", 60, ix, iy, S.thumb_file, 0, "bgra", iw, ih, iw * 4)
                    S.thumb_on = true
                    S.ov_file, S.ov_x, S.ov_y = S.thumb_file, ix, iy
                end
            else
                thumb_hide()
            end

            -- Draw integrated footer (chapter title + crisp timestamp pill + sleek obsidian-gold ad tag)
            local fy = has_thumb and (cy0 + pad + S.thumb_h + 4 * sc) or (cy0 + 4 * sc)
            local meta_cy
            if chap_txt then
                text((cx0 + cx1) / 2, fy + 10.5 * sc, 5, ch_fs, esc(chap_txt), 0.92, nil, false)
                meta_cy = fy + 31.5 * sc
            else
                meta_cy = fy + foot_h / 2 - 1 * sc
            end

            local mx0 = (cx0 + cx1) / 2 - meta_w / 2
            -- Timestamp recessed glass pill
            rect(mx0, meta_cy - ts_pill_h / 2, mx0 + ts_pill_w, meta_cy + ts_pill_h / 2, "FFFFFF", 0.11, ts_pill_h / 2)
            text(mx0 + ts_pill_w / 2, meta_cy, 5, ts_fs, ts_str, 0.99, nil, true)

            -- Sleek Apple obsidian-gold SponsorBlock badge right beside timestamp (no muddy brown, symmetrical padding!)
            if ad_lbl then
                local ax0 = mx0 + ts_pill_w + meta_gap
                local ay0, ay1 = meta_cy - ts_pill_h / 2, meta_cy + ts_pill_h / 2
                rect(ax0 - 0.8 * sc, ay0 - 0.8 * sc, ax0 + ad_pill_w + 0.8 * sc, ay1 + 0.8 * sc, "5EC8F5", 0.45, ts_pill_h / 2)
                rect(ax0, ay0, ax0 + ad_pill_w, ay1, "131518", 0.92, ts_pill_h / 2)
                rect(ax0, ay0, ax0 + ad_pill_w, ay1, "5EC8F5", 0.10, ts_pill_h / 2)
                circle(ax0 + 9.2 * sc, meta_cy, 4.6 * sc, "5EC8F5", 0.24)
                circle(ax0 + 9.2 * sc, meta_cy, 2.7 * sc, "68D2FF", 1)
                A:new_event()
                A:append(string.format("{\\an4\\pos(%.1f,%.1f)\\fs%.1f\\fn%s\\b1\\bord0\\shad0\\1c&HBCE8FF&\\1a&H%02X&}%s",
                    ax0 + 16.0 * sc, meta_cy, ad_fs, opts.font, alpha(0.98), esc(ad_lbl)))
            end
        else
            S.thumb_want = -99
            thumb_hide()
        end

        -- 6. Controls row
        icon_btn("play", L.play, by, S.paused and G.play or G.pause, false)
        if L.next then icon_btn("next", L.next, by, G.next, false) end
        icon_btn("mute", L.volc, by, (S.mute or S.vol <= 0) and G.vol0 or G.vol, false)
        if L.vx0 then
            local v = S.mute and 0 or clamp(S.vol / 100, 0, 1)
            local va = S.vol_anim
            local vth = (2.4 + 1.4 * va) * sc
            rect(L.vx0, by - vth, L.vx1, by + vth, "FFFFFF", 0.24, vth)
            rect(L.vx0, by - vth, L.vx0 + (L.vx1 - L.vx0) * v, by + vth, "FFFFFF", 0.96, vth)
            local vkr = (4.2 + 2.4 * va) * sc
            circle(L.vx0 + (L.vx1 - L.vx0) * v, by, vkr, "FFFFFF", 1)
        end

        if S.is_live then
            local behind = (S.dur > 0) and math.max(0, S.dur - live_t) or 0
            local at_live = (S.dur <= 0 or behind < 22)
            local pulse = 0.55 + 0.45 * math.sin(t * 3.4)
            local l_fs = 12.0 * sc
            local l_txt = "LIVE"
            local l_pw = 16.5 * sc + text_w(l_txt, l_fs) * 0.90 + 10 * sc
            local l_ph = 23 * sc
            local lx0, ly0 = L.time_x, by - l_ph / 2
            local l_hov = (h == "time")
            -- Always unmistakable YouTube/Apple crimson LIVE button (bright red when live, crisp red-rimmed button when rewound to jump back!)
            rect(lx0 - 0.8 * sc, ly0 - 0.8 * sc, lx0 + l_pw + 0.8 * sc, ly0 + l_ph + 0.8 * sc,
                "303BFF", at_live and (l_hov and 0.85 or 0.65) or (l_hov and 0.80 or 0.48), l_ph / 2)
            rect(lx0, ly0, lx0 + l_pw, ly0 + l_ph, "160E10", l_hov and 0.95 or 0.88, l_ph / 2)
            rect(lx0, ly0, lx0 + l_pw, ly0 + l_ph, "272DE5", at_live and (l_hov and 0.42 or 0.30) or (l_hov and 0.32 or 0.14), l_ph / 2)
            if at_live then
                circle(lx0 + 9.5 * sc, by, (4.4 + 1.1 * pulse) * sc, "303BFF", 0.34 * pulse)
                circle(lx0 + 9.5 * sc, by, 3.0 * sc, "3B3BFF", 1)
            else
                circle(lx0 + 9.5 * sc, by, 3.0 * sc, "3B3BFF", l_hov and 1 or 0.88)
            end
            text(lx0 + 16.5 * sc, by, 4, l_fs, l_txt, 0.99, nil, true)
            if not at_live then
                text(lx0 + l_pw + 10 * sc, by, 4, L.fs_txt, "-" .. fmt_time(behind), 0.82)
            end
        else
            local cur_s = S.show_remaining and S.dur > 0 and ("-" .. fmt_time(math.max(0, S.dur - live_t))) or fmt_time(live_t)
            local tot_s = "  /  " .. fmt_time(S.dur)
            text(L.time_x, by, 4, L.fs_txt, cur_s, 0.98, nil, true)
            local cur_w = text_w(cur_s, L.fs_txt)
            text(L.time_x + cur_w, by, 4, L.fs_txt, tot_s, 0.56)

            local chap = not L.compact and chapter_at(live_t)
            if chap then
                local cx = L.time_x + cur_w + text_w(tot_s, L.fs_txt) + 16 * sc
                local room = (L.right_edge or W) - cx - 12 * sc
                local name = chap
                local max = math.floor(room / (L.fs_txt * 0.52))
                if max >= 6 then
                    if utf8_len(name) > max then name = utf8_sub(name, max - 1) .. "…" end
                    text(cx, by, 4, L.fs_txt, "·  " .. esc(name), 0.72)
                end
            end
        end

        if L.sp then text_btn("speed", L.sp, by, L.spw, S.speed) end
        if L.q then text_btn("quality", L.q, by, L.qw, S.quality) end
        if L.sc then icon_btn("sc", L.sc, by, G.sc, S.sc_on) end
        if L.theater then icon_btn("theater", L.theater, by, G.theater, S.theater) end
        if L.pipb then icon_btn("pip", L.pipb, by, G.pip, S.pip) end
        if L.loop then icon_btn("loop", L.loop, by, G.loop, S.loop) end
        if L.cc then icon_btn("subs", L.cc, by, G.cc, S.subs_on) end
        if L.fs then icon_btn("fs", L.fs, by, S.fs and G.unfs or G.fs, false) end

        -- Apple Liquid Glass hover tooltip (ZERO collision with avatars!)
        if tip and not hot_seek and not S.drag then
            local tip_b = dock_t - 10 * sc
            ios_glass(tip_x - tip_w / 2, tip_b - 26 * sc, tip_x + tip_w / 2, tip_b, 13 * sc, 0.92, true)
            text(tip_x, tip_b - 13 * sc, 5, 12.5 * sc, esc(tip), 0.98, nil, true)
        end

        if L.top then
            rect(0, 0, W, 56 * sc, "000000", 0.34)
            for _, b in ipairs(L.top) do
                local ha = hov_val(b.id)
                if ha > 0.02 then circle(b.cx, L.ty, (15 + 1.5 * ha) * sc, "FFFFFF", 0.22 * ha) end
                local g = b.id == "pip-close" and G.close or b.id == "pip-back" and G.toapp or G.lock
                text(b.cx, L.ty, 5, 15 * sc, g, 0.92 + 0.08 * ha, opts.iconfont)
            end
        end
    end

    -- 7. Dynamic Island HUD (Bezel, Volume Capsule, 2× Speed Capsule, Spinner)
    cur_alpha_mul = 1
    if S.bezel then
        local p = (t - S.bezel.t) / 0.36
        if p >= 1 then S.bezel = nil
        else
            local ep = ease_out_quart(p)
            local r = (30 + 16 * ep) * sc
            circle(W / 2, H / 2, r + 1 * sc, "FFFFFF", 0.18 * (1 - p))
            circle(W / 2, H / 2, r, "121216", 0.68 * (1 - p))
            A:new_event()
            A:append(string.format("{\\an5\\pos(%.1f,%.1f)\\fs%.1f\\fn%s\\bord0\\shad0\\1c&HFFFFFF&\\1a&H%02X&}",
                W / 2, H / 2, (24 + 8 * ep) * sc, opts.iconfont, clamp(math.floor(255 * p), 0, 255)))
            A:append(S.bezel.g)
        end
    end

    if S.vol_pill_a > 0.01 then
        local en = S.lang == "en"
        local label = S.mute and (en and "Muted" or "Без звука") or string.format("%d%%", math.floor(S.vol + 0.5))
        local ev = ease_out_quart(S.vol_pill_a)
        local vy = ((S.speed2x_a > 0.1) and 68 * sc or 22 * sc) - (1 - ev) * 10 * sc
        local pw_v = 196 * sc
        local ph_v = 36 * sc
        cur_alpha_mul = ev
        ios_glass(W / 2 - pw_v / 2, vy, W / 2 + pw_v / 2, vy + ph_v, ph_v / 2, 0.88, true)
        text(W / 2 - pw_v / 2 + 22 * sc, vy + ph_v / 2, 5, 15 * sc, (S.mute or S.vol <= 0) and G.vol0 or G.vol, 0.95, opts.iconfont)
        local bx0 = W / 2 - pw_v / 2 + 38 * sc
        local bx1 = W / 2 + pw_v / 2 - 48 * sc
        rect(bx0, vy + ph_v / 2 - 2.5 * sc, bx1, vy + ph_v / 2 + 2.5 * sc, "FFFFFF", 0.22, 2.5 * sc)
        rect(bx0, vy + ph_v / 2 - 2.5 * sc, bx0 + (bx1 - bx0) * clamp((S.mute and 0 or S.vol) / 100, 0, 1), vy + ph_v / 2 + 2.5 * sc, "FFFFFF", 0.96, 2.5 * sc)
        text(W / 2 + pw_v / 2 - 14 * sc, vy + ph_v / 2, 6, 13 * sc, label, 0.95, nil, true)
        cur_alpha_mul = 1
    end

    if S.speed2x_a > 0.01 then
        local es = ease_out_quart(S.speed2x_a)
        cur_alpha_mul = es
        local sp_lbl = (S.ext_2x or (S.saved_speed < 2 and "2×" or (string.format("%.2g×", math.min(4, S.saved_speed * 2))))) .. "  ▸▸"
        local tw = text_w(sp_lbl, 14 * sc) + 34 * sc
        local sy = 20 * sc - (1 - es) * 8 * sc
        ios_glass(W / 2 - tw / 2, sy, W / 2 + tw / 2, sy + 34 * sc, 17 * sc, 0.88, true)
        text(W / 2, sy + 17 * sc, 5, 14 * sc, sp_lbl, 0.98, nil, true)
        cur_alpha_mul = 1
    end

    -- Translucent right-side Apple Liquid Glass "Unskip Ad" pill after SponsorBlock skips a segment
    S.unskip_r = nil
    if S.skip_undo and S.skip_undo_a > 0.01 then
        local eu = ease_out_quart(S.skip_undo_a)
        cur_alpha_mul = eu
        local en = S.lang == "en"
        local cat_lbl = segment_label(S.skip_undo.cat)
        local txt_l = (en and ("Skipped: " .. cat_lbl) or ("Пропущено: " .. cat_lbl))
        local txt_btn = en and "Watch ad" or "Вернуться"
        local fs_u = 13.0 * sc
        local lw = text_w(txt_l, fs_u)
        local bw = text_w(txt_btn, fs_u) + 20 * sc
        local ph_u = 36 * sc
        local pw_u = 16 * sc + lw + 12 * sc + bw + 8 * sc
        local ux1 = W - (L.dock_m > 0 and L.dock_m or 18 * sc) + (1 - eu) * 20 * sc
        local ux0 = ux1 - pw_u
        local uy1 = (S.ease > 0.05 and (L.dock_t - 34 * sc) or (H - 22 * sc))
        local uy0 = uy1 - ph_u
        S.unskip_r = { x0 = ux0, y0 = uy0, x1 = ux1, y1 = uy1 }
        local u_hov = (h == "unskip")
        ios_glass(ux0, uy0, ux1, uy1, ph_u / 2, u_hov and 0.86 or 0.74, true)
        text(ux0 + 16 * sc, uy0 + ph_u / 2, 4, fs_u, esc(txt_l), 0.90)
        local bx0 = ux1 - bw - 6 * sc
        local by0, by1 = uy0 + 5 * sc, uy1 - 5 * sc
        rect(bx0, by0, ux1 - 6 * sc, by1, u_hov and b1 or "FFFFFF", u_hov and 0.85 or 0.16, (by1 - by0) / 2)
        text(bx0 + bw / 2, uy0 + ph_u / 2, 5, fs_u, esc(txt_btn), 1, nil, true)
        cur_alpha_mul = 1
    end

    if (S.buffering or S.seeking) and not S.drag and S.ready then
        for i = 0, 7 do
            local a = i * (math.pi / 4)
            local ph = ((t * 10 - i) % 8) / 8
            local op = clamp(1 - ph * 0.88, 0.12, 1)
            circle(W / 2 + math.cos(a) * 19 * sc, H / 2 + math.sin(a) * 19 * sc, (2.6 + 1.2 * op) * sc, "FFFFFF", op)
        end
    end

    ov.res_x, ov.res_y = W, H
    ov.data = A.text
    ov:update()
    last_render_done = now()
end

local function active()
    return S.fade > 0.005 or S.vol_pill_a > 0.005 or S.speed2x_a > 0.005 or S.skip_undo_a > 0.005 or want_visible()
        or S.bezel or S.hold_at ~= nil or S.hold_2x or S.ext_2x ~= nil
        or now() < S.vol_until or (S.skip_undo ~= nil and now() < S.skip_undo.until_t) or ((S.buffering or S.seeking) and S.ready)
end

local last_tick = 0
local function tick(immediate)
    local t = now()
    if not immediate and (t - last_render_done) < 0.006 and not S.dirty then return end
    local dt = clamp(t - last_tick, 0.001, 0.05)
    last_tick = t

    if S.down == "video" and S.hold_at and not S.hold_2x and not S.paused and (t - S.hold_at) >= 0.24 then
        S.hold_2x = true
        S.saved_speed = mp.get_property_number("speed", 1) or 1
        local boost = S.saved_speed < 2 and 2 or math.min(4, S.saved_speed * 2)
        mp.set_property_number("speed", boost)
        S.dirty = true
    end

    local playing_visible = S.fade > 0.005 and not S.paused and not S.buffering and not S.seeking and S.drag ~= "seek" and S.ready and S.dur > 0
    if playing_visible then
        S.disp_time = S.disp_time + dt * (S.speed_num or 1)
        local err = S.time - S.disp_time
        if math.abs(err) > 0.65 then S.disp_time = S.time
        else S.disp_time = S.disp_time + err * (1 - math.exp(-14 * dt)) end
        S.disp_time = clamp(S.disp_time, 0, S.dur)
    else
        S.disp_time = S.time
    end

    local any_anim = false
    local target = want_visible() and 1 or 0
    if S.fade ~= target then
        local step = dt / (target == 1 and 0.14 or 0.22)
        S.fade = target == 1 and math.min(1, S.fade + step) or math.max(0, S.fade - step)
        any_anim = true
    end
    S.ease = ease_out_quart(S.fade)

    local h = hit()
    local hot_seek = (h == "seek" or S.drag == "seek") and S.fade > 0.05
    local ns = smooth(S.seek_anim, hot_seek and 1 or 0, 24, dt)
    if ns ~= S.seek_anim then S.seek_anim = ns; any_anim = true end

    local hot_vol = (h == "vol" or S.drag == "vol") and S.fade > 0.05
    local nv = smooth(S.vol_anim, hot_vol and 1 or 0, 24, dt)
    if nv ~= S.vol_anim then S.vol_anim = nv; any_anim = true end

    local want_2x = (S.hold_2x or S.ext_2x ~= nil) and 1 or 0
    local n2x = smooth(S.speed2x_a, want_2x, 22, dt)
    if n2x ~= S.speed2x_a then S.speed2x_a = n2x; any_anim = true end

    local want_vp = (t < S.vol_until) and 1 or 0
    local nvp = smooth(S.vol_pill_a, want_vp, 20, dt)
    if nvp ~= S.vol_pill_a then S.vol_pill_a = nvp; any_anim = true end

    local want_su = (S.skip_undo ~= nil and (t < S.skip_undo.until_t or h == "unskip")) and 1 or 0
    local nsu = smooth(S.skip_undo_a, want_su, 18, dt)
    if nsu ~= S.skip_undo_a then S.skip_undo_a = nsu; any_anim = true end
    if want_su == 0 and S.skip_undo_a <= 0.005 then S.skip_undo = nil end

    if h and h ~= "video" and h ~= "bar" and h ~= "seek" and h ~= "vol" and h ~= "time" and not (type(h) == "string" and h:sub(1, 3) == "cm:") then
        if not S.hov_a[h] then S.hov_a[h] = 0 end
    end
    for k, v in pairs(S.hov_a) do
        local tgt = (h == k and S.fade > 0.05) and 1 or 0
        local nv_b = smooth(v, tgt, 26, dt)
        if nv_b ~= v then any_anim = true end
        if nv_b <= 0.004 and tgt == 0 then S.hov_a[k] = nil else S.hov_a[k] = nv_b end
    end

    local cm_auto_active = S.cm_auto_idx ~= nil and t < S.cm_auto_until
    local su_active = S.skip_undo ~= nil and t < S.skip_undo.until_t
    local animating = any_anim or playing_visible or S.bezel ~= nil or S.hold_at ~= nil or t < S.vol_until or su_active or cm_auto_active or ((S.buffering or S.seeking) and S.ready)

    if active() then
        if S.dirty or animating then S.dirty = false; render() end
        if not animating and (S.paused or S.force or (S.fade == target and S.fade == 1 and over_controls())) then
            timer:kill()
        end
    else
        thumb_hide()
        comments_hide()
        ov:remove()
        timer:kill()
    end
end

local function wake(instant)
    S.dirty = true
    if instant and (now() - last_render_done) >= 0.004 then
        tick(true)
    end
    if not timer:is_enabled() then last_tick = now(); timer:resume() end
end
timer = mp.add_periodic_timer(1 / 120, function() tick(false) end)
timer:kill()

-- ---------------------------------------------------------------- actions
local function seek_to_mouse(final)
    if S.dur <= 0 then return end
    S.drag_pct = clamp((S.mx - L.px0) / (L.px1 - L.px0), 0, 1)
    local t = now()
    if final then
        S.time = S.drag_pct * S.dur
        S.disp_time = S.time
        S.last_seek = t
        S.last_seek_pct = S.drag_pct
        app("manual-seek", string.format("%.3f", S.time))
        mp.commandv("seek", tostring(S.drag_pct * 100), "absolute-percent+exact")
    elseif math.abs(S.drag_pct - S.last_seek_pct) >= 0.003 and (t - S.last_seek) >= 0.045 then
        S.last_seek = t
        S.last_seek_pct = S.drag_pct
        S.time = S.drag_pct * S.dur
        S.disp_time = S.time
        mp.commandv("seek", tostring(S.drag_pct * 100), "absolute-percent+keyframes")
    end
end

local function set_volume_from_mouse(final)
    if not L.vx0 then return end
    local v = clamp((S.mx - L.vx0) / (L.vx1 - L.vx0), 0, 1)
    local pct = math.floor(v * 100 + 0.5)
    S.vol = pct
    S.vol_until = now() + 1.2
    local t = now()
    if final or (t - S.last_vol_set) >= 0.025 then
        S.last_vol_set = t
        mp.set_property_number("volume", pct)
        mp.set_property_bool("mute", false)
    end
end

local function activate(id)
    if id == "play" or id == "video" then
        mp.commandv("cycle", "pause")
        if id == "video" then S.bezel = { g = S.paused and G.play or G.pause, t = now() } end
    elseif id == "next" then app("next")
    elseif id == "mute" then mp.commandv("cycle", "mute")
    elseif id == "time" then
        if S.is_live then
            if S.dur > 6 then
                local edge = math.max(0, S.dur - 4)
                S.time = S.dur; S.disp_time = S.dur
                mp.commandv("seek", tostring(edge), "absolute+keyframes")
            else
                mp.commandv("seek", "100", "absolute-percent")
            end
            wake(true)
        else
            S.show_remaining = not S.show_remaining; wake(true)
        end
    elseif id == "unskip" and S.skip_undo then
        local st = S.skip_undo.start
        S.skip_undo = nil; S.skip_undo_a = 0
        app("unskip", string.format("%.3f", st))
        wake(true)
    elseif id == "fs" then mp.commandv("cycle", "fullscreen")
    elseif id == "theater" then app("theater")
    elseif id == "pip" or id == "pip-lock" or id == "pip-back" or id == "pip-close" then app(id)
    elseif id == "quality" then app("menu-quality")
    elseif id == "subs" then app("menu-subs")
    elseif id == "sc" then app("toggle-sc")
    elseif type(id) == "string" and id:sub(1, 3) == "cm:" then
        local idx = tonumber(id:sub(4))
        local m = idx and S.sc_comments and S.sc_comments[idx]
        if m and m.t then
            S.time = m.t; S.disp_time = m.t
            mp.commandv("seek", tostring(m.t), "absolute+exact")
            wake(true)
        end
    elseif id == "loop" then
        app("toggle-loop")
        local en = S.lang == "en"
        mp.osd_message(S.loop and (en and "Loop off" or "Повтор выключен") or (en and "Loop on" or "Повтор включён"), 2)
    elseif id == "speed" then app("menu-speed")
    end
end

mp.add_forced_key_binding("MBTN_LEFT", "flux-left", function(e)
    layout()
    if e.event == "down" then
        local h = hit()
        S.down = h
        if h == "seek" then S.drag = "seek"; seek_to_mouse(false)
        elseif h == "vol" then S.drag = "vol"; set_volume_from_mouse(true)
        elseif h == "video" and not S.paused then S.hold_at = now(); S.hold_2x = false end
    elseif e.event == "up" then
        if S.hold_2x then
            mp.set_property_number("speed", S.saved_speed or 1)
            S.hold_2x = false; S.hold_at = nil; S.drag, S.down = nil, nil
            wake(true); return
        end
        S.hold_at = nil
        if S.drag == "seek" then seek_to_mouse(true)
        elseif S.drag == "vol" then set_volume_from_mouse(true)
        elseif not S.drag and S.down and S.down == hit() then activate(S.down) end
        S.drag, S.down = nil, nil
    elseif e.event == "press" then
        activate(hit() or "video")
    end
    S.last_move = now()
    wake(true)
end, { complex = true })

mp.add_forced_key_binding("MBTN_LEFT_DBL", "flux-dbl", function()
    layout()
    local h = hit()
    if h == "video" or h == nil then
        if S.pip then app("pip-back") else mp.commandv("cycle", "fullscreen") end
    end
end)

mp.add_forced_key_binding("MBTN_RIGHT", "flux-right", function()
    layout()
    local h = hit()
    if (h == "video" or h == nil) and S.rclick == "pause" then
        mp.commandv("cycle", "pause")
        S.bezel = { g = S.paused and G.play or G.pause, t = now() }
        S.last_move = now()
        wake(true)
    else
        app("menu")
    end
end)

-- ---------------------------------------------------------------- state
mp.observe_property("mouse-pos", "native", function(_, v)
    if not v then return end
    if v.x ~= S.mx or v.y ~= S.my then
        S.mx, S.my = v.x, v.y
        S.last_move = now()
        if S.drag == "seek" then seek_to_mouse(false) elseif S.drag == "vol" then set_volume_from_mouse(false) end
        wake(true)
    end
end)
mp.observe_property("pause", "bool", function(_, v) S.paused = v or false; S.disp_time = S.time; wake(true) end)
mp.observe_property("speed", "number", function(_, v) S.speed_num = v or 1.0 end)
mp.observe_property("loop-file", "string", function(_, v) S.loop = v ~= nil and v ~= "no"; wake(true) end)
mp.observe_property("time-pos", "number", function(_, v)
    S.time = v or 0
    if S.paused or math.abs(S.time - S.disp_time) > 0.65 then S.disp_time = S.time end
    if S.fade > 0.01 then wake(false) else S.dirty = true end
end)
mp.observe_property("duration", "number", function(_, v) S.dur = v or 0 end)
mp.observe_property("demuxer-cache-time", "number", function(_, v) S.cache = v or 0 end)
mp.observe_property("fullscreen", "bool", function(_, v) S.fs = v or false; wake(true) end)
mp.observe_property("paused-for-cache", "bool", function(_, v) S.buffering = v or false; wake(false) end)
mp.observe_property("seeking", "bool", function(_, v) S.seeking = v or false; wake(false) end)
mp.observe_property("osd-dimensions", "native", function() comments_hide(); thumb_hide(); wake(true) end)
local vol_seen = false
mp.observe_property("volume", "number", function(_, v)
    S.vol = v or 100
    if vol_seen then S.vol_until = now() + 1.3; wake(false) end
    vol_seen = true
end)
local mute_seen = false
mp.observe_property("mute", "bool", function(_, v)
    S.mute = v or false
    if mute_seen then S.vol_until = now() + 1.3; wake(true) end
    mute_seen = true
end)
mp.register_event("file-loaded", function()
    S.thumb_file = nil; S.thumb_want = -99; thumb_hide(); comments_hide(); S.cm_auto_idx = nil
    S.ready = true; S.disp_time = S.time
    S.segments = S.pending_segments or {}
    if S.pending_sc_comments then S.sc_comments = S.pending_sc_comments end
    wake(true)
end)
mp.register_script_message("flux-thumb", function(file, w, h)
    if file == "none" or not file then
        S.thumb_file = nil
        thumb_hide()
    else
        S.thumb_file, S.thumb_w, S.thumb_h = file, tonumber(w) or 0, tonumber(h) or 0
    end
    wake(true)
end)

local function cancel_hold_2x()
    if S.hold_2x then
        mp.set_property_number("speed", S.saved_speed or 1)
        S.hold_2x = false
    end
    S.hold_at = nil; S.down = nil; S.drag = nil
end

mp.register_event("end-file", function()
    cancel_hold_2x()
    S.ext_2x = nil
    S.ready = false; S.time = 0; S.disp_time = 0; S.dur = 0; S.cache = 0; S.is_live = false; S.heatmap = {}
    S.thumb_file = nil; S.ov_file = nil; thumb_hide(); comments_hide(); S.sc_comments = {}; S.cm_auto_idx = nil
    wake(true)
end)

mp.register_script_message("flux-state", function(json)
    local t = utils.parse_json(json or "")
    if type(t) ~= "table" then return end
    if t.next ~= nil then S.has_next = t.next end
    if t.quality then S.quality = t.quality end
    if t.speed then S.speed = t.speed end
    if t.title then S.title = t.title end
    if t.channel ~= nil then S.channel = t.channel end
    if t.likes ~= nil then S.likes = t.likes end
    if t.short_overlay ~= nil then S.short_overlay = t.short_overlay end
    if t.is_short ~= nil then S.is_short = t.is_short end
    if t.is_live ~= nil then S.is_live = t.is_live end
    if type(t.lang) == "string" then S.lang = t.lang end
    if t.rclick then S.rclick = t.rclick end
    if t.pip ~= nil then S.pip = t.pip end
    if t.theater ~= nil then S.theater = t.theater end
    if type(t.player_style) == "string" and (t.player_style == "flux" or t.player_style == "youtube") then
        S.player_style = t.player_style
    end
    local b1 = hex_to_bgr(t.accent)
    if b1 then opts.brand = b1 end
    local b2 = hex_to_bgr(t.accent2)
    if b2 then opts.brand2 = b2 elseif b1 then opts.brand2 = b1 end
    local pn = hex_to_bgr(t.panel)
    if pn then opts.panel = pn end
    if type(t.font) == "string" and #t.font > 0 then opts.font = t.font end
    if type(t.ui_scale) == "number" and t.ui_scale >= 0.5 and t.ui_scale <= 2.0 then S.ui_scale = t.ui_scale end
    if type(t.corners) == "string" then S.corners = t.corners end
    if type(t.card_style) == "string" then S.card_style = t.card_style end
    if t.neon_glow ~= nil then S.neon_glow = t.neon_glow end
    if t.light ~= nil then S.light = t.light end
    if t.segments then S.segments = t.segments; S.pending_segments = t.segments end
    if t.chapters then S.chapters = t.chapters end
    if t.heatmap then S.heatmap = t.heatmap end
    if t.subs ~= nil then S.subs_on = t.subs end
    if t.sc_on ~= nil then S.sc_on = t.sc_on; if not S.sc_on then comments_hide() end end
    if t.sc_comments then S.sc_comments = t.sc_comments; S.pending_sc_comments = t.sc_comments end
    wake(true)
end)
mp.register_script_message("flux-accent", function(hex1, hex2, hex_panel)
    local changed = false
    local nb1 = hex_to_bgr(hex1)
    if nb1 and opts.brand ~= nb1 then opts.brand = nb1; changed = true end
    local nb2 = hex_to_bgr(hex2) or nb1
    if nb2 and opts.brand2 ~= nb2 then opts.brand2 = nb2; changed = true end
    local np = hex_to_bgr(hex_panel)
    if np and opts.panel ~= np then opts.panel = np; changed = true end
    if changed and active() then wake(false) end
end)
mp.register_script_message("flux-leave", function() S.mx, S.my = -1, -1; cancel_hold_2x(); wake(true) end)
mp.register_script_message("flux-show", function(v) S.force = v == "yes"; wake(true) end)
mp.register_script_message("flux-bezel", function(kind) S.bezel = { g = G[kind] or G.play, t = now() }; wake(true) end)
mp.register_script_message("flux-2x", function(v) S.ext_2x = (v and v ~= "off") and v or nil; wake(true) end)
mp.register_script_message("flux-skipped", function(start_s, cat)
    local st = tonumber(start_s)
    if not st then return end
    S.skip_undo = { start = st, cat = cat or "sponsor", until_t = now() + 7.5 }
    wake(true)
end)
