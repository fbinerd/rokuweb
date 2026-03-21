sub init()
    m.top.functionName = "execute"
    m.requestCount = 0
end sub

sub execute()
    m.requestCount = m.requestCount + 1
    bridgeHost = m.top.bridgeHost
    if bridgeHost = invalid or bridgeHost = ""
        m.top.responseCode = 0
        m.top.errorMessage = "Bridge host nao configurado."
        m.top.responseBody = ""
        m.top.completedToken = m.requestCount.ToStr()
        return
    end if

    url = "http://" + bridgeHost + "/api/windows"
    transfer = CreateObject("roUrlTransfer")
    transfer.SetUrl(url)
    transfer.SetMinimumTransferRate(1, 2)

    responseBody = invalid
    responseCode = 0

    responseBody = transfer.GetToString()

    if responseBody <> invalid and responseBody <> ""
        responseCode = 200
        registerDisplay(bridgeHost)
        m.top.errorMessage = ""
        m.top.responseBody = responseBody
    else
        m.top.errorMessage = "Falha ao conectar em " + bridgeHost
        m.top.responseBody = ""
    end if

    m.top.responseCode = responseCode
    m.top.completedToken = m.requestCount.ToStr()
end sub

sub registerDisplay(bridgeHost as string)
    deviceInfo = CreateObject("roDeviceInfo")
    appInfo = CreateObject("roAppInfo")
    deviceModel = deviceInfo.GetModel()
    firmwareVersion = deviceInfo.GetVersion()
    channelVersion = GetRokuChannelReleaseId()
    screenWidth = "1280"
    screenHeight = "720"
    deviceId = "roku-" + deviceModel + "-" + firmwareVersion

    registerUrl = "http://" + bridgeHost + "/api/register-display"
    registerUrl = registerUrl + "?deviceId=" + urlEncode(deviceId)
    registerUrl = registerUrl + "&deviceType=roku"
    registerUrl = registerUrl + "&deviceModel=" + urlEncode(deviceModel)
    registerUrl = registerUrl + "&firmwareVersion=" + urlEncode(firmwareVersion)
    registerUrl = registerUrl + "&channelVersion=" + urlEncode(channelVersion)
    registerUrl = registerUrl + "&screenWidth=" + screenWidth
    registerUrl = registerUrl + "&screenHeight=" + screenHeight

    transfer = CreateObject("roUrlTransfer")
    transfer.SetUrl(registerUrl)
    transfer.SetMinimumTransferRate(1, 2)
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
