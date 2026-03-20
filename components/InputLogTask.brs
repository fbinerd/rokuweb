sub init()
    m.top.functionName = "execute"
end sub

sub execute()
    bridgeHost = m.top.bridgeHost
    keyName = m.top.keyName

    if bridgeHost = invalid or bridgeHost = "" or keyName = invalid or keyName = ""
        return
    end if

    fullscreenValue = "false"
    if m.top.fullscreen
        fullscreenValue = "true"
    end if

    selectedValue = m.top.selectedIndex.ToStr()
    deviceId = m.top.deviceId
    deviceModel = m.top.deviceModel
    firmwareVersion = m.top.firmwareVersion
    channelVersion = m.top.channelVersion

    url = "http://" + bridgeHost + "/api/input-log"
    url = url + "?key=" + urlEncode(keyName)
    url = url + "&fullscreen=" + fullscreenValue
    url = url + "&selected=" + urlEncode(selectedValue)
    url = url + "&deviceId=" + urlEncode(deviceId)
    url = url + "&deviceModel=" + urlEncode(deviceModel)
    url = url + "&firmwareVersion=" + urlEncode(firmwareVersion)
    url = url + "&channelVersion=" + urlEncode(channelVersion)

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
            hex = Right("0" + Hex(code), 2)
            result = result + "%" + hex
        end if
    end for

    return result
end function
