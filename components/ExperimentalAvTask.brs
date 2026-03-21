sub init()
    m.top.functionName = "execute"
    m.requestCount = 0
end sub

sub execute()
    m.requestCount = m.requestCount + 1
    bridgeUrl = m.top.bridgeUrl
    if bridgeUrl = invalid or bridgeUrl = ""
        m.top.responseCode = 0
        m.top.errorMessage = "Experimental AV URL nao configurada."
        m.top.responseBody = ""
        m.top.completedToken = m.requestCount.ToStr()
        return
    end if

    method = UCase(getString(m.top.httpMethod, "GET"))
    requestBody = getString(m.top.requestBody, "")

    transfer = CreateObject("roUrlTransfer")
    transfer.SetUrl(bridgeUrl)
    transfer.SetMinimumTransferRate(1, 2)

    if method = "POST"
        transfer.SetRequest("POST")
        transfer.AddHeader("Content-Type", "application/json")
        responseBody = transfer.PostFromString(requestBody)
    else
        responseBody = transfer.GetToString()
    end if

    if responseBody <> invalid and responseBody <> ""
        m.top.responseCode = 200
        m.top.errorMessage = ""
        m.top.responseBody = responseBody
    else
        m.top.responseCode = 0
        m.top.errorMessage = "Falha ao chamar " + bridgeUrl
        m.top.responseBody = ""
    end if

    m.top.completedToken = m.requestCount.ToStr()
end sub

function getString(value as dynamic, fallback as string) as string
    if value = invalid
        return fallback
    end if

    if Type(value) = "roString" or Type(value) = "String"
        return value
    end if

    return fallback
end function
