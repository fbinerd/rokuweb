sub init()
    m.top.functionName = "execute"
end sub

sub execute()
    bridgeHost = m.top.bridgeHost
    if bridgeHost = invalid or bridgeHost = ""
        m.top.responseCode = 0
        m.top.errorMessage = "Bridge host nao configurado."
        m.top.responseBody = ""
        return
    end if

    url = "http://" + bridgeHost + "/api/windows"
    transfer = CreateObject("roUrlTransfer")
    transfer.SetUrl(url)

    responseBody = invalid
    responseCode = 0

    responseBody = transfer.GetToString()
    responseCode = transfer.GetResponseCode()

    m.top.responseCode = responseCode
    if responseCode = 200 and responseBody <> invalid
        m.top.errorMessage = ""
        m.top.responseBody = responseBody
    else
        m.top.errorMessage = "Falha ao conectar em " + bridgeHost
        m.top.responseBody = ""
    end if
end sub
