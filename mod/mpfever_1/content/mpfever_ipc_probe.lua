-- One ping per Lua VM, polled by its ordinary update callback. No gameplay messages use this transport yet.
local M = {}
local enabled = os.getenv("MPFEVER_IPC_PROBE") == "1"
local done, ticket, attempts = false, nil, 0

function M.tick(log, label)
	if not enabled or done then return end
	local ipc = package.loaded["mpfever_ipc"]
	if type(ipc) ~= "table" or type(ipc.ping) ~= "function" or type(ipc.poll) ~= "function" then
		done = true
		log("pipe probe [" .. label .. "]: native module unavailable (see native.log)")
		return
	end
	local ok, result = pcall(function()
		if not ticket then
			attempts = attempts + 1
			local id = ipc.ping()
			if type(id) == "number" and id > 0 then
				ticket = id
				log("pipe probe [" .. label .. "]: ping #" .. tostring(ticket) .. " sent")
			elseif attempts >= 300 then return "queue unavailable" end
			return nil
		end
		return ipc.poll(ticket)
	end)
	if not ok or result ~= nil then
		done = true
		log("pipe probe [" .. label .. "]: " .. tostring(result) .. " #" .. tostring(ticket))
	end
end

return M
