sub init()
    m.bridgeHost = "10.1.0.10:8090"
    m.windowEntries = []
    m.selectedIndex = 0
    m.isFullscreen = false
    m.titleLabel = m.top.findNode("titleLabel")
    m.statusLabel = m.top.findNode("statusLabel")
    m.subtitleLabel = m.top.findNode("subtitleLabel")
    m.windowList = m.top.findNode("windowList")
    m.previewPoster = m.top.findNode("previewPoster")
    m.bridgeRequestTask = m.top.findNode("bridgeRequestTask")
    m.windowList.content = CreateObject("roSGNode", "ContentNode")
    m.bridgeRequestTask.observeField("responseCode", "onBridgeResponseCodeChanged")
    m.windowList.observeField("itemFocused", "onWindowListItemFocused")
    m.top.setFocus(true)
    m.previewPoster.loadDisplayMode = "scaleToFit"

    m.statusLabel.text = "Canal iniciado"
    m.subtitleLabel.text = "Pressione OK para consultar o servidor"
    setListMessage(["Canal carregado com sucesso.", "Pressione OK no controle para consultar o bridge."])
end sub

function onKeyEvent(key as string, press as boolean) as boolean
    if not press
        return false
    end if

    if m.isFullscreen
        if key = "back" or key = "Back"
            hideFullscreen()
            return true
        end if

        if key = "OK"
            sendRemoteCommand("ok")
            refreshFullscreenPreview()
            return true
        end if

        if key = "up" or key = "down" or key = "left" or key = "right"
            sendRemoteCommand(key)
            refreshFullscreenPreview()
            return true
        end if

        if key = "Play"
            sendRemoteCommand("play")
            refreshFullscreenPreview()
            return true
        end if

        return false
    end if

    if key = "OK" or key = "Play"
        if key = "OK" and m.windowEntries.Count() > 0
            showFullscreen()
            return true
        end if

        loadWindows()
        return true
    end if

    return false
end function

sub loadWindows()
    m.statusLabel.text = "Consultando bridge em " + m.bridgeHost
    m.subtitleLabel.text = "Aguarde a resposta do servidor"
    m.bridgeRequestTask.responseCode = -1
    m.bridgeRequestTask.responseBody = ""
    m.bridgeRequestTask.errorMessage = ""
    m.bridgeRequestTask.bridgeHost = m.bridgeHost
    m.bridgeRequestTask.control = "RUN"
end sub

sub onBridgeResponseCodeChanged()
    if m.bridgeRequestTask.responseCode < 0
        return
    end if

    applyBridgeResponse()
end sub

sub applyBridgeResponse()
    responseBody = m.bridgeRequestTask.responseBody
    responseCode = m.bridgeRequestTask.responseCode

    if responseCode <> 200 or responseBody = invalid or responseBody = ""
        m.statusLabel.text = "Falha ao conectar em " + m.bridgeHost
        m.subtitleLabel.text = "Verifique se o app .NET esta aberto na mesma rede"
        m.previewPoster.uri = ""
        setListMessage(["Nao foi possivel carregar as janelas do bridge HTTP.", "Pressione OK para tentar novamente."])
        return
    end if

    json = ParseJson(responseBody)
    if json = invalid or json.windows = invalid
        m.statusLabel.text = "Resposta invalida do bridge HTTP"
        m.previewPoster.uri = ""
        setListMessage(["O endpoint /api/windows nao retornou um JSON esperado."])
        return
    end if

    items = []
    m.windowEntries = []
    m.selectedIndex = 0
    m.previewPoster.uri = ""
    for each window in json.windows
        id = getString(window.id, "")
        title = getString(window.title, "Janela sem titulo")
        state = getString(window.state, "Desconhecido")
        initialUrl = getString(window.initialUrl, "")
        publishedUrl = getString(window.publishedWebRtcUrl, "")
        streamUrl = getString(window.streamUrl, "")
        thumbnailUrl = getString(window.thumbnailUrl, "")
        isPublishing = getBoolean(window.isPublishing)

        line = title + " | " + state
        if initialUrl <> ""
            line = line + " | " + initialUrl
        end if
        if publishedUrl <> ""
            line = line + " | Publicado: " + publishedUrl
        end if
        if streamUrl <> "" and streamUrl <> publishedUrl
            line = line + " | Stream: " + streamUrl
        end if
        if isPublishing
            line = line + " | Publicacao ligada"
        else
            line = line + " | Publicacao desligada"
        end if

        items.Push(line)
        m.windowEntries.Push({
            id: id
            title: title
            thumbnailUrl: thumbnailUrl
            initialUrl: initialUrl
        })

        if thumbnailUrl <> "" and items.Count() = 1
            m.previewPoster.uri = appendCacheBust(thumbnailUrl)
        end if
    end for

    if items.Count() = 0
        m.statusLabel.text = "Bridge conectado, sem janelas disponiveis"
        m.previewPoster.uri = ""
        setListMessage(["Nenhuma janela foi publicada no app .NET ainda."])
        return
    end if

    m.statusLabel.text = "Bridge conectado em " + m.bridgeHost
    m.subtitleLabel.text = "Janelas detectadas: " + json.windowCount.ToStr() + " | OK para atualizar"
    setListMessage(items)
    m.windowList.setFocus(true)
