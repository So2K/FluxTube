-- FluxTube player controls: the YouTube layout (gradient, thin red progress line with a scrubber, buttons under it),
-- drawn as one ASS overlay. Nothing is drawn or computed while the controls are hidden.
local mp = require 'mp'
local utils = require 'mp.utils'
local assdraw = require 'mp.assdraw'
local opts = { iconfont = "Segoe Fluent Icons", font = "Segoe UI", brand = "5533FF" } -- brand is BGR (FF3355)
require('mp.options').read_options(opts, "fluxosc")

local ov = mp.create_osd_overlay("ass-events")
local S = {
    mx = -1, my = -1, last_move = 0, drag = nil, down = nil, force = false,
    paused = false, time = 0, dur = 0, cache = 0, vol = 100, mute = false, fs = false, buffering = false, seeking = false,
    has_next = false, quality = "", speed = "1×", title = "", channel = "", likes = "", short_overlay = false, segments = {}, rclick = "pause",
    bezel = nil, vol_until = 0, fade = 0, last_seek = 0, drag_pct = 0, ready = false,
    hold_at = nil, hold_2x = false, ext_2x = nil, saved_speed = 1,
    thumb_file = nil, thumb_w = 0, thumb_h = 0, thumb_want = -99, thumb_on = false,
    sc_on = true, is_short = false, lang = "ru", sc_comments = {}, pending_sc_comments = nil, cm_ovs = {}, cm_auto_idx = nil, cm_auto_until = 0,
}
local W, H, sc = 1280, 720, 1
local L = {} -- layout of the current frame
local timer

local function now() return mp.get_time() end
local function clamp(v, a, b) if v < a then return a elseif v > b then return b else return v end end
local function glyph(cp) -- UTF-8 for a BMP code point
    return string.char(0xE0 + math.floor(cp / 4096), 0x80 + math.floor(cp / 64) % 64, 0x80 + cp % 64)
end
local G = { play = glyph(0xE768), pause = glyph(0xE769), next = glyph(0xE893), vol = glyph(0xE767), vol0 = glyph(0xE74F),
            fs = glyph(0xE740), unfs = glyph(0xE73F), theater = glyph(0xE9A6), back = glyph(0xED3C), fwd = glyph(0xED3D),
            cc = glyph(0xE7F0), loop = glyph(0xE8EE), pip = glyph(0xE8A7), lock = glyph(0xE72E), unlock = glyph(0xE785), toapp = glyph(0xE73F), close = glyph(0xE711), sc = glyph(0xE8BD) }

local function fmt_time(t)
    if not t or t < 0 then t = 0 end
    t = math.floor(t)
    local h, m, s = math.floor(t / 3600), math.floor(t / 60) % 60, t % 60
    if h > 0 then return string.format("%d:%02d:%02d", h, m, s) end
    return string.format("%d:%02d", m, s)
end

local function app(...) mp.commandv("script-message", "flux", ...) end

-- frame preview: the app writes the picture as raw pixels, mpv puts it over the video (overlay 7)
local function thumb_hide()
    if S.thumb_on then
        mp.commandv("overlay-remove", 7)
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

local function comment_pos(m)
    local pw = L.px1 - L.px0
    local frac = clamp((m.t or 0) / S.dur, 0.025, 0.975)
    return L.px0 + pw * frac, L.py - 23 * sc
end

local function esc(s) return (s:gsub("\\", "\\\226\128\139"):gsub("{", "\\{"):gsub("[\r\n\t]+", " ")) end

-- ---------------------------------------------------------------- drawing helpers
local A -- current assdraw
local function alpha(opacity) return clamp(math.floor(255 * (1 - opacity * S.fade) + 0.5), 0, 255) end
local function shape(color, opacity)
    A:new_event()
    A:append(string.format("{\\an7\\pos(0,0)\\bord0\\shad0\\1c&H%s&\\1a&H%02X&}", color, alpha(opacity)))
    A:draw_start()
end
local function rect(x0, y0, x1, y1, color, opacity, r)
    if x1 <= x0 or y1 <= y0 then return end
    shape(color, opacity)
    if r and r > 0 then A:round_rect_cw(x0, y0, x1, y1, r) else A:rect_cw(x0, y0, x1, y1) end
    A:draw_stop()
end
local function circle(cx, cy, r, color, opacity) rect(cx - r, cy - r, cx + r, cy + r, color, opacity, r) end
local function text(x, y, an, size, str, opacity, font, bold, shadow)
    A:new_event()
    A:append(string.format("{\\an%d\\pos(%.1f,%.1f)\\fs%.1f\\fn%s\\b%d\\bord%.1f\\shad0\\1c&HFFFFFF&\\3c&H000000&\\1a&H%02X&\\3a&H%02X&}",
        an, x, y, size, font or opts.font, bold and 1 or 0, shadow and 1.2 * sc or 0, alpha(opacity or 1), alpha(0.35)))
    A:append(str)
