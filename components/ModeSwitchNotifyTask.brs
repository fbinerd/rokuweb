sub init()
    m.top.functionName = "execute"
end sub

sub execute()
    bridgeHost = m.top.bridgeHost
    deviceId = m.top.deviceId
    windowId = m.top.windowId
    previousMode = m.top.previousMode
    targetMode = m.top.targetMode

    if bridgeHost = invalid or bridgeHost = "" or deviceId = invalid or deviceId = "" or windowId = invalid or windowId = ""
        return
    end if

    url = "http://" + bridgeHost + "/api/mode-switch-applied"
    url = url + "?deviceId=" + urlEncode(deviceId)
    url = url + "&windowId=" + urlEncode(windowId)
    url = url + "&previousMode=" + urlEncode(previousMode)
    url = url + "&targetMode=" + urlEncode(targetMode)

    transfer = CreateObject("roUrlTransfer")
    transfer.SetUrl(url)
    ignored = transfer.GetToString()
end sub

function urlEncode(value as string) as string
    result = ""
    for i = 1 to Len(value)
        char = Mid(value, i, 1)
        code = Asc(char)
        if (code >= 48 and code <= 57) or (code >= 65 and code <= 90) or (code >= 97 and code <= 122) or char = "-" or char = "_" or char = "."
            result = result + char
        else
            hex = toHexByte(code)
            result = result + "%" + hex
        end if
    end for

    return result
end function

function toHexByte(value as integer) as string
    digits = "0123456789ABCDEF"
    high = Int(value / 16)
    low = value Mod 16
    return Mid(digits, high + 1, 1) + Mid(digits, low + 1, 1)
end function
