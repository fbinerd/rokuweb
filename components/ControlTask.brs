sub init()
    m.top.functionName = "execute"
end sub

sub execute()
    bridgeHost = m.top.bridgeHost
    windowId = m.top.windowId
    command = m.top.command

    if bridgeHost = invalid or bridgeHost = "" or windowId = invalid or windowId = "" or command = invalid or command = ""
        return
    end if

    url = "http://" + bridgeHost + "/api/control"
    url = url + "?windowId=" + urlEncode(windowId)
    url = url + "&command=" + urlEncode(command)

    if command = "move" or command = "click"
        url = url + "&x=" + m.top.cursorX.ToStr()
        url = url + "&y=" + m.top.cursorY.ToStr()
    end if

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