end
local function text_w(str, size) -- rough advance, enough for laying out a row of short labels
    local n = 0
    for _ in str:gmatch("[%z\1-\127\194-\244][\128-\191]*") do n = n + 1 end
    return n * size * 0.52
end

-- ---------------------------------------------------------------- layout
local function layout()
    W, H = mp.get_osd_size()
    if not W or W <= 0 then W, H = 1280, 720 end
    sc = mp.get_property_number("display-hidpi-scale", 1) or 1
    local compact = W < 620 * sc
    if W < 460 * sc then sc = sc * 0.82 end
    local m = 12 * sc
    L = { compact = compact, m = m, bar = 46 * sc, btn = 40 * sc, icon = 19 * sc, fs_txt = 13.5 * sc, hits = {} }
    L.by = H - L.bar / 2 - 3 * sc              -- button row centre
    L.py = H - L.bar - 7 * sc                  -- progress line centre
    L.px0, L.px1 = m, W - m
    local x = m
    local function left(id, w) L.hits[#L.hits + 1] = { id = id, x0 = x, x1 = x + w }; x = x + w; return x - w / 2 end
    L.play = left("play", L.btn)
    if S.has_next and not compact then L.next = left("next", L.btn) end
    L.volc = left("mute", L.btn)
    if not compact then
        L.vx0, L.vx1 = x + 2 * sc, x + 80 * sc
        L.hits[#L.hits + 1] = { id = "vol", x0 = x - 4 * sc, x1 = x + 88 * sc }
        x = x + 94 * sc
    end
    L.time_x = x + 4 * sc
    local rx = W - m
    local function right(id, w) rx = rx - w; L.hits[#L.hits + 1] = { id = id, x0 = rx, x1 = rx + w }; return rx + w / 2 end
    local min_rx = L.time_x + text_w("0:00:00 / 0:00:00", L.fs_txt) + 14 * sc
    local function can_fit(w) return rx - w >= min_rx end
    if not S.pip then L.fs = right("fs", L.btn) end
    if not S.fs and not S.pip and can_fit(L.btn) then L.pipb = right("pip", L.btn) end
    if not compact and not S.fs and not S.pip and not S.is_short and can_fit(L.btn) then L.theater = right("theater", L.btn) end
    -- the separate window: lock, back into the app, close, in its top-right corner
    if S.pip then
        L.top = {}
        local tx = W - 8 * sc
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
            local w = text_w(S.quality, L.fs_txt) + 22 * sc
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
-- chapters come from the app as {{start_seconds, "title"}, ...}, in order
local function chapter_at(t)
    local c = S.chapters
    if not c or #c == 0 then return nil end
    local name = nil
    for _, ch in ipairs(c) do if t + 0.05 >= ch[1] then name = ch[2] else break end end
    return name
end

local function hit()
    if not L.py then layout() end
    if S.mx < 0 then return nil end
    local x, y = S.mx, S.my
    if L.top and y <= L.ty + 18 * sc then
        for _, h in ipairs(L.top) do if x >= h.x0 and x < h.x1 then return h.id end end
    end
    if show_comment_dots() and S.fade >= 0.3 and not (S.drag == "seek") then
        local lim = math.min(22, #S.sc_comments)
        local r2 = (11.5 * sc) * (11.5 * sc)
        for i = lim, 1, -1 do
            local mx, cy = comment_pos(S.sc_comments[i])
            local dx, dy = x - mx, y - cy
            if dx * dx + dy * dy <= r2 then return "cm:" .. i end
        end
    end
    if y >= L.py - 10 * sc and y <= L.py + 9 * sc and x >= L.px0 - 6 * sc and x <= L.px1 + 6 * sc then return "seek" end
    if y > L.py + 9 * sc and y <= H then
        for _, h in ipairs(L.hits) do if x >= h.x0 and x < h.x1 then return h.id end end
        return "bar"
    end
    return "video"
end

local function over_controls() local h = hit(); return h ~= nil and h ~= "video" end

-- ---------------------------------------------------------------- render
local function want_visible()
    if S.force or S.drag then return true end
    if not S.ready then return false end
    if S.paused then return true end
    if S.mx < 0 then return false end
    if over_controls() then return true end
    return now() - S.last_move < 2.4
end

local function icon_btn(cx, g, hot)
    if hot then circle(cx, L.by, L.btn * 0.46, "FFFFFF", 0.16) end
    text(cx, L.by, 5, L.icon, g, 1, opts.iconfont)
end
local function text_btn(cx, w, str, hot)
    rect(cx - w / 2 + 3 * sc, L.by - 14 * sc, cx + w / 2 - 3 * sc, L.by + 14 * sc, "FFFFFF", hot and 0.22 or 0.10, 14 * sc)
    text(cx, L.by, 5, L.fs_txt, esc(str), 1, nil, true)
end

local function wrap_utf8(str, maxc)
    maxc = math.max(12, math.floor(maxc))
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
            if #out >= 2 then
                out[2] = utf8_sub(out[2], math.max(1, maxc - 1)) .. "…"
                return out
            end
            if wl > maxc then cur = utf8_sub(word, maxc - 1) .. "…"; cur_n = maxc
            else cur = word; cur_n = wl end
        end
    end
    if cur_n > 0 and #out < 2 then out[#out + 1] = cur end
    return out
end

local function render()
    layout()
    if S.fade <= 0.01 then thumb_hide(); comments_hide() end
    A = assdraw.ass_new()
    local t = now()
    local h = hit()
    local hov_cm = (type(h) == "string" and h:sub(1, 3) == "cm:") and tonumber(h:sub(4)) or nil

    if S.fade > 0.01 then
        -- gradient under the controls
        -- strips on whole pixels: overlapping or fractional edges show up as lines
        local n = 28
        local gh = math.floor((S.short_overlay and 210 or 140) * sc / n) * n
        for i = 1, n do
            rect(0, H - gh + gh * (i - 1) / n, W, H - gh + gh * i / n, "000000", (S.short_overlay and 0.86 or 0.78) * (i / n) ^ 1.8)
        end
        if S.short_overlay and (S.channel ~= "" or S.title ~= "") then
            local info_y = L.py - 18 * sc
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
            local th = math.floor(96 * sc / 16) * 16
            for i = 1, 16 do rect(0, th * (16 - i) / 16, W, th * (17 - i) / 16, "000000", 0.6 * (i / 16) ^ 1.6) end
            text(22 * sc, 20 * sc, 7, 20 * sc, esc(S.title), 1, nil, true, true)
        end

        -- progress line: track, buffered, sponsor segments, played, scrubber
        local hot = h == "seek" or S.drag == "seek"
        local th = (hot and 5 or 3) * sc
        local pw = L.px1 - L.px0
        local pct = S.dur > 0 and clamp(S.time / S.dur, 0, 1) or 0
        if S.drag == "seek" then pct = S.drag_pct end
        rect(L.px0, L.py - th / 2, L.px1, L.py + th / 2, "FFFFFF", 0.25, th / 2)
        if S.dur > 0 and S.cache > S.time then
            rect(L.px0 + pw * pct, L.py - th / 2, L.px0 + pw * clamp(S.cache / S.dur, 0, 1), L.py + th / 2, "FFFFFF", 0.38)
        end
        rect(L.px0, L.py - th / 2, L.px0 + pw * pct, L.py + th / 2, opts.brand, 1, th / 2)
        -- SponsorBlock segments: thick, bold emerald bars with crisp dark outline so skipped sections pop out clearly
        if S.dur > 0 and S.segments and #S.segments > 0 then
            local sbh = (hot and 9.5 or 7.0) * sc
            local ob = 1.6 * sc
            for _, seg in ipairs(S.segments) do
                local sx0 = L.px0 + pw * clamp(seg[1] / S.dur, 0, 1)
                local sx1 = L.px0 + pw * clamp(seg[2] / S.dur, 0, 1)
                if sx1 - sx0 < 4 * sc then sx1 = math.min(L.px1, sx0 + 4 * sc) end
                rect(sx0 - ob, L.py - sbh / 2 - ob, sx1 + ob, L.py + sbh / 2 + ob, "08080C", 0.96, (sbh + ob * 2) / 2)
                rect(sx0, L.py - sbh / 2, sx1, L.py + sbh / 2, "68EE00", 1.0, sbh / 2)
                rect(sx0 + 1 * sc, L.py - sbh / 2 + 0.8 * sc, sx1 - 1 * sc, L.py - sbh / 2 + 2.2 * sc, "C8FFAA", 0.55, 1 * sc)
            end
        end
        if S.dur > 0 and S.chapters then
            for i, ch in ipairs(S.chapters) do
                if i > 1 and ch[1] < S.dur then
                    local gx = L.px0 + pw * (ch[1] / S.dur)
                    rect(gx - 1.2 * sc, L.py - th / 2 - 1, gx + 1.2 * sc, L.py + th / 2 + 1, "101010", 0.95)
                end
            end
        end
        if hot then circle(L.px0 + pw * pct, L.py, 7.0 * sc, opts.brand, 1) end

        -- SoundCloud-style timed comment avatar circles directly above the video seekbar (hidden cleanly while actively scrubbing!)
        if show_comment_dots() and not hot then
            local lim = math.min(22, #S.sc_comments)
            local used_slots = {}
            local last_mx = -1e9
            for i = 1, lim do
                local m = S.sc_comments[i]
                local mx, cy = comment_pos(m)
                local is_hov = (hov_cm == i)
                if not is_hov and (mx - last_mx) < 22 * sc then
                    local slot0 = 9 + i
                    if S.cm_ovs[slot0] then mp.commandv("overlay-remove", slot0); S.cm_ovs[slot0] = nil end
                else
                    last_mx = mx
                    -- Crisp pin connecting the avatar down to its exact timestamp on the seekbar
                    rect(mx - 0.8 * sc, cy + 11 * sc, mx + 0.8 * sc, L.py - 3 * sc, is_hov and opts.brand or "FFFFFF", is_hov and 0.96 or 0.44)
                    local slot = 9 + i
                    if m.f and m.w and m.h then
                        if S.fade >= 0.45 then
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
                            rect(mx - 11 * sc, cy + 12.5 * sc, mx + 11 * sc, cy + 15.2 * sc, opts.brand, 1, 1.3 * sc)
                        end
                    else
                        if S.cm_ovs[slot] then mp.commandv("overlay-remove", slot); S.cm_ovs[slot] = nil end
                        local r = (is_hov and 11.5 or 9.5) * sc
                        circle(mx, cy, r + 1.4 * sc, is_hov and opts.brand or "FFFFFF", is_hov and 1.0 or 0.85)
                        circle(mx, cy, r, m.c or "B67EFF", 0.96)
                        text(mx, cy, 5, (is_hov and 11.5 or 10) * sc, esc(m.i or "?"), 1, nil, true)
                    end
                    if not hov_cm and m.ts and not S.paused and math.abs(S.time - (m.t or -99)) < 1.6 then
                        if S.cm_auto_idx ~= i then S.cm_auto_idx = i; S.cm_auto_until = t + 3.2 end
                    end
                end
            end
            for slot, _ in pairs(S.cm_ovs) do
                if not used_slots[slot] then mp.commandv("overlay-remove", slot); S.cm_ovs[slot] = nil end
            end

            local show_idx = hov_cm
            if not show_idx and S.cm_auto_idx and t < S.cm_auto_until and S.sc_comments[S.cm_auto_idx] then
                show_idx = S.cm_auto_idx
            end
            if show_idx and S.sc_comments[show_idx] and W >= 200 * sc then
                local m = S.sc_comments[show_idx]
                local mx, cy = comment_pos(m)
                local max_card_w = math.min(W - 24 * sc, 390 * sc)
                local maxc = math.max(16, math.floor((max_card_w - 28 * sc) / (13 * sc * 0.60)))
                local author = m.a or ""
                if utf8_len(author) > 26 then author = utf8_sub(author, 25) .. "…" end
                local hdr = author .. "  ·  " .. fmt_time(m.t or 0)
                local lines = wrap_utf8(m.x or "", maxc)
                local maxw = text_w(hdr, 12.5 * sc)
                for _, ln in ipairs(lines) do maxw = math.max(maxw, text_w(ln, 13 * sc) * 1.12) end
                local cw = clamp(maxw + 28 * sc, math.min(150 * sc, max_card_w), max_card_w)
                local ch = (26 + #lines * 17.5 + 10) * sc
                local cx0 = clamp(mx - cw / 2, L.px0, math.max(L.px0, L.px1 - cw))
                local cy1 = cy - 16 * sc
                local cy0 = math.max(8 * sc, cy1 - ch)
                cy1 = cy0 + ch
                rect(cx0 - 1.5 * sc, cy0 - 1.5 * sc, cx0 + cw + 1.5 * sc, cy1 + 1.5 * sc, opts.brand, 0.45, 9.5 * sc)
                rect(cx0, cy0, cx0 + cw, cy1, "101016", 0.94, 8 * sc)
                rect(cx0 + 5 * sc, cy0 + 8 * sc, cx0 + 8 * sc, cy1 - 8 * sc, opts.brand, 1, 1.5 * sc)
                A:new_event()
                A:append(string.format("{\\an7\\pos(%.1f,%.1f)\\fs%.1f\\fn%s\\b1\\bord0\\shad0\\1c&H%s&\\1a&H%02X&}%s",
                    cx0 + 14 * sc, cy0 + 7 * sc, 12 * sc, opts.font, opts.brand, alpha(1), esc(hdr)))
                for li, ln in ipairs(lines) do
                    text(cx0 + 14 * sc, cy0 + (24 + (li - 1) * 17.5) * sc, 7, 13 * sc, esc(ln), 0.96)
                end
            end
        else
            comments_hide()
        end

        if hot and not hov_cm and S.dur > 0 and S.mx >= 0 then
            local hp = clamp((S.mx - L.px0) / pw, 0, 1)
            local ht = hp * S.dur
            if math.abs(ht - S.thumb_want) > 0.25 then
                S.thumb_want = ht
                app("thumb", string.format("%.2f", ht))
            end
            local bottom = L.py - 14 * sc
            if S.thumb_file and S.thumb_w > 0 then
                local iw, ih = S.thumb_w, S.thumb_h
                local ix = math.floor(clamp(S.mx - iw / 2, 6 * sc, math.max(6 * sc, W - iw - 6 * sc)))
                local iy = math.floor(L.py - 46 * sc - ih)
                if not S.thumb_on or S.ov_file ~= S.thumb_file or S.ov_x ~= ix or S.ov_y ~= iy then
                    mp.commandv("overlay-add", 7, ix, iy, S.thumb_file, 0, "bgra", iw, ih, iw * 4)
                    S.thumb_on = true
                    S.ov_file, S.ov_x, S.ov_y = S.thumb_file, ix, iy
                end
                -- the overlay sits under the OSD, so the frame around it is drawn here, on top
                local b = 2 * sc
                rect(ix - b, iy - b, ix + iw + b, iy, "0A0A0A", 0.95)
                rect(ix - b, iy + ih, ix + iw + b, iy + ih + b, "0A0A0A", 0.95)
                rect(ix - b, iy, ix, iy + ih, "0A0A0A", 0.95)
                rect(ix + iw, iy, ix + iw + b, iy + ih, "0A0A0A", 0.95)
                bottom = L.py - 20 * sc
            else
                thumb_hide()
            end
            local label = fmt_time(ht)
            local chap = chapter_at(ht)
            if chap then label = esc(chap) .. "   " .. label end
            local tw = text_w(label, 13 * sc) + 16 * sc
            local tx = clamp(S.mx, L.px0 + tw / 2, L.px1 - tw / 2)
            rect(tx - tw / 2, bottom - 22 * sc, tx + tw / 2, bottom, "141414", 0.92, 5 * sc)
            text(tx, bottom - 11 * sc, 5, 13 * sc, label, 1, nil, true)
            rect(S.mx - 0.75 * sc, L.py - th / 2, S.mx + 0.75 * sc, L.py + th / 2, "FFFFFF", 0.9)
        else
            S.thumb_want = -99
            thumb_hide()
        end

        -- buttons
        icon_btn(L.play, S.paused and G.play or G.pause, h == "play")
        if L.next then icon_btn(L.next, G.next, h == "next") end
        icon_btn(L.volc, (S.mute or S.vol <= 0) and G.vol0 or G.vol, h == "mute")
        if L.vx0 then
            local v = S.mute and 0 or clamp(S.vol / 100, 0, 1)
            rect(L.vx0, L.by - 1.5 * sc, L.vx1, L.by + 1.5 * sc, "FFFFFF", 0.3, 1.5 * sc)
            rect(L.vx0, L.by - 1.5 * sc, L.vx0 + (L.vx1 - L.vx0) * v, L.by + 1.5 * sc, "FFFFFF", 1, 1.5 * sc)
            circle(L.vx0 + (L.vx1 - L.vx0) * v, L.by, ((h == "vol" or S.drag == "vol") and 6 or 5) * sc, "FFFFFF", 1)
        end
        local shown = S.drag == "seek" and S.drag_pct * S.dur or S.time
        local clock = fmt_time(shown) .. "  /  " .. fmt_time(S.dur)
        text(L.time_x, L.by, 4, L.fs_txt, clock, 0.95)
        -- the chapter being played, when there is room for it
        local chap = not L.compact and chapter_at(shown)
        if chap then
            local cx = L.time_x + text_w(clock, L.fs_txt) + 18 * sc
            local room = (L.right_edge or W) - cx - 12 * sc
            local name = chap
            local max = math.floor(room / (L.fs_txt * 0.52))
            if max >= 6 then
                if utf8_len(name) > max then name = utf8_sub(name, max - 1) .. "…" end
                text(cx, L.by, 4, L.fs_txt, "·  " .. esc(name), 0.8)
            end
        end
        if L.sp then text_btn(L.sp, L.spw, S.speed, h == "speed") end
        if L.q then text_btn(L.q, L.qw, S.quality, h == "quality") end
        if L.sc then
            icon_btn(L.sc, G.sc, h == "sc")
            if S.sc_on then rect(L.sc - 9 * sc, L.by + 13 * sc, L.sc + 9 * sc, L.by + 15 * sc, opts.brand, 1, 1 * sc) end
        end
        if L.theater then icon_btn(L.theater, G.theater, h == "theater") end
        if L.pipb then icon_btn(L.pipb, G.pip, h == "pip") end
        if L.loop then
            -- repeat this video: lit and underlined while it is on
            icon_btn(L.loop, G.loop, h == "loop")
            if S.loop then rect(L.loop - 9 * sc, L.by + 13 * sc, L.loop + 9 * sc, L.by + 15 * sc, opts.brand, 1, 1 * sc) end
        end
        if L.cc then
            icon_btn(L.cc, G.cc, h == "subs")
            if S.subs_on then rect(L.cc - 9 * sc, L.by + 13 * sc, L.cc + 9 * sc, L.by + 15 * sc, opts.brand, 1, 1 * sc) end
        end
        if L.fs then icon_btn(L.fs, S.fs and G.unfs or G.fs, h == "fs") end
        if L.top then
            local n = 12
            local gh = math.floor(64 * sc / n) * n
            for i = 1, n do rect(0, gh * (n - i) / n, W, gh * (n - i + 1) / n, "000000", 0.55 * (i / n) ^ 1.6) end
            for _, b in ipairs(L.top) do
                if h == b.id then circle(b.cx, L.ty, 16 * sc, "FFFFFF", 0.18) end
                local g = b.id == "pip-close" and G.close or b.id == "pip-back" and G.toapp or G.lock
                text(b.cx, L.ty, 5, 15 * sc, g, 1, opts.iconfont)
            end
        end
    end

    -- things that show regardless of the bar: bezel, volume pill, buffering
    local keep = S.fade
    S.fade = 1
    if S.bezel then
        local p = (t - S.bezel.t) / 0.45
        if p >= 1 then S.bezel = nil
        else
            local r = (30 + 14 * p) * sc
            circle(W / 2, H / 2, r, "000000", 0.5 * (1 - p))
            A:new_event()
            A:append(string.format("{\\an5\\pos(%.1f,%.1f)\\fs%.1f\\fn%s\\bord0\\shad0\\1c&HFFFFFF&\\1a&H%02X&}", W / 2, H / 2, (24 + 8 * p) * sc, opts.iconfont,
                clamp(math.floor(255 * p), 0, 255)))
            A:append(S.bezel.g)
        end
    end
    if t < S.vol_until then
        local en = S.lang == "en"
        local label = S.mute and (en and "Muted" or "Звук выключен") or string.format(en and "Volume %d%%" or "Громкость %d%%", math.floor(S.vol + 0.5))
        local tw = text_w(label, 15 * sc) + 34 * sc
        local op = clamp((S.vol_until - t) / 0.25, 0, 1)
        local vy = (S.hold_2x or S.ext_2x) and 64 * sc or 26 * sc
        rect(W / 2 - tw / 2, vy, W / 2 + tw / 2, vy + 36 * sc, "101010", 0.72 * op, 18 * sc)
        text(W / 2, vy + 18 * sc, 5, 15 * sc, label, op, nil, true)
        local bw = tw - 30 * sc
        rect(W / 2 - bw / 2, vy + 32 * sc, W / 2 + bw / 2, vy + 34 * sc, "FFFFFF", 0.25 * op)
        rect(W / 2 - bw / 2, vy + 32 * sc, W / 2 - bw / 2 + bw * clamp((S.mute and 0 or S.vol) / 100, 0, 1), vy + 34 * sc, opts.brand, op)
    end
    if S.hold_2x or S.ext_2x then
        local sp_lbl = (S.ext_2x or (S.saved_speed < 2 and "2×" or (string.format("%.2g×", math.min(4, S.saved_speed * 2))))) .. "  ▸▸"
        local tw = text_w(sp_lbl, 14.5 * sc) + 30 * sc
        rect(W / 2 - tw / 2, 22 * sc, W / 2 + tw / 2, 54 * sc, "101010", 0.80, 16 * sc)
        rect(W / 2 - tw / 2 + 12 * sc, 51 * sc, W / 2 + tw / 2 - 12 * sc, 53 * sc, opts.brand, 0.95, 1 * sc)
        text(W / 2, 38 * sc, 5, 14.5 * sc, sp_lbl, 1, nil, true)
    end
    if (S.buffering or S.seeking) and not S.drag and S.ready then
        for i = 0, 7 do
            local a = i * math.pi / 4
            local ph = ((t * 9 - i) % 8) / 8
            circle(W / 2 + math.cos(a) * 20 * sc, H / 2 + math.sin(a) * 20 * sc, 4 * sc, "FFFFFF", 1 - ph * 0.85)
        end
    end
    S.fade = keep

    ov.res_x, ov.res_y = W, H
    ov.data = A.text
    ov:update()
end

local function active()
    return S.fade > 0.01 or want_visible() or S.bezel or S.hold_at ~= nil or S.hold_2x or S.ext_2x ~= nil or now() < S.vol_until or ((S.buffering or S.seeking) and S.ready)
end

local last_tick = 0
local function tick()
    local t = now()
    local dt = clamp(t - last_tick, 0, 0.1)
    last_tick = t
    -- YouTube-style hold LMB on video for 2x speed
    if S.down == "video" and S.hold_at and not S.hold_2x and not S.paused and (t - S.hold_at) >= 0.24 then
        S.hold_2x = true
        S.saved_speed = mp.get_property_number("speed", 1) or 1
        local boost = S.saved_speed < 2 and 2 or math.min(4, S.saved_speed * 2)
        mp.set_property_number("speed", boost)
        S.dirty = true
    end
    local target = want_visible() and 1 or 0
    if S.fade ~= target then
        local step = dt / (target == 1 and 0.10 or 0.22)
        S.fade = target == 1 and math.min(1, S.fade + step) or math.max(0, S.fade - step)
    end
    local vol_fading = t >= S.vol_until - 0.28 and t < S.vol_until + 0.05
    local cm_auto_active = S.cm_auto_idx ~= nil and t < S.cm_auto_until
    local animating = S.fade ~= target or S.bezel or S.hold_at ~= nil or vol_fading or cm_auto_active or ((S.buffering or S.seeking) and S.ready)
    if active() then
        if S.dirty or animating then S.dirty = false; render() end
        -- Pause 40Hz timer while purely static (paused / steady controls / static 2x pill) until an event wakes it
        if not animating and not S.hold_at and t >= S.vol_until and (S.paused or S.force or (S.fade == target and S.fade == 1 and over_controls())) then
            timer:kill()
        end
    else
        thumb_hide()
        comments_hide()
        ov:remove()
        timer:kill()
    end
end

local function wake()
    S.dirty = true
    if not timer:is_enabled() then last_tick = now(); timer:resume() end
end
timer = mp.add_periodic_timer(1 / 40, tick)
timer:kill()

-- ---------------------------------------------------------------- actions

local function seek_to_mouse(final)
    if S.dur <= 0 then return end
    S.drag_pct = clamp((S.mx - L.px0) / (L.px1 - L.px0), 0, 1)
    local t = now()
    if final then
        S.time = S.drag_pct * S.dur
        S.last_seek = t
        mp.commandv("seek", tostring(S.drag_pct * 100), "absolute-percent+exact")
    elseif t - S.last_seek > 0.08 then
        S.last_seek = t
        mp.commandv("seek", tostring(S.drag_pct * 100), "absolute-percent+keyframes")
    end
end

local function set_volume_from_mouse()
    if not L.vx0 then return end
    local v = clamp((S.mx - L.vx0) / (L.vx1 - L.vx0), 0, 1)
    local pct = math.floor(v * 100 + 0.5)
    S.vol = pct
    S.vol_until = now() + 1.2
    mp.set_property_number("volume", pct)
    mp.set_property_bool("mute", false)
end

local function activate(id)
    if id == "play" or id == "video" then
        mp.commandv("cycle", "pause")
        if id == "video" then S.bezel = { g = S.paused and G.play or G.pause, t = now() } end
    elseif id == "next" then app("next")
    elseif id == "mute" then mp.commandv("cycle", "mute")
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
            S.time = m.t
            mp.commandv("seek", tostring(m.t), "absolute+exact")
            wake()
        end
    elseif id == "loop" then
        app("toggle-loop")
        local en = S.lang == "en"
        mp.osd_message(S.loop and (en and "Loop off" or "Повтор выключен") or (en and "Loop on" or "Повтор включён: видео играет по кругу"), 2)
    elseif id == "speed" then app("menu-speed")
    end
end

mp.add_forced_key_binding("MBTN_LEFT", "flux-left", function(e)
    layout()
    if e.event == "down" then
        local h = hit()
        S.down = h
        if h == "seek" then S.drag = "seek"; seek_to_mouse(false)
        elseif h == "vol" then S.drag = "vol"; set_volume_from_mouse()
        elseif h == "video" and not S.paused then S.hold_at = now(); S.hold_2x = false end
    elseif e.event == "up" then
        if S.hold_2x then
            mp.set_property_number("speed", S.saved_speed or 1)
            S.hold_2x = false
            S.hold_at = nil
            S.drag, S.down = nil, nil
            wake()
            return
        end
        S.hold_at = nil
        if S.drag == "seek" then seek_to_mouse(true)
        elseif not S.drag and S.down and S.down == hit() then activate(S.down) end
        S.drag, S.down = nil, nil
    elseif e.event == "press" then
        activate(hit() or "video")
    end
    S.last_move = now()
    wake()
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
    -- over the picture: whatever the setting says; over the controls: always the menu
    if (h == "video" or h == nil) and S.rclick == "pause" then
        mp.commandv("cycle", "pause")
        S.bezel = { g = S.paused and G.play or G.pause, t = now() }
        S.last_move = now()
        wake()
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
        if S.drag == "seek" then seek_to_mouse(false) elseif S.drag == "vol" then set_volume_from_mouse() end
        wake()
    end
end)
mp.observe_property("pause", "bool", function(_, v) S.paused = v or false; wake() end)
mp.observe_property("loop-file", "string", function(_, v) S.loop = v ~= nil and v ~= "no"; wake() end)
local last_q = -1
mp.observe_property("time-pos", "number", function(_, v)
    S.time = v or 0
    local q = math.floor(S.time * 4)
    if q ~= last_q then
        last_q = q
        if S.fade > 0.01 then wake() else S.dirty = true end
    end
end)
mp.observe_property("duration", "number", function(_, v) S.dur = v or 0 end)
mp.observe_property("demuxer-cache-time", "number", function(_, v) S.cache = v or 0 end)
mp.observe_property("fullscreen", "bool", function(_, v) S.fs = v or false; wake() end)
mp.observe_property("paused-for-cache", "bool", function(_, v) S.buffering = v or false; wake() end)
mp.observe_property("seeking", "bool", function(_, v) S.seeking = v or false; wake() end)
mp.observe_property("osd-dimensions", "native", function() wake() end)
local vol_seen = false
mp.observe_property("volume", "number", function(_, v)
    S.vol = v or 100
    if vol_seen then S.vol_until = now() + 1.3; wake() end
    vol_seen = true
end)
local mute_seen = false
mp.observe_property("mute", "bool", function(_, v)
    S.mute = v or false
    if mute_seen then S.vol_until = now() + 1.3; wake() end
    mute_seen = true
end)
mp.register_event("file-loaded", function() S.thumb_file = nil; S.thumb_want = -99; thumb_hide(); comments_hide(); S.cm_auto_idx = nil; S.ready = true; S.segments = S.pending_segments or {}; if S.pending_sc_comments then S.sc_comments = S.pending_sc_comments end; wake() end)
mp.register_script_message("flux-thumb", function(file, w, h)
    if file == "none" or not file then
        S.thumb_file = nil
        thumb_hide()
    else
        S.thumb_file, S.thumb_w, S.thumb_h = file, tonumber(w) or 0, tonumber(h) or 0
    end
    wake()
end)

local function cancel_hold_2x()
    if S.hold_2x then
        mp.set_property_number("speed", S.saved_speed or 1)
        S.hold_2x = false
    end
    S.hold_at = nil
    S.down = nil
    S.drag = nil
end

mp.register_event("end-file", function()
    cancel_hold_2x()
    S.ext_2x = nil
    S.ready = false; S.time = 0; S.dur = 0; S.cache = 0
    S.thumb_file = nil; S.ov_file = nil; thumb_hide(); comments_hide(); S.sc_comments = {}; S.cm_auto_idx = nil
    wake()
end)

-- the app tells us what it knows: {"next":true,"quality":"1080p","speed":"1×","title":"…","segments":[[12.5,48],…]}
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
    if type(t.lang) == "string" then S.lang = t.lang end
    if t.rclick then S.rclick = t.rclick end
    if t.pip ~= nil then S.pip = t.pip end
    -- the app's theme accent, "#RRGGBB" -> ASS colour order BBGGRR
    if type(t.accent) == "string" and #t.accent == 7 then opts.brand = t.accent:sub(6, 7) .. t.accent:sub(4, 5) .. t.accent:sub(2, 3) end
    if t.segments then S.segments = t.segments; S.pending_segments = t.segments end
    if t.chapters then S.chapters = t.chapters end
    if t.subs ~= nil then S.subs_on = t.subs end
    if t.sc_on ~= nil then S.sc_on = t.sc_on; if not S.sc_on then comments_hide() end end
    if t.sc_comments then S.sc_comments = t.sc_comments; S.pending_sc_comments = t.sc_comments end
    wake()
end)
mp.register_script_message("flux-leave", function() S.mx, S.my = -1, -1; cancel_hold_2x(); wake() end)
mp.register_script_message("flux-show", function(v) S.force = v == "yes"; wake() end) -- for screenshots and tests
mp.register_script_message("flux-bezel", function(kind) S.bezel = { g = G[kind] or G.play, t = now() }; wake() end)
mp.register_script_message("flux-2x", function(v) S.ext_2x = (v and v ~= "off") and v or nil; wake() end)
