sub init()
    m.statusLabel = m.top.findNode("statusLabel")
    m.subtitleLabel = m.top.findNode("subtitleLabel")
    m.windowList = m.top.findNode("windowList")
    m.windowList.content = CreateObject("roSGNode", "ContentNode")

    loadWindows()
end sub

sub loadWindows()
    bridgeHost = "10.0.0.83:8090"
    url = "http://" + bridgeHost + "/api/windows"

    transfer = CreateObject("roUrlTransfer")
    transfer.SetUrl(url)

    responseBody = transfer.GetToString()
    responseCode = transfer.GetResponseCode()

    if responseCode <> 200 or responseBody = invalid
        m.statusLabel.text = "Falha ao conectar em " + bridgeHost
        m.subtitleLabel.text = "Verifique se o app .NET esta aberto na mesma rede"
        setListMessage(["Nao foi possivel carregar as janelas do bridge HTTP."])
        return
    end if

    json = ParseJson(responseBody)
    if json = invalid or json.windows = invalid
        m.statusLabel.text = "Resposta invalida do bridge HTTP"
        setListMessage(["O endpoint /api/windows nao retornou um JSON esperado."])
        return
    end if

    items = []
    for each window in json.windows
        title = getString(window.title, "Janela sem titulo")
        state = getString(window.state, "Desconhecido")
        initialUrl = getString(window.initialUrl, "")
        publishedUrl = getString(window.publishedWebRtcUrl, "")

        line = title + " | " + state
        if initialUrl <> ""
            line = line + " | " + initialUrl
        end if
        if publishedUrl <> ""
            line = line + " | LinkRTC: " + publishedUrl
        end if

        items.Push(line)
    end for

    if items.Count() = 0
        m.statusLabel.text = "Bridge conectado, sem janelas disponiveis"
        setListMessage(["Nenhuma janela foi publicada no app .NET ainda."])
        return
    end if

    m.statusLabel.text = "Bridge conectado em " + bridgeHost
    m.subtitleLabel.text = "Janelas detectadas: " + json.windowCount.ToStr()
    setListMessage(items)
end sub

sub setListMessage(items as object)
    content = CreateObject("roSGNode", "ContentNode")

    for each itemText in items
        item = content.CreateChild("ContentNode")
        item.title = itemText
    end for

    m.windowList.content = content
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
