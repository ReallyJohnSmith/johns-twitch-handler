local addonName = ...

-- SavedVariables are written by WoW at logout/reload; the desktop app only reads this table from disk.
StreamSidekickProfilerDB = StreamSidekickProfilerDB or {}

local addonApi = C_AddOns
local getNumAddOns = addonApi and addonApi.GetNumAddOns or GetNumAddOns
local getAddOnInfo = addonApi and addonApi.GetAddOnInfo or GetAddOnInfo
local isAddOnLoaded = addonApi and addonApi.IsAddOnLoaded or IsAddOnLoaded

local function CollectSnapshot()
    -- Older game clients do not expose Blizzard's profiler API, so record that cleanly instead of failing.
    if not C_AddOnProfiler or not C_AddOnProfiler.GetAddOnMetric or not Enum.AddOnProfilerMetric then
        StreamSidekickProfilerDB = {
            version = 1,
            capturedAt = time(),
            available = false,
            message = "C_AddOnProfiler is not available in this game client.",
            addons = {},
        }
        return
    end

    local metrics = Enum.AddOnProfilerMetric
    local rows = {}
    for index = 1, getNumAddOns() do
        local name = getAddOnInfo(index)
        if name and name ~= addonName and isAddOnLoaded(name) then
            rows[name] = {
                recent = C_AddOnProfiler.GetAddOnMetric(name, metrics.RecentAverageTime) or 0,
                session = C_AddOnProfiler.GetAddOnMetric(name, metrics.SessionAverageTime) or 0,
                peak = C_AddOnProfiler.GetAddOnMetric(name, metrics.PeakTime) or 0,
                over10 = C_AddOnProfiler.GetAddOnMetric(name, metrics.CountTimeOver10Ms) or 0,
                over50 = C_AddOnProfiler.GetAddOnMetric(name, metrics.CountTimeOver50Ms) or 0,
            }
        end
    end

    StreamSidekickProfilerDB = {
        version = 1,
        capturedAt = time(),
        available = true,
        addons = rows,
    }
end

local frame = CreateFrame("Frame")
frame:RegisterEvent("PLAYER_LOGIN")
frame:RegisterEvent("PLAYER_LOGOUT")
frame:SetScript("OnEvent", function(_, event)
    if event == "PLAYER_LOGIN" then
        -- Fifteen seconds is frequent enough to catch spikes without adding meaningful addon overhead.
        C_Timer.NewTicker(15, CollectSnapshot)
    else
        CollectSnapshot()
    end
end)

SLASH_STREAMSIDEKICKPROFILER1 = "/ssp"
SlashCmdList.STREAMSIDEKICKPROFILER = function(message)
    local command = string.lower(strtrim(message or ""))
    if command == "events" then
        -- Blizzard's combat log is opt-in; the desktop tailer cannot enable it from outside the game.
        local enabled = LoggingCombat()
        LoggingCombat(not enabled)
        print("|cff9867ffJohn's Twitch Handler:|r combat-log event bridge " .. (enabled and "disabled." or "enabled. Boss victories can now create stream markers."))
        return
    end
    CollectSnapshot()
    print("|cff9867ffJohn's Twitch Handler:|r addon performance snapshot updated. Use /reload to write it to disk. Use /ssp events to toggle live boss markers.")
end