end sub

sub onWindowListItemFocused()
    focusedIndex = m.windowList.itemFocused
    if focusedIndex = invalid
        return
    end if

    if focusedIndex < 0 or focusedIndex >= m.windowEntries.Count()
        return
    end if

    m.selectedIndex = focusedIndex
    entry = m.windowEntries[m.selectedIndex]
    if entry.thumbnailUrl <> invalid and entry.thumbnailUrl <> ""
        m.previewPoster.uri = appendCacheBust(entry.thumbnailUrl)
    end if
end sub

sub showFullscreen()
    if m.windowEntries.Count() = 0
        return
    end if

    entry = m.windowEntries[m.selectedIndex]
    if entry.thumbnailUrl = invalid or entry.thumbnailUrl = ""
        m.statusLabel.text = "Preview indisponivel para a janela selecionada"
        return
    end if

    m.isFullscreen = true
    m.previewPoster.translation = [0, 0]
    m.previewPoster.width = 1280
    m.previewPoster.height = 720
    m.previewPoster.loadDisplayMode = "zoomToFill"
    m.windowList.visible = false
    m.titleLabel.visible = false
    m.statusLabel.visible = false
    m.subtitleLabel.visible = false
    m.previewPoster.uri = appendCacheBust(entry.thumbnailUrl)
end sub

sub hideFullscreen()
    m.isFullscreen = false
    m.previewPoster.translation = [50, 190]
    m.previewPoster.width = 640
    m.previewPoster.height = 360
    m.previewPoster.loadDisplayMode = "scaleToFit"
    m.windowList.visible = true
    m.titleLabel.visible = true
    m.statusLabel.visible = true
    m.subtitleLabel.visible = true

    if m.windowEntries.Count() > 0
        entry = m.windowEntries[m.selectedIndex]
        m.statusLabel.text = "Bridge conectado em " + m.bridgeHost
        m.subtitleLabel.text = "Selecionado: " + entry.title + " | OK para tela cheia"
        if entry.thumbnailUrl <> invalid and entry.thumbnailUrl <> ""
            m.previewPoster.uri = appendCacheBust(entry.thumbnailUrl)
        end if
        m.windowList.setFocus(true)
    end if
end sub

sub sendRemoteCommand(command as string)
    if m.windowEntries.Count() = 0
        return
    end if

    entry = m.windowEntries[m.selectedIndex]
    if entry.id = invalid or entry.id = ""
        return
    end if

    transfer = CreateObject("roUrlTransfer")
    transfer.SetUrl("http://" + m.bridgeHost + "/api/control?windowId=" + entry.id + "&command=" + command)
    ignored = transfer.GetToString()
end sub

sub refreshFullscreenPreview()
    if not m.isFullscreen or m.windowEntries.Count() = 0
        return
    end if

    entry = m.windowEntries[m.selectedIndex]
    if entry.thumbnailUrl <> invalid and entry.thumbnailUrl <> ""
        m.previewPoster.uri = appendCacheBust(entry.thumbnailUrl)
    end if
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

function getBoolean(value as dynamic) as boolean
    if value = invalid
        return false
    end if

    if type(value) = "roBoolean" or type(value) = "Boolean"
        return value
    end if

    return false
end function

function appendCacheBust(url as string) as string
    separator = "?"
    if Instr(1, url, "?") > 0
        separator = "&"
    end if

    now = CreateObject("roDateTime")
    return url + separator + "ts=" + now.AsSeconds().ToStr()
end function
