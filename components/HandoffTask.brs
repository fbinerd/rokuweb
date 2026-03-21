sub init()
    m.top.functionName = "execute"
end sub

sub execute()
    bridgeHost = m.top.bridgeHost
    handoffType = m.top.handoffType
    handoffUrl = m.top.handoffUrl

    if bridgeHost = invalid or bridgeHost = "" or handoffType = invalid or handoffType = "" or handoffUrl = invalid or handoffUrl = ""
        m.top.responseCode = 0
        m.top.responseBody = ""
        m.top.completedToken = m.top.completedToken + 1
        return
    end if

    url = "http://" + bridgeHost + "/api/handoff"
    url = url + "?type=" + urlEncode(handoffType)
    url = url + "&url=" + urlEncode(handoffUrl)

    transfer = CreateObject("roUrlTransfer")
    transfer.SetUrl(url)
    body = transfer.GetToString()
    m.top.responseBody = body
    responseCode = transfer.GetResponseCode()
    if responseCode = invalid or responseCode = 0
        if body <> invalid and body <> ""
            responseCode = 200
        end if
    end if

    if responseCode = invalid or responseCode = 0
        m.top.responseCode = 0
    else
        m.top.responseCode = responseCode
    end if
    m.top.completedToken = m.top.completedToken + 1
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
